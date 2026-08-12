# 07 — G4 ~ G6: GPU Compute

> 상태: **✅ 완료 · 검증** (결정 ⑩ 2단계)
> 실측: G4 5000레이 miss 0 · G5 mean ~1e-9 · G6 653k 텍셀 MATCH + **7.2× 가속** · 실 씬 시각 검증 통과

---

## 0. 전략 — "Burst를 정답으로 확정하고 GPU가 그걸 미러"

G0~G3에서 알고리즘이 확정됐으므로, GPU 작업은 **HLSL 이식**으로 축소된다.
`PathTrace.compute` 헤더가 미러 대상을 명시적으로 나열한다:

```hlsl
// Shaders/Resources/PathTrace.compute:5
//   ground truth(정확 미러 대상):
//     RadianceCore.cs                  (Rng / CosineHemisphere / EvaluateDirect)
//     Burst/BurstAO.cs                 (AO)
//     Burst/BurstDirect.cs             (Direct)
//     Burst/BurstIndirect.cs           (Indirect 경로추적 루프)
//     Burst/BurstSky.cs                (Sky)
//     Burst/Occluders/BurstTwoLevelBVH.cs (ClosestHit 위치/노멀/알베도)
```

정밀도 규칙:

```hlsl
// Shaders/Resources/PathTrace.compute:25
// 정밀도: 전 경로 32-bit float. RNG 상태는 uint(xorshift32) → CPU 와 비트동일.
//   half/min16float 금지(그레이징 레이·인덱스 발산).
//
// ⚠️ 결정론 주의: RNG(uint)는 비트동일하나 sqrt/sin/cos(초월함수)는 GPU 하드웨어가
//   CPU(Mathf)와 미세하게 다르다 → Indirect/AO 는 per-texel 비트동일 불가, 통계(mean/ε)로 검증.
//   Direct 는 무작위 없음 → 타이트 일치 기대.
```

---

## 1. G4 — GPU 2단 BVH 순회

### 1.1 `GpuScene` — POD → ComputeBuffer

`BurstScene`(SoA POD)을 **명시적 GPU struct로 재패킹**한다. 재패킹 정책 2가지가 핵심 트랩 회피다.

```csharp
// Runtime/LightmapEvaluate/Gpu/GpuScene.cs:12
/// 재패킹 정책(트랩 회피):
///  ① 노드/삼각형은 명시적 GPU struct(GpuNode/GpuTri)로 재패킹 →
///     NativeArray<BVH.Node>/<Tri> 의 Vector3 정렬 모호성 제거. stride 32/36.
///  ② 행렬은 float4x4 를 통째로 올려 mul() 에 의존하지 않는다(column-major 트랩 회피).
///     Unity Matrix4x4.GetRow(r) 를 3행만 업로드 →
///     셰이더가 MultiplyPoint3x4/MultiplyVector 를 명시적 dot 으로 재현.
```

```csharp
// Runtime/LightmapEvaluate/Gpu/GpuScene.cs:26
[StructLayout(LayoutKind.Sequential)]
public struct GpuNode      { public Vector3 bmin, bmax; public int leftFirst, count;  public const int Stride = 32; }
public struct GpuTri       { public Vector3 v0, v1, v2;                                public const int Stride = 36; }
public struct GpuInstance  { public Vector4 w2lRow0, w2lRow1, w2lRow2; public int meshIndex, pad0, pad1, pad2;
                                                                                       public const int Stride = 64; }
public struct GpuInstNormal{ public Vector4 n2wRow0, n2wRow1, n2wRow2;                 public const int Stride = 48; }
```

행렬 업로드 — `mul()` 대신 **행 3개를 명시**:

```csharp
// Runtime/LightmapEvaluate/Gpu/GpuScene.cs:126
Matrix4x4 w2l = s.instWorldToLocal[i];
arr[i] = new GpuInstance
{
    w2lRow0 = w2l.GetRow(0),   // (m00, m01, m02, m03)
    w2lRow1 = w2l.GetRow(1),
    w2lRow2 = w2l.GetRow(2),
    meshIndex = s.instBlas[i],
};
```

셰이더가 Unity 시맨틱을 명시적으로 재현한다:

