# C0 — SceneCollector 개발문서 (v1, 2026-08-18)

> **이 문서는 "무엇을 어떻게 짜는가"** — 타입 시그니처·알고리즘·배선·테스트.
> **"왜 그렇게 정했는가"는 설계 문서**: `01_Planning_And_Design/커스텀_라이트매퍼_C0_씬수집_설계.md`
> (결정 ①~⑧ · 검증 게이트 C0-V1~V8). 두 문서는 짝이며, 결정 번호는 그대로 인용한다.
>
> 로드맵 위치: 개발문서 v14 §10.1. 파이프라인 단계 1.

---

## 1. 어셈블리 배치

| 코드 | asmdef | 이유 |
|---|---|---|
| `SceneCollectPolicy` · `SceneCollection` · POD 타입 | `HuskyLibs.CustomLightmapper` (Runtime) | 순수 함수 → **헤드리스 테스트 가능**. `UnityEditor` 미참조 |
| `SceneCollector` (씬 순회 · 플래그 조회) | `HuskyLibs.CustomLightmapper.Editor` | `StaticEditorFlags`/`LODGroup`/`ModelImporter` 는 `UnityEditor` 전용 |
| `SceneCollectPolicyTests` | `HuskyLibs.CustomLightmapper.Tests` | Tests asmdef 는 Runtime 만 참조 → 정책만 테스트 가능(그래서 정책을 분리했다, 결정 ③) |
| `AtlasApplyDebug` 배선 | `HuskyLibs.CustomLightmapper.Samples` | Samples 는 Runtime+Tests 참조. **Editor 참조가 없다** → §6 주의 |

**네임스페이스**: Runtime/Tests = `HuskyLibs.CustomLightmapper.Bake`, Editor = `HuskyLibs.CustomLightmapper.Bake.EditorTools`
(기존 파일과 동일 — `AlphaTypes.cs`, `LightmapDenoiseCompareMenu.cs` 참조).

> ⚠ **Samples asmdef 는 Editor asmdef 를 참조하지 않는다**(순환 방지: Editor → Tests → Runtime).
> 따라서 `AtlasApplyDebug` 가 `SceneCollector` 를 직접 부를 수 없다. §6 에서 해결한다.

---

## 2. 타입 정의

### 2.1 분류 · 사유

```csharp
namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>C0 3분류. v14.11 receiver/occluder 계약에 그대로 대응한다.</summary>
    public enum CollectClass
    {
        Receiver = 0,       // 라이트맵을 굽는다 → AtlasApplyDebug.targets
        OccluderOnly = 1,   // 차폐만 참여, 굽지 않는다 → AtlasApplyDebug.occluders
        Excluded = 2,       // 양쪽 모두에서 뺀다
    }

    /// <summary>Excluded/강등 사유. 리포트에 그대로 찍히므로 사용자가 읽을 수 있어야 한다(결정 ⑧).</summary>
    public enum CollectReason
    {
        None = 0,
        UserExcluded,        // 사용자가 명시 제외
        NoMeshFilter,        // MeshFilter 없음
        NoMesh,              // sharedMesh == null
        NotReadable,         // mesh.isReadable == false  ← 현재 조용히 반쪽 처리되던 케이스
        NoRenderer,          // MeshRenderer 없음
        EmptyBounds,         // 렌더러 bounds 가 0 → 면적 0
        NoTriangles,         // 인덱스 0
        LodChild,            // LODGroup 의 LOD1+ 전용 렌더러
        NotContributeGI,     // StaticEditorFlags.ContributeGI 꺼짐
        ReceiveGiLightProbes,// receiveGI = LightProbes → 라이트맵 안 받음(→ OccluderOnly)
        UserOccluder,        // 사용자가 occluder-only 로 지정(→ OccluderOnly)
    }
}
```

### 2.2 후보 (정책 입력 POD)

**이미 조회가 끝난 값만 담는다.** 정책 안에서 `UnityEditor` 나 무거운 메시 접근을 하지 않는다(결정 ③·⑦).

