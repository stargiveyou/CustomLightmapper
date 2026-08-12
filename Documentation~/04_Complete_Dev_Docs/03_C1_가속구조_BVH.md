# 03 — C1: 가속구조 (BVH / TwoLevelBVH)

> 상태: **✅ 완료 · 검증** (결정 ⑦⑧)
> 검증: `BVHTests` — 단일 Median/SAH 퍼즈 ≡ brute, 2단 TLAS/BLAS ≡ brute (5000레이, miss = 0)

---

## 0. 설계 원칙 (4가지)

이 4개가 이후 Burst(G0)·GPU(G4) 이식이 **비트동일**로 성립하게 만든 근거다.

1. **평탄 노드 배열** — `NativeArray<Node>`. Burst Job과 GPU `StructuredBuffer`가 **동일 레이아웃**을 그대로 쓴다.
2. **명시적 스택 순회(재귀 금지)** — `Span<int> stack = stackalloc int[64]`. GPU에 그대로 이식 가능.
3. **프리미티브 공유** — `RayGeometry.RayTri`를 BVH와 BruteForce가 **같이** 쓴다.
   → 교차검증이 "삼각형 교차"가 아니라 **"순회/컬링만"** 검사하게 된다.
4. **방향 비정규화 유지** — 2단에서 인스턴스 로컬로 변환한 방향을 정규화하지 않는다.
   → 파라메트릭 `T`가 월드와 일관 → `best.T`를 그대로 BLAS에 넘겨 가지치기해도 정확.

---

## 1. 공통 프리미티브 — `Occluder.cs`

```csharp
// Runtime/LightmapEvaluate/Occluders/Occluder.cs:6
public struct Tri { public Vector3 V0, V1, V2; }
public struct Hit { public bool Valid; public float T; public int TriIndex; }

public interface IOccluder
{
    Hit  Intersect(Vector3 o, Vector3 d, float tmin, float tmax);
    bool Occluded (Vector3 o, Vector3 d, float maxDist);
}
```

Möller–Trumbore 레이-삼각형:

```csharp
// Runtime/LightmapEvaluate/Occluders/Occluder.cs:23
public static bool RayTri(Vector3 o, Vector3 d, Tri t, float tmin, float tmax, out float hit)
{
    Vector3 e1 = t.V1 - t.V0, e2 = t.V2 - t.V0;
    Vector3 p  = Vector3.Cross(d, e2);
    float   det = Vector3.Dot(e1, p);
    if (Mathf.Abs(det) < 1e-12f) return false;      // 평행

    float inv = 1f / det;
    Vector3 tv = o - t.V0;
    float u = Vector3.Dot(tv, p) * inv;
    if (u < 0f || u > 1f) return false;

    Vector3 q = Vector3.Cross(tv, e1);
    float v = Vector3.Dot(d, q) * inv;
    if (v < 0f || u + v > 1f) return false;

    float tt = Vector3.Dot(e2, q) * inv;
    if (tt <= tmin || tt >= tmax) return false;     // ← 이 부등호가 Burst/GPU 미러의 계약
    hit = tt; return true;
}
```

> `det` 부호를 컬링하지 않으므로 **양면(double-sided)** 이다.
> 이 결정이 나중에 SH 렌더러의 `Cull Off`(v14.1) 필요성으로 이어진다 — 베이크가 양면이면 렌더도 양면이어야 정합.

`IOccluder` 구현체 3종:

