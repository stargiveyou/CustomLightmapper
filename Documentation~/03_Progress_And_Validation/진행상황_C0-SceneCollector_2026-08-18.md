# 커스텀 라이트매퍼 — 진행 상황 (C0 SceneCollector / 2026-08-18)

> **작업 추적용 문서.** 구현하면서 체크박스·게이트 결과·이슈를 이 파일에 채운다.
> 선행 스냅샷: `진행상황_G0-Burst_2026-06-29.md` 이후 A/C/G/SH/후처리/α/SSAA 완료
> (완료 현황은 `04_Complete_Dev_Docs/00_INDEX.md` 대시보드가 기준).

## 1. 한 줄 요약

**C0 = 파이프라인 단계 1(씬 수집·분류). C0-0~C0-4 구현 완료(2026-10-26, 개발문서 v15 §12 v15.12) — 에디터 게이트(V3·V4·V5 씬·V6~V8) 대기.**
지형 QuadTree 를 로드맵에서 제외(2026-08-18)하면서 최우선 항목이 됐다.
목표는 "실 씬을 한 번에 굽는 진입점" — `AtlasApplyDebug` 우클릭 **Collect Scene (C0)** 한 번으로 targets/occluders 를 채운다.
헤드리스 정책 테스트 `SceneCollectPolicyTests` 20/20 PASS(.NET 8 콘솔에서 실제 실행 — 에디터 밖).

## 2. 문서 지도

| 문서 | 역할 | 상태 |
|---|---|---|
| `01_Planning_And_Design/커스텀_라이트매퍼_C0_씬수집_설계.md` | **왜** — 결정 ①~⑧, 게이트 C0-V1~V8, 리스크 | ✅ v1 |
| `02_Development_And_Reference/커스텀_라이트매퍼_C0_SceneCollector_개발문서.md` | **무엇을 어떻게** — 타입 시그니처, 판정 순서, 배선, 테스트 | ✅ v1 |
| `03_Progress_And_Validation/진행상황_C0-SceneCollector_2026-08-18.md` | **어디까지 했나** — 이 문서 | ◕ 구현 완료 · 에디터 게이트 대기 |
| `02_Development_And_Reference/커스텀_라이트매퍼_개발문서_v15.md` §5.8 · §10 · §12 v15.12 | 구현 요약 · 로드맵 위치(구 v14 §10.1 은 병합 때 v15 §10 으로 이동) | ✅ 갱신됨 |

## 3. 작업 체크리스트

### C0-0 — 정책 코어 (Runtime, 헤드리스)

- [x] `Runtime/Collect/SceneCollection.cs` — `CollectClass` · `CollectReason`(+확장 4종) · `CollectWarning` · `CollectCandidate` · `CollectEntry` · `SceneCollection` · `SceneCollectOptions`
- [x] `Runtime/Collect/SceneCollectPolicy.cs` — `Classify` · `Warnings` · `Run` · `Pick`
- [x] `Tests/SceneCollectPolicyTests.cs` — T1~T8 + T9~T13(판정 순서 전수·확장 사유·경고 범위·Report·Pick)
- [x] `Samples/LightmapEvaluateDebugger.cs` — `RunAllTests` 배선 + ContextMenu "Run Scene Collect Tests (C0)"

**완료 조건**: T1~T8 전부 PASS. 에디터·씬·GPU 없이 실행됨. → ✅ 20/20 PASS(.NET 8 콘솔, 2026-10-26). 규칙 순서를 일부러 뒤집은 변이본에서 T4·T9 가 FAIL 하는 것도 확인.

### C0-1 — Editor 수집기

- [x] `Editor/SceneCollector.cs` — 씬 순회 · `StaticEditorFlags` · `LODGroup` · `receiveGI` · `SceneCollectOverride` 조회 → 정책 호출
- [x] LOD0 우선 규칙(같은 렌더러가 LOD0·LOD1 양쪽에 있으면 LOD0 승) — 전 LODGroup 에서 모은 뒤 차집합(그룹 순서 무관)

