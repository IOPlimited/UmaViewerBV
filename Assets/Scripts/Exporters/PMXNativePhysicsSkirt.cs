using System;
using System.Collections.Generic;
using System.Linq;
using LibMMD.Model;
using Newtonsoft.Json.Linq;
using UnityEngine;

internal sealed partial class PMXNativePhysicsRecipe
{
    sealed class Support
    {
        internal Node Node;
        internal int Basis,YawHalf,TangentBasis,TangentHalf,Carrier,Cap,TangentCap,Output;
        internal int[] Radial,Tangent;
        internal JObject Profile;
    }
    void ConfigureSkirts()
    {
        var roots=nodes.Where(n=>n.IsSkirt&&n.Slot==0&&bodies[n.Body].Type!=0).ToArray();if(roots.Length==0)return;
        int hip=Need("Hip"),torso=Need("Waist");int[] thighs={Need("Thigh_L"),Need("Thigh_R")};int order=Order;
        int[] targets=thighs.Select(t=>Grant("radial_thigh_"+bones[t].Name,bones[t].Position,hip,t,.5f,order)).ToArray();
        order++;var supports=new List<Support>();
        foreach(var n in roots){var j=joints[n.Joint];Vector3 pivot=j.Position,axis=Rotation(j.Rotation)*Vector3.right;axis.y=0;axis.Normalize();
            float yaw=Mathf.Atan2(-axis.z,axis.x);var profile=((JArray)recipe["skirtPanels"]).OfType<JObject>().OrderBy(p=>Mathf.Abs(Mathf.DeltaAngle(yaw*Mathf.Rad2Deg,(float)p["yawRadians"]*Mathf.Rad2Deg))).First();
            var s=new Support {Node=n,Profile=profile};s.YawHalf=Hinge("radial_basis_"+n.Name,pivot,pivot+Vector3.forward,hip,hip,pivot+Rotation(new Vector3(0,yaw*.5f,0))*Vector3.forward,1,yaw*.5f,yaw*.5f,order);
            s.Basis=Grant("radial_frame_"+n.Name,pivot,hip,s.YawHalf,2,order+3);
            s.TangentHalf=Hinge("tangent_basis_"+n.Name,pivot,pivot+Vector3.forward*2,s.Basis,s.Basis,pivot+Rotation(new Vector3(0,-Mathf.PI/4,0))*Vector3.forward*2,1,-Mathf.PI/4,-Mathf.PI/4,order+4);
            s.TangentBasis=Grant("tangent_frame_"+n.Name,pivot,s.Basis,s.TangentHalf,2,order+7);
            s.Radial=new int[2];s.Tangent=new int[2];float length=(P(V(n.Data["end"]))-pivot).magnitude;
            for(int side=0;side<2;side++){
                Vector3 offset=pivot-bones[thighs[side]].Position;float distance=offset.magnitude,projection=-offset.y;
                float lever=-projection+Mathf.Sqrt(projection*projection+2*distance*length+length*length);Vector3 end=pivot+Vector3.down*lever;
                s.Radial[side]=Hinge("radial_motion_"+bones[thighs[side]].Name+n.Name,pivot,end,s.Basis,targets[side],end,0,0,75*Mathf.Deg2Rad,order+4);
                s.Tangent[side]=Hinge("radial_tangent_"+bones[thighs[side]].Name+n.Name,pivot,end,s.TangentBasis,targets[side],end,0,-75*Mathf.Deg2Rad,75*Mathf.Deg2Rad,order+8);
            }
            // The frozen projected recipe replaces the preliminary radial sum.
            // Build its final max-of-two inputs directly, retaining the same IK
            // pivots, grants and cancellation used by the reference generator.
            int o=Order;int[] scaled=Enumerable.Range(0,2).Select(k=>Grant($"static_leg_scaled_{k}_"+n.Name,pivot,s.Basis,s.Radial[k],(float)profile["radialLegGains"][k]*.5f,o)).ToArray();
            Vector3 tip=pivot+Vector3.down*2;
            int difference=Hinge("static_leg_maxdiff_"+n.Name,pivot,tip,scaled[0],scaled[1],tip,0,0,150*Mathf.Deg2Rad,o+1);
            int raw=Grant("static_leg_maxleft_"+n.Name,pivot,s.Basis,scaled[0],1,o+4);
            raw=Grant("static_leg_maxsum_"+n.Name,pivot,raw,difference,1,o+5);
            s.Cap=Hinge("static_radial_halfcap_"+n.Name,pivot,tip,s.Basis,raw,tip,0,0,52.5f*Mathf.Deg2Rad,o+6);
            Vector3 outputPivot=pivot+V(profile["outputPivotOffsetLocalMm"])/80;
            int output=Grant("static_radial_double_"+n.Name,outputPivot,s.Basis,s.Cap,2,o+10);s.Output=output;
            int tangent=s.TangentBasis;
            for(int k=0;k<2;k++)tangent=Grant($"static_tangent_sum_{k}_"+n.Name,pivot,tangent,s.Tangent[k],(float)profile["tangentLegGains"][k],o+k);
            s.TangentCap=Hinge("static_tangent_cap_"+n.Name,pivot,tip,s.TangentBasis,tangent,tip,0,-45*Mathf.Deg2Rad,45*Mathf.Deg2Rad,o+2);
            output=Grant("static_tangent_frame_"+n.Name,outputPivot,output,s.TangentHalf,2,o+11);
            output=Grant("static_tangent_output_"+n.Name,outputPivot,output,s.TangentCap,1,o+12);
            output=Grant("static_tangent_unframe_"+n.Name,outputPivot,output,s.TangentHalf,-2,o+13);
            s.Carrier=Grant("radial_unframe_"+n.Name,pivot,output,s.YawHalf,-2,o+18);
            bodies[j.AssociatedRigidBodyIndex[0]].AssociatedBoneIndex=s.Carrier;
            int parent=s.Basis,first=-1;o=Order;
            float radialMm=(float)profile["radialMm"],tangentMm=(float)profile["tangentMm"];
            var heights=new[]{1f,0,-1f,0};var ratios=new[]{2f,-2,-2,2};
            for(int k=0;k<4;k++){parent=Grant($"panel_clearance_radial_{k}_"+n.Name,pivot+Vector3.up*(heights[k]*radialMm/160),parent,s.Cap,ratios[k],o+k);if(first<0)first=parent;}
            var depths=new[]{-1f,0,-1f,0};var tr=new[]{1f,-1,-1,1};
            for(int k=0;k<4;k++)parent=Grant($"panel_clearance_tangent_{k}_"+n.Name,pivot+Vector3.forward*(depths[k]*tangentMm/160),parent,s.TangentCap,tr[k],o+4+k);
            bones[s.Output].ParentIndex=parent;
            int transfer=TorsoTransfer(pivot,hip,torso,n.Name,(float)profile["torsoFraction"]);
            int basis=Grant("torso_seam_basis_"+n.Name,pivot,transfer,s.YawHalf,2,Order);
            bones[first].ParentIndex=basis;
            var original=bones[n.Bone];var clone=Clone(original);clone.Name=clone.NameEn="cloth_residual_"+n.Name;
            clone.ParentIndex=s.Carrier;clone.Visible=clone.Controllable=clone.PostPhysics=false;clone.ChildBoneVal=new Bone.ChildBone();clone.TransformLevel=Order;
            int driver=bones.Count;bones.Add(clone);skirtDrivers[n.Bone]=driver;bodies[n.Body].AssociatedBoneIndex=driver;
            original.ParentIndex=s.Carrier;original.AppendRotate=true;original.AppendTranslate=false;original.PostPhysics=true;
            original.AppendBoneVal=new Bone.AppendBone{Index=driver,Ratio=.25f};original.TransformLevel=clone.TransformLevel+1;
            supports.Add(s);
        }
        Reorder(new HashSet<int>(Enumerable.Range(originalBoneCount,bones.Count-originalBoneCount)));
        ApplySkirtChains();
    }
    List<int> Ancestors(int i){var a=new List<int>();while(i>=0){if(a.Contains(i))throw new InvalidOperationException("Cyclic source skeleton");a.Add(i);i=bones[i].ParentIndex;}return a;}
    int TorsoTransfer(Vector3 pivot,int hip,int torso,string label,float ratio)
    {
        var hp=Ancestors(hip);var tp=Ancestors(torso);int common=hp.First(tp.Contains);
        int[] hc=hp.TakeWhile(i=>i!=common).Reverse().ToArray(),tc=tp.TakeWhile(i=>i!=common).Reverse().ToArray();
        int parent=hip,order=Order;string suffix=label+"_"+ratio.ToString("G9",System.Globalization.CultureInfo.InvariantCulture);
        foreach(int source in hc.Reverse())parent=Grant($"torso_blend_relative_{source}_{suffix}",pivot,parent,source,-1,order++);
        foreach(var branch in new[]{(name:"hip",chain:hc,scale:-ratio),(name:"torso",chain:tc,scale:ratio)}){
            foreach(int source in branch.chain){var pos=pivot+branch.scale*(bones[source].Position-pivot);
                parent=Grant($"torso_blend_move_{branch.name}_{source}_{suffix}",pos,parent,source,branch.scale,order++,true);
                parent=Grant($"torso_blend_rotate_{branch.name}_{source}_{suffix}",pos,parent,source,1,order++);}
            foreach(int source in branch.chain.Reverse())parent=Grant($"torso_blend_cancel_{branch.name}_{source}_{suffix}",pivot,parent,source,-1,order++);}
        foreach(int source in hc)parent=Grant($"torso_blend_orient_{source}_{suffix}",pivot,parent,source,1,order++);return parent;
    }
    void ApplySkirtChains()
    {
        var selected=new HashSet<int>(nodes.Where(n=>n.IsSkirt&&n.Slot==0).Select(n=>n.Bone));
        bool more;do{more=false;for(int i=0;i<sourceBones.Length;i++)if(selected.Contains(sourceBones[i].ParentIndex))more|=selected.Add(i);}while(more);
        int depth(int i){int d=0;while(selected.Contains(sourceBones[i].ParentIndex)){d++;i=sourceBones[i].ParentIndex;}return d;}
        var map=new Dictionary<int,int>(skirtDrivers);int order=Order;
        foreach(int original in selected.OrderBy(i=>i))if(!map.ContainsKey(original)){
            var b=Clone(sourceBones[original]);b.Name=b.NameEn="long_cloth_physical_"+b.NameEn;b.Visible=b.Controllable=b.PostPhysics=false;b.ChildBoneVal=new Bone.ChildBone();map[original]=bones.Count;bones.Add(b);}
        foreach(var pair in map){var b=bones[pair.Value];if(map.TryGetValue(b.ParentIndex,out int p))b.ParentIndex=p;
            if((b.AppendRotate||b.AppendTranslate)&&map.TryGetValue(b.AppendBoneVal.Index,out p))b.AppendBoneVal.Index=p;b.TransformLevel=order+depth(pair.Key);}
        foreach(var b in bodies)if(map.TryGetValue(b.AssociatedBoneIndex,out int p))b.AssociatedBoneIndex=p;
        var physical=map.Values.ToHashSet();for(int i=0;i<bones.Count;i++)if(!selected.Contains(i)&&!physical.Contains(i)&&map.TryGetValue(bones[i].ParentIndex,out int p))bones[i].ParentIndex=p;
        int displayOrder=Order;
        foreach(int original in selected){var b=bones[original];b.PostPhysics=true;b.TransformLevel=displayOrder+depth(original);
            var n=nodes.FirstOrDefault(v=>v.IsSkirt&&v.Bone==original);if(n==null)continue;
            b.AppendRotate=true;b.AppendTranslate=false;b.AppendBoneVal=new Bone.AppendBone{Index=map[original],Ratio=.25f};
            var j=joints[n.Joint];j.RotationLowLimit=Vector3.Max(j.RotationLowLimit,Vector3.one*(-30*Mathf.Deg2Rad));j.RotationHiLimit=Vector3.Min(j.RotationHiLimit,Vector3.one*(30*Mathf.Deg2Rad));}
    }
}