```csharp
    public struct CollectCandidate
    {
        public int    Order;          // 씬 순회 순서. 정책은 이 값을 재정렬하지 않는다(결정 ②)
        public string Name;           // 로그 전용

        // 구성 요소 유무
        public bool   HasMeshFilter;
        public bool   HasRenderer;
        public bool   MeshNull;

        // 메시 메타데이터 — 전부 배열 복사 없는 접근(§4.4)
        public bool   MeshReadable;   // mesh.isReadable
        public int    VertexCount;    // mesh.vertexCount
        public int    TriangleCount;  // Σ GetIndexCount(s) / 3
        public int    SubMeshCount;   // mesh.subMeshCount
        public bool   HasUv0;         // mesh.HasVertexAttribute(VertexAttribute.TexCoord0)

        // 트랜스폼/렌더러
        public float  BoundsExtentSq; // renderer.bounds.extents.sqrMagnitude (면적 0 판정용 — WorldArea 아님)

        // 라이트맵 관련 플래그(Editor 어댑터가 조회)
        public bool   ContributeGI;
        public bool   ReceiveGiLightProbes;
        public bool   IsLodChild;

        // 사용자 오버라이드
        public bool   UserOccluder;
        public bool   UserExcluded;
    }
```

### 2.3 결과

```csharp
    public struct CollectEntry
    {
        public int           Order;
        public string        Name;
        public CollectClass  Class;
        public CollectReason Reason;   // Receiver 면 None
    }

    public sealed class SceneCollection
    {
        public CollectEntry[] Entries;      // Order 오름차순 = 씬 순회 순서(결정 ②)
        public int[]          ReceiverIdx;  // Entries 인덱스, 순서 보존
        public int[]          OccluderIdx;
        public int            ReceiverCount, OccluderCount, ExcludedCount;

        /// <summary>사유별 집계 + 상위 N건 목록. §7 형식.</summary>
        public string Report(int maxDetail = 20);
    }

    public struct SceneCollectOptions
    {
        /// <summary>ContributeGI 가 꺼진 정적 오브젝트를 occluder 로 살릴지. 기본 false(제외).</summary>
        public bool NonGiAsOccluder;
        /// <summary>LOD1+ 를 occluder 로 살릴지. 기본 false — LOD0 가 이미 같은 자리를 차폐한다.</summary>
        public bool LodChildAsOccluder;
        /// <summary>서브메시 255 초과·UV0 없음을 경고로만 남길지(true) 제외할지(false). 기본 true.</summary>
        public bool WarnOnly;

        public static SceneCollectOptions Default => new SceneCollectOptions { WarnOnly = true };
    }
```

### 2.4 정책

```csharp
    public static class SceneCollectPolicy
    {
        /// <summary>후보 1건 분류. 부작용 없음 · 결정적 · UnityEditor 미의존.</summary>
        public static CollectClass Classify(in CollectCandidate c, in SceneCollectOptions o,
                                            out CollectReason reason);

        /// <summary>후보 배열 → SceneCollection. 입력 순서를 그대로 유지한다.</summary>
        public static SceneCollection Run(CollectCandidate[] cands, in SceneCollectOptions o);
    }
```

---

## 3. Editor 어댑터

```csharp
namespace HuskyLibs.CustomLightmapper.Bake.EditorTools
{
    public static class SceneCollector
    {
        /// <param name="roots">null 이면 활성 씬의 루트 GameObject 전부</param>
        /// <param name="userOccluders">occluder-only 로 강제할 렌더러(인스펙터 지정)</param>
        /// <param name="userExcluded">제외할 렌더러</param>
        public static SceneCollection Collect(GameObject[] roots,
                                              in SceneCollectOptions opt,
                                              HashSet<MeshFilter> userOccluders = null,
                                              HashSet<MeshFilter> userExcluded = null,
                                              List<MeshFilter> outFilters = null);
    }
}
```