**완료 조건**: 실 씬에서 후보 수 == 계층의 활성 `MeshFilter` 수. → ⬜ 에디터 확인(리포트 머리줄 '순회 N건').

### C0-2 — 진단 리포트

- [x] `SceneCollection.Report(maxDetail)` — 사유별 집계(건수 내림차순) → 경고 → 상세(Excluded 사유별)
- [x] `NotReadable` 에 조치 문구("임포터 Read/Write Enabled") 포함
- [x] `!HasUv0` 경고(해당 메시 α 비활성) 표기

**완료 조건**: 개발문서 §7 형식으로 출력. → ✅ T12.

### C0-3 — AtlasApplyDebug 배선 ★ 여기까지가 최소 완결

- [x] `Runtime/LightmapAllocator/AtlasApplyDebug.cs`(병합으로 Runtime 이동) — `public void SetCollected(MeshFilter[], MeshFilter[])` **한 개만** 추가
- [x] `Editor/SceneCollectMenu.cs` — CONTEXT 메뉴 → `Collect` → `SetCollected` → `Report` 로그 → `Undo`/`SetDirty`(+ receiver 0 이면 적용 안 함)
- [ ] **C0-V3 비트동일 확인** (아래 게이트 표) — 에디터

**완료 조건**: 메뉴 한 번으로 실 씬 수집 → 베이크 완주 + 손으로 넣은 것과 아틀라스 비트동일.

### C0-4 — MeshCleaner 추출

- [x] `Runtime/HalfEdge.cs` — `WeldVertices` · `RemoveDegenerateTriangles` 접근자만 `private` → `public`(+ `WeldEpsilon` 상수 공개, 값 무변경)
- [x] 호출처: `WeldedHalfEdge` 생성자 + 진단 메뉴(`SceneCollector.DiagnoseCleanup`, 선택 오브젝트·보고만)뿐 — 차폐 씬/베이크 경로에서 호출 0
- [ ] **C0-V7 회귀** — 에디터 "Run All Tests" 26종(병합 후 기준)

**완료 조건**: 로직 무변경 · 회귀 통과.

### C0-5 — 선택 항목 (필요할 때)

- [ ] R/W 일괄 수정 버튼(`ModelImporter.isReadable = true`)
- [ ] 레이어/태그 필터 프리셋
- [ ] 수집 결과 프리뷰 기즈모(receiver/occluder 색 구분)

## 4. 검증 게이트 기록

구현하면서 채운다. 실측 수치를 그대로 적는다(추정 금지).

| ID | 항목 | 방법 | 기준 | 결과 |
|---|---|---|---|---|
| C0-V1 | 수집 결정성 | 같은 씬 2회 수집 → 순서·집합 비교 | 완전 일치 | ◐ 정책 ✅(T1·T2) · 씬 ⬜ 메뉴 `Scene Collect (C0)/Verify Determinism (C0-V1)` |
| C0-V2 | 분류 배타성 | receiver ∩ occluder | ∅ | ✅ T3(500 후보 · Class 와 인덱스 집합 일치 — 구조적으로 보장) |
| C0-V3 | **기존 경로 비트동일** | 수집기 배열 vs 손 배열로 같은 씬 베이크 | 아틀라스 비트동일 | ⬜ |
| C0-V4 | R/W 미설정 | non-readable 섞은 씬 | 양쪽 제외 + 리포트, 빈 슬롯 0 | ◐ 정책 ✅(T5·T12 조치 문구) · 씬 ⬜ |
| C0-V5 | LOD | LODGroup 3단 씬 | LOD0만 receiver | ◐ 정책 ✅(T6) · LOD 매핑(`LodChildRenderers`) 씬 ⬜ |
| C0-V6 | **α 정합 무손상** | 수집 경로 씬에서 `AlphaMaskBuilder` 로그 | `[정합오류]` 0건 | ⬜ |
| C0-V7 | 회귀 전수 | `Run All Tests (C1+C2)` | 전 PASS · 드리프트 0 (병합 후 26종) | ⬜ |
| C0-V8 | 실 씬 스모크 | 수백 오브젝트 "Collect Scene" → 베이크 | 완주 + 수집 시간 로그 | ⬜ |

