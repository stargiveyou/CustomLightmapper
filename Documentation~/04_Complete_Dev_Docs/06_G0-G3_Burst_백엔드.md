# 06 — G0 ~ G3: Burst 백엔드

> 상태: **✅ 완료 · 검증** (결정 ⑩ 1단계)
> 실측: `BurstSceneTests` 20인스턴스 × 5000레이 **비트동일** / `RadianceGI Backend Diff` **77만 텍셀 mean ≈ 0, over(1/255) = 1**

---

## 0. 왜 Burst 먼저인가

핵심 병목 = **텍셀별 경로추적**(수백만 텍셀 × spp × 바운스 × BVH 순회). UV·패킹은 일회성이라 대상이 아니다.

순서: **Burst(저위험) → GPU(대규모)**.

| 이유 | 내용 |
|---|---|
| 저위험 | 새 셰이더 언어 불필요, `NativeArray` 재사용 |
| 큰 이득 | 관리형 대비 수 배 |
| **GPU 사전검증** | 같은 알고리즘을 Burst에서 정답으로 확정 → **G5 compute가 그걸 미러**한다. GPU 포팅 시 "알고리즘이 틀린 건지 이식이 틀린 건지" 구분이 가능해진다 |

---

## 1. 설계 원칙 3가지

### ① POD 평탄화

Burst/GPU는 `IOccluder` / `ISky` / `IRadianceScene` **가상 디스패치가 불가능**하다.
→ 씬 전체를 blittable `NativeArray`(SoA)로 평탄화한다.

### ② 인터페이스는 ground truth로 보존

CPU 경로(`IOccluder` 등)는 **정답 기준**으로 그대로 남긴다. Burst/GPU는 **별도 POD 경로**.
백엔드는 enum(`CPU` / `Burst` / `Gpu`)으로 선택한다.

### ③ 프리미티브 재사용 → 비트동일

`BVH.RayAABB` / `RayGeometry.RayTri` / `Rng` / `RadianceCore.CosineHemisphere` 를
**Burst Job에서 그대로 호출**한다. 재구현하지 않는다.
→ G0·G1은 부동소수 오차조차 없는 **정확일치**가 된다.

### 접근자 패치 (가산 · 로직 무변경)

| 클래스 | 추가된 읽기전용 접근자 |
|---|---|
| `BVH` | `NodesRO`, `TriIdxRO`, `TrisRO` |
| `TwoLevelBVH` | `TlasRO`, `InstIdxRO`, `InstanceWorldToLocal(i)`, `InstanceNormalMatrix(i)`, `InstanceMesh(i)`, `Blas(m)` |

---

## 2. G0a — 단일레벨 static 순회 (`BurstBVH`)

[`BurstBVH.cs`](../../Runtime/LightmapEvaluate/Burst/Occluders/BurstBVH.cs) (103줄)

관리 타입·가상 디스패치 없이, 호출측이 `NodesRO`/`TriIdxRO`/`TrisRO`를 넘긴다.

```csharp
// Runtime/LightmapEvaluate/Burst/Occluders/BurstBVH.cs:18
public static Hit Intersect(in NativeArray<BVH.Node>.ReadOnly nodes,
                            in NativeArray<int>.ReadOnly triIdx,
                            in NativeArray<Tri>.ReadOnly tris, ...)
```

`BVH.RayAABB` · `RayGeometry.RayTri` 재사용 → **비트동일**.
전용 테스트는 분리하지 않고 `BurstSceneTests`로 통합 검증한다.

---

## 3. G0b — POD 씬 (`BurstScene`) ★ 핵심 작업량

### 3.1 구조 — BLAS concat + 오프셋

여러 메시의 BLAS를 **하나의 평탄 배열로 이어붙이고**, 메시별 시작 오프셋을 따로 든다.

```csharp
// Runtime/LightmapEvaluate/Burst/BurstScene.cs:12
public struct BurstScene : IDisposable
{
    // TLAS (월드)
    [ReadOnly] public NativeArray<BVH.Node> tlasNodes;
    public int tlasCount;
    [ReadOnly] public NativeArray<int> instIdx;          // TLAS 리프 슬롯 → 인스턴스

    // 인스턴스
    [ReadOnly] public NativeArray<Matrix4x4> instWorldToLocal;
    [ReadOnly] public NativeArray<Matrix4x4> instNormalMatrix;
    [ReadOnly] public NativeArray<int>       instBlas;

    // BLAS 연결 + 오프셋 (메시당 [START, COUNT])
    [ReadOnly] public NativeArray<BVH.Node> blasNodes;
    [ReadOnly] public NativeArray<int>      blasTriIdx;
    [ReadOnly] public NativeArray<Tri>      blasTris;
    [ReadOnly] public NativeArray<int>      blasNodeStart, blasNodeCount, blasTriIdxStart, blasTriStart;

    // G3: 메시별 알베도(모드 A). 미생성이면 ClosestHit 가 Fallback(0.5) 반환.
    [ReadOnly] public NativeArray<Vector3> meshAlbedo;
}
```

