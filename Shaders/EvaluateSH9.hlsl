#ifndef HUSKY_EVALUATE_SH9_INCLUDED
#define HUSKY_EVALUATE_SH9_INCLUDED

// ============================================================================
// EvaluateSH9.hlsl  —  per-instance SH9 조도 평가 (공유 include)
//   SH9.Evaluate(C#) 를 HLSL 로 1:1 포팅. 파이프라인 무관(Built-In / URP / HDRP 공유).
//   URP/HDRP 변환 시 이 파일은 그대로 재사용하고, 조명 wrapper(셰이더)만 교체.
//
//   버퍼: SHPacked = float4 × 7 (=112B, D3D11 StructuredBuffer 정렬).
//     C# SHInstanceBuffer.SHPacked.Pack 과 반드시 동일:
//       p0=(c0.rgb, c1.r)  p1=(c1.gb, c2.rg)  p2=(c2.b, c3.rgb)
//       p3=(c4.rgb, c5.r)  p4=(c5.gb, c6.rg)  p5=(c6.b, c7.rgb)  p6=(c8.rgb, pad)
//   계수 순서(Unity ShadeSH9 정합):
//       c0=Y00, c1..3=Y1(y,z,x), c4..8=Y2(xy, yz, 3z²-1, xz, x²-y²)
// ============================================================================

struct SH9Coeffs { float3 c0, c1, c2, c3, c4, c5, c6, c7, c8; };

// C# SHPacked(SHInstanceBuffer.cs) 미러: 7×float4 (=112B). StructuredBuffer<SHPackedGPU> 로
// 프로브 1개 단위(stride 112)로 접근할 때 사용(예: SHEvalProbe 검증 셰이더).
// 인스턴싱 경로(InstancedSH_*)는 StructuredBuffer<float4>(stride 16, iid*7) 를 쓰므로 이 struct 불필요.
struct SHPackedGPU { float4 p0, p1, p2, p3, p4, p5, p6; };

// 7×float4 → 9계수 (SHPacked.Unpack 미러)
SH9Coeffs UnpackSH9(float4 p0, float4 p1, float4 p2, float4 p3, float4 p4, float4 p5, float4 p6)
{
    SH9Coeffs s;
    s.c0 = float3(p0.x, p0.y, p0.z);
    s.c1 = float3(p0.w, p1.x, p1.y);
    s.c2 = float3(p1.z, p1.w, p2.x);
    s.c3 = float3(p2.y, p2.z, p2.w);
    s.c4 = float3(p3.x, p3.y, p3.z);
    s.c5 = float3(p3.w, p4.x, p4.y);
    s.c6 = float3(p4.z, p4.w, p5.x);
    s.c7 = float3(p5.y, p5.z, p5.w);
    s.c8 = float3(p6.x, p6.y, p6.z);
    return s;
}

// 노멀 n(정규화) 방향 Lambert 조도 재구성 E(n). SH9.Evaluate 와 동일 상수·순서·음수 클램프.
float3 EvaluateSH9(SH9Coeffs s, float3 n)
{
    // 실수 SH 기저 상수 (SH9.cs k0..k2c 동일)
    const float k0  = 0.2820948;   // 1/(2√π)
    const float k1  = 0.4886025;   // √(3/4π)
    const float k2a = 1.0925484;   // √(15/4π)
    const float k2b = 0.3153916;   // √(5/16π)
    const float k2c = 0.5462742;   // √(15/16π)
    // 코사인 로브 컨볼루션 밴드 계수 (SH9.cs A0/A1/A2 동일)
    const float A0 = 3.1415927;    // π
    const float A1 = 2.0943952;    // 2π/3
    const float A2 = 0.7853982;    // π/4
    // 디링잉 밴드 윈도우 (SH9.cs W1/W2 동일). L2 truncation ringing 억제 →
    // 고대비 입사에서 채널이 음수로 overshoot→클램프되어 단색(빨강)으로 무너지는 것 방지.
    const float W1 = 1.0;          // L1 유지
    const float W2 = 0.5;          // L2 절반 감쇠(주 링잉원)

    float x = n.x, y = n.y, z = n.z;
    float y0 = k0;
    float y1 = k1 * y;
    float y2 = k1 * z;
    float y3 = k1 * x;
    float y4 = k2a * x * y;
    float y5 = k2a * y * z;
    float y6 = k2b * (3.0 * z * z - 1.0);
    float y7 = k2a * x * z;
    float y8 = k2c * (x * x - y * y);

    float3 e = s.c0 * (A0 * y0)
             + (s.c1 * (A1 * y1) + s.c2 * (A1 * y2) + s.c3 * (A1 * y3)) * W1
             + (s.c4 * (A2 * y4) + s.c5 * (A2 * y5) + s.c6 * (A2 * y6) + s.c7 * (A2 * y7) + s.c8 * (A2 * y8)) * W2;
    return max(float3(0.0, 0.0, 0.0), e);
}

// StructuredBuffer(float4 뷰, 인스턴스당 7개)에서 인스턴스 SH 읽어 평가 (단일 프로브)
float3 EvaluateInstanceSH(StructuredBuffer<float4> buf, uint instanceID, float3 n)
{
    uint b = instanceID * 7u;
    SH9Coeffs s = UnpackSH9(buf[b + 0u], buf[b + 1u], buf[b + 2u],
                            buf[b + 3u], buf[b + 4u], buf[b + 5u], buf[b + 6u]);
    return EvaluateSH9(s, n);
}

// 헬퍼: buf 의 프로브 시작 인덱스(float4 단위)에서 SH9 언팩
SH9Coeffs UnpackProbe(StructuredBuffer<float4> buf, uint b0)
{
    return UnpackSH9(buf[b0 + 0u], buf[b0 + 1u], buf[b0 + 2u],
                     buf[b0 + 3u], buf[b0 + 4u], buf[b0 + 5u], buf[b0 + 6u]);
}

// 2-프로브(상단=probe0, 하단=probe1) 블렌드 (임의 축 upW). 인스턴스당 SH 2세트를
// upW(월드공간, 정규화 가정) 축으로 보간해 단일 프로브가 못 담는 수직(하늘↔바닥) 음영을 복원.
//   레이아웃: base = instanceID*14, top = base..base+6, bottom = base+7..base+13.
//   블렌드: w = saturate(dot(n, upW)*0.5+0.5)  (n∥upW→top, n∥-upW→bottom, 직교는 선형).
//   upW = 인스턴스 로컬 up 의 월드 방향 → 임의 3축 회전에서도 상/하 프로브 배치와 정합.
float3 EvaluateInstanceSH2Axis(StructuredBuffer<float4> buf, uint instanceID, float3 n, float3 upW)
{
    uint bt = instanceID * 14u;    // top  probe base
    uint bb = bt + 7u;             // bottom probe base
    float3 eTop = EvaluateSH9(UnpackProbe(buf, bt), n);
    float3 eBot = EvaluateSH9(UnpackProbe(buf, bb), n);
    float w = saturate(dot(n, upW) * 0.5 + 0.5);
    return lerp(eBot, eTop, w);
}

// 월드 +Y 고정 축 위임(Y회전만 있는 씬 하위호환). 거동은 기존 EvaluateInstanceSH2 와 동일.
float3 EvaluateInstanceSH2(StructuredBuffer<float4> buf, uint instanceID, float3 n)
{
    return EvaluateInstanceSH2Axis(buf, instanceID, n, float3(0.0, 1.0, 0.0));
}

#endif // HUSKY_EVALUATE_SH9_INCLUDED