```
MultiplyPoint3x4(o) = dot(row.xyz, o) + row.w
MultiplyVector(d)   = dot(row.xyz, d)
```

> **왜 `mul()`을 안 쓰는가**: HLSL `mul(matrix, vector)`는 행렬 major 해석에 의존한다.
> Unity `Matrix4x4`를 그대로 올리면 column-major 해석 차이로 조용히 틀린 값이 나온다.
> 행 단위 `dot`으로 쓰면 **해석 여지가 사라진다**.

바인딩은 2단계로 분리 — G4 순회 커널은 조명 버퍼를 선언하지 않으므로 건드리지 않는다:

```csharp
// Runtime/LightmapEvaluate/Gpu/GpuScene.cs:199
public void Bind(ComputeShader cs, int kernel)          // 순회 SRV 10종 + _TlasCount
public void BindLighting(ComputeShader cs, int kernel)  // G5: _InstNormals, _MeshAlbedo
```

### 1.2 `BvhTraverse.compute`

`CSClosestHit` / `CSOccluded` 커널(numthreads 64).
`BurstTwoLevelBVH.IntersectInstanced` / `Occluded` **비트 충실 미러**:

| 요소 | 미러 내용 |
|---|---|
| `RayAABB` | flat 박스 `<` / `>=` 부등호 그대로 |
| `RayTri` | Möller–Trumbore, `det` 1e-12, `tt <= tmin \|\| tt >= tmax` |
| `IntersectBlas` | 삼각형 인덱싱 `blasTriStart[mesh] + orig` |
| 스택 | fixed `stack[BVH_STACK]` |

### 1.3 검증 — `GpuBvhCompareTests`

**`BurstSceneTests.EquivFuzz`와 동일 씬**(3메시 · 20인스턴스 · seed 424242 · 회전+비균등 스케일)
**동일 레이**(seed 7)를 재사용한다. GPU 디스패치 → `GetData` → `BurstTwoLevelBVH` 정답과 대조.

| 항목 | 실측 |
|---|---|
| Valid / Occluded 불일치 | **0 / 5000** |
| T 불일치 | 0 (maxErr ≈ **1.1e-5**) |
| 삼각형 인덱스 hard-miss | **0** (near-tie도 0) |

비균등 스케일 20인스턴스에서 **삼각형 인덱스까지 정확 일치** →
행렬 major · stride · 순회가 전부 정합함이 확정됐다.

`supportsComputeShaders` 가드 + `finally` 정리 포함. ε = 1e-3, 인덱스 불일치는 `|Tcpu-Tgpu| < ε` near-tie만 허용.

---

## 2. G5 — GPU 경로추적

`PathTrace.compute` — G4 순회 프리미티브를 **복사 재사용**(`.compute`는 include 불가)하고 그 위에 라이팅을 얹는다.

### 2.1 RNG — CPU와 비트동일

```hlsl
// Shaders/Resources/PathTrace.compute:399
uint RngInit(uint seed) { return seed == 0u ? 1u : seed; }

float RngNext(inout uint s)
{
    s ^= s << 13;
    s ^= s >> 17;
    s ^= s << 5;
    return float(s & 0x00FFFFFFu) / 16777216.0;   // 분모 2^24, 분자 ≤ 2^24-1 → fp32 정확
}
```

### 2.2 CosineHemisphere — 호출 순서 고정

```hlsl
// Shaders/Resources/PathTrace.compute:410
float3 CosineHemisphere(float3 n, inout uint s)
{
    float r1 = RngNext(s);        // r1 먼저, r2 다음 (호출 순서 고정)
    float r2 = RngNext(s);
    float st = sqrt(r1);
    float phi = 2.0 * PI * r2;
    ...
    float3 up = (abs(n.x) < 0.9) ? float3(1,0,0) : float3(0,1,0);
    float3 t = normalize(cross(n, up));
    float3 b = cross(n, t);
    return normalize(t * lx + b * ly + n * lz);
}
```

### 2.3 ClosestHit — tmin 매개변수화 (SH-G에서 추가)

