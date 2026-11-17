using System;
using System.Collections.Generic;
using System.Linq;
using LibMMD.Model;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Body=LibMMD.Model.MMDRigidBody;

internal sealed partial class PMXNativePhysicsRecipe
{
    static double EarFrequency(Node n)
    {
        double rho=1-n.Native("DragForce")/2000.0,q=1/(1+n.Native("StiffnessForce")/(20000.0*n.Native("InitBoneDistance")));
        double b=q*(1+rho),discriminant=b*b-4*q*rho;
        if(discriminant<0){double real=b*.5,imaginary=Math.Sqrt(-discriminant)*.5;
            double log=Math.Log(Math.Sqrt(real*real+imaginary*imaginary)),angle=Math.Atan2(imaginary,real);return 60*Math.Sqrt(log*log+angle*angle);}
        double first=(b+Math.Sqrt(discriminant))*.5,second=(b-Math.Sqrt(discriminant))*.5;
        double Pole(double value){double log=Math.Log(Math.Abs(value)),angle=value<0?Math.PI:0;return 60*Math.Sqrt(log*log+angle*angle);}
        return Math.Max(Pole(first),Pole(second));
    }
    void ConfigureEars()
    {
        var ears=nodes.Where(n=>n.IsEar).ToArray();if(ears.Length==0)return;
        var earBodies=ears.Select(n=>n.Body).ToHashSet();var occupied=bodies.Select((b,i)=>(b,i)).Where(p=>!earBodies.Contains(p.i)).Select(p=>p.b.CollisionGroup).ToHashSet();
        int group=new[]{4}.Concat(Enumerable.Range(0,16)).First(g=>!occupied.Contains(g));var config=recipe["ear"];
        foreach(var b in bodies)if(b.AssociatedBoneIndex>=0&&(bones[b.AssociatedBoneIndex].NameEn.StartsWith("Sp_He_Hair",StringComparison.Ordinal)||bones[b.AssociatedBoneIndex].NameEn.StartsWith("Hair",StringComparison.Ordinal)))b.CollisionMask&=(ushort)~(1<<group);
        foreach(var n in ears){var b=bodies[n.Body];var j=joints[n.Joint];float length=n.Native("InitBoneDistance");
            b.Mass=(float)config["massAt50mm"]*Mathf.Pow(length/.05f,3)*Mathf.Pow((float)config["distalMassRatio"],n.Slot);
            b.TranslateDamp=(float)config["linearDamping"];b.RotateDamp=(float)config["angularDamping"];b.CollisionGroup=group;b.CollisionMask=0;
            b.Type=Body.RigidBodyType.RigidTypePhysicsStrict;b.Shape=Body.RigidBodyShape.RigidShapeSphere;b.Position=j.Position;b.Dimemsions=new Vector3(length*12.5f,0,0);
            double frequency=EarFrequency(n);float k=(float)(.4*b.Mass*b.Dimemsions.x*b.Dimemsions.x*frequency*frequency);
            j.RotationLowLimit=new Vector3(1,0,1);j.RotationHiLimit=new Vector3(-1,0,-1);j.SpringRotate=new Vector3(k,0,k);
        }
        foreach(string side in new[]{"L","R"})for(int segment=1;segment<=2;segment++){
            int target=Find($"Ear_0{segment+1}_{side}"),driver=Find($"Sp_He_Ear0_{side}_0{segment}");if(target<0||driver<0)continue;
            bones[target].AppendRotate=true;bones[target].AppendBoneVal=new Bone.AppendBone{Index=driver,Ratio=1};bones[target].PostPhysics=true;bones[target].TransformLevel=segment;
        }
    }
    void ConfigureFringe()
    {
        var groups=nodes.Where(n=>n.Name.StartsWith("Sp_He_Hair0",StringComparison.Ordinal)&&bodies[n.Body].Type!=0).GroupBy(n=>n.Root).ToArray();
        var removedBodies=new HashSet<int>();var removedJoints=new HashSet<int>();
        foreach(var group in groups){var chain=group.OrderBy(n=>n.Slot).ToArray();var primary=chain[0];var driver=bodies[primary.Body];var joint=joints[primary.Joint];
            var members=chain.Select(n=>n.Body).ToHashSet();var surfaces=new List<int>();
            foreach(var weld in joints)if(members.Contains(weld.AssociatedRigidBodyIndex[0])&&weld.Name.StartsWith("weld_surface_",StringComparison.Ordinal)){
                surfaces.Add(weld.AssociatedRigidBodyIndex[1]);weld.AssociatedRigidBodyIndex[0]=primary.Body;}
            foreach(var n in chain.Skip(1)){driver.Mass+=bodies[n.Body].Mass;removedBodies.Add(n.Body);removedJoints.Add(n.Joint);}
            foreach(int index in Descendants(new[]{primary.Bone}).Where(i=>i!=primary.Bone)){
                int depth=0,parent=index;while(parent!=primary.Bone){depth++;parent=bones[parent].ParentIndex;}
                bones[index].PostPhysics=true;bones[index].TransformLevel=bones[primary.Bone].TransformLevel+depth;}
            joint.RotationLowLimit=new Vector3(-5,0,-5)*Mathf.Deg2Rad;joint.RotationHiLimit=-joint.RotationLowLimit;
            Vector3 moment=Moments(driver,joint);foreach(int i in surfaces)moment+=Moments(bodies[i],joint);
            Vector3 stiffness=moment*Mathf.Pow(4*Mathf.PI,2);joint.SpringRotate=new Vector3(stiffness.x,0,stiffness.z);
        }
        RemovePhysics(removedBodies,removedJoints);
    }
    void RemovePhysics(HashSet<int> removedBodies,HashSet<int> removedJoints)
    {
        if(removedBodies.Count==0&&removedJoints.Count==0)return;
        var bm=Enumerable.Range(0,bodies.Count).Where(i=>!removedBodies.Contains(i)).Select((old,index)=>(old,index)).ToDictionary(p=>p.old,p=>p.index);
        var jm=Enumerable.Range(0,joints.Count).Where(i=>!removedJoints.Contains(i)).Select((old,index)=>(old,index)).ToDictionary(p=>p.old,p=>p.index);
        var keptBodies=bodies.Where((b,i)=>bm.ContainsKey(i)).ToArray();var keptJoints=joints.Where((j,i)=>jm.ContainsKey(i)).ToArray();
        bodies.Clear();bodies.AddRange(keptBodies);joints.Clear();joints.AddRange(keptJoints);
        foreach(var j in joints){j.AssociatedRigidBodyIndex[0]=bm[j.AssociatedRigidBodyIndex[0]];j.AssociatedRigidBodyIndex[1]=bm[j.AssociatedRigidBodyIndex[1]];}
        nodes.RemoveAll(n=>removedBodies.Contains(n.Body));foreach(var n in nodes){n.Body=bm[n.Body];n.Joint=jm[n.Joint];}
    }
    void ConfigureTail()
    {
        float twist=10*Mathf.Deg2Rad,swing=2*Mathf.Atan(Mathf.Sqrt(Mathf.Tan(twist*.5f)));
        foreach(var n in nodes.Where(n=>n.IsTail)){
            var b=bodies[n.Body];var j=joints[n.Joint];var frozen=((JArray)recipe["tailCoefficients"]).OfType<JObject>().FirstOrDefault(p=>(string)p["source"]==n.Name);
            // These are the retained tail coefficients, not another idle fit.
            if(frozen!=null){b.Mass=(float)frozen["mass"];j.SpringRotate=V(frozen["springRotate"]);}
            b.TranslateDamp=b.RotateDamp=.95f;Vector3 lo=j.RotationLowLimit,hi=j.RotationHiLimit;lo.y=hi.y=0;
            foreach(int k in new[]{0,2}){if(lo[k]>hi[k]){lo[k]=-swing;hi[k]=swing;}else{lo[k]=Mathf.Max(lo[k],-swing);hi[k]=Mathf.Min(hi[k],swing);}}
            j.RotationLowLimit=lo;j.RotationHiLimit=hi;
        }
    }
}