### 3.2 `[ReadOnly]`가 필수인 이유 (실제 버그)

```csharp
// Runtime/LightmapEvaluate/Burst/BurstScene.cs:14  — 코드 주석 원문
// 모든 NativeArray 는 잡 안에서 '읽기 전용'(BVH/씬 데이터). IJobParallelFor 에서 BurstScene 을
// 필드로 쓸 때 [ReadOnly] 가 없으면 병렬 writer 로 간주되어 임의 인덱스 접근이 막힌다
// (IndexOutOfRange: ReadWriteBuffers are restricted to the job index). 순회는 stack[] 으로
// 임의 노드/인스턴스/삼각형을 읽으므로 반드시 [ReadOnly] 표시.
```

BVH 순회는 본질적으로 **임의 인덱스 접근**이다. `IJobParallelFor`의 안전 시스템은
읽기/쓰기 배열의 접근을 `index`로 제한하므로, `[ReadOnly]` 없이는 순회가 런타임에 막힌다.

### 3.3 빌드

```csharp
// Runtime/LightmapEvaluate/Burst/BurstScene.cs:95
int no = 0, to = 0, tro = 0;
for (int m = 0; m < meshCount; m++)
{
    var bn = bvh.Blas(m).NodesRO; var bt = bvh.Blas(m).TriIdxRO; var br = bvh.Blas(m).TrisRO;
    s.blasNodeStart[m]   = no;  s.blasNodeCount[m] = bn.Length;
    s.blasTriIdxStart[m] = to;  s.blasTriStart[m]  = tro;
    for (int i = 0; i < bn.Length; i++) s.blasNodes[no + i]   = bn[i];
    for (int i = 0; i < bt.Length; i++) s.blasTriIdx[to + i]  = bt[i];
    for (int i = 0; i < br.Length; i++) s.blasTris[tro + i]   = br[i];
    no += bn.Length; to += bt.Length; tro += br.Length;
}
```

이 **오프셋 3종(node / triIdx / tri)** 이 GPU `GpuScene`에서도 그대로 `StructuredBuffer`가 된다.

---

## 4. G0b — 2단 순회 (`BurstTwoLevelBVH`)

`TwoLevelBVH`와 동일 로직의 static 함수. 차이는 **오프셋 적용**뿐이다.

```csharp
// Runtime/LightmapEvaluate/Burst/Occluders/BurstTwoLevelBVH.cs:16
private static bool IntersectBlas(BurstScene s, int mesh, Vector3 o, Vector3 d,
                                  float tmin, float t, out float hT, out int hTri)
{
    hT = t; hTri = 0;   // ← 들어온 best.T 를 초기 경계로(=managed BVH.Intersect).
                        //    0 으로 두면 모든 교차가 [tmin,0] 밖 → 전부 기각
    ...
    int triIdxBase = s.blasTriIdxStart[mesh];
    int triBase    = s.blasTriStart[mesh];
    int nodeBase   = s.blasNodeStart[mesh];
    ...
        BVH.Node node = s.blasNodes[nodeBase + stack[--sp]];       // 노드 오프셋
        ...
        int orig = s.blasTriIdx[triIdxBase + slot];                // triIdx 오프셋
        if (RayGeometry.RayTri(o, d, s.blasTris[triBase + orig], tmin, hT, out float h))
        { valid = true; hT = h; hTri = orig; }                      // tri 오프셋
}
```

> 🔴 **`hT` 초기화 누락 버그**(v11에서 수정): `hT = 0`으로 두면 모든 교차가 `[tmin, 0]` 밖으로 판정되어
> **closest-hit이 항상 실패**한다. G0 `IntersectInstanced`와 G3 `ClosestHit`을 동시에 막는 치명적 버그였다.
> 회귀 관찰점: 테스트에서 **`hits > 0`** 인지 확인 — `hits = 0`이면 재발 신호.

TLAS 순회는 관리형과 동일하며, 레이 로컬 변환도 동일하게 **비정규화 유지**:

```csharp
// Runtime/LightmapEvaluate/Burst/Occluders/BurstTwoLevelBVH.cs:83
Vector3 lo = w2l.MultiplyPoint3x4(o);
Vector3 ld = w2l.MultiplyVector(d);
if (IntersectBlas(s, mesh, lo, ld, tmin, best.T, out float hT, out int hTri) && hT < best.T) { ... }
```

