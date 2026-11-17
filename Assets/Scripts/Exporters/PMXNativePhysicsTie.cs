using System;
using System.Collections.Generic;
using System.Linq;
using LibMMD.Model;
using UnityEngine;
using Body=LibMMD.Model.MMDRigidBody;

internal sealed partial class PMXNativePhysicsRecipe
{
    int PointFollower(Vector3 pivot,Dictionary<int,float> influence,string label)
    {
        var paths=influence.Keys.ToDictionary(i=>i,Ancestors);int common=paths.First().Value.First(i=>paths.Values.All(p=>p.Contains(i)));
        int parent=common,order=Order;
        foreach(var pair in influence){var chain=paths[pair.Key].TakeWhile(i=>i!=common).Reverse().ToArray();
            foreach(int source in chain){Vector3 position=pivot+pair.Value*(bones[source].Position-pivot);
                parent=Grant($"tie_surface_move_{label}_{pair.Key}_{source}",position,parent,source,pair.Value,order++,true);
                parent=Grant($"tie_surface_turn_{label}_{pair.Key}_{source}",position,parent,source,1,order++);}
            foreach(int source in chain.Reverse())parent=Grant($"tie_surface_cancel_{label}_{pair.Key}_{source}",pivot,parent,source,-1,order++);}
        int dominant=influence.OrderByDescending(p=>p.Value).First().Key;
        foreach(int source in paths[dominant].TakeWhile(i=>i!=common).Reverse())parent=Grant($"tie_surface_orient_{label}_{source}",pivot,parent,source,1,order++);
        return Aux("tie_surface_follow_"+label,pivot,parent,order);
    }
    void BuildTieChest(List<Decoration> seeds,int dynamicGroup,int surfaceGroup,int originalBodyCount)
    {
        var ties=seeds.Where(d=>d.Rule=="chest-tie").ToArray();if(ties.Length==0)return;
        var attachment=Descendants(seeds.Select(d=>d.Node.Bone));
        var torso=bones.Select((b,i)=>(b,i)).Where(p=>new[]{"Waist","Spine","Chest"}.Contains(p.b.NameEn)||p.b.NameEn.StartsWith("Sp_Ch_Bust",StringComparison.Ordinal)).Select(p=>p.i).ToHashSet();
        float[] torsoWeights=Enumerable.Range(0,points.Length).Select(i=>weights[i].Where(w=>torso.Contains(w.bone)).Sum(w=>w.weight)).ToArray();
        float[] excludedWeights=Enumerable.Range(0,points.Length).Select(i=>weights[i].Where(w=>attachment.Contains(w.bone)).Sum(w=>w.weight)).ToArray();
        var faces=new List<int[]>();for(int f=0;f<model.TriangleIndexes.Length;f+=3){int[] ids={model.TriangleIndexes[f],model.TriangleIndexes[f+1],model.TriangleIndexes[f+2]};
            if(ids.Average(i=>torsoWeights[i])>=.5f&&ids.Max(i=>excludedWeights[i])<.001f)faces.Add(ids);}
        if(faces.Count==0)throw new InvalidOperationException("Tie body surface has no torso triangles");
        foreach(var group in ties.GroupBy(d=>d.Node.Root).OrderBy(g=>g.Key,StringComparer.Ordinal)){
            var chain=group.ToArray();var members=Descendants(chain.Select(d=>d.Node.Bone));
            var selected=Enumerable.Range(0,points.Length).Where(i=>weights[i].Where(w=>members.Contains(w.bone)).Sum(w=>w.weight)>=.5f).Select(i=>points[i]).ToArray();
            Vector3 lower=Bounds(selected,false),upper=Bounds(selected,true);
            float length=chain.Sum(d=>(P(V(d.Node.Data["end"]))-P(V(d.Node.Data["start"]))).magnitude),radius=length*.04f;
            float reach=length*Mathf.Sin(Mathf.Min(Mathf.PI*.5f,20*Mathf.Deg2Rad*chain.Length));float width=upper.x-lower.x+2*reach;
            int columns=Math.Max(2,Mathf.CeilToInt(width/(radius*4))+1),rows=Math.Max(2,Mathf.CeilToInt((upper.y-lower.y)/radius)+1);float patchLength=width/(columns-1);
            for(int row=0;row<rows;row++)for(int col=0;col<columns;col++){
                double x=(double)lower.x-reach+((double)upper.x-lower.x+2.0*reach)*col/(columns-1),y=lower.y+((double)upper.y-lower.y)*row/(rows-1);
                double best=double.PositiveInfinity;int[] face=null;Vector3 bary=Vector3.zero;
                foreach(var ids in faces){var a=points[ids[0]];var e1=points[ids[1]]-a;var e2=points[ids[2]]-a;double det=(double)e1.x*e2.y-(double)e1.y*e2.x;if(Math.Abs(det)<=1e-10)continue;
                    double u=((x-a.x)*e2.y-(y-a.y)*e2.x)/det,v=(e1.x*(y-a.y)-e1.y*(x-a.x))/det;
                    if(u< -1e-6||v< -1e-6||u+v>1+1e-6)continue;double z=a.z+u*e1.z+v*e2.z;
                    if(z<best){best=z;face=ids;bary=new Vector3((float)(1-u-v),(float)u,(float)v);}}
                if(face==null)continue;
                Vector3 point=new Vector3((float)x,(float)y,(float)best);var influence=new Dictionary<int,float>();
                for(int k=0;k<3;k++)foreach(var w in weights[face[k]]){if(!influence.ContainsKey(w.bone))influence[w.bone]=0;influence[w.bone]+=bary[k]*w.weight;}
                Vector3 normal=Vector3.Cross(points[face[1]]-points[face[0]],points[face[2]]-points[face[0]]).normalized;if(normal.z>0)normal=-normal;
                Vector3 tangent=Vector3.Cross(normal,Vector3.up).normalized;var frame=Quaternion.LookRotation(normal,tangent);
                Vector3 center=point-normal*(radius-.5f/80);string label=group.Key+"_"+(row*columns+col);
                int bone=PointFollower(center,influence,label);
                bodies.Add(new Body{Name="TieBodySurface_"+label,NameEn="TieBodySurface_"+label,AssociatedBoneIndex=bone,Position=center,Rotation=Euler(frame),
                    Shape=Body.RigidBodyShape.RigidShapeCapsule,Dimemsions=new Vector3(radius,patchLength,0),CollisionGroup=surfaceGroup,CollisionMask=(ushort)(1<<dynamicGroup)});
            }
        }
        foreach(var original in bodies.Take(originalBodyCount).Where(b=>b.Name.StartsWith("Body_",StringComparison.Ordinal)&&!b.Name.Contains("torso")).ToArray()){
            var b=Copy(original);b.Name=b.NameEn="TieContact_"+original.Name;b.CollisionGroup=surfaceGroup;b.CollisionMask=(ushort)(1<<dynamicGroup);bodies.Add(b);}
    }
    static Vector3 NearestAxis(Body b,Vector3 point)
    {
        Vector3 axis=Rotation(b.Rotation)*Vector3.up;float half=b.Shape==Body.RigidBodyShape.RigidShapeCapsule?b.Dimemsions.y*.5f:0;
        return b.Position+axis*Mathf.Clamp(Vector3.Dot(point-b.Position,axis),-half,half);
    }
    static float BoxCapsuleClearance(Body panel,Body collider,Vector3 pivot,Quaternion rotation)
    {
        Quaternion frame=rotation*Rotation(panel.Rotation);Vector3 center=pivot+rotation*(panel.Position-pivot),axis=Rotation(collider.Rotation)*Vector3.up;
        float half=collider.Shape==Body.RigidBodyShape.RigidShapeCapsule?collider.Dimemsions.y*.5f:0;
        var inverse=Quaternion.Inverse(frame);Vector3 start=inverse*(collider.Position-axis*half-center),direction=inverse*(axis*2*half),extent=panel.Dimemsions;
        var breaks=new List<float>{0,1};for(int i=0;i<3;i++)if(Mathf.Abs(direction[i])>1e-12f)foreach(float sign in new[]{-1f,1}){float t=(sign*extent[i]-start[i])/direction[i];if(t>0&&t<1)breaks.Add(t);}
        breaks=breaks.Distinct().OrderBy(t=>t).ToList();var candidates=new List<float>(breaks);
        for(int k=0;k<breaks.Count-1;k++){Vector3 middle=start+direction*((breaks[k]+breaks[k+1])*.5f);float denominator=0,numerator=0;
            for(int i=0;i<3;i++)if(Mathf.Abs(middle[i])>extent[i]){denominator+=direction[i]*direction[i];numerator+=direction[i]*(start[i]-Mathf.Sign(middle[i])*extent[i]);}
            if(denominator>1e-12f)candidates.Add(Mathf.Clamp(-numerator/denominator,breaks[k],breaks[k+1]));}
        float min=float.PositiveInfinity;foreach(float t in candidates){Vector3 p=start+t*direction;var gap=new Vector3(Mathf.Max(Mathf.Abs(p.x)-extent.x,0),Mathf.Max(Mathf.Abs(p.y)-extent.y,0),Mathf.Max(Mathf.Abs(p.z)-extent.z,0));min=Mathf.Min(min,gap.magnitude);}
        return min-collider.Dimemsions.x;
    }
    void TieLimits(Decoration d)
    {
        var n=d.Node;var j=joints[n.Joint];Vector3 pivot=j.Position;var panels=d.Contacts.Select(i=>bodies[i]).ToArray();
        var candidates=bodies.Where(b=>b.Type==0&&panels.Any(p=>Collides(p,b))).ToArray();if(candidates.Length==0)throw new InvalidOperationException("No permitted tie body contact");
        var collider=candidates.OrderBy(b=>Mathf.Abs((pivot-NearestAxis(b,pivot)).magnitude-b.Dimemsions.x)).First();
        Vector3 obstacle=NearestAxis(collider,pivot),outward=(pivot-obstacle).normalized,longitudinal=d.Frame*Vector3.up,tangent=Vector3.Cross(longitudinal,outward).normalized;
        var frame=Quaternion.LookRotation(Vector3.Cross(tangent,longitudinal),longitudinal);Vector3 toward=obstacle-pivot;float distance=toward.magnitude;
        float cone=Mathf.Asin(Mathf.Min(1,(collider.Dimemsions.x+1f/160)/distance));float center=Mathf.Acos(Mathf.Clamp(Vector3.Dot(longitudinal,toward)/distance,-1,1));
        float inward=Mathf.Min(75*Mathf.Deg2Rad,Mathf.Max(0,center-cone)),safe=0;int steps=Math.Max(2,Mathf.CeilToInt(inward*Mathf.Rad2Deg)+1);
        for(int k=0;k<steps;k++){float angle=inward*k/(steps-1);var r=Quaternion.AngleAxis(-angle*Mathf.Rad2Deg,tangent);
            if(panels.Any(p=>candidates.Any(b=>BoxCapsuleClearance(p,b,pivot,r)<=0))){inward=safe;break;}safe=angle;}
        j.Rotation=Euler(frame);j.RotationLowLimit=new Vector3(-75,0,-20)*Mathf.Deg2Rad;j.RotationHiLimit=new Vector3(inward,0,20*Mathf.Deg2Rad);SetDecorationSpring(d);
    }
}