`outFilters` 는 `SceneCollection.Entries[i]` 와 **인덱스가 1:1로 대응**하는 실제 `MeshFilter` 목록이다.
POD 결과와 유니티 오브젝트를 분리해 두기 위해 참조를 별도 리스트로 돌려준다.

---

## 4. 알고리즘

### 4.1 순회와 순서 (결정 ②)

```
roots = roots ?? SceneManager.GetActiveScene().GetRootGameObjects()   // 씬에 저장된 순서
order = 0
foreach root in roots:                                   // 배열 순서 그대로
    foreach mf in root.GetComponentsInChildren<MeshFilter>(includeInactive:false):
        cand[order] = BuildCandidate(mf, order); order++
```

- **정렬하지 않는다.** `GetComponentsInChildren` 은 깊이 우선 계층 순서를 보장하고,
  `GetRootGameObjects()` 는 씬 파일 순서를 보장한다. 이 둘의 합성이 결정적 순서다.
- `includeInactive:false` — 비활성 오브젝트는 렌더되지 않으므로 굽지도 차폐하지도 않는다.
- 이 순서가 곧 `AtlasApplyDebug` 의 인스턴스 인덱스 `i` 가 되고, ST 할당과
  per-lumel 시드 `seed + li*2654435761u` 에 들어간다. → **C0-V1 이 이것만 검사한다.**

### 4.2 LOD 매핑

```
lodChildren = HashSet<Renderer>()
foreach lg in root.GetComponentsInChildren<LODGroup>(false):
    LOD[] lods = lg.GetLODs()
    for i in 1 .. lods.Length-1:
        foreach r in lods[i].renderers: lodChildren.Add(r)
    for r in lods[0].renderers: lodChildren.Remove(r)   // LOD0 에도 있으면 LOD0 이 이긴다
```

마지막 줄이 중요하다 — LOD0 과 LOD1 에 **같은 렌더러**가 등록된 LODGroup 이 실재한다.
그 경우 굽는 쪽(LOD0)을 우선한다.

### 4.3 분류 판정 순서

**단락 평가 순서가 곧 리포트 사유의 우선순위**다. 위에서부터 먼저 걸리는 것이 사유가 된다.

| # | 조건 | 결과 |
|---|---|---|
| 1 | `UserExcluded` | Excluded(UserExcluded) |
| 2 | `!HasMeshFilter` | Excluded(NoMeshFilter) |
| 3 | `MeshNull` | Excluded(NoMesh) |
| 4 | **`!MeshReadable`** | **Excluded(NotReadable)** |
| 5 | `!HasRenderer` | Excluded(NoRenderer) |
| 6 | `TriangleCount == 0 \|\| VertexCount == 0` | Excluded(NoTriangles) |
| 7 | `BoundsExtentSq <= 1e-12` | Excluded(EmptyBounds) |
| 8 | `UserOccluder` | OccluderOnly(UserOccluder) |
| 9 | `IsLodChild` | `LodChildAsOccluder ? OccluderOnly : Excluded` (LodChild) |
| 10 | `!ContributeGI` | `NonGiAsOccluder ? OccluderOnly : Excluded` (NotContributeGI) |
| 11 | `ReceiveGiLightProbes` | OccluderOnly(ReceiveGiLightProbes) |
| 12 | 그 외 | **Receiver**(None) |

**지오메트리 유효성(2~7)을 라이트맵 플래그(9~11)보다 먼저 본 이유**:
ContributeGI 를 안 켠 것은 사용자의 의도지만, R/W 미설정·삼각형 0 은 **사고**다.
사고를 먼저 보고해야 리포트가 쓸모 있다. 특히 4번(`NotReadable`)은
현재 receiver 목록엔 남고 `BuildGiScene`(`AtlasApplyDebug.cs:1289`)에서만 빠져
**경고 없이 그림자가 사라지던** 케이스다(설계 §1.3, 결정 ⑤).

8번을 9~11보다 앞에 둔 이유: 사용자 지정은 자동 판정을 항상 이긴다(결정 ⑧).

