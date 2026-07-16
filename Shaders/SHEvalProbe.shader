// SHEvalProbe.shader — SH-5 검증용: StructuredBuffer<SHPackedGPU> 1개 + 노멀 → EvaluateSH9 를 float RT 로 출력.
// InstancedSH 와 '동일한 EvaluateSH9.hlsl' 을 써서, 셰이더의 SH 디코드/평가가 CPU SH9.Evaluate 와
// 일치하는지 Async/ReadPixels 로 수치 대조(=SH-5 셰이더 경로의 헤드리스 대체 검증).
// float4 반환 + float RT → HDR·정밀도 보존(albedo 없음, raw SH).
Shader "HuskyLibs/SHEvalProbe"
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
            #include "EvaluateSH9.hlsl"    // SHPackedGPU / UnpackSH9 / EvaluateSH9

            StructuredBuffer<SHPackedGPU> _ProbeSH;   // 1개
            float4 _ProbeNormal;                       // xyz = 평가 노멀

            struct v2f { float4 pos : SV_POSITION; };
            v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); return o; }

            float4 frag(v2f i) : SV_Target
            {
                SHPackedGPU q = _ProbeSH[0];
                SH9Coeffs s = UnpackSH9(q.p0, q.p1, q.p2, q.p3, q.p4, q.p5, q.p6);
                float3 e = EvaluateSH9(s, normalize(_ProbeNormal.xyz));
                return float4(e, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