`ClosestHit`은 `InstancedRadianceScene.ClosestHit`(모드 A)의 미러다.
면노멀은 저장하지 않고 **즉석 계산**한다(메모리 절약 · `BuildFaceNormals`와 동일 식):

```csharp
// Runtime/LightmapEvaluate/Burst/Occluders/BurstTwoLevelBVH.cs:192
Tri tri = s.blasTris[s.blasTriStart[h.MeshIndex] + h.MeshTriIndex];
Vector3 localN = Vector3.Cross(tri.V1 - tri.V0, tri.V2 - tri.V0).normalized;

Vector3 wn = TransformNormalToWorld(s, h.InstanceIndex, localN);   // (M⁻¹)ᵀ
if (Vector3.Dot(wn, d) > 0f) wn = -wn;
nrm = wn;

albedo = (s.meshAlbedo.IsCreated && (uint)h.MeshIndex < (uint)s.meshAlbedo.Length)
       ? s.meshAlbedo[h.MeshIndex] : new Vector3(0.5f, 0.5f, 0.5f);
```

---

## 5. G1 — AO Job

```csharp
// Runtime/LightmapEvaluate/Burst/BurstAO.cs:24
[BurstCompile]
public struct AoJob : IJobParallelFor
{
    public BurstScene scene;                     // 읽기 전용 사용(차폐 질의)
    [ReadOnly] public NativeArray<Vector3> Points, Normals;
    [ReadOnly] public NativeArray<bool>    Valid;
    public int   Samples;
    public uint  BaseSeed;
    public float MaxDist;
    [WriteOnly] public NativeArray<float> Ao;

    public void Execute(int index)
    {
        if (!Valid[index]) { Ao[index] = 0f; return; }
        uint seed = BaseSeed + (uint)index * 2654435761u;      // 베이크 시드 규약
        var rng = new Rng(seed);
        Vector3 n = Normals[index];
        Vector3 o = Points[index] + n * 1e-3f;                 // EvaluateAO 와 동일 하드코드

        int occ = 0;
        for (int s = 0; s < Samples; s++)
        {
            Vector3 d = RadianceCore.CosineHemisphere(n, ref rng);   // ← CPU 함수 그대로 호출
            if (BurstTwoLevelBVH.Occluded(scene, o, d, MaxDist)) occ++;
        }
        Ao[index] = 1f - ((float)occ / Samples);
    }
}
```

`Rng`·`CosineHemisphere`·`Occluded`를 전부 재사용하므로 **CPU `EvaluateAO`와 코드 동형**이다.
→ 전용 테스트를 만들지 않고 "코드 동형"으로 갈음했다(v12 결정).
실측에서도 GPU 대조 시 **AO mean 0, max 0(완전일치)** 이 나왔다.

---

## 6. G2 — Direct Job

[`BurstDirect.cs`](../../Runtime/LightmapEvaluate/Burst/BurstDirect.cs) — `DirectJob : IJobParallelFor` + `Compute` 헬퍼.
`EvaluateDirect`와 동일 식(L · ndl · 그림자 · 백페이스), 차폐만 `BurstTwoLevelBVH.Occluded`로 교체.
무작위가 없어 **타이트 일치**가 기대되고, 실측도 그렇다.

---

## 7. G3 ★ — Indirect Job (경로추적 + RR)

`RadianceCore.EvaluateIndirect`의 바운스 루프를 **그대로** 미러한다.

