// ProbeVolumeSampleTest.shader — Track B(B1-4) 검증용: HuskyProbeVolume.hlsl 을 N×1 float RT 로 평가.
// 픽셀 i 가 _TestPoints[i](위치)·_TestNormals[i](노멀, w = 투과율 t)로 HuskyPV_Irradiance(Translucent) 를 출력 →
// ProbeVolumeGpuSampleTests 가 ReadPixels 로 회수해 CPU 미러(ProbeVolumeShaderBinding.EvaluateIrradiance)와 대조.
// 식생 셰이더(InstancedVegetation_URP)와 같은 include 라 샘플·바이어스·페이드·투과 경로가 동일.
Shader "Hidden/HuskyLibs/ProbeVolumeSampleTest"
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
            #include "HuskyProbeVolume.hlsl"

            StructuredBuffer<float4> _TestPoints;    // xyz = 월드 위치
            StructuredBuffer<float4> _TestNormals;   // xyz = 노멀(정규화), w = 투과율 t(0 이면 HuskyPV_Irradiance)

            struct v2f { float4 pos : SV_POSITION; };
            v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); return o; }

            float4 frag(v2f i) : SV_Target
            {
                uint idx = (uint)i.pos.x;
                float3 p = _TestPoints[idx].xyz;
                float4 nt = _TestNormals[idx];
                float3 e = nt.w > 0.0 ? HuskyPV_IrradianceTranslucent(p, nt.xyz, nt.w) : HuskyPV_Irradiance(p, nt.xyz);
                return float4(e, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
