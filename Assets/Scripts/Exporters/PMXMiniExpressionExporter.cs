using System;
using System.Collections.Generic;
using System.Linq;
using LibMMD.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static LibMMD.Model.Morph;

/// <summary>Independent native atlas layers fade without sliding across atlas cells.</summary>
internal sealed class PMXMiniExpressionExporter : IDisposable
{
    private sealed class Source
    {
        internal Renderer Renderer;
        internal Material Material;
        internal int Submesh, Part;
    }
    private readonly UmaContainerCharacter character;
    private readonly Dictionary<(Renderer,int),Material> neutral = new Dictionary<(Renderer,int),Material>();
    private readonly List<Source> sources = new List<Source>();
    private static readonly string[] Roles = { "M_Mouth","M_Eye","M_Mayu_L","M_Mayu_R","M_Cheek" };

    internal PMXMiniExpressionExporter(UmaContainerCharacter character) => this.character=character;
    internal void IncludeRenderers(List<Renderer> renderers)
    {
        foreach (var r in character.GetComponentsInChildren<Renderer>(true).Where(r => Roles.Contains(r.name)))
            if (!renderers.Contains(r)) renderers.Add(r);
        var cheeks=renderers.FindAll(r=>r.name=="M_Cheek");renderers.RemoveAll(r=>r.name=="M_Cheek");renderers.AddRange(cheeks);
    }
    internal Material MaterialFor(Renderer renderer,int submesh,Material original)
    {
        if (!Roles.Contains(renderer.name)) return original;
        var key=(renderer,submesh);
        if (neutral.TryGetValue(key,out var material)) return material;
        material=new Material(original);
        material.mainTextureOffset=Vector2.zero;
        if(material.HasProperty("_UVOffset"))material.SetVector("_UVOffset",Vector4.zero);
        if(renderer.name=="M_Cheek")
            material.mainTexture=character.MiniHeadTextures.Load(t=>t.name.EndsWith("cheek0",StringComparison.Ordinal))??original.mainTexture;
        neutral.Add(key,material);return material;
    }
    internal void Record(Renderer renderer,int submesh,int part,Material material)
    {
        if(Roles.Contains(renderer.name))sources.Add(new Source{Renderer=renderer,Submesh=submesh,Part=part,Material=material});
    }
    private static string Control(string family,int id,string side="") => "Mini"+family+"_"+id+(side.Length>0?"_"+side:"");

