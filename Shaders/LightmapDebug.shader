// 커스텀 라이트매퍼 검증용 Unlit 셰이더 (URP)
// 개발문서 §8 계약: atlasUV = uv2 * _LightmapST.xy + _LightmapST.zw → Texture2DArray[_LightmapIndex] 샘플.
// 라이팅 없음(검증 전용): 베이커가 구운 디버그 데이터(월드노멀/체커/인스턴스색)를 그대로 표면에 보여준다.
// per-instance ST/Index 는 MaterialPropertyBlock 으로 주입(공유 머티리얼 1장).
Shader "CustomLightmapper/LightmapDebug"
{
    Properties
    {
        [NoScaleOffset] _Lightmaps ("Lightmap Atlas (2DArray)", 2DArray) = "" {}
        _LightmapST    ("Lightmap ST (xy=scale, zw=offset)", Vector) = (1,1,0,0)
        _LightmapIndex ("Lightmap Page Index", Float) = 0
        [Toggle] _ShowUV ("Show atlas UV (디버그)", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "LightmapDebugUnlit"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_ARRAY(_Lightmaps);
            SAMPLER(sampler_Lightmaps);

            CBUFFER_START(UnityPerMaterial)
                float4 _LightmapST;
                float  _LightmapIndex;
                float  _ShowUV;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv2        : TEXCOORD1;   // 조립 메시의 라이트맵 UV(채널1)
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 atlasUV     : TEXCOORD0;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                // §8 계약 그대로: uv2 를 인스턴스 영역으로 리맵
                OUT.atlasUV = IN.uv2 * _LightmapST.xy + _LightmapST.zw;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                if (_ShowUV > 0.5)
                    return half4(IN.atlasUV, 0, 1);    // 아틀라스 좌표를 색으로(영역 위치 즉시 확인)

                return SAMPLE_TEXTURE2D_ARRAY(_Lightmaps, sampler_Lightmaps, IN.atlasUV, _LightmapIndex);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
