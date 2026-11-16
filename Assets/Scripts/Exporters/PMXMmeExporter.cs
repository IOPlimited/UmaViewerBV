using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LibMMD.Model;
using Newtonsoft.Json.Linq;

/// <summary>Writes MME effects only for native animated emissive textures.</summary>
internal static class PMXMmeExporter
{
    private static string F(float value) => value.ToString("R",CultureInfo.InvariantCulture);
    private static float Number(JToken token,float fallback=0)=>token==null?fallback:(float)token;
    private static string Vector(JToken token,int count,float fallback)
        => "float"+count+"("+string.Join(",",Enumerable.Range(0,count).Select(i=>F(Number(token?[i],fallback))))+")";

    internal static void Write(string path,RawMMDModel model)
    {
        string directory=Path.GetDirectoryName(path);
        string effectDirectory=Path.Combine(directory,"MME");
        RemovePreviousExport(path,effectDirectory);
        var assignments=new StringBuilder("[Info]\r\nVersion = 3\r\n\r\n[Effect]\r\nObj = none\r\nObj.show = true\r\n");
        int count=0;
        for(int index=0;index<model.Parts.Length;index++)
        {
            var material=model.Parts[index].Material;var description=JObject.Parse(material.MetaInfo);
            var effects=description["effects"] as JObject??new JObject();
            JObject animation=(description["animation"] as JArray)?.OfType<JObject>().FirstOrDefault(x=>(string)x["type"]=="UVScrollEmissive");
            if(effects["_UVEmissiveTex"]?["texture"]==null || animation==null)continue;
        // A separately assigned effect has one material and one technique per
        // pass. PmxView's per-material draw path selects subset zero even for
        // later materials, so a model-wide effect with Subset routing is unsafe.
        var fx=new StringBuilder(@"// UmaViewer material effect. Time is the MMD timeline in seconds.
float4x4 WorldViewProjection : WORLDVIEWPROJECTION;
float4x4 WorldInverseTranspose : WORLDINVERSETRANSPOSE;
float3 LightDirection : DIRECTION < string Object = ""Light""; > = float3(0,-1,-1);
float4 MaterialDiffuse : DIFFUSE < string Object = ""Geometry""; > = float4(1,1,1,1);
float4 TextureMultiply : MULTIPLYINGTEXTURE = float4(1,1,1,1);
float4 TextureAdd : ADDINGTEXTURE = float4(0,0,0,0);
float Timeline : TIME < bool SyncInEditMode = true; >;
texture BaseTexture : MATERIALTEXTURE;
sampler2D BaseSampler = sampler_state { Texture=<BaseTexture>; MinFilter=LINEAR; MagFilter=LINEAR; MipFilter=LINEAR; AddressU=CLAMP; AddressV=CLAMP; };
struct VSInput { float4 position:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0; };
struct VSOutput { float4 position:POSITION; float3 normal:TEXCOORD0; float2 uv:TEXCOORD1; };
VSOutput VS(VSInput input) {
    VSOutput output; output.position=mul(input.position,WorldViewProjection);
    output.normal=mul(float4(input.normal,0),WorldInverseTranspose).xyz;
    output.uv=input.uv; return output;
}
float Bezier(float a,float b,float c,float d,float t) { float s=1-t; return s*s*s*a+3*s*s*t*b+3*s*t*t*c+t*t*t*d; }

");
            bool cheek=(string)description["expression"]=="multiplyCheek";
            bool manga=(string)description["expression"]=="nativeManga";
            bool tear=(string)description["expression"]=="nativeStaticTear";
            bool faceLine=(string)description["expression"]=="nativeFaceLine";
            string stem="m"+index;
            bool shadow=description["shadowColor"]!=null;
            if(shadow)Texture(fx,stem+"Shadow",(string)description["shadowColor"],false);
            bool dynamic=effects["_UVEmissiveTex"]?["texture"]!=null;
            bool mask=effects["_UVEmissiveMaskTex"]?["texture"]!=null;
            bool emission=effects["_EmissiveTex"]?["texture"]!=null;
            if(dynamic)Texture(fx,stem+"Dynamic",(string)effects["_UVEmissiveTex"]["texture"],true);
            if(mask)Texture(fx,stem+"Mask",(string)effects["_UVEmissiveMaskTex"]["texture"],false);
            if(emission)Texture(fx,stem+"Emission",(string)effects["_EmissiveTex"]["texture"],false);
            string[] animated=new string[3];
            for(int axis=0;axis<3;axis++)animated[axis]=Animated(fx,stem+"Axis"+axis,animation,axis,
                axis<2?Number(effects["_UVEmissiveScroll"]?[axis]):Number(effects["_UVEmissivePower"],1));
            fx.AppendLine("float4 PS"+index+"(VSOutput input, uniform bool textureMorph):COLOR0 {");
            fx.AppendLine("float4 base=tex2D(BaseSampler,input.uv);");
            // MME only guarantees texture-morph semantics for object_ss.
            fx.AppendLine("if(textureMorph)base=base*TextureMultiply+TextureAdd;");
            if(cheek)
                fx.AppendLine("return float4(lerp(float3(1,1,1),1-base.a+base.rgb*base.a,saturate(MaterialDiffuse.a)),1);\n}");
            else if(manga || tear || faceLine)
                fx.AppendLine("return float4(base.rgb*MaterialDiffuse.rgb,base.a*MaterialDiffuse.a);\n}");
            else
            {
            if((string)description["alphaMode"]=="OPAQUE")fx.AppendLine("base.a=1;");
            fx.AppendLine("float light=smoothstep(.46,.54,dot(normalize(input.normal),-normalize(LightDirection))*.5+.5);");
            fx.AppendLine(shadow?"float3 color=lerp(tex2D("+stem+"ShadowSampler,input.uv).rgb,base.rgb,light);":"float3 color=base.rgb*lerp(.6,1,light);");
            var rect=description["sourceUvRect"] as JArray??new JArray(0,0,1,1);
            fx.AppendLine("float2 sourceUv=float2(input.uv.x,1-input.uv.y)*"+Vector(new JArray(rect[2],rect[3]),2,1)+"+"+Vector(rect,2,0)+";");
            var st=description["sourceMainTransform"] as JArray??new JArray(1,1,0,0);
            fx.AppendLine("sourceUv=sourceUv*"+Vector(st,2,1)+"+"+Vector(new JArray(st[2],st[3]),2,0)+";");
            string color=Vector(effects["_EmissiveColor"],3,1);
            if(dynamic)
            {
                fx.AppendLine("float power="+animated[2]+";");
                fx.AppendLine("float amount="+(mask?"tex2D("+stem+"MaskSampler,float2(sourceUv.x,1-sourceUv.y)).r":"1")+"*power;");
                fx.AppendLine("float4 region="+Vector(effects["_UVEmissiveRange"],4,1)+";");
                fx.AppendLine("float2 q=sourceUv-region.xy+float2("+animated[0]+","+animated[1]+");");
                fx.AppendLine("float2 extent=float2(q.x*region.z>=0?region.z:-region.z,q.y*region.w>=0?region.w:-region.w);");
                fx.AppendLine("extent=float2(abs(extent.x)<1e-6?1:extent.x,abs(extent.y)<1e-6?1:extent.y);");
                fx.AppendLine("float2 uv=frac(q/extent)*extent+region.xy;");
                fx.AppendLine("color=saturate(color*(1-saturate(amount))+tex2D("+stem+"DynamicSampler,float2(uv.x,1-uv.y)).rgb*"+color+"*amount);");
            }
            if(emission)fx.AppendLine("color+=tex2D("+stem+"EmissionSampler,float2(sourceUv.x,1-sourceUv.y)).rgb*"+color+"*"+F(Number(effects["_EmissiveIntensity"],1))+";");
            fx.AppendLine("return float4(color*MaterialDiffuse.rgb,base.a*MaterialDiffuse.a);\n}");
            }
            foreach(string pass in new[]{"object","object_ss"})
            {
                fx.AppendLine("technique "+stem+"_"+pass+" < string MMDPass=\""+pass+"\"; > { pass P {");
                fx.AppendLine("CullMode="+(material.DrawDoubleFace?"NONE":"CCW")+"; ZEnable=TRUE; ZFunc=LESSEQUAL; ZWriteEnable="+((string)description["alphaMode"]=="BLEND"?"FALSE":"TRUE")+";");
                fx.AppendLine(cheek
                    ? "ColorWriteEnable=7; AlphaBlendEnable=TRUE; SrcBlend=DESTCOLOR; DestBlend=ZERO; AlphaTestEnable=TRUE; AlphaFunc=GREATER; AlphaRef=0;"
                    : "AlphaBlendEnable=TRUE; SrcBlend=SRCALPHA; DestBlend=INVSRCALPHA; AlphaTestEnable=TRUE; AlphaFunc=GREATER; AlphaRef=0;");
                fx.AppendLine("VertexShader=compile vs_3_0 VS(); PixelShader=compile ps_3_0 PS"+index+"("+(pass=="object_ss"?"true":"false")+"); } }");
            }
            string content=fx.ToString();
            string filename;
            using(var hash=SHA256.Create())filename=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(content))).Replace("-","").ToLowerInvariant()+".fx";
            Directory.CreateDirectory(effectDirectory);
            File.WriteAllText(Path.Combine(effectDirectory,filename),content,new UTF8Encoding(false));
            assignments.AppendLine("Obj["+index+"] = MME/"+filename);
            assignments.AppendLine("Obj["+index+"].show = true");
            count++;
        }
        if(count>0)File.WriteAllText(Path.ChangeExtension(path,"emd"),assignments.ToString(),new UTF8Encoding(false));
        // Migrate the generated model-wide effect from earlier exports. A
        // user's independently authored effect is left in place.
        string previous=Path.ChangeExtension(path,"fx");
        if(File.Exists(previous) && File.ReadLines(previous).FirstOrDefault()=="// UmaViewer material effect. Time is the MMD timeline in seconds.")File.Delete(previous);
    }

    private static void RemovePreviousExport(string path,string effectDirectory)
    {
        // Re-exporting a static model must not leave its old auto-assignment
        // active. Remove only our generated effects, retaining shared effects
        // referenced by another model and any user-authored files.
        string assignment=Path.ChangeExtension(path,"emd");
        if(!File.Exists(assignment))return;
        string text=File.ReadAllText(assignment);
        if(!text.StartsWith("[Info]\r\nVersion = 3\r\n\r\n[Effect]\r\nObj = none\r\nObj.show = true\r\n",StringComparison.Ordinal))return;
        var names=System.Text.RegularExpressions.Regex.Matches(text,@"(?m)^Obj\[\d+\] = MME/([0-9a-f]{64}\.fx)\r?$")
            .Cast<System.Text.RegularExpressions.Match>().Select(m=>m.Groups[1].Value).Distinct().ToArray();
        if(System.Text.RegularExpressions.Regex.Matches(text,@"(?m)^Obj\[\d+\] = (.+?)\r?$")
            .Cast<System.Text.RegularExpressions.Match>().Any(m=>!names.Any(name=>m.Groups[1].Value=="MME/"+name)))return;
        if(names.Length==0 || names.Any(name=>!File.Exists(Path.Combine(effectDirectory,name)) ||
            File.ReadLines(Path.Combine(effectDirectory,name)).FirstOrDefault()!="// UmaViewer material effect. Time is the MMD timeline in seconds."))return;
        File.Delete(assignment);
        var otherAssignments=Directory.GetFiles(Path.GetDirectoryName(path),"*.emd").Select(File.ReadAllText).ToArray();
        foreach(string name in names)
            if(!otherAssignments.Any(other=>other.Contains("MME/"+name)))File.Delete(Path.Combine(effectDirectory,name));
        if(!Directory.EnumerateFileSystemEntries(effectDirectory).Any())Directory.Delete(effectDirectory);
    }

    private static void Texture(StringBuilder fx,string name,string path,bool repeat)
    {
        fx.AppendLine("texture "+name+"Texture < string ResourceName=\"../"+path.Replace('\\','/')+"\"; >;");
        fx.AppendLine("sampler2D "+name+"Sampler=sampler_state { Texture=<"+name+"Texture>; MinFilter=LINEAR; MagFilter=LINEAR; MipFilter=LINEAR; AddressU="+(repeat?"WRAP":"CLAMP")+"; AddressV="+(repeat?"WRAP":"CLAMP")+"; };");
    }

    private static string Animated(StringBuilder fx,string name,JObject animation,int axis,float fallback)
    {
        var parameters=animation?["parameters"] as JArray;
        if(parameters==null||parameters.Count<9)return F(fallback);
        int mode=(int)parameters[axis*2];float value=(float)parameters[axis*2+1]*.001f;
        if(mode==0)return F(value);
        if(mode==1)return "("+F(value)+"*Timeline)";
        if(mode==2)return "fmod("+F(value)+"*Timeline,2)";
        if(mode!=5)return "0";
        int curveIndex=(int)parameters[axis+6];var curves=animation["curves"] as JArray;
        if(curves==null||curveIndex<0||curveIndex>=curves.Count||!(curves[curveIndex]?["keys"] is JArray keys)||keys.Count==0)return F(fallback);
        fx.AppendLine("float "+name+"(float time) { float x=frac(time*"+F(value)+");");
        fx.AppendLine("if(x<="+F(Number(keys[0]["time"]))+")return "+F(Number(keys[0]["value"]))+";");
        for(int i=0;i<keys.Count-1;i++)
        {
            JToken a=keys[i],b=keys[i+1];float duration=Number(b["time"])-Number(a["time"]);
            if(duration<=0)continue;
            float v0=Number(a["value"]),v1=Number(b["value"]),s0=Number(a["outTangent"]),s1=Number(b["inTangent"]);
            fx.AppendLine("if(x<"+F(Number(b["time"]))+"){");
            if(float.IsInfinity(s0)||float.IsInfinity(s1))fx.AppendLine("return "+F(v0)+";");
            else
            {
                float w0=((int)a["weightedMode"]&2)!=0?Number(a["outWeight"]):1f/3f;
                float w1=((int)b["weightedMode"]&1)!=0?Number(b["inWeight"]):1f/3f;
                fx.AppendLine("float u=(x-"+F(Number(a["time"]))+")/"+F(duration)+";");
                if(((int)a["weightedMode"]&2)!=0||((int)b["weightedMode"]&1)!=0)
                    fx.AppendLine("float lo=0,hi=1; [unroll] for(int j=0;j<16;j++){float mid=(lo+hi)*.5;if(Bezier(0,"+F(w0)+","+F(1-w1)+",1,mid)<u)lo=mid;else hi=mid;}u=(lo+hi)*.5;");
                fx.AppendLine("return Bezier("+F(v0)+","+F(v0+s0*duration*w0)+","+F(v1-s1*duration*w1)+","+F(v1)+",u);");
            }
            fx.AppendLine("}");
        }
        fx.AppendLine("return "+F(Number(keys.Last["value"]))+"; }");
        return name+"(Timeline)";
    }
}
