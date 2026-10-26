# 커스텀 라이트매퍼 — 진행 상황 (C0 SceneCollector / 2026-08-18)

> **작업 추적용 문서.** 구현하면서 체크박스·게이트 결과·이슈를 이 파일에 채운다.
> 선행 스냅샷: `진행상황_G0-Burst_2026-06-29.md` 이후 A/C/G/SH/후처리/α/SSAA 완료
> (완료 현황은 `04_Complete_Dev_Docs/00_INDEX.md` 대시보드가 기준).

## 1. 한 줄 요약

**C0 = 파이프라인 단계 1(씬 수집·분류). 설계·개발문서 완료, 구현 미착수.**
지형 QuadTree 를 로드맵에서 제외(2026-08-18)하면서 최우선 항목이 됐다.
목표는 "실 씬을 한 번에 굽는 진입점" — 지금은 `AtlasApplyDebug` 인스펙터에 손으로 끌어다 놓아야 한다.

## 2. 문서 지도

| 문서 | 역할 | 상태 |
|---|---|---|
| `01_Planning_And_Design/커스텀_라이트매퍼_C0_씬수집_설계.md` | **왜** — 결정 ①~⑧, 게이트 C0-V1~V8, 리스크 | ✅ v1 |
| `02_Development_And_Reference/커스텀_라이트매퍼_C0_SceneCollector_개발문서.md` | **무엇을 어떻게** — 타입 시그니처, 판정 순서, 배선, 테스트 | ✅ v1 |
| `03_Progress_And_Validation/진행상황_C0-SceneCollector_2026-08-18.md` | **어디까지 했나** — 이 문서 | 🔧 진행 중 |
| `02_Development_And_Reference/커스텀_라이트매퍼_개발문서_v14.md` §10.1 | 로드맵 위치 | ✅ 갱신됨 |

## 3. 작업 체크리스트

### C0-0 — 정책 코어 (Runtime, 헤드리스)

- [ ] `Runtime/Collect/SceneCollection.cs` — `CollectClass` · `CollectReason` · `CollectEntry` · `SceneCollection` · `SceneCollectOptions`
- [ ] `Runtime/Collect/SceneCollectPolicy.cs` — `Classify` · `Run`
- [ ] `Tests/SceneCollectPolicyTests.cs` — T1~T8
- [ ] `Samples/LightmapEvaluateDebugger.cs` — `RunAllTests` 배선 + ContextMenu "Run Scene Collect Tests (C0)"

**완료 조건**: T1~T8 전부 PASS. 에디터·씬·GPU 없이 실행됨.

### C0-1 — Editor 수집기

- [ ] `Editor/SceneCollector.cs` — 씬 순회 · `StaticEditorFlags` · `LODGroup` · `receiveGI` 조회 → 정책 호출
- [ ] LOD0 우선 규칙(같은 렌더러가 LOD0·LOD1 양쪽에 있으면 LOD0 승)

**완료 조건**: 실 씬에서 후보 수 == 계층의 활성 `MeshFilter` 수.

### C0-2 — 진단 리포트

- [ ] `SceneCollection.Report(maxDetail)` — 사유별 집계 → 경고 → 상세
- [ ] `NotReadable` 에 조치 문구("임포터 Read/Write Enabled") 포함
- [ ] `!HasUv0` 경고(해당 메시 α 비활성) 표기

**완료 조건**: 개발문서 §7 형식으로 출력.

### C0-3 — AtlasApplyDebug 배선 ★ 여기까지가 최소 완결

- [ ] `Samples/AtlasApplyDebug.cs` — `public void SetCollected(MeshFilter[], MeshFilter[])` **한 개만** 추가
- [ ] `Editor/SceneCollectMenu.cs` — MenuItem → `Collect` → `SetCollected` → `Report` 로그 → `Undo`/`SetDirty`
- [ ] **C0-V3 비트동일 확인** (아래 게이트 표)

**완료 조건**: 메뉴 한 번으로 실 씬 수집 → 베이크 완주 + 손으로 넣은 것과 아틀라스 비트동일.

### C0-4 — MeshCleaner 추출

