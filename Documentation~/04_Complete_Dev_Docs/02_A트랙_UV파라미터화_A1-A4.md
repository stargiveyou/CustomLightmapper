# 02 — Track A: UV 파라미터화 (A1 ~ A4)

> 상태: **✅ 완료** (평면투영 한정 · LSCM/MVC는 스텁)
> 범위: 입력 Mesh → 차트 분할 → 평탄화 → 밀도 정규화 → 패킹 → uv2 조립 → 텍셀 복원 → per-instance ST

---

## 0. 파이프라인 한눈에

```
Mesh
 │  A1
 ├─ WeldedHalfEdge(mesh)              용접 + 하프에지(DCEL) 위상
 ├─ ChartSegementer.GetResult(he, s)  이면각 시임컷 + best-first 영역성장
 ├─ ChartMeshBuilder.BuildAll(...)    차트-로컬 메시 + 경계 루프
 │  A2/A3
 ├─ ChartFlattener.FlattenAll(...)    Planar →(foldover) LSCM → MVC 디스패치
 │  A4
 ├─ DensityNormalizer.Normalize(...)  s = √(meshArea / uvArea)
 ├─ ShelfPacker.Pack(..., gutter)     높이 내림차순 선반 패킹 → [0,1]
 ├─ UVAssembly.Assemble(...)          단일 Mesh(uv2=ch1) + SeamTable
 │
 ├─ TexelMapper.Map(mesh, res, l2w)   uv2 래스터 → LumelMap{WorldPos, WorldNormal, Valid}
 └─ LightmapAllocator.Allocate(...)   per-instance ScaleOffset(ST) + 페이지
```