### 4.4 후보 조립 — 배열 복사 금지 (결정 ⑦)

수천 오브젝트를 훑으므로 **관리배열을 복사하는 API 를 쓰지 않는다.**

| 필요한 값 | 쓸 것 (복사 없음) | 쓰면 안 되는 것 |
|---|---|---|
| 삼각형 수 | `Σ mesh.GetIndexCount(s) / 3` | `mesh.triangles.Length/3` |
| UV0 유무 | `mesh.HasVertexAttribute(VertexAttribute.TexCoord0)` | `mesh.uv.Length > 0` |
| 정점 수 | `mesh.vertexCount` | `mesh.vertices.Length` |
| 면적 0 판정 | `renderer.bounds.extents` | **`LightmapAllocator.WorldArea`** |

> **`LightmapAllocator.WorldArea` 를 수집 단계에서 부르지 않는다.**
> 이 함수는 호출마다 `mesh.vertices` + `mesh.triangles` 를 관리배열로 복사한다
> (`LightmapAllocator.cs:58-59`). 수천 오브젝트에 돌리면 GC 폭탄이고, 게다가
> **베이크 시작 시 `BakeAndApply` 가 어차피 다시 계산한다**(`AtlasApplyDebug.cs:424`).
> 수집이 알아야 하는 건 "면적이 0인가"뿐이라 이미 계산돼 있는 `bounds` 로 충분하다.

`GetIndexCount`/`HasVertexAttribute`/`vertexCount`/`bounds` 는 **non-readable 메시에서도 동작**한다
(메타데이터라서). 그래서 4번 판정 이후에도 나머지 필드를 안전하게 채울 수 있다.

### 4.5 경고만 남기는 항목 (`WarnOnly`)

제외하지 않고 리포트에만 남긴다.

- `!HasUv0` → **α 알파 컷아웃이 이 메시에서 자동 비활성**된다
  (`AlphaMaskBuilder.BuildScene` 이 `meshHasUv[mesh]` 로 `matId=-1` 고정). 잎/펜스면 그림자가 통짜가 된다.
- `SubMeshCount > 255` → `TriSubmesh` 가 `byte` 라 clamp 된다(α 설계 §10.4).
- 비매니폴드 의심 — 값싸게 판정할 수 없으므로 수집에서는 검사하지 않는다.
  `WeldedHalfEdge` 가 파라미터화 시점에 경고를 이미 낸다(`HalfEdge.cs:178`).

---

## 5. MeshCleaner — 추출만 한다 (결정 ⑥)

`HalfEdge.cs` 의 두 private static 을 **로직 변경 없이** 공개로 올린다.

```csharp
// Runtime/HalfEdge.cs — 시그니처 유지, 접근자만 private → public
public static void WeldVertices(Vector3[] vertices, int[] triangles, float epsilon,
                                out Vector3[] weldVertices, out int[] immidiateWeldTriangles);   // :449
public static void RemoveDegenerateTriangles(int[] triangles, out int[] degeneratedTriangles);   // :495
```

**호출처를 늘리지 않는다.** `WeldedHalfEdge` 생성자(`:301-302`)가 지금처럼 파라미터화 경로에서 부르는 것이
유일한 실행 지점이다. C0는 **진단 목적으로만**(용접으로 줄어드는 정점 수, 제거되는 퇴화 삼각형 수) 호출할 수 있고,
그 결과를 **차폐 씬에 반영하지 않는다.**

> **왜 반영하면 안 되는가**: 차폐 씬의 `Tri[]` 는 `mesh.triangles` 순서를 그대로 쓰고,
> α의 `TriUV`/`TriSubmesh` 가 그 순서에 1:1로 대응한다. 삼각형 수가 달라지면
> `AlphaMaskBuilder.BuildScene` 의 정합 검사(`tris.Length/3 != nTri`, `AlphaMaskBuilder.cs:317`)에 걸려
> **그 메시의 알파가 조용히 꺼진다.** → C0-V6 이 이걸 지킨다.