| 구현 | 파일 | 용도 |
|---|---|---|
| `BruteForceOccluder` | [`BruteForceOccluder.cs:8`](../../Runtime/LightmapEvaluate/Occluders/BruteForceOccluder.cs#L8) | **ground truth**. 전 삼각형 선형 스캔 |
| `BVH` | [`BVH.cs:37`](../../Runtime/LightmapEvaluate/Occluders/BVH.cs#L37) | 단일레벨 (월드 또는 BLAS 로컬) |
| `TwoLevelBVH` | [`TwoLevelBVH.cs:17`](../../Runtime/LightmapEvaluate/Occluders/TwoLevelBVH.cs#L17) | 2단 TLAS/BLAS (인스턴싱) |

---

## 2. 단일레벨 BVH

### 2.1 노드 레이아웃

```csharp
// Runtime/LightmapEvaluate/Occluders/BVH.cs:45
public struct Node
{
    public Vector3 Min, Max;
    public int LeftFirst;   // 내부: 왼쪽 자식 인덱스(오른쪽 = +1) / 리프: _triIdx 시작 슬롯
    public int Count;       // 0 = 내부, >0 = 리프
}

NativeArray<Node> _nodes;
NativeArray<int>  _triIdx;
NativeArray<Tri>  _tris;

// G0: Burst/POD 경로용 읽기전용 접근 (인터페이스 없는 순회 함수가 사용)
public NativeArray<Node>.ReadOnly NodesRO  => _nodes.AsReadOnly();
public NativeArray<int>.ReadOnly  TriIdxRO => _triIdx.AsReadOnly();
public NativeArray<Tri>.ReadOnly  TrisRO   => _tris.AsReadOnly();
```

`Count`로 내부/리프를 구분하는 **단일 필드 판별** — GPU에서도 분기 하나로 처리된다.
`*RO` 접근자는 **G0 착수 시 가산된 패치**로, 순회 로직은 전혀 건드리지 않았다.

상수:

| 상수 | 값 | 의미 |
|---|---|---|
| `LeafMax` | 4 | 리프 최대 삼각형 수 (SAH가 더 큰 리프를 선호할 수 있어 4로 완화) |
| `SahBins` | 12 | binned SAH 빈 수 |
| `CTrav` | 1.0 | 순회 비용(상대). 리프 비용 = count × Cisect(=1) |
| `TlasLeafMax` | 2 | TLAS 리프 최대 인스턴스 수 |

### 2.2 빌드 — 반복형(스택) 하향식

```csharp
// Runtime/LightmapEvaluate/Occluders/BVH.cs:142
var stack = new Stack<int>();          // managed 빌드 스택 → 깊이 오버플로 없음
stack.Push(0);
while (stack.Count > 0)
{
    int ni = stack.Pop();
    Node node = nodes[ni];
    int first = node.LeftFirst, count = node.Count;
    if (count <= LeafMax) continue;

    int splitAt = (quality == BuildQuality.SAH)
        ? SahSplit(ctx, first, count, Area(node.Min, node.Max))
        : MedianSplit(ctx, first, count);

    if (splitAt < 0) continue;         // SAH가 '리프 유지'로 판단

    int li = nodes.Count, ri = li + 1;
    nodes.Add(new Node { LeftFirst = first,   Count = splitAt - first });
    nodes.Add(new Node { LeftFirst = splitAt, Count = first + count - splitAt });
    UpdateBounds(nodes, li, idx, triMin, triMax);
    UpdateBounds(nodes, ri, idx, triMin, triMax);

    node.LeftFirst = li; node.Count = 0;
    nodes[ni] = node;                  // ← struct 값 복사 함정: 반드시 되쓰기
    stack.Push(li); stack.Push(ri);
}
```

### 2.3 분할 전략 A — Median

가장 긴 centroid 축으로 정렬 후 **개수 반반**. 균형 보장, degenerate(한쪽 0개) 없음.

```csharp
// Runtime/LightmapEvaluate/Occluders/BVH.cs:316
static int MedianSplit(BuildCtx ctx, int first, int count)
{
    int axis = LongestCentroidAxis(ctx, first, count);
    ctx.cmp.Axis = axis;
    System.Array.Sort(ctx.idx, first, count, ctx.cmp);
    return first + count / 2;
}
```

`AxisComparer`는 동률 시 **인덱스로 tie-break** → 정렬이 결정적(재실행 동일 트리).

### 2.4 분할 전략 B — Binned SAH (12 bins)

비용식: `Cost = C_trav·SA(node) + SA(L)·N_L + SA(R)·N_R`

```csharp
// Runtime/LightmapEvaluate/Occluders/BVH.cs:329
static float Area(Vector3 min, Vector3 max)   // AABB 표면적
{
    Vector3 e = max - min;
    if (e.x < 0f || e.y < 0f || e.z < 0f) return 0f;   // 빈 박스
    return 2f * (e.x * e.y + e.y * e.z + e.z * e.x);
}
```

3축 × 12빈 스캔 → 좌/우 누적 면적·개수 → 최소 비용 분할면 선택:

```csharp
// Runtime/LightmapEvaluate/Occluders/BVH.cs:421
for (int i = 0; i < SahBins - 1; i++)
{
    if (leftCnt[i] == 0 || rightCnt[i] == 0) continue;
    float cost = leftArea[i] * leftCnt[i] + rightArea[i] * rightCnt[i];
    if (cost < bestCost) { bestCost = cost; bestAxis = axis; bestPos = lo + (i + 1) / scale; }
}

// 리프 비용과 비교(CTrav 포함). 분할 이득 없으면 리프 유지
float leafCost = count * nodeArea;
if (bestAxis < 0 || bestCost + CTrav * nodeArea >= leafCost) return -1;
```

분할 실행은 **in-place 파티션**(quicksort 스타일 양끝 스왑), 퇴화 시 median 폴백:

```csharp
// Runtime/LightmapEvaluate/Occluders/BVH.cs:440
int io = first, jo = end - 1;
while (io <= jo)
{
    if (ctx.centroid[ctx.idx[io]][bestAxis] < bestPos) io++;
    else { (ctx.idx[io], ctx.idx[jo]) = (ctx.idx[jo], ctx.idx[io]); jo--; }
}
if (io == first || io == end)   // degenerate → median 폴백
{
    ctx.cmp.Axis = bestAxis;
    System.Array.Sort(ctx.idx, first, count, ctx.cmp);
    return first + count / 2;
}
return io;
```

> **순회 코드는 Median/SAH 공통이다.** 분할 함수만 교체된다 — 기획 §6 "분할 함수만 교체(순회 코드 불변)" 계약 준수.

### 2.5 순회

```csharp
// Runtime/LightmapEvaluate/Occluders/BVH.cs:223
public Hit Intersect(Vector3 o, Vector3 d, float tmin, float tmax)
{
    Hit best = new Hit { Valid = false, T = tmax };
    if (_nodeCount == 0) return best;
    Vector3 invD = new Vector3(1f / d.x, 1f / d.y, 1f / d.z);

    Span<int> stack = stackalloc int[64];
    int sp = 0; stack[sp++] = 0;
    while (sp > 0)
    {
        Node node = _nodes[stack[--sp]];
        if (!RayAABB(o, invD, node.Min, node.Max, tmin, best.T)) continue;  // best.T로 가지치기
        if (node.Count > 0)
        {
            int end = node.LeftFirst + node.Count;
            for (int s = node.LeftFirst; s < end; s++)
            {
                int orig = _triIdx[s];
                if (RayGeometry.RayTri(o, d, _tris[orig], tmin, best.T, out float h))
                { best.Valid = true; best.T = h; best.TriIndex = orig; }
            }
        }
        else { stack[sp++] = node.LeftFirst; stack[sp++] = node.LeftFirst + 1; }
    }
    return best;
}
```

> `Hit.TriIndex`는 **원본 `Tri[]` 인덱스**(`_triIdx[s]`로 역참조한 값)를 반환한다.
> BruteForce와 동일 기준 → 교차검증에서 인덱스까지 비교 가능.

### 2.6 슬랩 테스트 — 부등호가 핵심

```csharp
// Runtime/LightmapEvaluate/Occluders/BVH.cs:470
public static bool RayAABB(Vector3 o, Vector3 invD, Vector3 bmin, Vector3 bmax, float tmin, float tmax)
{
    float t0 = (bmin.x - o.x) * invD.x, t1 = (bmax.x - o.x) * invD.x;
    if (invD.x < 0f) { float tmp = t0; t0 = t1; t1 = tmp; }
    tmin = t0 > tmin ? t0 : tmin;  tmax = t1 < tmax ? t1 : tmax;
    if (tmax < tmin) return false;   // < (not <=): flat(두께0) 박스도 통과(보수적 컬링)
    ... y, z 동일 ...
    return tmax >= tmin;             // >= : 두께0(coplanar) 박스 통과(false negative 방지)
}
```

**`<` / `>=` 선택 이유**: 평면 지오메트리(바닥 플레인 등)의 AABB는 한 축 두께가 0이다.
`<=` 로 컬링하면 그 박스가 통째로 기각되어 **바닥 그림자가 통째로 사라진다**.
Burst(`BurstBVH`)와 GPU(`BvhTraverse.compute` / `PathTrace.compute`)가 이 부등호를 그대로 미러한다.

### 2.7 품질 측정

```csharp
// Runtime/LightmapEvaluate/Occluders/BVH.cs:497
public float SahCost()   // 루트 면적 정규화. 낮을수록 좋음 → Median vs SAH 비교용
{
    float rootArea = Area(_nodes[0].Min, _nodes[0].Max);
    float sum = 0f;
    for (int i = 0; i < _nodeCount; i++)
    {
        Node n = _nodes[i];
        float a = Area(n.Min, n.Max) / rootArea;
        sum += n.Count > 0 ? a * n.Count : a * CTrav;
    }
    return sum;
}
public int MaxDepth() => ...
```

---

## 3. 2단 BVH (TLAS / BLAS) — DXR 구조

```
TLAS (월드)  ── 인스턴스 AABB BVH ── 리프 = 인스턴스 슬롯
                       │  적중
                       ▼  레이를 인스턴스 로컬로 변환 (WorldToLocal)
BLAS (로컬)  ── 메시당 단일레벨 BVH, 인스턴스 간 공유
```

### 3.1 데이터

```csharp
// Runtime/LightmapEvaluate/Occluders/TwoLevelBVH.cs:19
public struct Instance          // 입력
{
    public int MeshIndex;               // uniqueMeshes 인덱스
    public Matrix4x4 LocalToWorld;
}
struct InstanceRec              // 내부 레코드
{
    public Matrix4x4 WorldToLocal;  // 레이 변환용
    public Matrix4x4 NormalMatrix;  // (M⁻¹)ᵀ : 로컬 노멀 → 월드
    public int Blas;                // BLAS 인덱스
}
public struct InstancedHit      // 속성 조회용 히트
{
    public bool  Valid;
    public float T;
    public int   InstanceIndex, MeshIndex, MeshTriIndex;
}
```

메모리는 **∝ 메시 종류 수**(월드 전개 없음). 인스턴스는 행렬 2개 + int 1개만 든다.

### 3.2 빌드

BLAS는 메시당 1회, 인스턴스 월드 AABB는 **BLAS 로컬 루트 박스의 8코너 변환**:

```csharp
// Runtime/LightmapEvaluate/Occluders/TwoLevelBVH.cs:89
_inst[i] = new InstanceRec
{
    WorldToLocal = inst.LocalToWorld.inverse,
    NormalMatrix = inst.LocalToWorld.inverse.transpose,  // (M⁻¹)ᵀ
    Blas         = inst.MeshIndex
};
WorldAabbOfBlas(_blas[inst.MeshIndex], inst.LocalToWorld, out instMin[i], out instMax[i]);
```

```csharp
// Runtime/LightmapEvaluate/Occluders/TwoLevelBVH.cs:202
for (int c = 0; c < 8; c++)
{
    Vector3 corner = new Vector3((c & 1) == 0 ? a.x : b.x,
                                 (c & 2) == 0 ? a.y : b.y,
                                 (c & 4) == 0 ? a.z : b.z);
    Vector3 w = localToWorld.MultiplyPoint3x4(corner);
    instMin = Vector3.Min(instMin, w);  instMax = Vector3.Max(instMax, w);
}
```

TLAS는 인스턴스 centroid에 대한 **median BVH**(`TlasLeafMax = 2`). 인스턴스 수는 삼각형보다 훨씬 적어 SAH 불필요.

### 3.3 순회 — 방향 비정규화가 핵심

```csharp
// Runtime/LightmapEvaluate/Occluders/TwoLevelBVH.cs:305
public InstancedHit IntersectInstanced(Vector3 o, Vector3 d, float tmin, float tmax)
{
    InstancedHit best = new InstancedHit { Valid = false, T = tmax };
    Vector3 invD = new Vector3(1f / d.x, 1f / d.y, 1f / d.z);
    Span<int> stack = stackalloc int[64];
    int sp = 0; stack[sp++] = 0;
    while (sp > 0)
    {
        BVH.Node node = _tlas[stack[--sp]];
        if (!BVH.RayAABB(o, invD, node.Min, node.Max, tmin, best.T)) continue;
        if (node.Count > 0)
        {
            for (int s = node.LeftFirst; s < node.LeftFirst + node.Count; s++)
            {
                int instIdx = _instIdx[s];
                InstanceRec rec = _inst[instIdx];

                // 월드 레이 → 인스턴스 로컬 레이
                // 방향은 MultiplyVector 로 변환하여 '비정규화' 상태 유지 (T값 일관성)
                Vector3 lo = rec.WorldToLocal.MultiplyPoint3x4(o);
                Vector3 ld = rec.WorldToLocal.MultiplyVector(d);

                Hit hit = _blas[rec.Blas].Intersect(lo, ld, tmin, best.T);   // best.T 그대로 전달
                if (hit.Valid && hit.T < best.T)
                {
                    best.Valid = true; best.T = hit.T;
                    best.InstanceIndex = instIdx; best.MeshIndex = rec.Blas;
                    best.MeshTriIndex  = hit.TriIndex;
                }
            }
        }
        else { stack[sp++] = node.LeftFirst; stack[sp++] = node.LeftFirst + 1; }
    }
    return best;
}
```

**왜 정규화하면 안 되는가**: 로컬 방향을 정규화하면 파라메트릭 `T`의 척도가 인스턴스 스케일만큼 달라진다.
그러면 `best.T`를 다른 인스턴스의 BLAS로 넘겨 가지치기할 수 없고, 차폐 `maxDist` 비교도 깨진다.
비정규화를 유지하면 **모든 인스턴스가 같은 `T` 공간을 공유**한다.

### 3.4 노멀 역전치

```csharp
// Runtime/LightmapEvaluate/Occluders/TwoLevelBVH.cs:355
/// <summary>인스턴스의 로컬 노멀을 월드로 변환(정규화). 역전치 행렬 사용.</summary>
public Vector3 TransformNormalToWorld(int instanceIndex, Vector3 localNormal)
    => _inst[instanceIndex].NormalMatrix.MultiplyVector(localNormal).normalized;
```

`(M⁻¹)ᵀ` 를 쓰는 이유는 **비균등 스케일**에서 순진한 `M·n`이 표면에 수직이 아니게 되기 때문이다.
이 규약이 3곳에서 동일하게 미러된다:

| 위치 | 구현 |
|---|---|
| CPU | `TwoLevelBVH.TransformNormalToWorld` |
| Burst | `BurstTwoLevelBVH.TransformNormalToWorld` (`BurstScene.instNormalMatrix`) |
| GPU compute | `MulNormal(_InstNormals[inst], localN)` (3행 업로드) |
| 렌더 셰이더 | `InstancedSH_URP.NormalMatrix(M)` (코팩터/det 직접 계산) |

---

## 4. 검증

### `BVHTests` ([`Tests/BVHTests.cs`](../../Tests/BVHTests.cs), 324줄)

| 케이스 | 단언 |
|---|---|
| 단일 Median 퍼즈 | ≡ `BruteForceOccluder` (Valid/T/TriIndex) |
| 단일 SAH 퍼즈 | ≡ brute + `SahCost()` 비교 |
| **2단 TLAS/BLAS** | 20인스턴스(**회전 + 비균등 스케일**) × 5000레이 ≡ brute, **miss = 0/5000** |
| 빈 인스턴스 | 엣지 케이스 안전 |

### `InstancedSceneTester` (Sample)

2단 `ClosestHit`(위치·노멀·알베도) ≡ brute + Indirect(inst ≡ brute).

### `AtlasApplyDebug.RadianceDiffTest`

**실제 베이크 레이**로 `BruteForce` vs `BVH` 픽셀 차이 측정 — 합성 퍼즈가 아닌 실전 경로 검증.

```csharp
// Samples/AtlasApplyDebug.cs:179
[ContextMenu("Radiance Diff Test (BruteForce vs BVH)")]
```

---

## 5. 알려진 함정 (코드 주석에 박제된 것들)

| 함정 | 증상 | 방어 |
|---|---|---|
| `Node`가 struct → 수정 후 되쓰기 누락 | 바운드가 갱신 안 됨 | `nodes[nodeIndex] = node;` 명시 (`UpdateBounds` 주석) |
| 스택 pop이 `stack[--sp]` | push는 **반드시** `stack[sp++]` | `TwoLevelBVH.cs:347` 주석 |
| 슬랩 `<=` 사용 | 두께 0 박스 기각 → 평면 지오메트리 소실 | `<` / `>=` 고정 |
| SAH 빈 인덱스 연산자 우선순위 | 잘못된 빈 배정 | `(centroid - lo) * scale` 괄호 명시 |
| 로컬 방향 정규화 | `T` 척도 불일치 → 가지치기·차폐 오류 | `MultiplyVector` 유지 |
| 비균등 스케일에서 `M·n` | 노멀이 표면에 수직 아님 | `(M⁻¹)ᵀ` |

---

## 6. 다음 단계로의 연결

이 문서의 4개 설계 원칙이 그대로 다음 두 문서의 전제가 된다:

- [06 — G0~G3 Burst 백엔드](06_G0-G3_Burst_백엔드.md): `NodesRO`/`TriIdxRO`/`TrisRO` + `TlasRO`/`InstIdxRO` 접근자로 POD 평탄화
- [07 — G4~G6 GPU Compute](07_G4-G6_GPU_Compute.md): 같은 `Node`/`Tri` 레이아웃을 `StructuredBuffer`로 재패킹

> ⚠ 미사용 `using Cysharp.Threading.Tasks` 가 `TwoLevelBVH.cs`에 있었다는 v14 문서 기록은
> 현재 트리 기준 **해소됨**(파일 상단은 `System`/`System.Collections.Generic`/`Unity.Collections`/`UnityEngine`만).
