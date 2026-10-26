#ifndef HUSKY_PROBE_VOLUME_INCLUDED
#define HUSKY_PROBE_VOLUME_INCLUDED

// ============================================================================
// HuskyProbeVolume.hlsl  —  Track B(B1-4) 공간 프로브 볼륨 픽셀 샘플 (공유 include)
//   파이프라인 무관(URP 식생 셰이더 · UnityCG 검증 셰이더 공유). 파일명은 RP core 의 APV
//   `ProbeVolume.hlsl` 과 구분하려고 접두어 Husky 를 붙임(식별자도 _HuskyPV_ 접두).
//
//   데이터: ProbeVolumeAsset.textures — Texture3D×7, 텍스처 t 의 텍셀 (i,j,k) = 프로브 (i,j,k) 의 SHPacked float4 t.
//     프로브 = 텍셀 중심: uvw = (g + 0.5) / dims, g = (p - origin) / spacing  (ProbeGrid.TextureUVW 와 동일).
//     HW trilinear(Clamp) = ProbeGrid.Sample(sh, null, p) — 무효 프로브 값은 베이크 때 dilation 으로 채워져 있음.
//   C# 바인딩·CPU 미러: ProbeVolumeShaderBinding(Bind / EvaluateIrradiance). 상수·순서를 바꾸면 양쪽 동시 수정.
//
//   샘플 위치 = p + n·normalBias(셀 크기 비례 — 얇은 벽·지면 누수 완화).
//   볼륨 밖: 경계 클램프 + (fade > 0 이면) 볼륨 경계에서 fade 셀 거리 동안 폴백 SH(하늘만)로 선형 전환.
//   비활성(바인딩 없음): 폴백 SH 만.
// ============================================================================

#include "EvaluateSH9.hlsl"

Texture3D<float4> _HuskyProbeSH0;
Texture3D<float4> _HuskyProbeSH1;
Texture3D<float4> _HuskyProbeSH2;
Texture3D<float4> _HuskyProbeSH3;
Texture3D<float4> _HuskyProbeSH4;
Texture3D<float4> _HuskyProbeSH5;
Texture3D<float4> _HuskyProbeSH6;
// 인라인 샘플러(이름의 linear·clamp 로 Unity 가 생성) — 텍스처 임포트/필터 설정과 무관하게 trilinear·Clamp
SamplerState husky_probevolume_linear_clamp_sampler;

float4 _HuskyPV_Origin;          // xyz = 프로브 (0,0,0) 월드 위치
float4 _HuskyPV_InvSpacing;      // xyz = 1 / spacing
float4 _HuskyPV_Dims;            // xyz = 축별 프로브 수
float4 _HuskyPV_Params;          // x = 활성(1)/비활성(0), y = normalBias(월드 단위), z = 가장자리 페이드 폭(셀, 0 = 클램프만)
float4 _HuskyPV_FallbackSH[7];   // 폴백 SH(하늘만, SHPacked 7×float4)

float3 HuskyPV_GridCoord(float3 p)
{
    return (p - _HuskyPV_Origin.xyz) * _HuskyPV_InvSpacing.xyz;
}

// 볼륨 가중치: 안 = 1, 경계 밖 d 셀 → 1 - d/fade (0 에서 클램프). fade = 0 이면 항상 1(경계 클램프만).
float HuskyPV_VolumeWeight(float3 g)
{
    float fade = _HuskyPV_Params.z;
    if (fade <= 0.0) return 1.0;
    float3 o = max(-g, g - (_HuskyPV_Dims.xyz - 1.0));
    float d = max(o.x, max(o.y, o.z));
    return saturate(1.0 - d / fade);
}

SH9Coeffs HuskyPV_SampleSH(float3 p)
{
    float3 uvw = (HuskyPV_GridCoord(p) + 0.5) / _HuskyPV_Dims.xyz;
    return UnpackSH9(
        _HuskyProbeSH0.SampleLevel(husky_probevolume_linear_clamp_sampler, uvw, 0.0),
        _HuskyProbeSH1.SampleLevel(husky_probevolume_linear_clamp_sampler, uvw, 0.0),
        _HuskyProbeSH2.SampleLevel(husky_probevolume_linear_clamp_sampler, uvw, 0.0),
        _HuskyProbeSH3.SampleLevel(husky_probevolume_linear_clamp_sampler, uvw, 0.0),
        _HuskyProbeSH4.SampleLevel(husky_probevolume_linear_clamp_sampler, uvw, 0.0),
        _HuskyProbeSH5.SampleLevel(husky_probevolume_linear_clamp_sampler, uvw, 0.0),
        _HuskyProbeSH6.SampleLevel(husky_probevolume_linear_clamp_sampler, uvw, 0.0));
}

SH9Coeffs HuskyPV_FallbackSH()
{
    return UnpackSH9(_HuskyPV_FallbackSH[0], _HuskyPV_FallbackSH[1], _HuskyPV_FallbackSH[2], _HuskyPV_FallbackSH[3],
                     _HuskyPV_FallbackSH[4], _HuskyPV_FallbackSH[5], _HuskyPV_FallbackSH[6]);
}

// 조도 E(n) at p (albedo 미포함 — EvaluateSH9 와 같은 단위). 페이드는 평가 후 선형 보간(CPU 미러 동일).
float3 HuskyPV_Irradiance(float3 p, float3 n)
{
    float3 eFb = EvaluateSH9(HuskyPV_FallbackSH(), n);
    if (_HuskyPV_Params.x < 0.5) return eFb;
    float3 ps = p + n * _HuskyPV_Params.y;
    float w = HuskyPV_VolumeWeight(HuskyPV_GridCoord(ps));
    float3 eVol = EvaluateSH9(HuskyPV_SampleSH(ps), n);
    return lerp(eFb, eVol, w);
}

// 잎 투과: E(n) + t·E(-n). SH 는 1회 샘플(같은 위치)해 두 방향 평가.
float3 HuskyPV_IrradianceTranslucent(float3 p, float3 n, float t)
{
    SH9Coeffs fb = HuskyPV_FallbackSH();
    float3 eFb = EvaluateSH9(fb, n) + t * EvaluateSH9(fb, -n);
    if (_HuskyPV_Params.x < 0.5) return eFb;
    float3 ps = p + n * _HuskyPV_Params.y;
    float w = HuskyPV_VolumeWeight(HuskyPV_GridCoord(ps));
    SH9Coeffs s = HuskyPV_SampleSH(ps);
    float3 eVol = EvaluateSH9(s, n) + t * EvaluateSH9(s, -n);
    return lerp(eFb, eVol, w);
}

#endif // HUSKY_PROBE_VOLUME_INCLUDED