**V3·V6 이 핵심이다** — 이 둘이 통과하면 C0가 기존 검증 자산(109 테스트 + α 수치)을 하나도 무효화하지 않았다는 뜻이다.

## 5. 구현 중 반드시 지킬 것 (함정 모음)

문서 여기저기 흩어진 제약을 한 곳에 모았다. **이 5개가 C0에서 사고가 날 수 있는 전부다.**

1. **정제한 메시를 차폐 씬에 넣지 말 것** (설계 결정 ⑥)
   차폐 `Tri[]` 는 `mesh.triangles` 순서를 쓰고 α의 `TriUV`/`TriSubmesh` 가 1:1 대응한다.
   삼각형 수가 달라지면 `AlphaMaskBuilder.cs:317` 정합 검사에 걸려 **그 메시 알파가 조용히 꺼진다.**
   클린업은 파라미터화 입력 전용. → C0-V6

2. **수집 순서를 정렬하지 말 것** (설계 결정 ②)
   인스턴스 인덱스가 ST 할당과 per-lumel 시드 `seed + li*2654435761u` 에 직접 들어간다.
   순서가 흔들리면 CPU≡Burst≡GPU 교차검증이 통째로 무효. → C0-V1

3. **수집 단계에서 `LightmapAllocator.WorldArea` 호출 금지** (개발문서 §4.4)
   호출마다 `mesh.vertices`+`mesh.triangles` 관리배열 복사(`LightmapAllocator.cs:58-59`).
   게다가 `BakeAndApply`(`AtlasApplyDebug.cs:424`)가 어차피 다시 계산한다.
   면적 0 판정은 `renderer.bounds` 로. 삼각형 수는 `GetIndexCount`, UV0 유무는 `HasVertexAttribute`.

4. **베이크 경로 코드를 건드리지 말 것** (설계 결정 ①)
   `ResolveTargets`/`ResolveOccluderUnion`/`BuildGiScene`/할당·베이크 루프/후처리 전부 무변경.
   `AtlasApplyDebug` 에 추가하는 것은 `SetCollected` 세터 **하나뿐**. → C0-V3

5. **Samples asmdef 는 Editor asmdef 를 참조하지 않는다** (개발문서 §1)
   `AtlasApplyDebug` 안에서 `SceneCollector` 를 직접 못 부른다.
   메뉴는 `Editor/SceneCollectMenu.cs` 에 두고, 컴포넌트에는 세터만 노출한다.

## 6. 이슈 로그

구현 중 발견한 것을 여기 적는다. (α 트랙에서 `meshAlbedo` 미할당 같은 부수 발견이 나온 자리다.)

| 날짜 | 증상 | 원인 | 조치 |
|---|---|---|---|
| 2026-10-26 | 설계의 `Samples/AtlasApplyDebug.cs` 경로·"Samples 는 Editor 미참조라 세터만" 전제 | 병합(v15.11)으로 `AtlasApplyDebug` 가 **Runtime** 으로 이동 — Editor 가 직접 참조 가능 | 메뉴는 그대로 Editor(`SceneCollectMenu`)에 두고 CONTEXT 메뉴(`CONTEXT/AtlasApplyDebug/Collect Scene (C0)`)로. 세터 1개 원칙 유지 |
| 2026-10-26 | 수집 결과 receiver 0 일 때 `SetCollected` 하면 `targets` 가 비어 **자식 폴백**으로 바뀜 | `ResolveTargets` 계약(빈 targets = 자식 수집) | 메뉴가 receiver 0 이면 적용하지 않고 경고 + 리포트 |
| 2026-10-26 | 재수집하면 인스펙터에서 손으로 고친 배열이 덮어써짐(결정 ⑧ 의 손 수정 지점이 휘발) | 결과가 값(결정 ⑧)이라 재수집 = 재계산 | `SceneCollectOverride` 표식 컴포넌트(OccluderOnly / Exclude, includeChildren) — 수집기가 `userOccluders`/`userExcluded` 와 합집합으로 읽음. 베이크 경로는 모름 |
| 2026-10-26 | `MeshRenderer.receiveGI` 가 플레이어 빌드 참조 DLL(UnityEngine.Modules 2021.3)에 없음 | **Editor 전용 프로퍼티**(`scaleInLightmap`·`stitchLightmapSeams` 도 동일하게 빠짐) | 설계대로 Editor 어댑터에서만 읽고 정책엔 bool 로 전달 — §7 확인 1 확정 |
| 2026-10-26 | 개발문서 §4.3 판정표에 없는 케이스: 꺼진 MeshRenderer · Triangles 아닌 토폴로지 · WarnOnly=false 의 제외 사유 | — | 사유 4종 추가: `RendererDisabled`(5 뒤) · `NonTriangleTopology`(6 앞) · `NoUv0`/`TooManySubMeshes`(7 뒤, WarnOnly=false 에서만). 기존 12단계 순서·사유 값은 그대로 |