```hlsl
// Shaders/Resources/PathTrace.compute:376
bool ClosestHitNA(float3 o, float3 d, float tmin, out float3 pos, out float3 nrm, out float3 albedo)
{
    float bestT; int inst, mesh, tri;
    if (!TlasClosest(o, d, tmin, INF, bestT, inst, mesh, tri)) return false;

    pos = o + d * bestT;

    GpuTri t3 = _BlasTris[_BlasTriStart[mesh] + tri];
    float3 localN = normalize(cross(t3.v1 - t3.v0, t3.v2 - t3.v0));
    float3 wn = normalize(MulNormal(_InstNormals[inst], localN));
    if (dot(wn, d) > 0.0) wn = -wn;                 // 레이 향함
    nrm = wn;

    albedo = _MeshAlbedo[mesh];
    return true;
}
```

`tmin`을 인자로 뺀 이유: 경로추적(`EvalIndirect`)은 **0.0**(G5 거동 보존), SH 베이크는 **1e-4**(`BurstSHBaker` 규약).

### 2.4 경로추적 루프 — `EvalIndirect`

```hlsl
// Shaders/Resources/PathTrace.compute:453
float3 EvalIndirect(float3 p, float3 n, inout uint s)
{
    float3 sum = float3(0,0,0);

    [loop] for (int sp = 0; sp < _IndirectSamples; sp++)
    {
        float3 acc = float3(0,0,0), tp = float3(1,1,1);
        float3 dir = CosineHemisphere(n, s);
        float3 o   = p + n * _RayBias;

        // 바운스 루프: b+1>=_MaxBounces 또는 미스 또는 RR 로 반드시 종료(캡은 안전장치)
        [loop] for (int b = 0; b < 1024; b++)
        {
            float3 hp, hn, alb;
            if (!ClosestHitNA(o, dir, 0.0, hp, hn, alb))
            {
                acc += tp * SkyRadiance(dir) * PI;      // 미스: 하늘(π)
                break;
            }

            float3 eD = DirectNEE(hp, hn);
            acc += tp * (alb * eD);
            tp = tp * alb;                              // throughput *= ρ

            if (b + 1 >= _MaxBounces) break;

            if (b + 1 >= _RRStartDepth)
            {
                float pSurv = clamp(max(tp.x, max(tp.y, tp.z)), 0.05, 1.0);
                if (RngNext(s) > pSurv) break;
                tp /= pSurv;
            }

            dir = CosineHemisphere(hn, s);
            o = hp + hn * _RayBias;
        }
        sum += acc;
    }
    return sum / (float)_IndirectSamples;
}
```

`BurstIndirect.IndirectJob`과 **줄 단위로 대응**한다. `b < 1024` 캡은 GPU에서 `[loop]` 종료를 보장하기 위한 안전장치일 뿐이다.

### 2.5 커널 4종

| 커널 | 출력 | 미러 대상 |
|---|---|---|
| `CSAO` | `_AoOut[i]` (float) | `BurstAO.AoJob` — `o=p+n*1e-3`, `AO = 1 - occ/N` |
| `CSDirect` | `_DirectOut[i]` (float3) | `BurstDirect.DirectJob` |
| `CSIndirect` | `_IndirectOut[i]` (float3) | `BurstIndirect.IndirectJob` |
| **`CSRadiance`** | `_RadianceOut[i]` (float3) | `BurstRadianceBaker.Bake` (Direct + Indirect, **1디스패치·1readback**) |

`CSRadiance`의 RNG 순서 계약:

```hlsl
// Shaders/Resources/PathTrace.compute:560
void CSRadiance(uint3 id : SV_DispatchThreadID)
{
    ...
    float3 direct = DirectNEE(p, n);        // = CSDirect 본문 (RNG 미소비)
    uint s = RngInit(_Seeds[i]);            // Indirect 만 시드 초기화
    float3 indirect = EvalIndirect(p, n, s);
    _RadianceOut[i] = direct + indirect;
}
```

Burst도 "Direct 계산 후 `new Rng(Seeds[i])`" 순서라 **호출 순서가 일치**한다.

### 2.6 검증 — `GpuRadianceCompareTests`

기준이 G4와 **다르다**: GPU 초월함수 발산 때문에 항목별로 다른 기준을 쓴다.