진입점: [`ParameterizationPipeline.Run`](../../Runtime/UVAssembly/ParameterizationPipeline.cs#L30)

```csharp
// Runtime/UVAssembly/ParameterizationPipeline.cs:30
public static ParamResult Run(Mesh sourceMesh, SegmentationSettings seg)
{
    // A1) 용접 + 하프에지 빌드. NativeArray 를 쓰므로 반드시 Dispose.
    var he = new WeldedHalfEdge(sourceMesh);
    try
    {
        // A2) 차트 분할 → 차트-로컬 메시/경계 루프 추출
        var s      = ChartSegementer.GetResult(he, seg);
        var charts = ChartMeshBuilder.BuildAll(he, s);

        // A3) 차트별 평탄화 (Planar 우선, foldover 시 LSCM → 최후 MVC)
        var methods = ChartFlattener.FlattenAll(charts);

        int foldovers = 0;
        for (int i = 0; i < charts.Length; i++)
            if (UVValidator.HasFoldover(charts[i])) foldovers++;

        return new ParamResult { Charts = charts, Methods = methods,
                                 ChartCount = charts.Length, FoldoverCharts = foldovers };
    }
    finally { he.Dispose(); }
}
```

> ⚠ `ParameterizationPipeline`은 A1~A3까지만 구동한다. A4(정규화/패킹/조립)는 호출측
> (`AtlasApplyDebug`, `UVAssemblerTestDebugger`)이 `ParamResult.Charts`를 받아 이어서 처리한다.

---

## A1 — 메시 구조 & 차트 분할

### A1.1 Half-Edge (DCEL)

| 타입 | 파일 | 역할 |
|---|---|---|
| `HalfEdge_Vertex` / `HalfEdge_Edge` / `HalfEdge_Face` | [`HalfEdge.cs:14~41`](../../Runtime/HalfEdge.cs#L14) | DCEL 요소 |
| `HalfEdge : IDisposable` | [`HalfEdge.cs:41`](../../Runtime/HalfEdge.cs#L41) | `NativeArray` 기반 위상 |
| `WeldedHalfEdge : IDisposable` | [`HalfEdge.cs:249`](../../Runtime/HalfEdge.cs#L249) | **weld 내장** — MeshCleaner 스탠드인 |

- 정점/에지/면 전부 `NativeArray` + `float3` → Burst 이식 준비.
- `WeldedHalfEdge`가 중복 정점 용접을 수행하므로 별도 MeshCleaner(C0) 없이 파이프라인이 성립한다.
- Half-Edge는 **임시 생성 후 폐기**(파이프라인 `finally`에서 Dispose) — 런타임 잔존 없음.

### A1.2 차트 분할 — `ChartSegementer`

듀얼 그래프(face = 노드, 비-시임 공유 에지 = 간선) 위의 **best-first(min-heap) greedy 영역성장**.

```csharp
// Runtime/ChartSegmentation.cs:35  — 분할 파라미터
public struct SegmentationSettings
{
    public float SeamAngleDeg;      // 이면각 초과 시 시임(차트 경계)으로 절단. 건물은 30~45°
    public float MaxChartAngleDeg;  // 차트 평균 노멀과 후보 face 노멀의 허용 편차
    public static SegmentationSettings Default => new SegmentationSettings()
    { SeamAngleDeg = 40, MaxChartAngleDeg = 60 };
}
```

동작 4단계:

1. **시임 사전 계산** — 경계(pair 없음)이거나 이면각이 `SeamAngleDeg` 초과면 `heSeam[e] = true`.
   큐브의 직각(90°) 모서리는 여기서 즉시 잘린다.
   ```csharp
   // Runtime/ChartSegmentation.cs:110
   float seamCos = Mathf.Cos(setting.SeamAngleDeg * Mathf.Deg2Rad);
   for (int e = 0; e < heSeam.Length; e++)
   {
       int t = he.edges[e].pairIndex;
       if (t < 0) { heSeam[e] = true; continue; }         // 경계 = 무조건 시임
       float d = Vector3.Dot(he.faces[he.edges[e].faceIndex].normal,
                             he.faces[he.edges[t].faceIndex].normal);
       heSeam[e] = d < seamCos;                            // 각도 > 임계값
   }
   ```
2. **Best-first 성장** — `cost = 1 - dot(neighborNormal, chartNormal)` 로 min-heap push.
   정렬도가 높은(평면적) 이웃부터 병합된다.
3. **면적 가중 노멀 갱신** — `nSum += faceNormal * faceArea` → 작고 찌그러진 삼각형의 방향 노이즈 억제.
4. **한계 각도 도달** — 차트 평균 노멀과 편차가 `MaxChartAngleDeg`를 넘으면 편입 거부 → 보류된 face가
   다음 루프에서 새 시드가 되어 별도 차트를 형성한다.

시드 순서 = face 인덱스 순서 → **결정적**(재실행 시 동일 결과).

산출:

```csharp
// Runtime/ChartSegmentation.cs:55
public sealed class ChartSegmentationResult
{
    public int[] FaceChart;     // face → chart id
    public List<Chart> Charts;  // Chart{ faces, Normal(면적가중), Area }
    public bool[] HEseam;       // half-edge → 시임 여부 (후단 시임 테이블·스티칭에서 재사용)
}
```

### A1.3 차트-로컬 메시 — `ChartMesh` / `ChartMeshBuilder`

[`ChartMesh.cs:11`](../../Runtime/ChartMesh.cs#L11) — 차트별 로컬 정점·삼각형·`MeshVertex`(원본 정점 역참조)·경계 루프·`PlaneNormal`.
`MeshVertex`가 이후 `SeamTable` 그룹핑의 키가 된다.

---

## A2 / A3 — 평탄화

### A2.1 평면 투영 — `PlanarProjector`

차트 평균 노멀 평면에 **직교 정사영**. 평면 차트에는 무왜곡(등거리)·foldover 불가.

```csharp
// Runtime/UVAssembly/PlanarProjector.cs:13
public static void Projector(ref ChartMesh cm)
{
    Vector3 n  = cm.PlaneNormal.sqrMagnitude > 1e-12f ? cm.PlaneNormal.normalized : Vector3.up;
    Vector3 up = MathF.Abs(n.x) < 0.9f ? Vector3.right : Vector3.up;   // 비평행 보조축
    Vector3 t  = Vector3.Cross(n, up).normalized;
    Vector3 b  = Vector3.Cross(n, t);

    Vector3 c = Vector3.zero;                       // 원점 = 차트 중심
    for (int i = 0; i < cm.positions.Length; i++) c += cm.positions[i];
    c /= Mathf.Max(1, cm.positions.Length);

    var uv = new Vector2[cm.positions.Length];
    for (int i = 0; i < cm.positions.Length; i++)
    {
        Vector3 p = cm.positions[i] - c;
        uv[i] = new Vector2(Vector3.Dot(p, t), Vector3.Dot(p, b));
    }
    cm.UV = uv;
}
```

### A2.2 겹침 검출 — `UVValidator`

**부호 면적 혼재 = foldover** 판정. 이것이 A3 폴백 디스패치의 트리거다.

```csharp
// Runtime/UVAssembly/UVValidator.cs:31
float area = 0.5f * ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x));  // 부호 면적
...
HasFoldover = pos > 0 && neg > 0,   // 양수/음수 삼각형이 섞이면 겹침
Flipped     = Mathf.Min(pos, neg),  // 소수파 = 뒤집힌 삼각형 수
```

### A3 — 평탄화 디스패처 `ChartFlattener`

기획 계약 "평면투영 > LSCM > MVC"를 차트마다 적용.

```csharp
// Runtime/ChartFlatten/ChartFlattener.cs:24
public static FlattenMethod Flatten(ref ChartMesh cm)
{
    // 1) 근평면 정사영 — 평면 차트엔 최적. foldover 불가.
    PlanarProjector.Projector(ref cm);
    if (!UVValidator.HasFoldover(cm)) return FlattenMethod.Planar;

    // 2) 곡률 차트 → LSCM (등각). 자유경계라 결과가 여전히 겹칠 수 있음 → 재검사.
    LSCMSolver.Solve(ref cm);
    if (!UVValidator.HasFoldover(cm)) return FlattenMethod.LSCM;

    // 3) 최후 폴백 → MVC. 볼록 경계 고정 + 양수 가중으로 전단사 보장 → 재검사 없이 종료.
    MVCFallback.Solve(ref cm);
    return FlattenMethod.MVC;
}
```

> ⚠ **LSCM / MVC는 스텁이다.**
> [`LSCMSolver.cs:18`](../../Runtime/ChartFlatten/LSCMSolver.cs#L18) · [`MVCFallback.cs:18`](../../Runtime/ChartFlatten/MVCFallback.cs#L18)
> 둘 다 `LogWarning("미구현 stub")` + no-op. 디스패처 배선만 완성돼 있고 솔버 본체는 없다.
> **결과**: 곡면 차트는 평면투영 UV가 그대로 남아 foldover가 잔존할 수 있다.
> 현재 대상(건물·프롭)은 평면 차트가 지배적이라 실사용에 문제가 없다는 판단.
> 향후 구현 방향은 기획서 §11-3: MVC는 양수 가중 전단사, LSCM은 정점 2고정 + CG.

---

## A4 — 밀도 정규화 · 패킹 · 조립

### A4.1 밀도 정규화 — `DensityNormalizer`

**UV 면적 = 메시 면적**이 되도록 차트별 스케일 → 메시 전역 텍셀 밀도 균일화.

```csharp
// Runtime/UVAssembly/UVPacker.cs:13
public static void Normalize(ChartMesh[] charts)
{
    foreach (var cm in charts)
    {
        float ma = MeshArea(cm), ua = UVArea(cm);
        float s  = ua > 1e-12f ? Mathf.Sqrt(ma / ua) : 1f;    // s = √(meshArea / uvArea)
        for (int i = 0; i < cm.UV.Length; i++) cm.UV[i] *= s;
    }
}
```

평면투영은 이미 등거리에 가까워 보정량이 작지만, **곡률·방법 차이를 흡수**하는 단계다.
LSCM이 다른 스케일 UV를 내도 여기서 자동으로 맞춰진다.

### A4.2 셸프 패킹 — `ShelfPacker`

높이 내림차순 정렬 → 선반(shelf)에 가로로 채우고, 폭이 차면 다음 층.

```csharp
// Runtime/UVAssembly/UVPacker.cs:99
var order = new int[n];
for (int i = 0; i < n; i++) order[i] = i;              // ← 과거 버그: 전부 n 으로 채워 범위초과/정렬불가
System.Array.Sort(order, (a, b) => h[b].CompareTo(h[a]));   // 높이 내림차순

float W = Mathf.Sqrt(totalA) * 1.1f;                  // 행 폭 휴리스틱
...
    if (x + ww > W && x > 0f)   // 빈 선반(x==0)에선 폭 초과라도 줄바꿈 안 함(무한루프/빈행 방지)
    { x = 0f; y += shelfH; shelfH = 0f; }
...
// 휴리스틱 W 가 아니라 '실제 사용 폭/높이'로 나눠야 [0,1] 보장(넓은 차트 오버플로 방지)
float scale = 1f / Mathf.Max(maxX, y + shelfH);
```

핵심 방어 2가지 — 둘 다 실제 버그 수정 흔적:
- 빈 선반에서는 폭 초과여도 줄바꿈하지 않음 → **무한 루프 / 빈 행 방지**
- 정규화 분모를 휴리스틱 `W`가 아니라 `max(maxX, y+shelfH)`(실제 사용 범위)로 → **[0,1] 보장**

### A4.3 조립 — `UVAssembly.Assemble` + `SeamTable`

차트들을 하나의 런타임 메시로 통합. **차트마다 자기 로컬 정점을 쓰므로 시임 정점은 자동 복제**된다.

```csharp
// Runtime/UVAssembly/UVAssembler.cs:46
var m = new Mesh { name = (source ? source.name : "chart") + "_uv2" };
m.indexFormat = verts.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
m.SetVertices(verts);
m.SetTriangles(tris, 0);
m.SetUVs(1, uv2);                 // uv2 = UV 채널 1 (Unity 라이트맵 UV)
if (srcUV != null) m.SetUVs(0, uv0);
m.RecalculateNormals();           // 차트 경계서 정점이 분리돼 있어 하드 노멀로 복원됨
m.RecalculateBounds();

// SeamTable : 원본 정점별 조립 정점 묶음
var groups = new Dictionary<int, List<int>>();
for (int vi = 0; vi < src.Count; vi++) { ... groups[src[vi]].Add(vi); }
var seams = new SeamTable();
foreach (var kv in groups)
    if (kv.Value.Count >= 2) seams.Groups.Add(kv.Value.ToArray());
```

`SeamTable.Groups` = **같은 원본 정점에서 갈라진 조립 정점 인덱스 묶음**.
→ [05 후처리 문서](05_후처리_스티칭_디레이션_디노이즈.md)의 시임 스티칭 Tier1/Tier2 입력이 된다.

---

## A4' — 텍셀 복원 (`TexelMapper`)

uv2 삼각형을 아틀라스에 **소프트웨어 래스터**하고, 텍셀마다 바리센트릭으로
worldPos·worldNormal·valid를 복원한다. **레이도 BVH도 쓰지 않는다** → C1/C2 없이 단독 테스트 가능.

```csharp
// Runtime/TexelMapper/TexelMapper.cs:12
public struct LumelMap
{
    public int Resolution;
    public Vector3[] WorldPos;      // R*R
    public Vector3[] WorldNormal;   // R*R (정규화)
    public bool[]    Valid;         // R*R (차트 커버 여부)
    public Vector3   BoundsMin, BoundsMax;
}
```

정점을 먼저 월드로 옮긴 뒤 래스터한다 — 노멀은 이동 성분을 빼야 하므로 `MultiplyVector`:

```csharp
// Runtime/TexelMapper/TexelMapper.cs:44
wp[i] = l2w.MultiplyPoint3x4(V[i]);
wn[i] = l2w.MultiplyVector(N[i]).normalized;   // 노멀=방향 → 이동 성분 제외
```

래스터 내부 — 픽셀 **정중앙**(+0.5) 샘플 + 양방향 부호 허용(뒤집힌 삼각형도 커버):

```csharp
// Runtime/TexelMapper/TexelMapper.cs:112
float fx = x + 0.5f, fy = y + 0.5f;   // 테두리가 아닌 픽셀 정중앙

float b0 = ((p1.y - p2.y) * (fx - p2.x) + (p2.x - p1.x) * (fy - p2.y)) / denom;
float b1 = ((p2.y - p0.y) * (fx - p2.x) + (p0.x - p2.x) * (fy - p2.y)) / denom;
float b2 = 1f - b0 - b1;

if (!((b0 >= 0 && b1 >= 0 && b2 >= 0) || (b0 <= 0 && b1 <= 0 && b2 <= 0))) continue;

m.WorldPos[idx]    = w0 * b0 + w1 * b1 + w2 * b2;
m.WorldNormal[idx] = (n0 * b0 + n1 * b1 + n2 * b2).normalized;
m.Valid[idx]       = true;
```

`LumelMap`은 이후 **모든 백엔드(CPU/Burst/GPU)의 공통 베이크 입력**이며,
디노이즈의 가이드(노멀·월드위치)로도 재사용된다.

---

## A4'' — per-instance ST 할당 (`LightmapAllocator`)

**유니크 메시 1회 언랩(공유 uv2) + 인스턴스별 ST**. `UNITY_INSTANCED_PROP` 배칭을 유지하는 핵심 설계.

```csharp
// Runtime/LightmapAllocator/LightmapAllocator.cs:8
public struct InstanceLM
{
    public int InstanceId;
    public int LightmapIndex;    // 아틀라스 페이지(Texture2DArray)
    public Vector4 ScaleOffset;  // atlasUV = uv2 * ST.xy + ST.zw
}
```

영역 크기: **변 ∝ √(월드면적) × 밀도** → 면적이 월드 면적에 비례한다.

```csharp
// Runtime/LightmapAllocator/LightmapAllocator.cs:78
// 영역 변 길이(텍셀) = sqrt(월드 면적) * 밀도 → 면적 ∝ 월드면적, 변 ∝ 스케일
int px = Mathf.CeilToInt(Mathf.Sqrt(Mathf.Max(0f, insts[i].WorldArea)) * s.TexelsPerWorldUnit);
side[i] = Mathf.Clamp(px, 1, r - g);
```

배치는 변 길이 내림차순 셸프 + **페이지 넘김**:

```csharp
// Runtime/LightmapAllocator/LightmapAllocator.cs:104
if (x + w > r) { x = 0; y += selfH; selfH = 0; }        // 1) 가로 초과 → 다음 선반
if (y + h > r)                                          // 2) 세로 초과 → 다음 페이지 (누락됐던 분기)
{
    page++; x = 0; y = 0; selfH = 0;
    if (page >= maxPages) { overflow = true; page = maxPages - 1; }  // 한도 초과분은 마지막 페이지 클램프
}
```

월드 면적은 **인스턴스 트랜스폼 적용 후** 계산한다(비균등 스케일 반영):

```csharp
// Runtime/LightmapAllocator/LightmapAllocator.cs:56
public static float WorldArea(Mesh mesh, Matrix4x4 l2w)
{
    ... a += 0.5f * Vector3.Cross(p1 - p0, p2 - p0).magnitude;   // 변환 후 삼각형 면적 합
}
```

### 자체 검증 (`RunSelfTests`)

[`LightmapAllocator.cs:146`](../../Runtime/LightmapAllocator/LightmapAllocator.cs#L146)

| 케이스 | 단언 |
|---|---|
| 큐브 3개(월드면적 6/24/1.5) | 변 비율 **2 : 1 : 0.5** (±5%) · 1페이지 |
| 단위 큐브 200개 | 페이지 > 1 · ST ∈ [0,1] · 영역 겹침 0 |

`StInRange` / `NoOverlap` 는 O(n²) 전수 검사로 구현돼 있어 회귀 방어가 확실하다.

---

## 확정 기본값

| 설정 | 값 | 위치 |
|---|---|---|
| 페이지 해상도 | 1024×1024 | `AllocationSettings.Default` |
| 텍셀 밀도(코드 기본) | 64 texels/unit | `AllocationSettings.Default.TexelsPerWorldUnit` |
| 텍셀 밀도(인스펙터 기본) | 16 texels/unit | `AtlasApplyDebug.texelsPerWorldUnit` |
| 거터 | 2 texels | `AllocationSettings.Default.GutterTexels` |
| 최대 페이지 | 8 | `AllocationSettings.Default.MaxPages` |
| 차트 거터(UV) | 0.01 | `ShelfPacker.Pack(gutter)` / `AtlasApplyDebug.chartGutter` |
| 시임 각도 | 40° | `SegmentationSettings.Default.SeamAngleDeg` |
| 차트 최대 각도 | 60° | `SegmentationSettings.Default.MaxChartAngleDeg` |
| 최종 인코딩 | RGBAHalf | 후처리 step 8 |
| 디버그 인코딩 | RGBA32 + 감마(`ToColor`) | `AtlasApplyDebug` |

---

## 검증

| 테스트 | 커버리지 |
|---|---|
| `ParameterizationPipeline.RunSelfTests` | 큐브 24v / 12t / 시임 8 / uv2 ∈ [0,1] / foldover 0 |
| `LightmapAllocator.RunSelfTests` | 변 비율 2:1:0.5 · 200큐브 다페이지 · ST ∈ [0,1] · 겹침 0 |
| `UVLayoutViewer` (Editor) | UV 레이아웃 시각 확인 |
| `ChartSegmentationDebugger` (Sample) | 차트 컬러라이즈 |
| `ParameterizationTestDebugger` / `UVAssemblerTestDebugger` (Sample) | 파이프라인 엔드투엔드 |

---

## 문서 ↔ 트리 차이

| 항목 | v14 문서 | 실제 트리 |
|---|---|---|
| 파일 배치 | `Script/…` | `Runtime/…` (UPM 전환) |
| `TexelMapperDebug` | `Script/TexelMapper/` | `Samples/TexelMapperDebug.cs` |
| A1/A2 leaf 소스 "등록 스냅샷 미포함" | 재검증 대상 아님으로 표기 | **전부 트리에 존재** (`HalfEdge.cs` 540줄, `ChartSegmentation.cs` 338줄, `ChartMesh.cs` 146줄) |