    internal void Add(RawMMDModel model,PMXExportMaterials textures,List<Material> sourceMaterials)
    {
        var vertices=model.Vertices.ToList();
        var triangles=model.TriangleIndexes.ToList();var parts=model.Parts.ToList();
        var morphs=model.Morphs.ToList();
        var names=morphs.Select((m,i)=>(m.Name,i)).ToDictionary(x=>x.Name,x=>x.i,StringComparer.Ordinal);
        void Layer(string role,string family,int id,string side,int rows,Texture replacement=null)
        {
            string name=Control(family,id,side);var data=new List<MorphData>();
            foreach(var source in sources.Where(s=>s.Renderer.name==role))
            {
                var original=model.Parts[source.Part];var selected=new List<int>();
                for(int t=original.BaseShift;t<original.BaseShift+original.TriangleIndexNum;t+=3)
                {
                    int a=model.TriangleIndexes[t],b=model.TriangleIndexes[t+1],c=model.TriangleIndexes[t+2];
                    // The native mouth also contains an inner wall sampling a blank
                    // atlas cell. Keep it in the base mesh, outside the facial overlay.
                    if(role=="M_Mouth" && new[]{a,b,c}.Any(i=>
                        model.Vertices[i].UvCoordinate.x>=.25f || model.Vertices[i].UvCoordinate.y>=.125f))continue;
                    if(side.Length>0 && role=="M_Eye")
                    {
                        bool left=side=="L";
                        if(new[]{a,b,c}.Any(i=>left?model.Vertices[i].Coordinate.x<=0:model.Vertices[i].Coordinate.x>=0))continue;
                    }
                    selected.Add(a);selected.Add(b);selected.Add(c);
                }
                if(selected.Count==0)continue;
                var material=new Material(source.Material);
                LibMMD.Material.MMDMaterial output;
                try
                {
                    if(replacement!=null)material.mainTexture=replacement;
                    else material.mainTextureOffset=new Vector2(.25f*(id%4),-(1f/rows)*(id/4));
                    output=textures.Convert(source.Renderer,source.Submesh,material,model,textureVariant:"_"+name);
                }
                finally{UnityEngine.Object.DestroyImmediate(material);}
                output.Name+="_"+name;output.NameEn=output.Name;
                var description=JObject.Parse(output.MetaInfo);description["alphaMode"]="BLEND";
                description["nativeMiniControl"]=name;
                output.MetaInfo=description.ToString(Formatting.None);
                var color=output.DiffuseColor;color.a=0;output.DiffuseColor=color;
                // MMD's translucent pass needs both facings for the mini's
                // layered mouth patch, including fractional morph weights.
                output.DrawDoubleFace=true;
                output.CastSelfShadow=false;output.DrawSelfShadow=false;output.DrawGroundShadow=false;
                // MMD stores material ownership on vertices. Independent layers
                // need independent indices even though their positions coincide.
                var remap=new Dictionary<int,int>();
                for(int i=0;i<selected.Count;i++)
                {
                    int originalIndex=selected[i];
                    if(!remap.TryGetValue(originalIndex,out int mapped))
                    {
                        var v=model.Vertices[originalIndex];mapped=vertices.Count;
                        // A 0.4 mm separation keeps MMD's translucent depth test
                        // stable against the opaque neutral patch (notably 1025).
                        vertices.Add(new Vertex{Coordinate=v.Coordinate+v.Normal*.0004f,Normal=v.Normal,
                            UvCoordinate=v.UvCoordinate,ExtraUvCoordinate=(Vector4[])v.ExtraUvCoordinate.Clone(),
                            SkinningOperator=v.SkinningOperator,EdgeScale=v.EdgeScale});
                        remap.Add(originalIndex,mapped);
                    }
                    selected[i]=mapped;
                }
                int start=triangles.Count;triangles.AddRange(selected);
                int target=parts.Count;
                parts.Add(new Part{Material=output,BaseShift=start,TriangleIndexNum=selected.Count});
                sourceMaterials.Add(source.Material);
                data.Add(new MaterialMorphData{MaterialIndex=target,Method=MaterialMorphData.MaterialMorphMethod.MorphMatAdd,Diffuse=new Color(0,0,0,1)});
                // Opaque mouth/eye patches retain an opaque neutral backing.
                // Alpha-only brows and multiply cheeks instead fade their base.
                if(role.StartsWith("M_Mayu",StringComparison.Ordinal)||role=="M_Cheek")
                    data.Add(new MaterialMorphData{MaterialIndex=source.Part,Method=MaterialMorphData.MaterialMorphMethod.MorphMatAdd,Diffuse=new Color(0,0,0,-1)});
            }
            if(data.Count==0)return;
            names.Add(name,morphs.Count);
            morphs.Add(new Morph{Name=name,NameEn=name,Category=family=="Mouth"?MorphCategory.MorphCatMouth:
                family=="Brow"?MorphCategory.MorphCatEyebrow:family=="Eye"?MorphCategory.MorphCatEye:MorphCategory.MorphCatOther,
                Type=MorphType.MorphTypeMaterial,MorphDatas=data.ToArray()});
        }
        for(int id=1;id<19;id++)Layer("M_Mouth","Mouth",id,"",8);
        foreach(string side in new[]{"L","R"})
        {
            for(int id=1;id<15;id++)Layer("M_Eye","Eye",id,side,8);
            for(int id=1;id<9;id++)Layer("M_Mayu_"+side,"Brow",id,side,4);
        }
        var cheek1=character.MiniHeadTextures.Load(t=>t.name.EndsWith("cheek1",StringComparison.Ordinal));
        if(cheek1!=null)Layer("M_Cheek","Cheek",1,"",1,cheek1);
        var hide=sources.Where(s=>s.Renderer.name=="M_Cheek").Select(s=>(MorphData)new MaterialMorphData{
            MaterialIndex=s.Part,Method=MaterialMorphData.MaterialMorphMethod.MorphMatAdd,Diffuse=new Color(0,0,0,-1)}).ToArray();
        if(hide.Length>0){names.Add("MiniCheekOff",morphs.Count);morphs.Add(new Morph{Name="MiniCheekOff",NameEn="MiniCheekOff",Category=MorphCategory.MorphCatOther,Type=MorphType.MorphTypeMaterial,MorphDatas=hide});}

        void Alias(string name,params string[] controls)
        {
            if(controls.All(names.ContainsKey))PMXNativeExpressionExporter.AddGroup(morphs,names,name,MorphCategory.MorphCatEye,
                controls.Select(c=>new KeyValuePair<string,float>(c,1)));
        }
        Alias("まばたき",Control("Eye",8,"L"),Control("Eye",8,"R"));
        Alias("ウィンク",Control("Eye",8,"L"));Alias("ウィンク右",Control("Eye",8,"R"));
        foreach(var pair in new[]{("笑い",9),("ぐるぐる",14),("しいたけ",13),("漫画目丸",12),("はぅ",7)})
            Alias(pair.Item1,Control("Eye",pair.Item2,"L"),Control("Eye",pair.Item2,"R"));
        if(names.ContainsKey(Control("Cheek",1)))PMXNativeExpressionExporter.AddGroup(morphs,names,"照れ",MorphCategory.MorphCatOther,
            new[]{new KeyValuePair<string,float>(Control("Cheek",1),1)});
        foreach(var preset in UmaDatabaseController.Instance.MiniFaceTypeData)
        {
            var terms=new Dictionary<string,float>();
            void Use(string family,int id,string side=""){if(id!=0)terms[Control(family,id,side)]=1;}
            Use("Mouth",preset.mouth);Use("Eye",preset.eye_l,"L");Use("Eye",preset.eye_r,"R");
            Use("Brow",preset.eyebrow_l,"L");Use("Brow",preset.eyebrow_r,"R");
            if(preset.cheek==0)terms["MiniCheekOff"]=1;
            else if(preset.cheek>1)Use("Cheek",preset.cheek-1);
            if(terms.Count==0||terms.Any(t=>!names.ContainsKey(t.Key)))continue;
            PMXNativeExpressionExporter.AddGroup(morphs,names,preset.label,MorphCategory.MorphCatOther,terms);
        }
        model.Vertices=vertices.ToArray();model.TriangleIndexes=triangles.ToArray();model.Parts=parts.ToArray();model.Morphs=morphs.ToArray();
    }
    public void Dispose(){foreach(var material in neutral.Values)UnityEngine.Object.DestroyImmediate(material);}
}
