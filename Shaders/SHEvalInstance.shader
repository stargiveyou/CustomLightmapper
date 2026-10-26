// SHEvalInstance.shader — SH-5 인스턴스 인덱싱 검증용(iid*7 / iid*14).
//   SHEvalProbe 가 '디코드 수학'(StructuredBuffer<SHPackedGPU>[0], stride 112)만 검증하는 것과 달리,
//   이 셰이더는 '실제 인스턴싱 경로와 동일한' StructuredBuffer<float4>(stride 16) 를 쓰고
//   EvaluateInstanceSH(iid*7) / EvaluateInstanceSH2Axis(iid*14) 로 인스턴스 오프셋 인덱싱을 거친다.
//   → 오프셋/stride 인덱싱 버그(엉뚱한 인스턴스·계수 오독)를 ReadPixels 수치 대조로 포착.
//   InstancedSH_BuiltIn(iid*7) · InstancedSH_URP(iid*14) 와 '동일한 EvaluateSH9.hlsl' 사용.
//   SV_InstanceID 는 Blit 로 못 넣으므로 _InstanceID 유니폼으로 대체(인덱싱 경로는 동일).
Shader "HuskyLibs/SHEvalInstance"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"
            #include "EvaluateSH9.hlsl"    // EvaluateInstanceSH / EvaluateInstanceSH2Axis (공유)

            StructuredBuffer<float4> _InstanceSH;   // 실 인스턴싱 경로와 동일(stride 16, 인스턴스당 7 또는 14)
            float4 _ProbeNormal;                     // xyz = 평가 노멀
            float4 _UpW;                             // xyz = 2-프로브 블렌드 축(월드 up)
            // float 유니폼: Unity 머티리얼 프로퍼티는 float 백엔드라, int 재해석 함정을 피해
            // 작은 정수를 float 로 전달(≤2^24 정확). SetFloat 로 설정.
            float  _InstanceID;                      // SV_InstanceID 대체(인덱싱 경로 동일)
            float  _Mode;                            // 0 = 단일(iid*7), 1 = 2-프로브(iid*14)

            struct v2f { float4 pos : SV_POSITION; };
            v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); return o; }

            float4 frag(v2f i) : SV_Target
            {
                uint   iid = (uint)max(0.0, _InstanceID);
                float3 n   = normalize(_ProbeNormal.xyz);
                float3 e = (_Mode < 0.5)
                    ? EvaluateInstanceSH(_InstanceSH, iid, n)
                    : EvaluateInstanceSH2Axis(_InstanceSH, iid, n, normalize(_UpW.xyz));
                return float4(e, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
