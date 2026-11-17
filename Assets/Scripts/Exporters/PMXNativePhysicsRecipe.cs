using System;
using System.Collections.Generic;
using System.Linq;
using LibMMD.Model;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Body = LibMMD.Model.MMDRigidBody;

/// <summary>Native port of the frozen 2026-11-15 recipes. Calculations use PMX
/// units internally; only this boundary converts to the exporter's Unity space.</summary>
internal sealed partial class PMXNativePhysicsRecipe
{
    readonly RawMMDModel model;
    readonly List<Bone> bones;
    readonly List<Body> bodies;
    readonly List<MMDJoint> joints;
    readonly List<Node> nodes;
    readonly JObject metadata;
    readonly List<Body> anatomy = new List<Body>();
    readonly Dictionary<int, List<(int bone, float weight)>> weights;
    readonly Vector3[] points;
    readonly int originalBoneCount;
    readonly Bone[] sourceBones;
    readonly Dictionary<int, int> skirtDrivers = new Dictionary<int, int>();
    JObject recipe;
    sealed class Node
    {
        internal string Name, Root;
        internal int Bone, Body, Joint, Slot, Skirt;
        internal JObject Data;
        internal float Native(string key) => (float)Data["native"][key];
        internal bool IsHair => Root.StartsWith("Sp_He_Hair", StringComparison.Ordinal);
        internal bool IsEar => Root.StartsWith("Sp_He_Ear", StringComparison.Ordinal);
        internal bool IsTail => Root.StartsWith("Sp_Hi_Tail", StringComparison.Ordinal);
        internal bool IsSkirt => Skirt >= 0 || Root.StartsWith("Sp_Hi_",StringComparison.Ordinal)&&Root.Contains("Skirt");
    }
    static Vector3 P(Vector3 v) => new Vector3(-v.x, v.y, -v.z) * 12.5f;
    static Vector3 U(Vector3 v) => new Vector3(-v.x, v.y, -v.z) / 12.5f;
    static Vector3 EulerIn(Vector3 v) => new Vector3(-v.x, v.y, -v.z) * Mathf.Deg2Rad;
    static Vector3 EulerOut(Vector3 v) => new Vector3(-v.x, v.y, -v.z) * Mathf.Rad2Deg;
    static Vector3 V(JToken t) => new Vector3((float)t[0], (float)t[1], (float)t[2]);
    static Quaternion Rotation(Vector3 v) => Quaternion.AngleAxis(v.y * Mathf.Rad2Deg, Vector3.up) *
        Quaternion.AngleAxis(v.x * Mathf.Rad2Deg, Vector3.right) * Quaternion.AngleAxis(v.z * Mathf.Rad2Deg, Vector3.forward);
    static Vector3 Euler(Quaternion q)
    {
        var m = Matrix4x4.Rotate(q); float x = Mathf.Asin(Mathf.Clamp(-m.m12, -1, 1));
        return new Vector3(x, Mathf.Atan2(m.m02, m.m22), Mathf.Atan2(m.m10, m.m11));
    }
    static float Quantile(IEnumerable<float> values, float p)
    {
        var a = values.OrderBy(x => x).ToArray(); if (a.Length == 0) throw new InvalidOperationException("Empty physics geometry sample");
        float at = (a.Length - 1) * p; int low = (int)at; return Mathf.Lerp(a[low], a[Math.Min(low + 1, a.Length - 1)], at - low);
    }
    static Body Copy(Body b) => new Body { Name=b.Name, NameEn=b.NameEn, AssociatedBoneIndex=b.AssociatedBoneIndex,
        CollisionGroup=b.CollisionGroup, CollisionMask=b.CollisionMask, Shape=b.Shape, Dimemsions=b.Dimemsions,
        Position=b.Position, Rotation=b.Rotation, Mass=b.Mass, TranslateDamp=b.TranslateDamp, RotateDamp=b.RotateDamp,
        Restitution=b.Restitution, Friction=b.Friction, Type=b.Type };
    static Bone Clone(Bone b) => new Bone { Name=b.Name, NameEn=b.NameEn, Position=b.Position, ParentIndex=b.ParentIndex,
        TransformLevel=b.TransformLevel, Rotatable=b.Rotatable, Movable=b.Movable, Visible=b.Visible, Controllable=b.Controllable,
        HasIk=b.HasIk, AppendRotate=b.AppendRotate, AppendTranslate=b.AppendTranslate, RotAxisFixed=b.RotAxisFixed,
        UseLocalAxis=b.UseLocalAxis, PostPhysics=b.PostPhysics, ReceiveTransform=b.ReceiveTransform,
        ChildBoneVal=Bone.ChildBone.CopyOf(b.ChildBoneVal), AppendBoneVal=Bone.AppendBone.CopyOf(b.AppendBoneVal),
        LocalAxisVal=Bone.LocalAxis.CopyOf(b.LocalAxisVal), RotAxis=b.RotAxis, ExportKey=b.ExportKey,
        IkInfoVal=b.IkInfoVal==null?null:Bone.IkInfo.CopyOf(b.IkInfoVal) };
    PMXNativePhysicsRecipe(RawMMDModel m)
    {
        model=m; metadata=JObject.FromObject(m.PhysicsExportInfo); bones=m.Bones.ToList(); bodies=m.Rigidbodies.ToList(); joints=m.Joints.ToList();
        originalBoneCount=bones.Count;
        nodes=((JArray)metadata["nodes"]).OfType<JObject>().Select(n=>new Node { Name=(string)n["source"], Root=(string)n["Root"],
            Bone=(int)n["bone"], Body=(int)n["body"], Joint=(int)n["joint"], Slot=(int)n["Slot"], Skirt=(int)n["SkirtIndex"], Data=n }).ToList();
        foreach(var b in bones) b.Position=P(b.Position);
        foreach(var b in bodies) { b.Position=P(b.Position); b.Rotation=EulerIn(b.Rotation); b.Dimemsions*=12.5f; }
        foreach(var j in joints) { j.Position=P(j.Position); j.Rotation=EulerIn(j.Rotation); }
        sourceBones=bones.Select(Clone).ToArray(); points=m.Vertices.Select(v=>P(v.Coordinate)).ToArray();
        weights=Enumerable.Range(0,m.Vertices.Length).ToDictionary(i=>i,i=>Weights(m.Vertices[i]).ToList());
        var asset=Resources.Load<TextAsset>("PMXPhysicsRecipe20261115");
        if(asset==null)throw new InvalidOperationException("Missing frozen PMX physics recipe");
        recipe=JObject.Parse(asset.text);
    }
    internal static void Apply(RawMMDModel model)
    {
        if(model.Rigidbodies.Length==0)return;
        var p=new PMXNativePhysicsRecipe(model);
        p.PruneUnsupported(); p.BuildAnatomy(); p.ConfigureSurfaces(); p.ReleaseUnusedCollisionGroups(); p.ConfigureSkirts(); p.ConfigureEars(); p.ConfigureFringe(); p.ConfigureTail(); p.ConfigureDecorations(); p.Finish();
    }
    void Finish()
    {
        foreach(var b in bones)b.Position=U(b.Position);
        foreach(var b in bodies){b.Position=U(b.Position);b.Rotation=EulerOut(b.Rotation);b.Dimemsions/=12.5f;}
        foreach(var j in joints){j.Position=U(j.Position);j.Rotation=EulerOut(j.Rotation);}
        model.Bones=bones.ToArray();model.Rigidbodies=bodies.ToArray();model.Joints=joints.ToArray();
        model.PhysicsExportInfo=new { schema="UmaViewer.NativePhysicsRecipe/1", recipe="2026-11-15-frozen", ears=true,
            unifiedSkirt=true, source=metadata, bones=bones.Count, rigidBodies=bodies.Count, joints=joints.Count };
    }
    int Find(string name) => bones.FindIndex(b=>b.NameEn==name||b.Name==name);
    void PruneUnsupported()
    {
        var excluded=nodes.Where(n=>!n.IsEar&&!n.IsHair&&!n.IsSkirt&&!n.IsTail).ToArray();
        if(metadata["decorationSeeds"]==null)metadata["decorationSeeds"]=new JArray();
        foreach(var n in excluded){string kind=n.Root.Split('_').ElementAtOrDefault(2)??"";
            string rule=kind.StartsWith("Tie")?"chest-tie":kind.StartsWith("Mantle")?"long-hair":kind.StartsWith("Belt")?"ponytail":kind.StartsWith("Sleeve")?"arm-follower":null;
            if(rule!=null){var seed=(JObject)n.Data.DeepClone();seed["rule"]=rule;seed["kinematicZeroSwingRoot"]=bodies[n.Body].Type==0;((JArray)metadata["decorationSeeds"]).Add(seed);}}
        var removed=excluded.Select(n=>n.Body).ToHashSet();var roots=excluded.Select(n=>n.Root).Except(nodes.Except(excluded).Select(n=>n.Root)).ToHashSet();
        for(int i=0;i<bodies.Count;i++)if(roots.Any(r=>bodies[i].Name==r+"_anchor"))removed.Add(i);
        var removedJoints=joints.Select((j,i)=>(j,i)).Where(p=>p.j.AssociatedRigidBodyIndex.Any(removed.Contains)).Select(p=>p.i).ToHashSet();
        RemovePhysics(removed,removedJoints);
    }
    void ReleaseUnusedCollisionGroups()
    {
        var dynamic=bodies.Where(b=>b.Type!=0).ToArray();
        foreach(var b in bodies.Where(b=>b.Type==0))if(!dynamic.Any(d=>Collides(b,d))){b.CollisionGroup=15;b.CollisionMask=0;}
    }
    int Need(string name){int i=Find(name);if(i<0)throw new InvalidOperationException("Physics skeleton missing "+name);return i;}
    int Order => bones.Max(b=>b.TransformLevel)+1;
    HashSet<int> Descendants(IEnumerable<int> roots, int count=-1)
    {
        var selected=new HashSet<int>(roots);int end=count<0?bones.Count:count;
        bool changed;do{changed=false;for(int i=0;i<end;i++)if(selected.Contains(bones[i].ParentIndex))changed|=selected.Add(i);}while(changed);return selected;
    }
    int Aux(string name,Vector3 pos,int parent,int order)
    {
        bones.Add(new Bone { Name=name,NameEn=name,Position=pos,ParentIndex=parent,TransformLevel=order,Rotatable=true,Movable=true,
            ChildBoneVal=new Bone.ChildBone { Offset=Vector3.zero } });return bones.Count-1;
    }
    int Grant(string name,Vector3 pos,int parent,int source,float ratio,int order,bool translation=false)
    {
        int i=Aux(name,pos,parent,order);bones[i].AppendRotate=!translation;bones[i].AppendTranslate=translation;
        bones[i].AppendBoneVal=new Bone.AppendBone{Index=source,Ratio=ratio};return i;
    }
    int Hinge(string name,Vector3 pivot,Vector3 end,int parent,int targetParent,Vector3 targetPosition,int axis,float low,float high,int order)
    {
        int link=Aux(name+"_link",pivot,parent,order),tip=Aux(name+"_tip",end,link,order+1),target=Aux(name+"_target",targetPosition,targetParent,order+2);
        Vector3 lo=Vector3.zero,hi=Vector3.zero;lo[axis]=low;hi[axis]=high;
        bones[target].HasIk=true;bones[target].IkInfoVal=new Bone.IkInfo {IkTargetIndex=tip,CcdIterateLimit=32,CcdAngleLimit=15*Mathf.Deg2Rad,
            IkLinks=new[]{new Bone.IkLink {LinkIndex=link,HasLimit=true,LoLimit=lo,HiLimit=hi}}};return link;
    }
    void Reorder(HashSet<int> selected)
    {
        selected=Descendants(selected);int order=Order;
        var solvers=bones.Select((b,i)=>(b,i)).Where(p=>p.b.HasIk).SelectMany(p=>p.b.IkInfoVal.IkLinks.Select(link=>(link.LinkIndex,solver:p.i)))
            .GroupBy(p=>p.LinkIndex).ToDictionary(g=>g.Key,g=>g.Select(p=>p.solver).ToArray());
        IEnumerable<int> Dependencies(int i)
        {
            var b=bones[i];yield return b.ParentIndex;
            if(b.AppendRotate||b.AppendTranslate){int source=b.AppendBoneVal.Index;yield return source;
                // A grant must read the link after its IK controller has solved it.
                if(solvers.TryGetValue(source,out var controllers))foreach(int controller in controllers)yield return controller;}
            if(b.HasIk){yield return b.IkInfoVal.IkTargetIndex;foreach(var link in b.IkInfoVal.IkLinks)yield return link.LinkIndex;}
        }
        while(selected.Count>0){var ready=selected.Where(i=>!Dependencies(i).Any(selected.Contains)).OrderBy(i=>i).ToArray();
            if(ready.Length==0)throw new InvalidOperationException("Cyclic physics helper dependency");
            foreach(int i in ready){bones[i].TransformLevel=order;selected.Remove(i);}order++;}
    }
    static IEnumerable<(int bone,float weight)> Weights(Vertex v)
    {
        switch(v.SkinningOperator.Param){
        case SkinningOperator.Bdef1 w:yield return(w.BoneId,1);break;
        case SkinningOperator.Bdef2 w:yield return(w.BoneId[0],w.BoneWeight);yield return(w.BoneId[1],1-w.BoneWeight);break;
        case SkinningOperator.Sdef w:yield return(w.BoneId[0],w.BoneWeight);yield return(w.BoneId[1],1-w.BoneWeight);break;
        case SkinningOperator.Bdef4 w:for(int i=0;i<4;i++)yield return(w.BoneId[i],w.BoneWeight[i]);break;}
    }
    static bool Collides(Body a,Body b) => (a.CollisionMask&(1<<b.CollisionGroup))!=0&&(b.CollisionMask&(1<<a.CollisionGroup))!=0;
    static float Gap(Body b,Vector3 point,float radius=0)
    {
        Vector3 axis=Rotation(b.Rotation)*Vector3.up,delta=axis*(b.Shape==Body.RigidBodyShape.RigidShapeCapsule?b.Dimemsions.y:0),a=b.Position-delta*.5f;
        float f=Mathf.Clamp01(Vector3.Dot(point-a,delta)/Mathf.Max(delta.sqrMagnitude,1e-12f));return (point-a-f*delta).magnitude-b.Dimemsions.x-radius;
    }
    static Vector3 Moments(Body b,MMDJoint j)
    {
        var frame=Quaternion.Inverse(Rotation(j.Rotation));var relative=frame*Rotation(b.Rotation);
        float r=b.Dimemsions.x,h=b.Dimemsions.y;
        Vector3 own=new Vector3((h+2*r)*(h+2*r)/12+r*r/4,r*r/2,(h+2*r)*(h+2*r)/12+r*r/4)*b.Mass;
        var m=Matrix4x4.Rotate(relative);Vector3 tensor=Vector3.zero;
        for(int i=0;i<3;i++)for(int k=0;k<3;k++)tensor[i]+=m[i,k]*m[i,k]*own[k];
        Vector3 off=frame*(b.Position-j.Position);return tensor+b.Mass*(Vector3.one*off.sqrMagnitude-Vector3.Scale(off,off));
    }
    void AssemblySpring(Node n,float hz)
    {
        var j=joints[n.Joint];Vector3 moment=Moments(bodies[n.Body],j);
        foreach(var weld in joints.Where(w=>w.AssociatedRigidBodyIndex[0]==n.Body&&w.Name.StartsWith("weld_surface_",StringComparison.Ordinal)))moment+=Moments(bodies[weld.AssociatedRigidBodyIndex[1]],j);
        Vector3 k=moment*Mathf.Pow(2*Mathf.PI*hz,2);for(int axis=0;axis<3;axis++)if(j.RotationLowLimit[axis]==j.RotationHiLimit[axis])k[axis]=0;j.SpringRotate=k;
    }
}
