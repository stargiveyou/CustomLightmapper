// ============================================================================
// InstancedSH_URP.shader
//   SH-5: per-instance SH9 indirect 셰이더 (Universal RP · HLSL · D3D11 StructuredBuffer)
//   InstancedSH_BuiltIn 의 URP 포팅 — 조명 wrapper만 교체, SH 디코드는 EvaluateSH9.hlsl(공유) 재사용.
//   DrawMeshInstancedIndirect 의 SV_InstanceID 로 _InstanceSH(SHPacked 7×float4) 인덱싱.
//   조명 = SH(간접+환경, BurstSHBaker) + 메인 라이트 실시간(SH 밖: 링잉·그림자 뭉개짐 방지).
//   비균등 스케일 대응: 노멀은 역전치 (M⁻¹)ᵀ 로 변환(등록 TwoLevelBVH.NormalMatrix 규약과 일치).
// ============================================================================
Shader "HuskyLibs/InstancedSH_URP"
{
    Properties
    {
        _Color      ("Albedo", Color)          = (1, 1, 1, 1)
        _SHStrength ("SH Strength", Range(0,4)) = 1
        _Exposure   ("Exposure", Range(0,4))    = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }

        // ---- UniversalForward: SH 간접 + 메인 라이트 직사광 + 그림자 ----
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Cull Off   // 얇은 단면 플레인 실 FBX 대응: 뒤에서 본 인스턴스 소실 방지(양면 렌더)
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 4.5                 // StructuredBuffer(D3D11 SM5)

            // URP 메인 라이트 그림자 키워드
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "EvaluateSH9.hlsl"

            StructuredBuffer<float4>   _InstanceSH;      // SHPacked: 인스턴스당 float4 × 7
            StructuredBuffer<float4x4> _InstanceMatrix;  // 인스턴스 L2W (그리기 버퍼와 동일 순서)

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float  _SHStrength;
                float  _Exposure;
            CBUFFER_END

            // (M⁻¹)ᵀ 상단 3x3 — 비균등 스케일에서도 노멀이 표면에 수직 유지.
            // HLSL 에 inverse() 내장이 없어 코팩터/det 로 직접 계산.
            // U 의 행이 a,b,c 이면 U⁻¹ 의 '열'은 (b×c, c×a, a×b)/det →
            // (U⁻¹)ᵀ 의 '행'이 그 값들. float3x3(r0,r1,r2) 는 행 우선 생성.
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

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 worldN     : TEXCOORD0;
                float3 worldP     : TEXCOORD1;
                nointerpolation uint iid : TEXCOORD2;
                float3 upW        : TEXCOORD3;   // 인스턴스 로컬 up 의 월드 방향(2-프로브 보간 축)
            };

            Varyings vert(Attributes v, uint iid : SV_InstanceID)
            {
                Varyings o;
                float4x4 M = _InstanceMatrix[iid];
                float3 worldP = mul(M, float4(v.positionOS.xyz, 1.0)).xyz;
                float3 worldN = normalize(mul(NormalMatrix(M), v.normalOS)); // (M⁻¹)ᵀ · n

                // 보간 축 = 인스턴스 로컬 up(0,1,0)의 월드 방향. M 은 이미 로드됨 → 추가 버퍼 읽기 없음.
                float3 upW = normalize(mul((float3x3)M, float3(0.0, 1.0, 0.0)));

                o.worldP = worldP;
                o.worldN = worldN;
                o.positionCS = TransformWorldToHClip(worldP);
                o.iid = iid;
                o.upW = upW;
                return o;
            }

            half4 frag(Varyings i, bool isFront : SV_IsFrontFace) : SV_Target
            {
                // worldSpaceNormal: 단면 플레인 백페이스는 노멀 반전 → 뒷면 SH·NdotL 정합
                float3 worldSpaceNormal = normalize(i.worldN);
                float3 n = isFront ? worldSpaceNormal : -worldSpaceNormal;
                float3 albedo = _Color.rgb;

                // (1) 간접 + 환경: SH9 조도 × albedo (SHPacked 디코드는 공유 include)
                //   2-프로브(상/하) 블렌드 → 면별 수직음영(하늘↔바닥). 버퍼는 인스턴스당 14 float4.
                //   보간 축 = 인스턴스 로컬 up 의 월드 방향(upW) → 임의 3축 회전에서도 정합.
                float3 upAxis = normalize(i.upW);
                float3 shIrradiance = EvaluateInstanceSH2Axis(_InstanceSH, i.iid, n, upAxis) * _SHStrength;
                float3 indirect = albedo * shIrradiance;

                // (2) 직사광: URP 메인 라이트 실시간(SH 밖) + 그림자
                float4 shadowCoord = TransformWorldToShadowCoord(i.worldP);
                Light mainLight = GetMainLight(shadowCoord);
                float  ndl   = saturate(dot(n, mainLight.direction));
                float  atten = mainLight.shadowAttenuation * mainLight.distanceAttenuation;
                float3 direct = albedo * mainLight.color * (ndl * atten);

                float3 col = (indirect + direct) * _Exposure;
                return half4(col, 1.0);
            }
            ENDHLSL
        }

        // ---- ShadowCaster: 인스턴스 행렬 반영 그림자(URP shadow bias) ----
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            Cull Off   // 얇은 단면 플레인 실 FBX 대응: 뒷면만 보이는 각도의 그림자 누락 방지
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex   vertSC
            #pragma fragment fragSC
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            StructuredBuffer<float4x4> _InstanceMatrix;
            float3 _LightDirection;   // URP 그림자 패스가 세팅

            float3x3 NormalMatrixSC(float4x4 m)
            {
                float3x3 u = (float3x3)m;
                float3 a = u[0], b = u[1], c = u[2];
                float3 r0 = cross(b, c), r1 = cross(c, a), r2 = cross(a, b);
                float det = dot(a, r0);
                float invDet = (abs(det) > 1e-8) ? 1.0 / det : 0.0;
                return float3x3(r0 * invDet, r1 * invDet, r2 * invDet);
            }

            struct AttributesSC { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct VaryingsSC { float4 positionCS : SV_POSITION; };

            VaryingsSC vertSC(AttributesSC v, uint iid : SV_InstanceID)
            {
                VaryingsSC o;
                float4x4 M = _InstanceMatrix[iid];
                float3 worldP = mul(M, float4(v.positionOS.xyz, 1.0)).xyz;
                float3 worldN = normalize(mul(NormalMatrixSC(M), v.normalOS));

                // 피터패닝 완화용 노멀·라이트 방향 바이어스(URP 규약)
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(worldP, worldN, _LightDirection));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                o.positionCS = positionCS;
                return o;
            }

            half4 fragSC(VaryingsSC i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    Fallback Off
}
