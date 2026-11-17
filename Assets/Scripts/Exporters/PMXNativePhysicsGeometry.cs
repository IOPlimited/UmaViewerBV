using System;
using System.Collections.Generic;
using System.Linq;
using LibMMD.Model;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Body=LibMMD.Model.MMDRigidBody;

internal sealed partial class PMXNativePhysicsRecipe
{
    static Vector3 AlignY(Vector3 direction)
    {
        var d=direction.normalized;return new Vector3(Mathf.Atan2(d.z,d.y),0,-Mathf.Asin(Mathf.Clamp(d.x,-1,1)));
    }
    Body Primitive(string name,int bone,Vector3 a,Vector3 z,float radius)
    {
        float length=(z-a).magnitude;return new Body {Name=name,NameEn=name,AssociatedBoneIndex=bone,
            Position=(a+z)*.5f,Rotation=length>.01f?AlignY(z-a):Vector3.zero,Dimemsions=new Vector3(radius,Mathf.Max(.01f,length-2*radius),0),
            Shape=length>2*radius+.01f?Body.RigidBodyShape.RigidShapeCapsule:Body.RigidBodyShape.RigidShapeSphere,
            CollisionGroup=15,CollisionMask=0x7fff};
    }
    void BuildAnatomy()
    {
        var excluded=Descendants(nodes.Select(n=>n.Bone),originalBoneCount);
        var prototypes=new Dictionary<string,Body>();
        void Add(string part,string start,string end,float radius,float min,float max,Vector3? fixedA=null,Vector3? fixedZ=null)
        {
            int bi=Need(start);Vector3 a=fixedA??bones[bi].Position,z=fixedZ??bones[Need(end)].Position,delta=z-a;float length=delta.magnitude;
            if(length>.05f){var sample=new List<float>();for(int i=0;i<points.Length;i++){
                float t=Vector3.Dot(points[i]-a,delta)/delta.sqrMagnitude;
                if(t>.15f&&t<.85f&&weights[i].Where(w=>w.bone==bi).Sum(w=>(double)w.weight)>.6&&weights[i].Where(w=>excluded.Contains(w.bone)).Sum(w=>(double)w.weight)<.05)
                    sample.Add((points[i]-a-t*delta).magnitude);}
                if(sample.Count>0)radius=Quantile(sample,.85f);}
            var b=Primitive("Body_"+part,bi,a,z,Mathf.Clamp(radius,min*12.5f,max*12.5f));
            if(b.Shape==Body.RigidBodyShape.RigidShapeSphere)b.Dimemsions=new Vector3(b.Dimemsions.x,0,0);
            prototypes[part]=b;
        }
        int head=Need("Head"),neck=Need("Neck");float scale=Mathf.Clamp((bones[head].Position-bones[neck].Position).magnitude/(.055f*12.5f),.7f,1.3f);
        Vector3 center=bones[head].Position+new Vector3(0,.10f*12.5f*scale,.015f*12.5f*scale);
        var face=new HashSet<int>();int cursor=0;
        foreach(var part in model.Parts){if(part.Material.Name.ToLowerInvariant().Contains("face"))for(int i=cursor;i<cursor+part.TriangleIndexNum;i++)face.Add(model.TriangleIndexes[i]);cursor+=part.TriangleIndexNum;}
        float headRadius=.13f*12.5f*scale;
        if(face.Count>0){float width=Quantile(face.Select(i=>points[i].x),.97f)-Quantile(face.Select(i=>points[i].x),.03f);
            float height=Quantile(face.Select(i=>points[i].y),.97f)-Quantile(face.Select(i=>points[i].y),.03f);headRadius=Mathf.Max(width*.55f,height*.5f,.12f*12.5f*scale);}
        Add("head","Head","Head",headRadius,.10f,.16f,center,center);
        Add("neck","Neck","Head",.045f*12.5f,.03f,.055f);
        Add("torso","Waist","Neck",.12f*12.5f,.095f,.15f);
        var pelvis=bones[Need("Hip")].Position+new Vector3(0,.035f*12.5f,0);
        Add("pelvis","Hip","Hip",.115f*12.5f,.09f,.14f,pelvis,pelvis);
        foreach(string side in new[]{"L","R"}){
            string label=side=="L"?"left_":"right_";
            foreach(var s in new[]{("upper_arm","Arm","Elbow",.20f,.025f,.055f),("forearm","Elbow","Wrist",.17f,.02f,.045f),
                ("thigh","Thigh","Knee",.20f,.045f,.09f),("shin","Knee","Ankle",.14f,.03f,.065f)}){
                float length=(bones[Need(s.Item2+"_"+side)].Position-bones[Need(s.Item3+"_"+side)].Position).magnitude;
                Add(label+s.Item1,s.Item2+"_"+side,s.Item3+"_"+side,length*s.Item4,s.Item5,s.Item6);}
            string finger=Find("Middle_02_"+side)>=0?"Middle_02_"+side:Find("Middle2_"+side)>=0?"Middle2_"+side:"Wrist_"+side;
            // The native naming table also covers the Japanese finger alias.
            int mid=Find(side=="L"?"左中指２":"右中指２");if(mid>=0)finger=bones[mid].NameEn;
            Add(label+"hand","Wrist_"+side,finger,.035f*12.5f,.025f,.05f);
            int ankle=Need("Ankle_"+side),toe=Find("Toe_"+side);if(toe<0)toe=Need("Toes_"+side);
            Vector3 a=bones[ankle].Position,z=bones[toe].Position,d=z-a;d.y=0;d.Normalize();Vector3 c=(a+z)*.5f;c.y=Mathf.Max(.035f*12.5f,z.y);
            Add(label+"foot","Ankle_"+side,"Ankle_"+side,.04f*12.5f,.03f,.055f,c-d*.085f*12.5f,c+d*.085f*12.5f);
        }
        var attach=new Dictionary<string,string>{{"Head","head"},{"Chest","torso"},{"Spine","pelvis"},{"Waist","pelvis"},{"Hip","pelvis"}};
        foreach(string side in new[]{"L","R"})foreach(var pair in new[]{("Arm","upper_arm"),("Elbow","forearm"),("Wrist","hand"),("Thigh","thigh"),("Knee","shin"),("Ankle","foot")})
            attach[pair.Item1+"_"+side]=(side=="L"?"left_":"right_")+pair.Item2;
        var chosen=new Dictionary<string,(float score,JObject row)>();
        foreach(var c in ((JArray)(metadata["sourceInventory"]??metadata["colliders"])).OfType<JObject>()){
            string name=((string)c["name"]).ToLowerInvariant();string attachment=(string)c["attachment"];
            if(!attach.TryGetValue(attachment,out string part)|| (bool)c["inner"]||(int)c["bone"]<0||c["enabled"]!=null&&!(bool)c["enabled"]||
                new[]{"flip","block","wall","gurd","guard","dummy","_tail","ribbon","_tie","_acc","collar","jacket"}.Any(name.Contains))continue;
            bool capsule=(string)c["type"]=="Capsule";if(!capsule&&(string)c["type"]!="Sphere")continue;
            var target=prototypes[part];var a=P(V(c["start"]));var z=P(V(c["end"]));var location=capsule?(a+z)*.5f:a;
            float r=(float)c["radius"]*12.5f,offset=(location-target.Position).magnitude,ratio=r/target.Dimemsions.x;
            float extent=r+(capsule?(z-a).magnitude*.5f:0),targetExtent=target.Dimemsions.x+(target.Shape==Body.RigidBodyShape.RigidShapeCapsule?target.Dimemsions.y*.5f:0);
            if(part=="head"&&Mathf.Abs(location.x-target.Position.x)>.03f*12.5f||ratio<.55f||ratio>1.45f||offset>Mathf.Max(.06f*12.5f,target.Dimemsions.x*.9f)||extent>targetExtent*1.6f||extent<targetExtent*.70f)continue;
            float score=offset/target.Dimemsions.x+Mathf.Abs(Mathf.Log(ratio))+.2f*Mathf.Abs(extent/targetExtent-1)-(name.Contains("round")?2:0);
            if(!chosen.ContainsKey(part)||score<chosen[part].score)chosen[part]=(score,c);
        }
        foreach(var pair in prototypes){Body b=pair.Value;if(chosen.TryGetValue(pair.Key,out var candidate)){
            var c=candidate.row;Vector3 a=P(V(c["start"])),z=P(V(c["end"]));bool capsule=(string)c["type"]=="Capsule"&&(z-a).magnitude>1e-5f;
            b.Name=b.NameEn+"_native";b.NameEn=b.Name;b.AssociatedBoneIndex=(int)c["bone"];b.Position=capsule?(a+z)*.5f:a;
            b.Rotation=capsule?AlignY(z-a):Vector3.zero;b.Shape=capsule?Body.RigidBodyShape.RigidShapeCapsule:Body.RigidBodyShape.RigidShapeSphere;
            b.Dimemsions=new Vector3((float)c["radius"]*12.5f,capsule?(z-a).magnitude:0,0);}
            anatomy.Add(b);}
    }
    void ConfigureSurfaces()
    {
        // Reserve group 15 while preserving unaffected source contacts. Hair and
        // skirt contacts are rebuilt against the selected anatomical foundation.
        var rebuilt=nodes.Where(n=>(n.IsSkirt||n.IsHair)&&bodies[n.Body].Type!=0).Select(n=>n.Body).ToHashSet();
        if(bodies.Any(b=>b.CollisionGroup==15)){
            int released=Enumerable.Range(1,14).First(g=>bodies.Select((b,i)=>(b,i)).Any(p=>p.b.Type!=0&&p.b.CollisionGroup==g)&&
                bodies.Select((b,i)=>(b,i)).Where(p=>p.b.CollisionGroup==g&&p.b.Type!=0).All(p=>rebuilt.Contains(p.i)));
            for(int i=0;i<bodies.Count;i++){var b=bodies[i];if(rebuilt.Contains(i)){b.CollisionGroup=0;b.CollisionMask=0;continue;}
                bool contact=(b.CollisionMask&(1<<15))!=0;if(b.CollisionGroup==15)b.CollisionGroup=released;
                b.CollisionMask=(ushort)((b.CollisionMask&~((1<<15)|(1<<released)))|(contact?1<<released:0));}}
        foreach(var b in bodies)b.CollisionMask&=0x7fff;
        foreach(var n in nodes.Where(n=>n.IsSkirt||n.IsHair)){
            var b=bodies[n.Body];if(b.Type==0)continue;var j=joints[n.Joint];Vector3 start=j.Position,end=b.Position;float length=(end-start).magnitude;
            if(length<1e-6f)throw new InvalidOperationException("Zero native physics segment "+n.Name);
            float radius=Mathf.Min(b.Dimemsions.x,length*.20f),trim=0;Vector3 direction=(end-start)/length;
            var contacts=n.IsHair?anatomy:anatomy.Where(p=>p.Name.Contains("pelvis")||p.Name.Contains("thigh")||p.Name.Contains("shin")).ToList();
            for(int i=0;i<=300;i++){float distance=Mathf.Lerp(radius,Mathf.Max(radius,length-radius),i/300f);if(contacts.Any(p=>Gap(p,start+distance*direction,radius)<.025f))trim=Mathf.Min(length-2*radius,distance+.025f-radius);}
            b.Shape=Body.RigidBodyShape.RigidShapeCapsule;b.Position=start+direction*(length+trim)*.5f;b.Dimemsions=new Vector3(radius,Mathf.Max(.001f,length-trim-2*radius),0);
            b.Type=Body.RigidBodyType.RigidTypePhysics;b.Mass=(n.IsHair?.07f:.15f)*Mathf.Pow(.8f,n.Slot);b.TranslateDamp=.95f;b.RotateDamp=n.IsHair?.98f:.95f;b.Restitution=b.Friction=0;b.CollisionMask=1<<15;
            float moment=b.Mass*((length-trim)*(length-trim)/12+radius*radius/4+(length+trim)*(length+trim)/4);
            j.SpringRotate=new Vector3(moment,0,moment)*Mathf.Pow(4*Mathf.PI,2);
            if(n.IsSkirt){j.RotationLowLimit=new Vector3(-75,0,-75)*Mathf.Deg2Rad;j.RotationHiLimit=-j.RotationLowLimit;}
            else{var lo=j.RotationLowLimit;var hi=j.RotationHiLimit;for(int k=0;k<3;k++)if(lo[k]>hi[k]){lo[k]=-55*Mathf.Deg2Rad;hi[k]=55*Mathf.Deg2Rad;}lo.y=hi.y=0;j.RotationLowLimit=lo;j.RotationHiLimit=hi;}
        }
        bodies.AddRange(anatomy);
        var simulated=nodes.Select(n=>n.Bone).ToHashSet();
        foreach(var n in nodes.Where(n=>n.IsHair||n.IsSkirt)){
            var driver=bodies[n.Body];if(driver.Type==0)continue;var joint=joints[n.Joint];var original=Copy(driver);
            var attached=new HashSet<int>{n.Bone};bool change;do{change=false;for(int i=0;i<originalBoneCount;i++)if(!simulated.Contains(i)&&attached.Contains(bones[i].ParentIndex))change|=attached.Add(i);}while(change);
            var selected=weights.Where(w=>w.Value.Where(v=>attached.Contains(v.bone)).Sum(v=>v.weight)>=.25f).Select(w=>w.Key).ToArray();if(selected.Length<5)continue;
            float length=(P(V(n.Data["end"]))-joint.Position).magnitude;driver.Position=(joint.Position+P(V(n.Data["end"])))*.5f;
            driver.Dimemsions=new Vector3(length*.25f,Mathf.Max(.001f,length*.5f),0);driver.CollisionMask=0;
            var frame=Rotation(driver.Rotation);var local=selected.Select(i=>Quaternion.Inverse(frame)*(points[i]-joint.Position)).ToArray();
            Vector3 lo=Vector3.zero,hi=Vector3.zero;for(int k=0;k<3;k++){int axis=k;lo[k]=Quantile(local.Select(p=>p[axis]),.025f);hi[k]=Quantile(local.Select(p=>p[axis]),.975f);}lo.y=Mathf.Max(0,lo.y);
            float r=Mathf.Min(.5f,Mathf.Max(.20f,(hi.x-lo.x)/4));var supplements=new List<Body>();
            for(int side=0;side<2;side++){float horizontal=Mathf.Lerp(lo.x,hi.x,side==0?.15f:.85f);var subset=local.Where(p=>Mathf.Abs(p.x-horizontal)<=(hi.x-lo.x)*.35f).ToArray();if(subset.Length<3)continue;
                var b=Copy(driver);b.Name=b.NameEn=$"surface_{n.Bone}_{side}";b.Shape=Body.RigidBodyShape.RigidShapeCapsule;
                b.Position=joint.Position+frame*new Vector3(horizontal,(lo.y+hi.y)*.5f,Quantile(subset.Select(p=>p.z),.5f));
                var reference=anatomy.First(p=>p.Name.Contains("pelvis"));var head=anatomy.First(p=>p.Name.Contains("head"));if(n.IsHair&&b.Position.y>=head.Position.y-head.Dimemsions.x)reference=head;
                Vector3 outward=b.Position-reference.Position;if(reference!=head)outward.y=0;var axis=frame*Vector3.up;outward-=axis*Vector3.Dot(outward,axis);outward=outward.sqrMagnitude<1e-16f?frame*Vector3.forward:outward.normalized;
                b.Position+=outward*r*.8f;b.Dimemsions=new Vector3(r,Mathf.Max(.001f,hi.y-lo.y-2*r),0);b.CollisionMask=1<<15;
                if(!TrimSurface(b,anatomy))continue;
                b.AssociatedBoneIndex=Aux(b.Name,b.Position,driver.AssociatedBoneIndex,bones[driver.AssociatedBoneIndex].TransformLevel+1);
                joints.Add(new MMDJoint {Name="weld_"+b.Name,NameEn="weld_"+b.Name,AssociatedRigidBodyIndex=new[]{n.Body,bodies.Count},Position=(driver.Position+b.Position)*.5f,Rotation=driver.Rotation});
                bodies.Add(b);supplements.Add(b);
            }
            if(supplements.Count>0){driver.Mass=original.Mass*.5f;foreach(var b in supplements)b.Mass=(original.Mass-driver.Mass)/supplements.Count;AssemblySpring(n,2);}
            else{original.CollisionMask=0;bodies[n.Body]=original;}
        }
        foreach(var n in nodes.Where(n=>n.Name.StartsWith("Sp_He_Hair0",StringComparison.Ordinal)))if(bodies[n.Body].Type!=0)AssemblySpring(n,6);
    }
    static bool TrimSurface(Body body,List<Body> contacts,float clearance=.05f)
    {
        Vector3 axis=Rotation(body.Rotation)*Vector3.up,start=body.Position-axis*body.Dimemsions.y*.5f,end=body.Position+axis*body.Dimemsions.y*.5f;
        int blocked=-1;for(int i=0;i<=500;i++){var point=Vector3.Lerp(start,end,i/500f);if(contacts.Any(p=>Gap(p,point,body.Dimemsions.x)<clearance))blocked=i;}
        if(blocked==500)return false;var first=Vector3.Lerp(start,end,(blocked+1)/500f);body.Position=(first+end)*.5f;body.Dimemsions=new Vector3(body.Dimemsions.x,Mathf.Max(.001f,(end-first).magnitude),0);return true;
    }
}