| 항목 | 기준 | 실측 (n=144, spp 64, 4바운스) |
|---|---|---|
| Direct | 무작위 없음 → 타이트 `over(1e-3) = 0` | mean **8.3e-10**, max 3.0e-8, over **0** |
| AO | 통계(mean/ε) | mean **0**, max **0** (완전일치) |
| Indirect | 통계(mean/ε) | mean **2.5e-9**, max 1.2e-7, over(5e-2) **0** |

→ 예상했던 "통계적 발산"이 아니라 **사실상 비트동일**이 나왔다(coarse 씬 + 근접 초월함수).

---

## 3. G6 — 통합 · 최적화

### 3.1 G6-1: X4714 순회 레지스터 압박 해소

**증상**: `CSIndirect` 컴파일 시 X4714 경고(성능 advisory, 정확성 무관).

**원인**: `ClosestHitNA` → `TlasClosest(stack)` + 중첩 `IntersectBlas(stack)` 가 **동시 live**
→ `int stack[64] × 2 = 512B/스레드` × 64 threads = indexable temp가 권장치 16384 초과.

**해법**: 스택 배열 크기만 축소.

```hlsl
// Shaders/Resources/PathTrace.compute:44
// 이 iterative BVH 순회는 내부노드마다 두 자식을 push(하나 pop) → 스택 최대 점유 = 트리 높이.
// SAH 빌드(LeafMax=4)에서 균형 트리 높이 H=32 ⇒ ~2^31·4≈86억 삼각형까지 커버 → 실 씬에 충분한 마진.
// CPU/Burst 레퍼런스는 stackalloc int[64]지만, GPU는 CSIndirect 의 스택이 동시 live 라
// 64 threads × indexable-temp 가 권장치 16384 초과(X4714).
// 32 로 절반 축소 → 권장치 이하(48 이미 통과, 32 는 추가 헤드룸). 순회 로직/결과는 무변경.
#define BVH_STACK 32
```

**중요**: 반복형 순회의 최대 스택 = **트리 높이**(2^높이가 아님). 실 씬 TLAS는 9노드 수준으로 매우 얕다.

> ⚠ **남은 위험**: CPU/Burst 레퍼런스는 `stack[64]`다.
> 높이 33~64인 **초대형 불균형 메시**에서는 GPU만 오버플로할 수 있다.
> 프로덕션 배선 시 재검토 항목(48도 X4714 무경고 확인됨).

검증: fxc `/T cs_5_0`(Unity d3d11 백엔드)로 before YES → after 없음. 5커널 rc=0.
에디터 회귀: G4 4/4 · G5 3/3 **이전과 비트동일**.

### 3.2 G6-2: 실 백엔드 배선

두 축의 변경:

**① 명시 시드 전환** — 디스패치 인덱스 기반 `_BaseSeed + i*const` → `StructuredBuffer<uint> _Seeds` 읽기.
Burst의 `seed + li*const`(**lumel 인덱스**) 시드를 GPU에도 명시 공급 → 백엔드 교차검증이 가능해진다.

**② `AtlasApplyDebug`에 `RadianceBackend.Gpu` 추가** — `BuildGiScene`에서 `_burstScene` + `GpuScene` + `PathTrace` 로드.
미지원/로드실패/커널 미발견 시 **Burst 자동 폴백**.

```csharp
// Samples/AtlasApplyDebug.cs:751  — BakeGiLumelsBurst 미러
Vector3[] BakeGiLumelsGpu(LumelMap lm)
{
    var idx = new List<int>(total);
    for (int li = 0; li < total; li++) if (lm.Valid[li]) idx.Add(li);   // valid lumel 수집
    ...
    for (int k = 0; k < n; k++)
    {
        int li = idx[k];
        Vector3 wn = lm.WorldNormal[li];
        pts[k]   = lm.WorldPos[li] + wn * surfaceBias;   // BakeGiLumelsBurst 와 동일 원점
        nrm[k]   = wn;
        seeds[k] = seed + (uint)li * 2654435761u;        // Burst/CPU 와 동일 시드
    }
    var rad = DispatchRadianceGpu(_gpuScene, _pathCS, _kRadiance, _sun, _burstSky, _giQ, pts, nrm, seeds, n, _gpuIo);
    for (int k = 0; k < n; k++) result[idx[k]] = rad[k];   // li 인덱스로 산란
    return result;
}
```