```csharp
// Runtime/LightmapEvaluate/Burst/BurstIndirect.cs:19
[BurstCompile]
public struct IndirectJob : IJobParallelFor
{
    public BurstScene Scene;
    public BurstSky   Sky;
    public DirectionalLight Sun;
    public BakeQualitySettings Q;
    [ReadOnly] public NativeArray<uint>    Seeds;      // per-texel 시드(베이크 규약)
    [ReadOnly] public NativeArray<Vector3> Points, Normals;
    [ReadOnly] public NativeArray<bool>    Valid;
    [WriteOnly] public NativeArray<Vector3> Indirect;

    public void Execute(int i)
    {
        if (!Valid[i]) { Indirect[i] = Vector3.zero; return; }

        var rng = new Rng(Seeds[i]);
        Vector3 n = Normals[i], p = Points[i], sum = Vector3.zero;

        for (int sp = 0; sp < Q.IndirectSamples; sp++)
        {
            Vector3 acc = Vector3.zero, tp = Vector3.one;
            Vector3 dir = RadianceCore.CosineHemisphere(n, ref rng);
            Vector3 o   = p + n * Q.RayBias;

            for (int b = 0; ; b++)
            {
                if (!BurstTwoLevelBVH.ClosestHit(Scene, o, dir, 0f, float.MaxValue,
                                                 out Vector3 hp, out Vector3 hn, out Vector3 alb))
                {
                    acc += Vector3.Scale(tp, Sky.Radiance(dir)) * Mathf.PI;   // 미스: 하늘(π)
                    break;
                }

                Vector3 eD = DirectAt(hp, hn);                       // 바운스 표면 직접 조도
                acc += Vector3.Scale(tp, Vector3.Scale(alb, eD));
                tp = Vector3.Scale(tp, alb);                         // throughput *= ρ

                if (b + 1 >= Q.MaxBounces) break;

                if (b + 1 >= Q.RRStartDepth)
                {
                    float pSurv = Mathf.Clamp(Mathf.Max(tp.x, Mathf.Max(tp.y, tp.z)), 0.05f, 1f);
                    if (rng.Next() > pSurv) break;
                    tp /= pSurv;
                }

                dir = RadianceCore.CosineHemisphere(hn, ref rng);
                o   = hp + hn * Q.RayBias;
            }
            sum += acc;
        }
        Indirect[i] = sum / (float)Q.IndirectSamples;
    }

    // EvaluateDirect 미러(그림자 레이 origin 은 1e-3 하드코드)
    readonly Vector3 DirectAt(Vector3 hp, Vector3 hn)
    {
        Vector3 L = -Sun.Direction.normalized;
        float ndl = Vector3.Dot(L, hn);
        if (ndl <= 0f) return Vector3.zero;
        if (BurstTwoLevelBVH.Occluded(Scene, hp + hn * 1e-3f, L, 1e30f)) return Vector3.zero;
        return Sun.Color * Sun.Intensity * ndl;
    }
}
```

> **CPU와 다른 점은 단 하나**: `scene.ClosestHit`(가상 호출) → `BurstTwoLevelBVH.ClosestHit`(static).
> 나머지 식·상수·RNG 소비 순서가 전부 동일하다.

### 진입점 2종 (시드 계약)

```csharp
// Runtime/LightmapEvaluate/Burst/BurstIndirect.cs:87   — per-texel 시드 배열 (베이크 규약 일치)
public static NativeArray<Vector3> Compute(..., NativeArray<uint> seeds, ...)

// Runtime/LightmapEvaluate/Burst/BurstIndirect.cs:112  — baseSeed 버전
// seed_i = baseSeed + i*2654435761u. Job 안전 시스템상 Seeds 필드는 스케줄 시 항상 할당돼야 하므로
// fallback 대신 '명시 배열'을 만든다.
public static NativeArray<Vector3> Compute(..., uint baseSeed, ...)
{
    var seeds = new NativeArray<uint>(n, Allocator.TempJob);
    for (int i = 0; i < n; i++) seeds[i] = baseSeed + (uint)i * 2654435761u;
    ...
}
```

이 **explicit-seeds 오버로드**가 나중에 GPU `_Seeds` StructuredBuffer와 동일 계약이 되어
G6-2 백엔드 교차검증을 가능하게 했다.

---

## 8. 하늘 POD 미러 — `BurstSky`

```csharp
// Runtime/LightmapEvaluate/Burst/BurstSky.cs:6
public struct BurstSky : ISky
{
    public int Type;        // 0 = uniform, 1 = gradient
    public Vector3 A, B;    // uniform: A = L | gradient: A = Top, B = Bottom

    public readonly Vector3 Radiance(Vector3 dir)
    {
        if (Type == 1)
        {
            float t = Mathf.Clamp01(dir.y * 0.5f + 0.5f);
            return Vector3.Lerp(B, A, t);      // Lerp(Bottom, Top, t)
        }
        return A;
    }

    public static BurstSky FromSky(ISky sky)   // ISky → POD 변환
    {
        if (sky is UniformSky  u) return Uniform(u.L);
        if (sky is GradientSky g) return Gradient(g.Top, g.Bottom);
        return Uniform(Vector3.zero);
    }
}
```

인터페이스 `ISky`를 **구현하면서 동시에 POD**다 — CPU 경로와 Burst 경로 양쪽에서 쓸 수 있다.
GPU `SkyRadiance`(`_SkyType` / `_SkyTop` / `_SkyBottom`)가 이 3필드를 그대로 받는다.

---

## 9. 합성 — `BurstRadianceBaker`

