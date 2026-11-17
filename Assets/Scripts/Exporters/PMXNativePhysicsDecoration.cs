using System;
using System.Collections.Generic;
using System.Linq;
using LibMMD.Model;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Body=LibMMD.Model.MMDRigidBody;

internal sealed partial class PMXNativePhysicsRecipe
{
    sealed class Decoration
    {
        internal Node Node;
        internal string Rule;
        internal Quaternion Frame;
        internal readonly List<int> Contacts=new List<int>();
    }
    static Vector3 Bounds(IEnumerable<Vector3> data,bool maximum)
    {
        var result=maximum?Vector3.one*float.NegativeInfinity:Vector3.one*float.PositiveInfinity;
        foreach(var v in data)result=maximum?Vector3.Max(result,v):Vector3.Min(result,v);return result;
    }
    static Vector3 PivotMoments(Body b,MMDJoint joint)
    {
        if(b.Shape==Body.RigidBodyShape.RigidShapeCapsule)return Moments(b,joint);
        Vector3 own=b.Shape==Body.RigidBodyShape.RigidShapeSphere?Vector3.one*(.4f*b.Mass*b.Dimemsions.x*b.Dimemsions.x):
            b.Mass/3*(Vector3.one*b.Dimemsions.sqrMagnitude-Vector3.Scale(b.Dimemsions,b.Dimemsions));
        var inverse=Quaternion.Inverse(Rotation(joint.Rotation));var relative=Matrix4x4.Rotate(inverse*Rotation(b.Rotation));Vector3 tensor=Vector3.zero;
        for(int i=0;i<3;i++)for(int k=0;k<3;k++)tensor[i]+=relative[i,k]*relative[i,k]*own[k];
        var offset=inverse*(b.Position-joint.Position);return tensor+b.Mass*(Vector3.one*offset.sqrMagnitude-Vector3.Scale(offset,offset));
    }
    List<Vector3> WeightedPoints(Decoration d,HashSet<int> simulated)
    {
        var attached=new HashSet<int>{d.Node.Bone};bool changed;
        do{changed=false;for(int i=0;i<originalBoneCount;i++)if(!simulated.Contains(i)&&attached.Contains(bones[i].ParentIndex))changed|=attached.Add(i);}while(changed);
        return weights.Where(w=>w.Value.Where(v=>attached.Contains(v.bone)).Sum(v=>v.weight)>=.5f).Select(w=>points[w.Key]).ToList();
    }
    void ConfigureDecorations()
    {
        var seeds=((JArray)metadata["decorationSeeds"]).OfType<JObject>().OrderBy(n=>(string)n["Root"],StringComparer.Ordinal).ThenBy(n=>(int)n["Slot"]).Select(n=>new Decoration{
            Node=new Node{Name=(string)n["source"],Root=(string)n["Root"],Slot=(int)n["Slot"],Bone=(int)n["bone"],Data=n},Rule=(string)n["rule"],
            Frame=Quaternion.identity}).ToList();
        if(seeds.Count==0)return;
        foreach(var d in seeds){var q=d.Node.Data["frame"];d.Frame=new Quaternion(0,1,0,0)*new Quaternion((float)q[0],(float)q[1],(float)q[2],(float)q[3]);}
        foreach(var d in seeds.Where(d=>d.Rule=="arm-follower"&&d.Node.Slot==0)){
            var candidates=new[]{"L","R"}.Where(s=>!d.Node.Root.Contains("_L_")&&!d.Node.Root.Contains("_R_")||d.Node.Root.Contains("_"+s+"_"))
                .SelectMany(s=>new[]{Find("Arm_"+s),Find("Elbow_"+s)}).Where(i=>i>=0).ToArray();
            int ancestor=bones[d.Node.Bone].ParentIndex;while(ancestor>=0&&!candidates.Contains(ancestor))ancestor=bones[ancestor].ParentIndex;
            if(ancestor<0&&candidates.Length>0)bones[d.Node.Bone].ParentIndex=candidates.OrderBy(i=>(bones[i].Position-bones[d.Node.Bone].Position).sqrMagnitude).First();}
        seeds=seeds.Where(d=>d.Rule!="arm-follower").ToList();if(seeds.Count==0)return;
        int originalBodies=bodies.Count;var indexes=new Dictionary<int,int>();var selected=seeds.Select(d=>d.Node.Bone).ToHashSet();
        foreach(var d in seeds){var n=d.Node;Vector3 start=P(V(n.Data["start"])),end=P(V(n.Data["end"]));var config=recipe["decoration"]["hanging"][d.Rule];
            int parent=bones[n.Bone].ParentIndex;while(selected.Contains(parent)&&!indexes.ContainsKey(parent))parent=bones[parent].ParentIndex;
            int anchor;if(!indexes.TryGetValue(parent,out anchor)){anchor=bodies.Count;bodies.Add(new Body{Name=n.Root+"_decoration_anchor",NameEn=n.Root+"_decoration_anchor",AssociatedBoneIndex=parent,
                Position=start,Rotation=Euler(d.Frame),Dimemsions=new Vector3(.00001f,0,0)});}
            float length=(end-start).magnitude,radius=Mathf.Min((float)n.Data["radius"]*12.5f,length*.15f),mass=(float)config["mass"]*Mathf.Pow((float)config["massTaper"],n.Slot);
            var b=new Body{Name=n.Name+"_decoration",NameEn=n.Name+"_decoration",AssociatedBoneIndex=n.Bone,Position=(start+end)*.5f,Rotation=Euler(d.Frame),
                Shape=Body.RigidBodyShape.RigidShapeCapsule,Dimemsions=new Vector3(radius,Mathf.Max(.001f,length-2*radius),0),Mass=mass,
                Type=d.Rule=="chest-tie"?Body.RigidBodyType.RigidTypePhysicsStrict:Body.RigidBodyType.RigidTypePhysics,
                TranslateDamp=(float)config["linearDamping"],RotateDamp=(float)config["angularDamping"]};
            if(d.Rule!="chest-tie"&&((bool?)n.Data["kinematicZeroSwingRoot"]??false)){b.Type=0;b.Mass=0;}
            n.Body=bodies.Count;n.Joint=joints.Count;indexes[n.Bone]=n.Body;bodies.Add(b);
            Vector3 limit=config["swingDegrees"]!=null?V(config["swingDegrees"])*Mathf.Deg2Rad:
                new Vector3(1,0,1)*(2*Mathf.Atan(Mathf.Sqrt(Mathf.Tan((float)config["twistBudgetDegrees"]*Mathf.Deg2Rad*.5f))));
            joints.Add(new MMDJoint{Name=n.Name+"_decoration_joint",NameEn=n.Name+"_decoration_joint",AssociatedRigidBodyIndex=new[]{anchor,n.Body},Position=start,Rotation=Euler(d.Frame),RotationLowLimit=-limit,RotationHiLimit=limit});}
        var simulated=seeds.Select(d=>d.Node.Bone).ToHashSet();
        foreach(var d in seeds){var n=d.Node;var b=bodies[n.Body];var j=joints[n.Joint];if(d.Rule=="chest-tie"){
                var local=WeightedPoints(d,simulated).Select(p=>Quaternion.Inverse(d.Frame)*(p-j.Position)).ToArray();if(local.Length<3)throw new InvalidOperationException("Tie has no weighted mesh: "+n.Name);
                var lo=Bounds(local,false);var hi=Bounds(local,true);b.Shape=Body.RigidBodyShape.RigidShapeBox;b.Dimemsions=Vector3.Max((hi-lo)*.5f,Vector3.one/160);b.Position=j.Position+d.Frame*((lo+hi)*.5f);}
            SetDecorationSpring(d);}
        foreach(int i in Descendants(seeds.Where(d=>bodies[d.Node.Body].Type!=0).Select(d=>d.Node.Bone)))bones[i].PostPhysics=true;
        foreach(var d in seeds.Where(d=>bodies[d.Node.Body].Type==0))bones[d.Node.Bone].PostPhysics=false;
        foreach(var d in seeds.Where(d=>d.Rule=="chest-tie"))TiePanels(d,seeds,simulated);
        var occupied=bodies.Take(originalBodies).Where(b=>b.Type!=0).Select(b=>b.CollisionGroup).ToHashSet();var free=Enumerable.Range(0,16).Where(g=>!occupied.Contains(g)&&g!=15).ToArray();
        int hangingGroup=free[0],tieGroup=seeds.Any(d=>d.Rule=="chest-tie")?free[1]:-1;
        int reserved=(1<<hangingGroup)|(tieGroup>=0?1<<tieGroup:0);
        foreach(var b in bodies.Take(originalBodies)){if(b.Type==0)b.CollisionMask&=(ushort)~reserved;if(b.Name.StartsWith("Body_",StringComparison.Ordinal))b.CollisionMask|=(ushort)(1<<hangingGroup);}
        var drivers=seeds.Where(d=>d.Rule=="chest-tie").Select(d=>d.Node.Body).ToHashSet();var tieBodies=drivers.Concat(seeds.SelectMany(d=>d.Contacts)).ToHashSet();
        for(int i=originalBodies;i<bodies.Count;i++){var b=bodies[i];if(b.Type==0){b.CollisionGroup=hangingGroup;b.CollisionMask=0;continue;}
            b.CollisionGroup=tieBodies.Contains(i)?tieGroup:hangingGroup;b.CollisionMask=(ushort)(tieBodies.Contains(i)?1<<hangingGroup:1<<15);
            if(drivers.Contains(i))b.CollisionMask=0;
            if(!tieBodies.Contains(i)&&!TrimSurface(b,anatomy,.01f))b.CollisionMask=0;}
        BuildTieChest(seeds,tieGroup,hangingGroup,originalBodies);
        foreach(var d in seeds.Where(d=>d.Rule=="chest-tie"&&d.Node.Slot==0))TieLimits(d);
    }
    void SetDecorationSpring(Decoration d)
    {
        var j=joints[d.Node.Joint];Vector3 moment=PivotMoments(bodies[d.Node.Body],j);foreach(int i in d.Contacts)moment+=PivotMoments(bodies[i],j);
        float hz=(float)recipe["decoration"]["hanging"][d.Rule]["frequencyHz"];Vector3 k=moment*Mathf.Pow(2*Mathf.PI*hz,2);
        for(int i=0;i<3;i++)if(j.RotationLowLimit[i]==j.RotationHiLimit[i])k[i]=0;j.SpringRotate=k;
    }
    void TiePanels(Decoration d,List<Decoration> seeds,HashSet<int> simulated)
    {
        var n=d.Node;var b=bodies[n.Body];var j=joints[n.Joint];float length=seeds.Where(s=>s.Node.Root==n.Root).Sum(s=>(P(V(s.Node.Data["end"]))-P(V(s.Node.Data["start"]))).magnitude);
        var local=WeightedPoints(d,simulated).Select(p=>Quaternion.Inverse(d.Frame)*(p-j.Position)).ToArray();float lower=local.Min(p=>p.y),upper=local.Max(p=>p.y);
        int count=Math.Max(1,Mathf.CeilToInt((upper-lower)/(length*.16f)));var groups=new List<Vector3[]>();
        for(int k=0;k<count;k++){float lo=Mathf.Lerp(lower,upper,k/(float)count),hi=Mathf.Lerp(lower,upper,(k+1f)/count);var g=local.Where(p=>p.y>=lo&&p.y<=hi).ToArray();if(g.Length>0)groups.Add(g);}
        float mass=b.Mass;Vector3 moment=PivotMoments(b,j);float inertia=Mathf.Max(moment.x,moment.y,moment.z);
        b.Shape=Body.RigidBodyShape.RigidShapeSphere;b.Position=j.Position;b.Dimemsions=new Vector3(Mathf.Sqrt(inertia/(.4f*mass)),0,0);b.Mass=mass*.8f;
        for(int k=0;k<groups.Count;k++){
            var g=groups[k];Vector3 average=g.Aggregate(Vector3.zero,(sum,p)=>sum+p)/g.Length;
            float syz=0,syy=0;foreach(var p in g){syz+=(p.y-average.y)*(p.z-average.z);syy+=(p.y-average.y)*(p.y-average.y);}
            var longitudinal=new Vector3(0,1,syz/Mathf.Max(syy,1e-12f)).normalized;var basis=Quaternion.LookRotation(Vector3.Cross(Vector3.right,longitudinal),longitudinal);
            var oriented=g.Select(p=>Quaternion.Inverse(basis)*p).ToArray();var lo=Bounds(oriented,false);var hi=Bounds(oriented,true);var frame=d.Frame*basis;
            Vector3 center=j.Position+frame*((lo+hi)*.5f);string label=n.Name+"_contact_"+k;int bone=Aux(label,center,n.Bone,bones[n.Bone].TransformLevel+1);bones[bone].PostPhysics=true;
            var panel=new Body{Name=label,NameEn=label,AssociatedBoneIndex=bone,Position=center,Rotation=Euler(frame),Shape=Body.RigidBodyShape.RigidShapeBox,
                Dimemsions=Vector3.Max((hi-lo)*.5f,Vector3.one/160)+Vector3.one*(length*.02f),Mass=mass*.2f/groups.Count,Type=Body.RigidBodyType.RigidTypePhysics,
                TranslateDamp=b.TranslateDamp,RotateDamp=b.RotateDamp};
            joints.Add(new MMDJoint{Name=label+"_weld",NameEn=label+"_weld",AssociatedRigidBodyIndex=new[]{n.Body,bodies.Count},Position=center,Rotation=panel.Rotation});d.Contacts.Add(bodies.Count);bodies.Add(panel);
        }
        SetDecorationSpring(d);
    }
}
