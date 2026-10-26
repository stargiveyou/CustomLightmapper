// ============================================================================
// InstancedVegetation_URP.shader
//   Track B(B1-4): 식생(나무·풀·꽃) 인스턴스 셰이더 — 공간 프로브 볼륨을 픽셀 단위로 샘플 (Universal RP)
//   DrawMeshInstancedIndirect + _InstanceMatrix(InstancedSH_URP 와 같은 규약). SH 인스턴스 버퍼 없음 —
//   간접광은 HuskyProbeVolume.hlsl(Texture3D×7 trilinear → EvaluateSH9)로 위치마다 샘플.
//   조명 = 볼륨 GI(간접+환경, 직사광 제외) + 메인 라이트 실시간 + 그림자. 단위·규약은 InstancedSH_URP 와 동일
//   (albedo × E — _SHStrength·_Exposure 로 맞춤).
//   식생 대응: 알파 클립(_BaseMap.a < _Cutoff 폐기, 그림자 패스 동일) · 양면(백페이스 노멀 반전) ·
//   잎 투과 _Translucency t: 간접 E(n) + t·E(-n), 직사 saturate(n·l) + t·saturate(-n·l).
//   _HUSKY_PV_PIVOT(풀처럼 극히 많은 인스턴스): 볼륨을 인스턴스 피벗에서 정점 셰이더로 샘플(픽셀당 7 fetch 제거).
//   _HUSKY_VERTEX_AO(B1-5): uv3 = (앞면, 뒷면) 템플릿 로컬 정점 AO(VertexAOBake) → 볼륨 GI(간접)에만 곱함. AO 메시에만 켤 것.
// ============================================================================
Shader "HuskyLibs/InstancedVegetation_URP"
{
    Properties
    {
        _BaseMap      ("Albedo (RGB) Alpha (A)", 2D) = "white" {}
        _Color        ("Albedo Tint", Color)            = (1, 1, 1, 1)
        _Cutoff       ("Alpha Cutoff", Range(0,1))      = 0.5
        _Translucency ("Leaf Translucency", Range(0,1)) = 0.3
        _SHStrength   ("Volume GI Strength", Range(0,4)) = 1
        _Exposure     ("Exposure", Range(0,4))          = 1
        [Toggle(_HUSKY_PV_PIVOT)] _SamplePivot ("Sample Volume At Instance Pivot (grass)", Float) = 0
        [Toggle(_HUSKY_VERTEX_AO)] _UseVertexAO ("Vertex AO (uv3 — AO mesh only)", Float) = 0
        _VertexAOStrength ("Vertex AO Strength", Range(0,1)) = 1
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" "RenderPipeline"="UniversalPipeline" }

        // ---- UniversalForward: 볼륨 GI + 메인 라이트 직사광 + 그림자 ----
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Cull Off   // 잎 카드 양면
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 4.5                 // StructuredBuffer + 정점 텍스처 샘플(SM5)

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma shader_feature_local _HUSKY_PV_PIVOT
            #pragma shader_feature_local _HUSKY_VERTEX_AO

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "HuskyProbeVolume.hlsl"

            StructuredBuffer<float4x4> _InstanceMatrix;  // 인스턴스 L2W (그리기 버퍼와 동일 순서)
            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _Color;
                float  _Cutoff;
                float  _Translucency;
                float  _SHStrength;
                float  _Exposure;
                float  _VertexAOStrength;
            CBUFFER_END

            // (M⁻¹)ᵀ 상단 3x3 — InstancedSH_URP.NormalMatrix 와 동일(코팩터/det)
            float3x3 NormalMatrix(float4x4 m)
            {
                float3x3 u = (float3x3)m;
                float3 a = u[0], b = u[1], c = u[2];
                float3 r0 = cross(b, c);
                float3 r1 = cross(c, a);
                float3 r2 = cross(a, b);
                float det = dot(a, r0);
                float invDet = (abs(det) > 1e-8) ? 1.0 / det : 0.0;
                return float3x3(r0 * invDet, r1 * invDet, r2 * invDet);
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            #if defined(_HUSKY_VERTEX_AO)
                float2 vertexAO : TEXCOORD3;   // (앞면, 뒷면) — VertexAOBake
            #endif
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 worldN     : TEXCOORD0;
                float3 worldP     : TEXCOORD1;
                float2 uv         : TEXCOORD2;
            #if defined(_HUSKY_PV_PIVOT)
                float3 giFront    : TEXCOORD3;   // 피벗 샘플: 앞면 노멀 기준 E(n) + t·E(-n)
                float3 giBack     : TEXCOORD4;   //            뒷면(노멀 반전) 기준
            #endif
            #if defined(_HUSKY_VERTEX_AO)
                float2 vertexAO   : TEXCOORD5;
            #endif
            };

            Varyings vert(Attributes v, uint iid : SV_InstanceID)
            {
                Varyings o;
                float4x4 M = _InstanceMatrix[iid];
                float3 worldP = mul(M, float4(v.positionOS.xyz, 1.0)).xyz;
                float3 worldN = normalize(mul(NormalMatrix(M), v.normalOS));
                o.worldP = worldP;
                o.worldN = worldN;
                o.uv = v.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
                o.positionCS = TransformWorldToHClip(worldP);
            #if defined(_HUSKY_PV_PIVOT)
                float3 pivot = float3(M._m03, M._m13, M._m23);
                o.giFront = HuskyPV_IrradianceTranslucent(pivot,  worldN, _Translucency);
                o.giBack  = HuskyPV_IrradianceTranslucent(pivot, -worldN, _Translucency);
            #endif
            #if defined(_HUSKY_VERTEX_AO)
                o.vertexAO = v.vertexAO;
            #endif
                return o;
            }

            half4 frag(Varyings i, bool isFront : SV_IsFrontFace) : SV_Target
            {
                float4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv) * _Color;
                clip(tex.a - _Cutoff);
                float3 albedo = tex.rgb;

                float3 nw = normalize(i.worldN);
                float3 n = isFront ? nw : -nw;

                // (1) 간접 + 환경: 볼륨 GI(잎 투과 포함)
            #if defined(_HUSKY_PV_PIVOT)
                float3 gi = isFront ? i.giFront : i.giBack;
            #else
                float3 gi = HuskyPV_IrradianceTranslucent(i.worldP, n, _Translucency);
            #endif
                float3 indirect = albedo * gi * _SHStrength;
            #if defined(_HUSKY_VERTEX_AO)
                indirect *= lerp(1.0, isFront ? i.vertexAO.x : i.vertexAO.y, _VertexAOStrength);   // 템플릿 로컬 자기차폐(간접만)
            #endif

                // (2) 직사광: 메인 라이트 실시간 + 그림자, 뒷면 투과
                float4 shadowCoord = TransformWorldToShadowCoord(i.worldP);
                Light mainLight = GetMainLight(shadowCoord);
                float  nl    = dot(n, mainLight.direction);
                float  wrap  = saturate(nl) + _Translucency * saturate(-nl);
                float  atten = mainLight.shadowAttenuation * mainLight.distanceAttenuation;
                float3 direct = albedo * mainLight.color * (wrap * atten);

                float3 col = (indirect + direct) * _Exposure;
                return half4(col, 1.0);
            }
            ENDHLSL
        }

        // ---- ShadowCaster: 인스턴스 행렬 + 알파 클립 ----
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            Cull Off
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex   vertSC
            #pragma fragment fragSC
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            StructuredBuffer<float4x4> _InstanceMatrix;
            float3 _LightDirection;   // URP 그림자 패스가 세팅
            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _Color;
                float  _Cutoff;
                float  _Translucency;
                float  _SHStrength;
                float  _Exposure;
                float  _VertexAOStrength;
            CBUFFER_END

            float3x3 NormalMatrixSC(float4x4 m)
            {
                float3x3 u = (float3x3)m;
                float3 a = u[0], b = u[1], c = u[2];
                float3 r0 = cross(b, c), r1 = cross(c, a), r2 = cross(a, b);
                float det = dot(a, r0);
                float invDet = (abs(det) > 1e-8) ? 1.0 / det : 0.0;
                return float3x3(r0 * invDet, r1 * invDet, r2 * invDet);
            }

            struct AttributesSC { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct VaryingsSC { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            VaryingsSC vertSC(AttributesSC v, uint iid : SV_InstanceID)
            {
                VaryingsSC o;
                float4x4 M = _InstanceMatrix[iid];
                float3 worldP = mul(M, float4(v.positionOS.xyz, 1.0)).xyz;
                float3 worldN = normalize(mul(NormalMatrixSC(M), v.normalOS));
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(worldP, worldN, _LightDirection));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                o.positionCS = positionCS;
                o.uv = v.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
                return o;
            }

            half4 fragSC(VaryingsSC i) : SV_Target
            {
                float a = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).a * _Color.a;
                clip(a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