```csharp
// Runtime/LightmapEvaluate/Burst/BurstRadianceBaker.cs:14
public static NativeArray<Vector3> Bake(in BurstScene scene, BurstSky sky, DirectionalLight sun,
                                        BakeQualitySettings q,
                                        NativeArray<Vector3> points, NativeArray<Vector3> normals,
                                        NativeArray<bool> valid, NativeArray<uint> seeds,
                                        Allocator allocator)
{
    var direct = BurstDirect.Compute(scene, points, normals, valid, sun, allocator);
    var ind    = BurstIndirect.Compute(scene, sky, sun, q, points, normals, valid, seeds, allocator);

    var rad = new NativeArray<Vector3>(points.Length, allocator);
    for (int i = 0; i < points.Length; i++) rad[i] = direct[i] + ind[i];

    direct.Dispose(); ind.Dispose();
    return rad;
}

// CPU 백엔드(비교용) — 같은 시그니처
public static Vector3[] BakeCPU(IRadianceScene scene, ..., uint[] seeds) { ... }
```

**두 백엔드가 같은 파일에서 같은 시그니처로 노출**되므로 호출측(`AtlasApplyDebug`)이 토글만 하면 된다.

---

## 10. 베이크 통합 (`AtlasApplyDebug`)

```csharp
// Samples/AtlasApplyDebug.cs:469
Vector3[] giRad = (mode == BakeMode.RadianceGI)
    ? (_gpuReady ? BakeGiLumelsGpu(lumel) : (_burstReady ? BakeGiLumelsBurst(lumel) : null))
    : null;
BlitRegion(..., giRad, ...);   // null 이면 BlitRegion 이 인라인 CPU 로 폴백
```

- `BurstScene`은 인스턴스 루프 **밖에서 1회 구성**하고, 인스턴스별 lumel만 병렬 베이크한다.
- 폴백 체인: `GPU → Burst → CPU`(인라인).
- 검증 메뉴: `ContextMenu("RadianceGI Backend Diff (CPU vs Burst)")`

---

## 11. 수정된 버그 3종 (v11 전달분)

| # | 버그 | 증상 |
|---|---|---|
| ① | `BurstScene.meshAlbedo` Dispose 누락 | 종료 시 NativeArray 누수 경고 |
| ② | `IntersectBlas` `hT` 초기화 누락(=0) | **closest-hit 항상 실패** — G0 `IntersectInstanced` + G3 `ClosestHit` 동시 차단 |
| ③ | `BurstScene` NativeArray `[ReadOnly]` 누락 | `IJobParallelFor` 병렬 순회 시 `IndexOutOfRange` |

---

## 12. 모듈 독립화 (asmdef)

| 어셈블리 | 참조 |
|---|---|
| `HuskyLibs.CustomLightmapper`(Runtime) | `Unity.Burst`, `Unity.Collections`, `Unity.Mathematics` |
| `…Editor` | Editor 플랫폼 전용, 런타임 참조 |
| `…Tests` / `…Samples` | Editor 플랫폼 전용 → 소비자 빌드 제외 |

미사용 using 제거로 **Entities / UniTask / NativeTrees 의존 제거** 완료.
현재 UPM 패키지는 4분할(Runtime / Editor / Tests / Samples).

---

## 13. 검증

| 테스트 | 대상 | 실측 |
|---|---|---|
| [`BurstSceneTests`](../../Tests/BurstSceneTests.cs) (G0) | `BurstTwoLevel` ≡ 관리형 `TwoLevelBVH` | 3메시·20인스턴스(회전+비균등 스케일)·seed 424242 × 5000레이 — Valid/T/Instance/Mesh/Tri **전부 일치**, ΔT < 1e-4 |
| [`BurstRadianceCompareTests`](../../Tests/BurstRadianceCompareTests.cs) (G2/G3) | `BurstDirect`/`BurstIndirect` ≡ `RadianceCore` | per-texel 시드 동일 → maxDiff 사실상 0 |
| `AtlasApplyDebug.RadianceGiBackendDiffTest` | 실 GI 베이크 CPU vs Burst | **77만 텍셀 mean ≈ 0, over(1/255) = 1 → MATCH** |

비자명성 관찰점(실측게이트 §2): 결과가 **전부 0이 아닐 것**(`nonZero > 0`).
0만 나오면 씬/알베도/시드 연결 오류다.

---

## 14. 다음 단계

G0~G3에서 **알고리즘이 정답으로 확정**되었으므로, G4~G6은 "이 Burst 코드를 HLSL로 미러"하는 작업이 된다.
→ [07 — G4~G6 GPU Compute](07_G4-G6_GPU_Compute.md)