진단 호출은 비싸므로(배열 복사) **옵션이며 기본 off**, 선택된 오브젝트에만 돌린다.

---

## 6. AtlasApplyDebug 배선

**문제**: Samples asmdef 가 Editor asmdef 를 참조하지 않는다(§1) → `AtlasApplyDebug` 안에서
`SceneCollector.Collect` 를 직접 호출할 수 없다.

**해결**: 메뉴를 **Editor 쪽에 둔다.** 기존 `Editor/LightmapDenoiseCompareMenu.cs` 와 같은 패턴이다.

```csharp
// Editor/SceneCollectMenu.cs
[MenuItem("Tools/CustomLightmapper/Collect Scene → AtlasApplyDebug")]
static void CollectIntoSelected()
{
    var dbg = Selection.activeGameObject?.GetComponent<AtlasApplyDebug>();
    if (dbg == null) { Debug.LogWarning("AtlasApplyDebug 를 가진 오브젝트를 선택하세요."); return; }

    var col = SceneCollector.Collect(null, SceneCollectOptions.Default, ..., outFilters: filters);

    Undo.RecordObject(dbg, "Collect Scene");
    dbg.SetCollected(receivers, occluders);      // ← 아래 신설 API
    EditorUtility.SetDirty(dbg);
    Debug.Log(col.Report(), dbg);
}
```

`AtlasApplyDebug` 에 추가하는 것은 **세터 하나뿐**이다. `targets` 가 `[SerializeField] private` 이라
외부에서 못 쓰기 때문이다(`AtlasApplyDebug.cs:33`).

```csharp
// Samples/AtlasApplyDebug.cs — 신설(유일한 수정)
public void SetCollected(MeshFilter[] receivers, MeshFilter[] occluderOnly)
{
    targets   = receivers   ?? System.Array.Empty<MeshFilter>();
    occluders = occluderOnly ?? System.Array.Empty<MeshFilter>();
}
```

`ResolveTargets()` 의 occluder 차집합(`:1609-1614`)이 그대로 살아 있으므로,
세터가 두 배열을 채우면 **기존 계약대로** 동작한다. 베이크 경로는 한 줄도 바뀌지 않는다(결정 ①).

---

## 7. 리포트 형식

```
[C0] 수집 완료 — receiver 128 · occluder 341 · excluded 27  (roots 4, 순회 496건, 21ms)
  분류 사유:
    OccluderOnly  ReceiveGiLightProbes  312
    OccluderOnly  UserOccluder           29
    Excluded      LodChild               18
    Excluded      NotReadable             6   ← 아틀라스 슬롯만 먹고 차폐에서 빠지던 케이스
    Excluded      NotContributeGI         2
    Excluded      NoTriangles             1
  경고:
    UV0 없음 4건 → 해당 메시는 α 알파 컷아웃 비활성 (Rock_01, Rock_02, …)
  상세(NotReadable):
    Tree_Willow_LOD0, Fence_A, Fence_B, …   → 임포터 Read/Write Enabled 를 켜세요
```

`Report(maxDetail)` 이 이 문자열을 만든다. 사유별 집계를 먼저, 상세 목록은 뒤에.
`NotReadable` 은 조치 문구를 함께 낸다 — 사용자가 가장 자주 만나고, 원인을 가장 모르는 항목이라서다.

---

## 8. 테스트

`Tests/SceneCollectPolicyTests.cs`, 프로젝트 규약대로 `public static string RunAll()` 반환.
`LightmapEvaluateDebugger.RunAllTests`(`:58~78`)에 한 줄 추가 + 전용 ContextMenu.

```csharp
sb.AppendLine(SceneCollectPolicyTests.RunAll());   // C0 수집 정책(결정성·배타성·LOD·사유 우선순위)
```

```csharp
[ContextMenu("Run Scene Collect Tests (C0)")]
public void RunSceneCollectTests() => Debug.Log(SceneCollectPolicyTests.RunAll(), this);
```