**검증(에디터 실측)**: 653,780 텍셀(spp 32 · 2바운스) — meanDiff **3e-7**, over(1/255) **7건(0.001%)** → **MATCH**.
실 파이프라인 전체(`BuildGiScene` → `BakeGiLumelsGpu` → `CSRadiance` → 산란)를 Burst 백엔드와 대조했으므로
배선·시드 정합·`CSRadiance`가 전부 확정된다(G5 개별 커널 재검증을 포괄).

**속도**: 653k 텍셀 실 씬 — Burst 3134ms → GPU **514ms (6.1×)**.
(인스턴스별 동기 `GetData` 오버헤드를 포함하고도.)

### 3.3 G6-3: 영구 버퍼 + AsyncGPUReadback

인스턴스별 `ComputeBuffer` 5개 alloc/dispose를 **재사용 홀더**로 제거.

```csharp
// Samples/AtlasApplyDebug.cs:783
sealed class GpuIoBuffers : System.IDisposable
{
    public ComputeBuffer Points, Normals, Valid, Seeds, Radiance;
    public uint[] ValidScratch;   // 항상 1u — 재할당 시 1회만 채움(매 호출 new uint[n] GC 제거)
    int _capacity;

    public void Ensure(int n)     // capacity < n 일 때만 재할당(그 외 no-op)
    {
        if (Points != null && n <= _capacity) return;
        Dispose();
        int cap = Mathf.NextPowerOfTwo(Mathf.Max(1, n));
        ...
        for (int i = 0; i < cap; i++) ValidScratch[i] = 1u;
    }
}
```

**데이터 경로 불변성 보장 3중 장치**:

```csharp
// Samples/AtlasApplyDebug.cs:828
io.Points.SetData(pts, 0, 0, n);       // ① 앞 n개만 업로드(count 지정 오버로드)
...
cs.SetInt("_Count", n);                // ② 커널 가드 → 초과 스레드 무시
int groups = (n + 63) / 64;
cs.Dispatch(kernel, groups, 1, 1);
...
var req = AsyncGPUReadback.Request(io.Radiance, n * 12, 0);   // ③ 앞 n*12 바이트만 요청
req.WaitForCompletion();
if (req.hasError) { io.Radiance.GetData(result, 0, 0, n); }   // 드문 실패 시 동기 폴백
else             { req.GetData<Vector3>().CopyTo(result); }
```

→ 재사용 버퍼의 잔여 구간 `[n, capacity)`를 **읽지도 쓰지도 않으므로** 오염이 원천 차단된다.
커널·`GpuScene`·`PathTrace`·시드·pts·uniform은 전부 무변경.

**검증**: 653k 텍셀 meanDiff **3e-7**, over(1/255) 0.001% — G6-2와 **동일 수치**(정확성 보존).
**속도**: GPU 514ms → **440ms (~14%↓)**, Burst 3165ms 대비 **7.2×**.

**수정된 버그**: Backend Diff 메뉴의 로컬 `gpuIo` 미해제 → 호출당 `ComputeBuffer` 5개 누수. `finally` 해제 추가.

### 3.4 G6-4(후속, 미착수): 단일 글로벌 디스패치

전 인스턴스 lumel을 1회 수집 → 1디스패치 → 1readback.
베이크 루프의 2단계 재구조화가 필요해 별도 과제로 남았다. G6-3보다 이득이 클 것으로 예상.

---

## 4. 실 씬 시각 검증 (v14.8)

`AtlasApplyDebug` "Bake & Apply"(mode = RadianceGI, **radianceBackend = Gpu**)로
프리미티브 18~19개 + 바닥 플레인 베이크.

```
로그: 18 insts → atlas 2048² util 43.1%, stitch(T1=672, T2=714), dilate=4×
      폴백 경고 없음 → GPU 백엔드 실사용 확정
```

