// ============================================================================
// InstancedSH_BuiltIn.shader
//   SH-5: per-instance SH9 indirect 셰이더 (Built-In RP · HLSL · D3D11 StructuredBuffer)
//   DrawMeshInstancedIndirect 의 SV_InstanceID 로 _InstanceSH(SHPacked) 인덱싱.
//   조명 = SH(간접+환경, BurstSHBaker) + 직사광 실시간(SH 밖: 링잉·그림자 뭉개짐 방지).
//   2-프로브(상/하) 블렌드 → 면별 수직음영(하늘↔바닥). 버퍼는 인스턴스당 14 float4(iid*14).
//   비균등 스케일 대응: 노멀은 역전치 (M⁻¹)ᵀ 로 변환(등록 TwoLevelBVH.NormalMatrix 규약과 일치).
//   URP/HDRP 변환 시 EvaluateSH9.hlsl(공유) 재사용, 이 파일의 조명부만 교체(InstancedSH_URP 참조).
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
            };

            v2f vert(appdata v, uint iid : SV_InstanceID)
            {
                v2f o;
                float4x4 M = _InstanceMatrix[iid];
                float3 worldP = mul(M, float4(v.vertex.xyz, 1.0)).xyz;
                float3 worldN = normalize(mul(NormalMatrix(M), v.normal)); // (M⁻¹)ᵀ · n

                o.worldP = worldP;
                o.worldN = worldN;
                o.pos = mul(UNITY_MATRIX_VP, float4(worldP, 1.0));
                o.iid = iid;
                TRANSFER_SHADOW(o);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.worldN);
                float3 albedo = _Color.rgb;

                // (1) 간접 + 환경: SH9 조도 × albedo
                float3 shIrradiance = EvaluateInstanceSH(_InstanceSH, i.iid, n) * _SHStrength;
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

        // ---- ShadowCaster: 인스턴스 행렬 반영 그림자 ----
        Pass
        {
            Tags { "LightMode"="ShadowCaster" }
            HLSLPROGRAM
            #pragma vertex   vertSC
            #pragma fragment fragSC
            #pragma target 4.5
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"

            StructuredBuffer<float4x4> _InstanceMatrix;

            struct appdataSC { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2fSC { float4 pos : SV_POSITION; };

            v2fSC vertSC(appdataSC v, uint iid : SV_InstanceID)
            {
                v2fSC o;
                float4x4 M = _InstanceMatrix[iid];
                float3 worldP = mul(M, float4(v.vertex.xyz, 1.0)).xyz;
                o.pos = mul(UNITY_MATRIX_VP, float4(worldP, 1.0));
                #if defined(UNITY_REVERSED_Z)
                    o.pos.z = min(o.pos.z, o.pos.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    o.pos.z = max(o.pos.z, o.pos.w * UNITY_NEAR_CLIP_VALUE);
                #endif
                return o;
            }

            fixed4 fragSC(v2fSC i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    Fallback Off
}
