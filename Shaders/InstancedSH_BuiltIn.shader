// ============================================================================
// InstancedSH_BuiltIn.shader
//   SH-5: per-instance SH9 indirect 셰이더 (Built-In RP · HLSL · D3D11 StructuredBuffer)
//   DrawMeshInstancedIndirect 의 SV_InstanceID 로 _InstanceSH(SHPacked) 인덱싱.
//   조명 = SH(간접+환경, BurstSHBaker) + 직사광 실시간(SH 밖: 링잉·그림자 뭉개짐 방지).
//   2-프로브(상/하) 블렌드 → 면별 수직음영(하늘↔바닥). 버퍼는 인스턴스당 14 float4(iid*14)
//   → C# SHInstancedBuffer.Create(sh, probesPerInstance: 2) 와 정합.
//   비균등 스케일 대응: 노멀은 역전치 (M⁻¹)ᵀ 로 변환(등록 TwoLevelBVH.NormalMatrix 규약과 일치).
//   SH 디코드는 EvaluateSH9.hlsl(공유) — 파이프라인 이식 시 이 파일의 조명부만 교체.
// ============================================================================
Shader "HuskyLibs/InstancedSH_BuiltIn"
{
    Properties
    {
        _Color      ("Albedo", Color)          = (0.7, 0.7, 0.7, 1)
        _SHStrength ("SH Strength", Range(0,4)) = 1
        _Exposure   ("Exposure", Range(0,4))    = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }

        // ---- ForwardBase: SH 간접 + 직사광 + 그림자 ----
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            Cull Off   // 얇은 단면 플레인 실 FBX 대응: 뒤에서 본 인스턴스 소실 방지(양면 렌더)
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 4.5                 // StructuredBuffer(D3D11 SM5)
            #pragma multi_compile_fwdbase      // 그림자/라이트 매크로

            #include "UnityCG.cginc"
            #include "AutoLight.cginc"
            #include "Lighting.cginc"
            #include "EvaluateSH9.hlsl"

            StructuredBuffer<float4>   _InstanceSH;      // SHPacked: 인스턴스당 float4 × 14 (2-프로브 상/하)
            StructuredBuffer<float4x4> _InstanceMatrix;  // 인스턴스 L2W (그리기 버퍼와 동일 순서)

            float4 _Color;
            float  _SHStrength;
            float  _Exposure;

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

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f
            {
                float4 pos    : SV_POSITION;
                float3 worldN : TEXCOORD0;
                float3 worldP : TEXCOORD1;
                nointerpolation uint iid : TEXCOORD2;
                SHADOW_COORDS(3)
                float3 upW    : TEXCOORD4;   // 인스턴스 로컬 up 의 월드 방향(2-프로브 보간 축)
            };

            v2f vert(appdata v, uint iid : SV_InstanceID)
            {
                v2f o;
                float4x4 M = _InstanceMatrix[iid];
                float3 worldP = mul(M, float4(v.vertex.xyz, 1.0)).xyz;
                float3 worldN = normalize(mul(NormalMatrix(M), v.normal)); // (M⁻¹)ᵀ · n

                // 보간 축 = 인스턴스 로컬 up(0,1,0)의 월드 방향. M 은 이미 로드됨 → 추가 버퍼 읽기 없음.
                float3 upW = normalize(mul((float3x3)M, float3(0.0, 1.0, 0.0)));

                o.worldP = worldP;
                o.worldN = worldN;
                o.pos = mul(UNITY_MATRIX_VP, float4(worldP, 1.0));
                o.iid = iid;
                o.upW = upW;
                TRANSFER_SHADOW(o);
                return o;
            }

            fixed4 frag(v2f i, bool isFront : SV_IsFrontFace) : SV_Target
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

                // (2) 직사광: 실시간(SH 밖) + 그림자
                float3 Ldir = normalize(_WorldSpaceLightPos0.xyz);
                float  ndl  = saturate(dot(n, Ldir));
                float  atten = SHADOW_ATTENUATION(i);
                float3 direct = albedo * _LightColor0.rgb * (ndl * atten);

                float3 col = (indirect + direct) * _Exposure;
                return fixed4(col, 1.0);
            }
            ENDHLSL
        }

        // ---- ShadowCaster: 인스턴스 행렬 반영 그림자(Built-In shadow bias) ----
        Pass
        {
            Tags { "LightMode"="ShadowCaster" }
            Cull Off   // 얇은 단면 플레인 실 FBX 대응: 뒷면만 보이는 각도의 그림자 누락 방지
            HLSLPROGRAM
            #pragma vertex   vertSC
            #pragma fragment fragSC
            #pragma target 4.5
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"

            StructuredBuffer<float4x4> _InstanceMatrix;

            float3x3 NormalMatrixSC(float4x4 m)
            {
                float3x3 u = (float3x3)m;
                float3 a = u[0], b = u[1], c = u[2];
                float3 r0 = cross(b, c), r1 = cross(c, a), r2 = cross(a, b);
                float det = dot(a, r0);
                float invDet = (abs(det) > 1e-8) ? 1.0 / det : 0.0;
                return float3x3(r0 * invDet, r1 * invDet, r2 * invDet);
            }

            struct appdataSC { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2fSC { float4 pos : SV_POSITION; };

            v2fSC vertSC(appdataSC v, uint iid : SV_InstanceID)
            {
                v2fSC o;
                float4x4 M = _InstanceMatrix[iid];
                float3 worldP = mul(M, float4(v.vertex.xyz, 1.0)).xyz;
                float3 worldN = normalize(mul(NormalMatrixSC(M), v.normal));

                // 피터패닝 완화용 노멀 바이어스 — UnityClipSpaceShadowCasterPos 를 월드공간으로 옮긴 것.
                // 원본은 unity_ObjectToWorld 로 변환하지만 여기선 인스턴스 행렬 M 을 써야 하므로 직접 계산.
                if (unity_LightShadowBias.z != 0.0)
                {
                    float3 wLight = normalize(UnityWorldSpaceLightDir(worldP));
                    float  shadowCos  = dot(worldN, wLight);
                    float  shadowSine = sqrt(saturate(1.0 - shadowCos * shadowCos));
                    worldP -= worldN * (unity_LightShadowBias.z * shadowSine);
                }

                // 깊이 바이어스 + 근평면 클램프는 UnityApplyLinearShadowBias 가 처리.
                o.pos = UnityApplyLinearShadowBias(mul(UNITY_MATRIX_VP, float4(worldP, 1.0)));
                return o;
            }

            fixed4 fragSC(v2fSC i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    Fallback Off
}