**시각 결과**: 바닥에 방향성 occlusion 그림자 + **그림자 = 하늘(파랑) 간접광 채움 · 조명면 = 태양(따뜻)**
→ 직사광 + 하늘 GI의 전형. 표면 MC 노이즈(spp 32 · 2바운스)로 경로추적 조도 적용이 확인된다.

**결정적 검증**: 씬의 실시간 Directional Light를 **OFF** 해도 조명·그림자가 유지된다.
→ 실시간 기여가 0인 상태에서 보이는 전부가 **순수 라이트맵 베이크**임이 확정.

### GPU 트랙 전 층위 검증 완료

| 층위 | 검증 | 결과 |
|---|---|---|
| 순회 | G4 ≡ Burst | 5000레이 miss 0 |
| 경로추적 | G5 ≡ Burst | mean ~1e-9 |
| 실 백엔드 | G6 ≡ Burst | 653k 텍셀 MATCH, 7.2× |
| 실 씬 시각 | 라이트 OFF 유지 | 순수 베이크 확정 |

---

## 5. 성능 요약

| 백엔드 | 653k 텍셀(spp 32 · 2바운스) | 배수 |
|---|---|---|
| Burst | 3134 ~ 3165 ms | 1× |
| GPU (G6-2, 동기 GetData) | 514 ms | **6.1×** |
| GPU (G6-3, 재사용버퍼 + AsyncReadback) | **440 ms** | **7.2×** |

> ⚠ **소형 씬 주의**: `DispatchRadianceGpu`는 인스턴스별 디스패치라 오버헤드가 있다.
> 소형 씬에서는 GPU가 오히려 느릴 수 있고, 이득은 **대형 씬 / 고 spp**에서 나온다.

---

## 6. 파일 맵

| 파일 | 줄 | 역할 |
|---|---|---|
| [`Shaders/Resources/BvhTraverse.compute`](../../Shaders/Resources/BvhTraverse.compute) | 358 | G4 — `CSClosestHit` / `CSOccluded` |
| [`Shaders/Resources/PathTrace.compute`](../../Shaders/Resources/PathTrace.compute) | 659 | G5/G6/SH-G — `CSAO`/`CSDirect`/`CSIndirect`/`CSRadiance`/`CSSHBake` |
| [`Runtime/LightmapEvaluate/Gpu/GpuScene.cs`](../../Runtime/LightmapEvaluate/Gpu/GpuScene.cs) | 238 | POD → ComputeBuffer 재패킹 |
| [`Runtime/LightmapEvaluate/Gpu/GpuSHBaker.cs`](../../Runtime/LightmapEvaluate/Gpu/GpuSHBaker.cs) | 71 | SH-G 디스패치 헬퍼 |
| [`Tests/GpuBvhCompareTests.cs`](../../Tests/GpuBvhCompareTests.cs) | 224 | G4 검증 |
| [`Tests/GpuRadianceCompareTests.cs`](../../Tests/GpuRadianceCompareTests.cs) | 323 | G5 검증 |
| [`Tests/GpuSHBakeCompareTests.cs`](../../Tests/GpuSHBakeCompareTests.cs) | 176 | SH-G 검증 |

> compute 셰이더는 `Shaders/Resources/`에 배치되어 `Resources.Load`로 로드된다.
> (v0.14.0 UPM 전환 시 하드코딩 `AssetDatabase` 절대경로에서 변경 — 패키지 소비자 환경 대응.)

---

## 7. 알려진 주의사항

| 항목 | 내용 |
|---|---|
| `.compute` include 불가 | G4 순회 프리미티브를 `PathTrace.compute`에 **복사** — 수정 시 두 파일 동기화 필요 |
| `BVH_STACK 32` vs CPU 64 | 높이 33~64 초대형 불균형 메시에서 GPU만 오버플로 가능 |
| 인스턴스별 동기 디스패치 | 소형 씬에서 GPU가 느릴 수 있음 |
| fxc vs dxc | dxc는 SM 6.0 승격으로 X4714 재현 불가 → **fxc `/T cs_5_0`** 로 검증해야 Unity d3d11 백엔드와 일치 |
| `_MeshAlbedo` 모드 A 고정 | GPU 경로는 per-mesh 알베도만 지원(모드 B 미이식) |