| 케이스 | 게이트 | 내용 |
|---|---|---|
| T1 순서 보존 | C0-V1 | 후보 N개 → `Entries[i].Order == i`, `ReceiverIdx` 오름차순 |
| T2 결정성 | C0-V1 | 같은 입력 2회 `Run` → 결과 배열 완전 일치 |
| T3 배타성 | C0-V2 | `ReceiverIdx ∩ OccluderIdx == ∅`, 합 + Excluded = 전체 |
| T4 사유 우선순위 | — | `UserExcluded ∧ !MeshReadable` → 사유는 `UserExcluded`(§4.3 순서) |
| T5 NotReadable | C0-V4 | readable=false → Excluded, ContributeGI 켜져 있어도 |
| T6 LOD | C0-V5 | `IsLodChild` → 기본 Excluded, `LodChildAsOccluder` 면 OccluderOnly |
| T7 옵션 | — | `NonGiAsOccluder` on/off 로 !ContributeGI 결과가 뒤집힘 |
| T8 경고 무제외 | — | `!HasUv0` 인 정상 후보가 **Receiver 로 남음**(WarnOnly) |

**정책이 순수 함수라 이 8개는 전부 헤드리스로 돈다** — 씬·에디터·GPU 불필요.
씬이 필요한 C0-V3(비트동일)·C0-V8(실 씬 스모크)는 에디터 수동 절차로 남긴다.

---

## 9. 구현 순서 체크리스트

| 단계 | 파일 | 완료 조건 |
|---|---|---|
| **C0-0** | `Runtime/Collect/SceneCollection.cs`, `SceneCollectPolicy.cs` | T1~T8 통과 |
| **C0-1** | `Editor/SceneCollector.cs` | 실 씬에서 후보 수 = 계층의 활성 MeshFilter 수 |
| **C0-2** | `SceneCollection.Report()` | §7 형식 출력, NotReadable 조치 문구 포함 |
| **C0-3** | `Editor/SceneCollectMenu.cs`, `AtlasApplyDebug.SetCollected` | 메뉴 → 배열 채움 → 베이크 완주 (**C0-V3 비트동일**) |
| **C0-4** | `Runtime/HalfEdge.cs` 접근자 공개 | `Run All Tests` 109/109 유지 (**C0-V7**) |
| **C0-5** | (선택) R/W 일괄 수정, 레이어 필터, 프리뷰 기즈모 | — |

C0-3 이 끝나면 "실 씬을 한 번에 굽는다"가 성립한다. C0-4/5 는 품질·편의.

---

## 10. 구현 시 확인할 것 (미확정)

1. **`MeshRenderer.receiveGI` 의 런타임 접근성** — `UnityEngine.ReceiveGI` enum 은 있으나 이 프로퍼티가
   런타임 빌드에서도 유효한지 확인한다. 불확실하면 **Editor 어댑터에서만 읽고** 정책에는 bool 로 넘긴다
   (지금 설계가 이미 그렇게 돼 있어 어느 쪽이든 안전하다).
2. **프리팹 스테이지** — 프리팹 편집 중 `GetActiveScene()` 은 프리팹 스테이지 씬을 준다.
   의도치 않은 수집을 막으려면 `PrefabStageUtility.GetCurrentPrefabStage() != null` 일 때 경고 후 중단.
3. **`Undo.RecordObject` 로 배열 필드 기록이 충분한지** — 배열 참조 교체라 `RecordObject` 로 잡히지만,
   실제로 Ctrl+Z 가 복원하는지 손으로 확인한다.
4. **멀티 씬 편집** — 여러 씬이 열려 있으면 `GetActiveScene()` 만으로는 부족하다.
   `SceneManager.sceneCount` 루프로 확장할지 결정(기본은 활성 씬만, 옵션으로 전체).
5. **`GetIndexCount` 의 토폴로지** — 메시가 `MeshTopology.Triangles` 가 아니면 `/3` 이 틀린다.
   `mesh.GetTopology(s) != Triangles` 면 그 메시를 Excluded(NoTriangles)로 내리고 사유에 표기.
