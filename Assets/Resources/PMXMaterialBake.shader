Shader "Hidden/UmaViewer/PMXMaterialBake"
{
    Properties
    {
        _MainTex ("Main", 2D) = "white" {}
        _TripleMaskMap ("Packed mask", 2D) = "white" {}
        _MaskColorTex ("Area", 2D) = "gray" {}
        _ZekkenNumberTex ("Number", 2D) = "black" {}
        _ZekkenNameTex ("Name", 2D) = "black" {}
        _High0Tex ("Highlight 0", 2D) = "black" {}
        _High1Tex ("Highlight 1", 2D) = "black" {}
        _High2Tex ("Highlight 2", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            ZWrite Off ZTest Always Cull Off Blend Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _TripleMaskMap, _MaskColorTex, _ZekkenNumberTex, _ZekkenNameTex;
            sampler2D _High0Tex, _High1Tex, _High2Tex;
            float4 _SampleST, _High0Tex_ST, _Tint, _ZekkenFontColor, _UvRect;
            float4 _MaskColorR1,_MaskColorR2,_MaskColorG1,_MaskColorG2,_MaskColorB1,_MaskColorB2;
            float4 _MainParam[2],_HighParam1[3],_HighParam2[2],_Offset;
            float _UseMask, _UsePackedAlpha, _Cutoff, _AlphaTest, _ForceOpaque, _UseNumber, _UseName, _EyeMode, _RasterMode, _Limit;
            float _CheekMode, _CheekSaturation;
            float _NoEyeHighlight, _FaceShade, _FaceShadeEnd, _FaceShadeLength;
            float4 _FaceShadeColor;
            float _FaceLine;
            float4 _FaceOutlineColor;
            float4x4 _FaceToHead;
            struct Input { float4 vertex:POSITION; float2 uv:TEXCOORD0; float2 uv1:TEXCOORD1; float2 uv2:TEXCOORD2; };
            struct Varying { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; float4 high01:TEXCOORD1; float2 high2:TEXCOORD2; float3 strengths:TEXCOORD3; float headY:TEXCOORD4; };
            float2 pose(float2 uv,float4 p)
            {
                float2 v=uv-0.5+p.xy; float sn,cs; sincos(p.z,sn,cs);
                return float2(v.x*cs-v.y*sn,v.y*cs+v.x*sn)+0.5;
            }
            Varying vert(Input input)
            {
                Varying o;
                if (_RasterMode > .5)
                {
                    o.vertex=float4(((input.uv-_UvRect.xy)/_UvRect.zw)*2-1,0,1);
                    #if UNITY_UV_STARTS_AT_TOP
                    o.vertex.y=-o.vertex.y;
                    #endif
                }
                else o.vertex=UnityObjectToClipPos(input.vertex);
                float2 uv=_RasterMode>.5?input.uv:input.uv*_UvRect.zw+_UvRect.xy;
                o.uv=uv*_SampleST.xy+_SampleST.zw;
                o.high01=0; o.high2=0; o.strengths=0;
                o.headY=mul(_FaceToHead,input.vertex).y;
                if (_EyeMode>.5)
                {
                    bool left=uv.y>=.5;
                    o.uv=pose(uv,_MainParam[left?0:1])*_SampleST.xy+_SampleST.zw;
                    float4 p0=left?_HighParam1[0]:_HighParam2[0];
                    float4 p1=left?_HighParam1[1]:_HighParam2[1];
                    float2 offset0=left?_Offset.xy:_Offset.zw;
                    o.high01.xy=(pose(input.uv1,p0)+offset0)*_High0Tex_ST.xy+_High0Tex_ST.zw;
                    o.high01.zw=pose(input.uv1,p1)*_High0Tex_ST.xy+_High0Tex_ST.zw;
                    o.high2=pose(input.uv2,_HighParam1[2])*_High0Tex_ST.xy+_High0Tex_ST.zw;
                    o.strengths=float3(p0.w,p1.w,_HighParam1[2].w);
                }
                return o;
            }
            float3 area(float3 color,float3 mask)
            {
                float3 lo=max(.49-mask,0)/.49,hi=max(mask-.51,0)/.49;
                color*=lerp(1,_MaskColorR1.rgb,lo.r)*lerp(1,_MaskColorR2.rgb,hi.r);
                color*=lerp(1,_MaskColorG1.rgb,lo.g)*lerp(1,_MaskColorG2.rgb,hi.g);
                return color*lerp(1,_MaskColorB1.rgb,lo.b)*lerp(1,_MaskColorB2.rgb,hi.b);
            }
            float4 lettering(float4 base,float4 a,float4 b)
            {
                a*=_ZekkenFontColor; b*=_ZekkenFontColor;
                float wa=a.a*a.a*a.a,wb=b.a*b.a*b.a;
                return base*(1-min(wa*a.a+wb*b.a,1))+wa*a+wb*b;
            }
            float4 frag(Varying i):SV_Target
            {
                float4 color=tex2D(_MainTex,i.uv);
                if(_FaceLine>.5)
                {
                    // Native ToonFace Outline uses alpha as the color blend
                    // control, not as output opacity (DXBC program 17).
                    float a=_FaceOutlineColor.a;
                    color.rgb=a<.5 ? lerp(_FaceOutlineColor.rgb,color.rgb,2*a)
                        : color.rgb*lerp(1,_FaceOutlineColor.rgb,2*a-1);
                    return float4(saturate(color.rgb*_Tint.rgb),1);
                }
                if(_CheekMode>.5)
                {
                    // Factor F = (1-alpha) + alpha*RGB. White becomes transparent;
                    // MME reconstructs F exactly, ordinary MMD uses the alpha overlay.
                    float luma=dot(color.rgb,float3(.2125,.7154,.0721));
                    float3 factor=saturate(lerp(luma.xxx,color.rgb,_CheekSaturation));
                    float alpha=1-min(factor.r,min(factor.g,factor.b));
                    return float4(saturate((factor-(1-alpha))/max(alpha,1e-6)),alpha);
                }
                if(_UseMask>.5)color.rgb=area(color.rgb,tex2D(_MaskColorTex,i.uv).rgb);
                if(_UseNumber>.5)
                    color=lettering(color,tex2D(_ZekkenNumberTex,i.uv*float2(3.381806,4.168404)+float2(-1.739770,-2.772197)),
                        tex2D(_ZekkenNumberTex,i.uv*float2(3.707824,4.149378)+float2(-1.948276,-.792946)));
                if(_UseName>.5)
                    color=lettering(color,tex2D(_ZekkenNameTex,i.uv*float2(2.179599,15.974441)+float2(-.909547,-8.563897)),
                        tex2D(_ZekkenNameTex,i.uv*float2(2.189142,14.792900)+float2(-.922067,-1)));
                if(_EyeMode>.5 && _NoEyeHighlight<.5)
                {
                    float3 high=float3(tex2D(_High0Tex,i.high01.xy).r,tex2D(_High1Tex,i.high01.zw).r,tex2D(_High2Tex,i.high2).r);
                    high=high*i.strengths-_Limit;
                    color.rgb+=(high.x>0?1:0)+(high.y>0?1:0)+(high.z>0?1:0);
                }
                if(_FaceShade>.5)
                {
                    float mask=tex2D(_TripleMaskMap,i.uv).b;
                    float strength=saturate((i.headY-_FaceShadeEnd)/max(_FaceShadeLength,1e-6))*mask;
                    color.rgb*=lerp(float3(1,1,1),_FaceShadeColor.rgb,strength);
                }
                if(_UsePackedAlpha>.5)color.a=tex2D(_TripleMaskMap,i.uv).b;
                color*=_Tint;
                if(_AlphaTest>.5)color.a=color.a>=_Cutoff?1:0;
                if(_ForceOpaque>.5)color.a=1;
                return saturate(color);
            }
            ENDHLSL
        }
    }
}
