// 커스텀 라이트매퍼 검증용 Unlit 셰이더 (Built-In RP)
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
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            Name "LightmapDebugUnlit"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5                 // Texture2DArray 샘플링 최소 요구

            #include "UnityCG.cginc"

            UNITY_DECLARE_TEX2DARRAY(_Lightmaps);

            // MaterialPropertyBlock 주입 대상 — BIRP 에선 CBUFFER 없이 전역 선언.
            float4 _LightmapST;
            float  _LightmapIndex;
            float  _ShowUV;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv2    : TEXCOORD1;   // 조립 메시의 라이트맵 UV(채널1)
            };

            struct v2f
            {
                float4 pos     : SV_POSITION;
                float2 atlasUV : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // §8 계약 그대로: uv2 를 인스턴스 영역으로 리맵
                o.atlasUV = v.uv2 * _LightmapST.xy + _LightmapST.zw;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                if (_ShowUV > 0.5)
                    return fixed4(i.atlasUV, 0, 1);    // 아틀라스 좌표를 색으로(영역 위치 즉시 확인)

                return UNITY_SAMPLE_TEX2DARRAY(_Lightmaps, float3(i.atlasUV, _LightmapIndex));
            }
            ENDCG
        }
    }
    Fallback Off
}