- [ ] `Runtime/HalfEdge.cs` — `WeldVertices`(:449) · `RemoveDegenerateTriangles`(:495) 접근자만 `private` → `public`
- [ ] 호출처를 늘리지 않았는지 확인(`WeldedHalfEdge` 생성자 :301-302 가 여전히 유일한 실행 지점)
- [ ] **C0-V7 회귀** 109/109

**완료 조건**: 로직 무변경 · 회귀 통과.

### C0-5 — 선택 항목 (필요할 때)

- [ ] R/W 일괄 수정 버튼(`ModelImporter.isReadable = true`)
- [ ] 레이어/태그 필터 프리셋
- [ ] 수집 결과 프리뷰 기즈모(receiver/occluder 색 구분)

## 4. 검증 게이트 기록

구현하면서 채운다. 실측 수치를 그대로 적는다(추정 금지).

| ID | 항목 | 방법 | 기준 | 결과 |
|---|---|---|---|---|
| C0-V1 | 수집 결정성 | 같은 씬 2회 수집 → 순서·집합 비교 | 완전 일치 | ⬜ |
| C0-V2 | 분류 배타성 | receiver ∩ occluder | ∅ | ⬜ |
| C0-V3 | **기존 경로 비트동일** | 수집기 배열 vs 손 배열로 같은 씬 베이크 | 아틀라스 비트동일 | ⬜ |
| C0-V4 | R/W 미설정 | non-readable 섞은 씬 | 양쪽 제외 + 리포트, 빈 슬롯 0 | ⬜ |
| C0-V5 | LOD | LODGroup 3단 씬 | LOD0만 receiver | ⬜ |
| C0-V6 | **α 정합 무손상** | 수집 경로 씬에서 `AlphaMaskBuilder` 로그 | `[정합오류]` 0건 | ⬜ |
| C0-V7 | 회귀 전수 | `Run All Tests (C1+C2)` | 109/109 · 드리프트 0 | ⬜ |
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
| | | | |

## 7. 구현 전 확인이 필요한 것 (개발문서 §10)

- [ ] `MeshRenderer.receiveGI` 런타임 접근 가능 여부 — 불가면 Editor 어댑터에서만 읽는다(현 설계는 이미 안전)
- [ ] 프리팹 스테이지에서 `GetActiveScene()` 오수집 방지(`PrefabStageUtility.GetCurrentPrefabStage() != null` 가드)
- [ ] `Undo.RecordObject` 로 배열 필드가 실제 복원되는지 손으로 확인
- [ ] 멀티 씬 편집 — 활성 씬만 vs 전체(옵션)
- [ ] `MeshTopology != Triangles` 메시 → Excluded(NoTriangles) 처리

## 8. 완료 시 갱신할 문서

C0 구현이 끝나면 아래를 같이 고쳐야 문서가 어긋나지 않는다.

- [ ] `04_Complete_Dev_Docs/00_INDEX.md` — 단계 1 `◐` → `✅`, 마일스톤 C0 행, 다음 단계 표에서 C0 제거(1순위를 B1으로)
- [ ] `04_Complete_Dev_Docs/01_기획_단계별_정리.md` — §9 에서 C0 행 제거, 마일스톤 시간순 표에 C0 추가
- [ ] `02_Development_And_Reference/커스텀_라이트매퍼_개발문서_v14.md` — §10.1 제거·재번호, §12 변경 이력에 C0 항목 추가
- [ ] `02_Development_And_Reference/커스텀_라이트매퍼_C0_SceneCollector_개발문서.md` — §10 미확정 항목을 확정 결과로 교체
- [ ] 이 문서 — 게이트 표 실측 수치 기입, 상단 상태를 ✅ 로

## 9. 다음 단계

1. **C0-0 정책 코어** 착수 — 순수 함수라 씬 없이 바로 테스트가 돈다. 가장 싼 시작점.
2. C0-3 까지 완주 → C0-V3 비트동일 확인이 실질적 완료 판정.
3. C0 다음은 **B1(Track B, 범용 프로브 격자)** — 같은 수집 결과를 프로브 배치 입력으로 재사용한다.
