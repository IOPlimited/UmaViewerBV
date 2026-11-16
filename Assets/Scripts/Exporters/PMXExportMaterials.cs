using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibMMD.Material;
using LibMMD.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Converts runtime material inputs into self-contained export textures.</summary>
internal sealed class PMXExportMaterials : IDisposable
{
    private readonly string directory;
    private readonly Material bake;
    internal readonly List<Rect> UvRects = new List<Rect>();
    private readonly Dictionary<Texture2D,string> raw = new Dictionary<Texture2D,string>();
    private readonly Dictionary<string,string> names = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);

    internal PMXExportMaterials(string directory)
    {
        this.directory = directory;
        Shader shader = Resources.Load<Shader>("PMXMaterialBake");
        if (shader == null || !shader.isSupported) throw new InvalidOperationException("PMX material bake shader is unavailable");
        bake = new Material(shader);
    }

    internal MMDMaterial Convert(Renderer renderer, int submesh, Material source, RawMMDModel model,
        bool cheek = false, bool manga = false, bool tear = false, int facialSurface = 0, Color? faceShadeColor = null,
        string textureVariant = null)
    {
        Mesh original = PMXGeometryExporter.MeshOf(renderer);
        Mesh readable = original.isReadable ? original : ModelExporter.MakeReadableMeshCopy(original);
        Mesh posed = null;
        try
        {
            int[] indices = readable.GetTriangles(submesh);
            var uv = readable.uv;
            Rect rect = new Rect(0, 0, 1, 1);
            foreach (int index in indices)
                if (uv.Length > index)
                    rect = Rect.MinMaxRect(Mathf.Min(rect.xMin, uv[index].x), Mathf.Min(rect.yMin, uv[index].y),
                        Mathf.Max(rect.xMax, uv[index].x), Mathf.Max(rect.yMax, uv[index].y));
            UvRects.Add(rect);
            string shaderName = source.shader.name;
            var character = renderer.GetComponentInParent<UmaContainerCharacter>();
            bool miniCheek = character != null && character.IsMini && renderer.name == "M_Cheek";
            cheek |= miniCheek;
            bool eye = shaderName.Contains("ToonEye/");
            bool faceLine = facialSurface == 3;
            // ToonFace declares the same packed-mask properties as body/hair,
            // but its opaque primary pass does not use the blue channel for alpha.
            bool packed = !shaderName.Contains("ToonFace/") && shaderName.Contains("TSER") && Texture(source,"_TripleMaskMap") != null;
            bool transparent = facialSurface == 1 || facialSurface == 2 || tear || manga || cheek || source.renderQueue >= 3000 || shaderName.Contains("Alpha") || shaderName.Contains("Transparent");
            bool cutout = !transparent && source.HasProperty("_Cutoff") && (packed || source.IsKeywordEnabled("_ALPHATEST_ON"));
            string mainProperty = source.shader.FindPropertyIndex("_BaseMap")>=0 ? "_BaseMap" : "_MainTex";
            Texture2D main = Texture(source, mainProperty) ?? Texture2D.whiteTexture;
            Configure(source, mainProperty, rect, eye, packed, cutout, facialSurface != 0 || !transparent && !cutout);
            bool multiplyCheek = cheek && (miniCheek || shaderName.Contains("MultiplyCheek") || source.HasProperty("_CheekRate"));
            bake.SetFloat("_CheekMode", multiplyCheek ? 1 : 0);
            bake.SetFloat("_CheekSaturation", Float(source, "_Saturation", 1));
            bake.SetFloat("_NoEyeHighlight", facialSurface == 1 ? 1 : 0);
            bake.SetFloat("_FaceShade", facialSurface == 2 ? 1 : 0);
            bake.SetFloat("_FaceLine", faceLine ? 1 : 0);
            bake.SetColor("_FaceOutlineColor", ColorValue(source, "_OutlineColor", Color.black));
            if (faceLine) bake.SetFloat("_UseMask", 0);
            // Regular native eye0 atlases contain four columns. Column one is
            // the authored iris without the painted reflections; the separate
            // _High* layers alone account only for the small animated glints.
            bool noHighlightAtlas = facialSurface == 1 && eye && main.name.EndsWith("eye0", StringComparison.OrdinalIgnoreCase)
                && Mathf.Abs(source.GetTextureScale(mainProperty).x - .25f) < 1e-6f;
            if (noHighlightAtlas)
            {
                Vector4 sampling = bake.GetVector("_SampleST"); sampling.z = .25f;
                bake.SetVector("_SampleST", sampling);
            }
            if (facialSurface == 2)
            {
                var shadeActor = renderer.GetComponentInParent<UmaContainerCharacter>();
                var head = shadeActor.HeadToonBaseTransform;
                bake.SetMatrix("_FaceToHead", head.worldToLocalMatrix * renderer.localToWorldMatrix);
                bake.SetFloat("_FaceShadeEnd", Float(source, "_faceShadowEndY", 0));
                bake.SetFloat("_FaceShadeLength", Float(source, "_faceShadowLength", .1f));
                bake.SetColor("_FaceShadeColor", faceShadeColor ?? ColorValue(source, "_faceShadowColor", Color.white));
                bake.SetFloat("_RasterMode", 1);
            }
            int width = Mathf.Clamp(Mathf.CeilToInt(main.width * rect.width), 4, 4096);
            int height = Mathf.Clamp(Mathf.CeilToInt(main.height * rect.height), 4, 4096);
            if (facialSurface == 2 && renderer is SkinnedMeshRenderer skin)
            {
                posed = UnityEngine.Object.Instantiate(readable);
                var geometry = new Mesh();
                try { skin.BakeMesh(geometry, false); posed.vertices = geometry.vertices; }
                finally { UnityEngine.Object.DestroyImmediate(geometry); }
            }
            Texture2D image = eye || facialSurface == 2 ? Raster(posed ?? readable, submesh, width, height)
                : TextureExporter.ReadTexture(main, width, height, bake);
            string path;
            string variant = facialSurface == 1 ? "_highlight_off" : facialSurface == 2
                ? faceShadeColor.HasValue ? "_shade_black" : "_shade_blue" : faceLine ? "_face_line" : multiplyCheek ? "_alpha" : "";
            variant += textureVariant;
            try { path = Save(image, main.name + variant); }
            finally { UnityEngine.Object.DestroyImmediate(image); }

            var metadata = new JObject { ["schema"] = "UmaViewer.Material/1", ["baseColor"] = path,
                ["alphaMode"] = transparent ? "BLEND" : cutout ? "MASK" : "OPAQUE",
                ["sourceUvRect"] = new JArray(rect.x,rect.y,rect.width,rect.height),
                ["sourceMainTransform"] = new JArray(source.GetTextureScale(mainProperty).x,source.GetTextureScale(mainProperty).y,
                    source.GetTextureOffset(mainProperty).x,source.GetTextureOffset(mainProperty).y) };
            if (cheek) metadata["expression"] = multiplyCheek ? "multiplyCheek" : "alphaCheek";
            if (manga) metadata["expression"] = "nativeManga";
            if (tear) metadata["expression"] = "nativeStaticTear";
            if (facialSurface != 0) metadata["expression"] = facialSurface == 1 ? "nativeHighlightOff" : faceLine ? "nativeFaceLine" : "nativeFaceShade";
            if (facialSurface == 2 && faceShadeColor.HasValue) metadata["expression"] = "derivedBlackFaceShade";
            if (noHighlightAtlas) metadata["nativeHighlightOff"] = "eye0 atlas column 1 and all three shader highlights disabled";
            if (faceLine) metadata["nativeOutlineColor"] = Array(ColorValue(source, "_OutlineColor", Color.black));
            if (facialSurface == 2)
                metadata["nativeShade"] = new JObject { ["endY"] = Float(source,"_faceShadowEndY",0),
                    ["length"] = Float(source,"_faceShadowLength",.1f),
                    ["color"] = Array(faceShadeColor ?? ColorValue(source,"_faceShadowColor",Color.white)),
                    ["mask"] = "_TripleMaskMap.b", ["height"] = "head local y" };
            if (facialSurface == 2 && faceShadeColor.HasValue)
                metadata["nativeShade"]["sourceColor"] = Array(ColorValue(source,"_faceShadowColor",Color.white));
            // Keep the shadow palette as a separate authored texture, rather than
            // baking the light falling on the preview model into its base color.
            if (!faceLine && Texture(source,"_ToonMap") is Texture2D shade)
            {
                bake.SetTexture("_MainTex", shade); bake.SetFloat("_UseNumber",0); bake.SetFloat("_UseName",0);
                bake.SetFloat("_EyeMode",0); bake.SetFloat("_UsePackedAlpha",0); bake.SetFloat("_AlphaTest",0);
                foreach (string channel in new[]{"R1","R2","G1","G2","B1","B2"})
                    bake.SetColor("_MaskColor"+channel, ColorValue(source,"_MaskToonColor"+channel,Color.white));
                var shadow = facialSurface == 2 ? Raster(posed ?? readable, submesh, width, height)
                    : TextureExporter.ReadTexture(shade, width, height, bake);
                try { metadata["shadowColor"] = Save(shadow, shade.name + variant); }
                finally { UnityEngine.Object.DestroyImmediate(shadow); }
            }
            var extras = new JObject();
            foreach (string property in new[]{"_EmissiveTex","_UVEmissiveTex","_UVEmissiveMaskTex","_UVEmissiveMask","_EnvMap","_OptionMaskMap"})
                if (Texture(source,property) is Texture2D texture)
                    extras[property] = new JObject { ["texture"] = SaveRaw(texture),
                        ["scale"] = Array(source.GetTextureScale(property)), ["offset"] = Array(source.GetTextureOffset(property)) };
            foreach (string property in new[]{"_EmissiveColor","_UVEmissiveScroll","_UVEmissiveRange","_EmissiveIntensity","_UVEmissivePower","_EmissiveRimIntensity","_EmissiveRimPower"})
                if (source.HasProperty(property))
                    extras[property] = source.shader.GetPropertyType(source.shader.FindPropertyIndex(property)) == UnityEngine.Rendering.ShaderPropertyType.Color
                        ? (JToken)Array(source.GetColor(property)) : source.shader.GetPropertyType(source.shader.FindPropertyIndex(property)) == UnityEngine.Rendering.ShaderPropertyType.Vector
                            ? Array(source.GetVector(property)) : new JValue(source.GetFloat(property));
            if (extras.Count > 0) metadata["effects"] = extras;
            var actor=renderer.GetComponentInParent<UmaContainerCharacter>();
            if(actor!=null)
            {
                var animation=new JArray();
                foreach(var effect in new[]{actor.BodyShaderEffectData,actor.HeadShaderEffectData})
                    if(effect!=null && effect.Settings!=null)
                        foreach(var setting in effect.Settings)
                            if(setting.TargetMaterial!=null && (setting.TargetMaterial==source ||
                                setting.TargetMaterial.name.Replace(" (Instance)","")==source.name.Replace(" (Instance)","")))
                                animation.Add(new JObject{["type"]=setting.Type.ToString(),["parameters"]=new JArray(setting.ParamArray??System.Array.Empty<int>()),
                                    ["curves"]=new JArray((setting.ParamCurveArray??System.Array.Empty<AnimationCurve>()).Select(curve=>curve==null?JValue.CreateNull():(JToken)new JObject{
                                        ["preWrap"]=curve.preWrapMode.ToString(),["postWrap"]=curve.postWrapMode.ToString(),
                                        ["keys"]=new JArray(curve.keys.Select(key=>(JToken)new JObject{["time"]=key.time,["value"]=key.value,["inTangent"]=key.inTangent,["outTangent"]=key.outTangent,
                                            ["inWeight"]=key.inWeight,["outWeight"]=key.outWeight,["weightedMode"]=(int)key.weightedMode}))}))});
                if(animation.Count>0)metadata["animation"]=animation;
            }
            bool doubleSided = source.HasProperty("_Cull") && Mathf.RoundToInt(source.GetFloat("_Cull")) == 0;
            if (!source.HasProperty("_Cull")) doubleSided = shaderName.Contains("Alpha") || shaderName.Contains("Behind") || shaderName.Contains("MiniChara");
            var output = new MMDMaterial { Name = source.name.Replace(" (Instance)",""), NameEn = source.name.Replace(" (Instance)",""),
                Texture = AddTexture(model,path), DiffuseColor = Color.white, SpecularColor = Color.clear, AmbientColor = Color.white*.5f,
                Shiness = 0, DrawDoubleFace = doubleSided, DrawGroundShadow = !transparent, CastSelfShadow = !transparent,
                DrawSelfShadow = !transparent, DrawEdge = false, EdgeColor = Color.black, EdgeSize = .4f,
                MetaInfo = metadata.ToString(Formatting.None) };
            return output;
        }
        finally
        {
            if (posed != null) UnityEngine.Object.DestroyImmediate(posed);
            if (readable != original) UnityEngine.Object.DestroyImmediate(readable);
        }
    }

    private void Configure(Material source,string mainProperty,Rect rect,bool eye,bool packed,bool cutout,bool opaque)
    {
        foreach (string property in new[]{"_MainTex","_TripleMaskMap","_MaskColorTex","_ZekkenNumberTex","_ZekkenNameTex","_High0Tex","_High1Tex","_High2Tex"})
            bake.SetTexture(property, Texture(source,property) ?? (property=="_MainTex" || property=="_TripleMaskMap" ? Texture2D.whiteTexture : Texture2D.blackTexture));
        bake.SetTexture("_MainTex",Texture(source,mainProperty)??Texture2D.whiteTexture);
        Vector2 scale=source.HasProperty(mainProperty)?source.GetTextureScale(mainProperty):Vector2.one;
        Vector2 offset=source.HasProperty(mainProperty)?source.GetTextureOffset(mainProperty):Vector2.zero;
        // Graphics.Blit owns _MainTex_ST. Use an independent uniform for the
        // source material's transform, including zero/negative scale.
        bake.SetVector("_SampleST",new Vector4(scale.x,scale.y,offset.x,offset.y));
        bake.SetVector("_UvRect",new Vector4(rect.x,rect.y,rect.width,rect.height));
        bake.SetFloat("_UseMask",source.IsKeywordEnabled("USE_MASK_COLOR") && Texture(source,"_MaskColorTex") != null?1:0);
        foreach (string channel in new[]{"R1","R2","G1","G2","B1","B2"}) bake.SetColor("_MaskColor"+channel,ColorValue(source,"_MaskColor"+channel,Color.white));
        bake.SetFloat("_UsePackedAlpha",packed?1:0); bake.SetFloat("_AlphaTest",cutout?1:0); bake.SetFloat("_Cutoff",Float(source,"_Cutoff",.5f));
        bake.SetFloat("_ForceOpaque",opaque?1:0);
        bake.SetFloat("_UseNumber",Texture(source,"_ZekkenNumberTex")!=null?1:0); bake.SetFloat("_UseName",Texture(source,"_ZekkenNameTex")!=null?1:0);
        bake.SetColor("_ZekkenFontColor",ColorValue(source,"_ZekkenFontColor",Color.white));
        bake.SetColor("_Tint",ColorValue(source,"_BaseColor",ColorValue(source,"_Color",Color.white)));
        bake.SetFloat("_EyeMode",eye?1:0); bake.SetFloat("_RasterMode",eye?1:0);
        if (!eye) return;
        CopyArray(source,"_MainParam",new[]{Vector4.zero,Vector4.zero});
        CopyArray(source,"_HighParam1",new[]{new Vector4(0,0,0,1),new Vector4(0,0,0,1),Vector4.zero});
        CopyArray(source,"_HighParam2",new[]{new Vector4(0,0,0,1),new Vector4(0,0,0,1)});
        Vector2 hs=source.GetTextureScale("_High0Tex"),ho=source.GetTextureOffset("_High0Tex");
        bake.SetVector("_High0Tex_ST",new Vector4(hs.x,hs.y,ho.x,ho.y));
        bake.SetVector("_Offset",source.HasProperty("_Offset")?source.GetVector("_Offset"):Vector4.zero); bake.SetFloat("_Limit",Float(source,"_Limit",.5f));
    }

    private Texture2D Raster(Mesh mesh,int submesh,int width,int height)
    {
        RenderTexture previous=RenderTexture.active; bool srgb=GL.sRGBWrite;
        var target=RenderTexture.GetTemporary(width,height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
        try
        {
            GL.sRGBWrite=false; RenderTexture.active=target;
            bool shadeSurface = bake.GetFloat("_FaceShade") > .5f;
            GL.Clear(false,true,!shadeSurface && bake.GetFloat("_ForceOpaque")>.5f?Color.black:Color.clear);
            if (!bake.SetPass(0)) throw new InvalidOperationException("PMX eye material bake failed");
            Graphics.DrawMeshNow(mesh,Matrix4x4.identity,submesh);
            var image=new Texture2D(width,height,TextureFormat.RGBA32,false);
            image.ReadPixels(new Rect(0,0,width,height),0,0); image.Apply();
            if (shadeSurface) PadUvIslands(image);
            return image;
        }
        finally { GL.sRGBWrite=srgb; RenderTexture.active=previous; RenderTexture.ReleaseTemporary(target); }
    }

    // The ordinary face uses a complete authored texture. UV rasterization
    // leaves empty texels around each island, so pad those edges before linear
    // filtering; otherwise the replacement introduces black mouth/eyelid seams.
    private static void PadUvIslands(Texture2D image)
    {
        int width = image.width, height = image.height;
        Color32[] pixels = image.GetPixels32();
        for (int pass = 0; pass < 4; pass++)
        {
            var next = (Color32[])pixels.Clone();
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    if (pixels[index].a != 0) continue;
                    if (x > 0 && pixels[index - 1].a != 0) next[index] = pixels[index - 1];
                    else if (x + 1 < width && pixels[index + 1].a != 0) next[index] = pixels[index + 1];
                    else if (y > 0 && pixels[index - width].a != 0) next[index] = pixels[index - width];
                    else if (y + 1 < height && pixels[index + width].a != 0) next[index] = pixels[index + width];
                }
            pixels = next;
        }
        image.SetPixels32(pixels); image.Apply();
    }

    private void CopyArray(Material source,string name,Vector4[] fallback)
    { Vector4[] values=source.GetVectorArray(name); bake.SetVectorArray(name,values!=null&&values.Length==fallback.Length?values:fallback); }
    private string Save(Texture2D image, string name) => TextureExporter.WritePng(directory,image,name,names);
    private string SaveRaw(Texture2D texture)
    {
        if(raw.TryGetValue(texture,out string path))return path;
        var copy=TextureExporter.duplicateTexture(texture);
        try { path=Save(copy,texture.name);raw.Add(texture,path);return path; }
        finally { UnityEngine.Object.DestroyImmediate(copy); }
    }
    internal static MMDTexture AddTexture(RawMMDModel model,string path)
    { var found=model.TextureList.Find(x=>x.TexturePath==path);if(found!=null)return found;var texture=new MMDTexture(path);model.TextureList.Add(texture);return texture; }
    private static Texture2D Texture(Material m,string name)
    {
        int index=m.shader.FindPropertyIndex(name);
        return index>=0 && m.shader.GetPropertyType(index)==UnityEngine.Rendering.ShaderPropertyType.Texture ? m.GetTexture(name) as Texture2D : null;
    }
    private static Color ColorValue(Material m,string name,Color fallback)=>m.HasProperty(name)?m.GetColor(name):fallback;
    private static float Float(Material m,string name,float fallback)=>m.HasProperty(name)?m.GetFloat(name):fallback;
    private static JArray Array(Vector2 v)=>new JArray(v.x,v.y);
    private static JArray Array(Vector4 v)=>new JArray(v.x,v.y,v.z,v.w);
    private static JArray Array(Color v)=>new JArray(v.r,v.g,v.b,v.a);
    public void Dispose()=>UnityEngine.Object.DestroyImmediate(bake);
}