## 7. 구현 전 확인이 필요한 것 (개발문서 §10)

- [x] `MeshRenderer.receiveGI` — **Editor 전용**(플레이어 참조 DLL 에 없음, 이슈 로그). Editor 어댑터에서만 읽음
- [x] 프리팹 스테이지 가드 — 메뉴 전부 `PrefabStageUtility.GetCurrentPrefabStage() != null` 이면 경고 후 중단
- [ ] `Undo.RecordObject` 로 배열 필드가 실제 복원되는지 손으로 확인 — 에디터(+ 프리팹 인스턴스면 `RecordPrefabInstancePropertyModifications`)
- [x] 멀티 씬 편집 — 기본 활성 씬만, `Collect Scene - All Loaded Scenes (C0)` 메뉴로 로드된 씬 전부(SceneManager 순서 → 씬 파일 순서)
- [x] `MeshTopology != Triangles` 메시 → Excluded(**`NonTriangleTopology`** — NoTriangles 와 구분해 조치 문구 표시)

## 8. 완료 시 갱신할 문서 (2026-10-26 구현분 반영 — 에디터 게이트 통과 시 상태 ✅ 로)

C0 구현이 끝나면 아래를 같이 고쳐야 문서가 어긋나지 않는다.

- [x] `04_Complete_Dev_Docs/00_INDEX.md` — 단계 1 · 마일스톤 C0 행(◕ 구현 · 에디터 게이트 대기), 다음 단계 표에서 C0 제거(1순위 = 에디터 게이트, 2순위 = B1-6)
- [x] `04_Complete_Dev_Docs/01_기획_단계별_정리.md` — §9 C0 행 상태 갱신
- [x] `02_Development_And_Reference/커스텀_라이트매퍼_개발문서_v15.md`(병합 후 기준 문서) — §5.8 신설, §10 C0 항목, §12 v15.12 · 기획서 v15 §0/§8/§11/§12
- [x] `02_Development_And_Reference/커스텀_라이트매퍼_C0_SceneCollector_개발문서.md` — §10 확정 결과 · §11 구현 반영(문서 대비 차이)
- [ ] 이 문서 — 게이트 표 에디터 실측 기입, 상단 상태를 ✅ 로

## 9. 다음 단계

1. **에디터 게이트**: 재컴파일 0 에러 → `Run Scene Collect Tests (C0)` 20/20 → 샘플 씬에서 `Collect Scene (C0)` → 리포트 확인 → **C0-V3**(같은 집합을 손으로 넣은 배열과 아틀라스 비트동일) · **C0-V6**(`AlphaMaskBuilder` 로그 `[정합오류]` 0) · Undo 복원 · `Verify Determinism (C0-V1)`.
2. C0-5(선택): R/W 일괄 수정 버튼 · 레이어/태그 필터 · 프리뷰 기즈모 — 필요해질 때.
3. Track B: B1(B1-1~5)은 병합으로 이미 구현됨 → 다음은 **B1-6 알파 잎 차폐**. `ProbeVolumeBaker` 의 `staticRoots`/템플릿 입력을 C0 분류 결과로 대체하는 것은 그 뒤(Track B 설계 §7 씬 입력 이원화).
