using System.Collections.Generic;
using System.Linq;
using LibMMD.Model;
using UnityEngine;

/// <summary>Maps material bake rectangles without changing shared source vertices.</summary>
internal static class PMXUvExporter
{
    internal static void Apply(RawMMDModel model,IReadOnlyList<Rect> rects)
    {
        var vertices=model.Vertices.ToList();
        var copies=new Dictionary<int,List<int>>();
        var remap=new Dictionary<(int,Rect),int>();
        for(int p=0;p<model.Parts.Length;p++)
        {
            Rect rect=rects[p];if(rect==new Rect(0,0,1,1))continue;
            Part part=model.Parts[p];
            for(int t=part.BaseShift;t<part.BaseShift+part.TriangleIndexNum;t++)
            {
                int original=model.TriangleIndexes[t];var key=(original,rect);
                if(!remap.TryGetValue(key,out int mapped))
                {
                    Vertex v=model.Vertices[original];mapped=vertices.Count;
                    vertices.Add(new Vertex{Coordinate=v.Coordinate,Normal=v.Normal,SkinningOperator=v.SkinningOperator,EdgeScale=v.EdgeScale,
                        ExtraUvCoordinate=(Vector4[])v.ExtraUvCoordinate.Clone(),
                        UvCoordinate=new Vector2((v.UvCoordinate.x-rect.x)/rect.width,1-((1-v.UvCoordinate.y)-rect.y)/rect.height)});
                    remap.Add(key,mapped);
                    if(!copies.TryGetValue(original,out var list))copies.Add(original,list=new List<int>());list.Add(mapped);
                }
                model.TriangleIndexes[t]=mapped;
            }
        }
        model.Vertices=vertices.ToArray();
        foreach(Morph morph in model.Morphs)
        {
            var data=morph.MorphDatas.ToList();
            foreach(var item in morph.MorphDatas)
                if(item is Morph.VertexMorphData v && copies.TryGetValue(v.VertexIndex,out var targets))
                    foreach(int target in targets)data.Add(new Morph.VertexMorphData{VertexIndex=target,Offset=v.Offset});
            morph.MorphDatas=data.ToArray();
        }
    }
}
