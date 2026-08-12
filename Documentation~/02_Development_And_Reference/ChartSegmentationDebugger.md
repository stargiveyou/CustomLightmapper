# ChartSegmentationDebugger 사용 문서

`ChartSegementer`(차트 분할기)와 `ChartMeshBuilder`(차트별 로컬 메시·경계 루프 추출)를
**Unity 에디터/플레이에서 실행하고 시각화**하기 위한 디버그 MonoBehaviour.

> 위치: `Assets/Study/CustomLightmapper/Script/Test/ChartSegmentationDebugger.cs`
> 네임스페이스: `HuskyLibs.CustomLightmapper.Bake` (대상 로직은 `HuskyLibs.CustomLightmapper.Bake`)

---

## 1. 빠른 시작

1. 메시가 있는 GameObject(보통 `MeshFilter` 보유)에 **`ChartSegmentationDebugger`** 컴포넌트를 추가.
2. (선택) `Target Mesh` 를 직접 지정. 비우면 같은 오브젝트의 `MeshFilter.sharedMesh` 사용.
3. `Settings` 의 각도 파라미터 조정(아래 표).
4. 컴포넌트 헤더 우클릭 → 컨텍스트 메뉴 실행:
   - **Run Segmentation** — `HalfEdge`(비용접) 기반
   - **Run Segmentation - Welded** — `WeldedHalfEdge`(용접) 기반 *(경계 루프는 이 모드에서만)*
   - **Clear Result** — 캐시·기즈모 초기화
5. **씬뷰에서 오브젝트를 선택**하면 기즈모가 보인다 (`OnDrawGizmosSelected`).
6. Console 에 차트 통계 로그 출력.

---

## 2. 인스펙터 파라미터

### Settings (`SegmentationSettings`)
| 필드 | 기본 | 의미 |
|------|------|------|
| `SeamAngleDeg` | 40° | 이면각이 이 값을 넘는 모서리는 **시임**으로 사전 절단(차트 경계 강제). |
| `MaxChartAngleDeg` | 60° | 차트 평균 노멀과 후보 면의 허용 편차. 넘으면 면 편입 보류 → 새 차트 시드. |

### Debug View
| 필드 | 의미 |
|------|------|
| `drawGizmos` | 차트별 색으로 삼각형 외곽선 그리기. |
| `drawSeams` / `seamColor` | 시임 half-edge 를 별도 색으로 강조. |
| `bakeVertexColors` | 결과를 메시 **정점 컬러**로 굽기(`DebugColorize`). *정점 컬러 셰이더 필요.* |

### Chart Loops (Welded 전용)
| 필드 | 의미 |
|------|------|
| `drawChartLoops` | 차트 경계 루프를 폴리라인으로 그리기. |
| `outerLoopColor` | 외곽 루프 `Loops[0]` 색(기본 cyan). |
| `holeLoopColor` | 내부 홀 루프 `Loops[1+]` 색(기본 magenta). |
| `loopOffset` | 루프 라인을 차트 노멀 방향으로 띄워 면과의 z-fighting 감소. |

### Result (read-only)
| 필드 | 의미 |
|------|------|
| `chartCount` | 생성된 차트 수. |
| `faceCount` | 전체 면 수. |

---

## 3. 시각 요소 해석

| 색/요소 | 의미 |
|---------|------|
| 면 외곽선 색(랜덤 HSV) | **차트 ID** (id 기반 결정적 팔레트). 색이 바뀌는 경계 = 차트 분할선. |
| `seamColor` 선 | **시임** half-edge (경계이거나 이면각 초과). |
| `outerLoopColor` 폴리곤 | 차트의 **외곽 경계 루프**(둘레 최대). |
| `holeLoopColor` 폴리곤 | 차트 내부 **홀 경계 루프**. |

### 정상 동작 기준(검증 체크리스트)
- **Cube (Welded)**: 면 6개가 각자 차트 → 6색. 각 면 외곽이 cyan 사각 루프 1개씩.
- **Sphere (Welded)**: 노멀 편차에 따라 여러 차트로 분할, 각 차트가 닫힌 cyan 루프를 가짐.
- **빨강/마젠타 루프가 면 위에서 한 바퀴 닫혀야** 정상. 열려 있거나 끊기면 경계 워크 버그 신호.

---

## 4. 내부 동작 요약

```
ResolveMesh()
  └ targetMesh ?? MeshFilter.sharedMesh

Run() / Run_Welded()
  ├ HalfEdge / WeldedHalfEdge 빌드  (NativeArray, Allocator.Persistent)
  ├ ChartSegementer.GetResult(he, settings)         → FaceChart / Charts / HEseam
  ├ CacheGeometry(he.vertices, he.edges, he.faces)  → 기즈모용 정점/면 캐시
  ├ BuildPalette(chartCount)                        → 차트별 색
  ├ [Welded] ChartMeshBuilder.BuildAll(he, result)  → 차트별 ChartMesh + 경계 루프
  ├ Console 로그 (차트별 faces/area/loops/normal)
  └ finally { he.Dispose() }    ← NativeArray 즉시 해제(누수 방지)

OnDrawGizmosSelected()  [#if UNITY_EDITOR]
  ├ 차트색 삼각형 외곽선
  ├ 시임 강조
  └ 차트 경계 루프 폴리라인
```

### 핵심 설계 포인트
- **NativeArray 생명주기**: half-edge 는 실행 직후 `try/finally` 로 `Dispose`. 기즈모는
  네이티브 데이터를 참조하지 않고 **managed 배열로 복사한 캐시**(`cachedVertices`,
  `cachedFaceVerts`, `cachedCharts`)만 사용 → 매 프레임 안전.
- **지오메트리 추출이 mesh 비의존**: `mesh.triangles` 대신 half-edge 구조에서 면-정점을
  복원(`CacheGeometry`). 그래서 **용접본도 정확히** 그려진다.
- **경계 루프는 Welded 전용**: `ChartMeshBuilder` 가 `WeldedHalfEdge` 를 받는다. 비용접
  메시는 UV/노멀 seam 때문에 정점이 중복돼 거의 모든 에지가 경계가 되므로 루프가 무의미.
  → `Run()`(비용접)에서는 `cachedCharts = null` 로 루프를 그리지 않는다.

---

## 5. 제약 / 주의

- 기즈모는 **선택 시에만** 표시(`OnDrawGizmosSelected`). 항상 보이게 하려면
  `OnDrawGizmos` 로 바꾸면 된다.
- `bakeVertexColors`(`DebugColorize`)는 아직 **`mesh.triangles` 기준**이라 용접 모드에서는
  색 매핑이 어긋난다. 정점 컬러 베이킹은 비용접 `Run()` 에서만 정확. (기즈모 경로는 모두 정확)
- `BuildPalette` / `DebugColorize` 는 `Random.InitState` 로 **전역 랜덤 시드**를 건드린다
  (디버그 한정, 실행 시 1회).
- 차트 색은 차트 **개수**가 아니라 **id** 기반이라 실행마다 동일 차트는 동일 색(결정적).

---

## 6. 관련 파일

| 파일 | 역할 |
|------|------|
| `HalfEdge.cs` | `HalfEdge` / `WeldedHalfEdge` 자료구조(노멀·면적·prev 포함) |
| `ChartSegmentation.cs` | `ChartSegementer`(분할), `Chart`, `MinHeap` |
| `ChartMesh.cs` | `ChartMeshBuilder`(차트 로컬 메시 + 경계 루프 추출) |
| `Test/ChartSegmentationDebugger.cs` | 본 디버그 컴포넌트 |
