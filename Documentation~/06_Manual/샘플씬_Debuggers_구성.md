# 샘플 씬 `Debuggers` 구성 정리

> 기준: 2026-10-28 · 씬 `Samples/Mesh_Struct_Sample_Scene.unity` · Unity 6000.7.0b4 · 매뉴얼 `06_Manual/챕터별_테스트_실행_매뉴얼.md`(v15.19)
> 목적: 매뉴얼의 챕터별 실행 소스·하니스를 샘플 씬 `Debuggers` 아래에 빠짐없이 두고, 우클릭 한 번으로 바로 돌릴 수 있게 한다.

---

## 1. 결론

- 매뉴얼에 나오는 MonoBehaviour 하니스 중 **7종이 씬에 없었다** → `Debuggers` 자식으로 추가했다.
- 디버그용 메시가 C0 씬 수집에 섞이지 않도록 `Debuggers` 루트에 `BakeSceneCollectOverride(Exclude)` 를 붙였다.
- `Debuggers` 자식 14개 전부를 **기획 단계 순서로 정렬하고 이름 앞에 단계 ID** 를 붙였다(§2).
- 추가한 하니스 중 실행해도 에셋이 생기지 않는 6종은 한 번씩 돌려 봤고 모두 정상이다(§5). `B1_ProbeVolumeBaker` 는 Bake 가 에셋을 쓰므로 돌리지 않고 설정만 확인했다.
- **씬은 저장하지 않았다.** 작업 전부터 씬에 저장 안 된 변경이 있었기 때문이다(§7).

---

## 2. 이름 · 순서 (기획 단계순)

순서 기준은 `04_Complete_Dev_Docs/01_기획_단계별_정리.md` §7 마일스톤(A1~A4 → C1 → C2 → C2.5 → … → SH-1~5 → SH-G → B1)이다. 같은 단계 안에서는 해당 문서의 절 순서를 따랐다. 이름 형식은 `<단계ID>_<원래 이름>` 이다. `/` 는 Transform 경로 구분자라 쓰지 않았다.

| # | 오브젝트 | 단계 | 근거 |
|---|---|---|---|
| 0 | `A1_ChartSegmentationDebugger` | A1 | A1.2 차트 분할 |
| 1 | `A3_ParameterizationTestDebugger` | A3 | A2/A3 평탄화 — Planar → LSCM → MVC 디스패처(A3). 체크리스트 §3.6 "A3" 항목 |
| 2 | `A4_UVAssemblerTestDebugger` | A4 | A4.3 조립(`UVAssembly.Assemble` + `SeamTable`) |
| 3 | `A4_TexelMapperDebug` | A4 | A4' 텍셀 복원 |
| 4 | `A4_LightmapAllocatorDebug` | A4 | A4'' per-instance ST 할당 |
| 5 | `C1_BVHGizmo` | C1 | 가속구조 BVH |
| 6 | `C2_LightmapEvaluateDebugger` | C2 | RadianceCore(AO·Direct·Indirect) — **Run All Tests 38종 진입점**도 여기 |
| 7 | `C2_InstancedSceneTester` | C2 | 2단 인스턴싱 Radiance ≡ brute (매뉴얼 §2.6) |
| 8 | `C2.5_AtlasApplyDebug` | C2.5 | GI 베이크 연결(`RadianceGI`). 이후 G·후처리·α·SSAA·C0·로컬 광원 기능도 이 컴포넌트에 붙었다 |
| 9 | `SH-1_SH9BakeTextureTest` | SH-1 | `SH9` 프로젝션 |
| 10 | `SH-4_SHBakeDemo` | SH-4 | 뷰·데모(`SHBakeDemo`·`SHDebugView`). 같은 오브젝트의 `SHRenderDemo` 는 SH-5 배선·SH-G 토글 |
| 11 | `SH-5_SHEvalProbeVerify` | SH-5 | GPU 디코드 자동화(`SHEvalProbeGpuTests`) |
| 12 | `SH_InstanceSceneBaker` | SH | SH 트랙 실사용 베이커(v15.4, SH-G 이후). 단계 번호가 따로 없어 트랙 이름만 붙였다 |
| 13 | `B1_ProbeVolumeBaker` | B1 | Track B 프로브 볼륨(B1-1~6) |

- 씬에 G0~G6·α·SSAA·로컬 광원 전용 오브젝트는 없다. 모두 `C2.5_AtlasApplyDebug` 의 필드·우클릭 메뉴다.
- C0 표식 `BakeSceneCollectOverride` 는 자식이 아니라 `Debuggers` 루트에 있어 정렬 대상이 아니다.
- 이름 변경과 순서 변경은 모두 Undo 로 되돌릴 수 있다.

**이름 변경 영향**: 베이커 3종은 결과 파일 이름에 GameObject 이름을 쓴다(`<씬>_<이름>_Atlas` · `_ProbeVolume` · `_InstanceSH`). 세 컴포넌트 모두 지금 `bakeAsset`/`volumeAsset` 이 비어 있다. 그래서 다음 저장부터는 새 이름의 파일이 생긴다(예: `Mesh_Struct_Sample_Scene_C2.5_AtlasApplyDebug_Atlas.asset`). 씬의 `GameObject` 그룹이 쓰는 uv2 메시(`Cube_uv2`·`Capsule_uv2`)는 기존 `Mesh_Struct_Sample_Scene_AtlasApplyDebug_Atlas.asset` 의 서브에셋이라 그 파일은 지우면 안 된다.

---

## 3. 작업 전 `Debuggers` 구성 (7개)

이름은 작업 전 기준이다.

| 작업 전 이름 → 지금 이름 | 컴포넌트 | 매뉴얼 장 | 주요 설정 |
|---|---|---|---|
| TexelMapperDebug → `A4_TexelMapperDebug` | `TexelMapperDebug` | 2.3 A4 | `targetMesh = Cube`(내장), 256 |
| LightmapAllocatorDebug → `A4_LightmapAllocatorDebug` | `LightmapAllocatorDebug` | 2.3 A4 | `targets` 9개(Cube 계열) |
| AtlasApplyDebug → `C2.5_AtlasApplyDebug` | `AtlasApplyDebug` | 2.4 · 2.7~2.12 | `targets` 18개, `mode = PerInstanceColor`, `radianceBackend = CPU` |
| LightmapEvaluateDebugger → `C2_LightmapEvaluateDebugger` | `LightmapEvaluateDebugger` | 2.5 · 2.6 · §3 테스트 | `occluders` 18개 |
| SHBakeDemo → `SH-4_SHBakeDemo` | `SHDebugView` · `SHBakeDemo` · `SHRenderDemo` | 2.13 | `shBakeBackend = Gpu` |
| BVHGizmo → `C1_BVHGizmo` | `BVHGizmoDebugger` | 2.5 C1 | `targets` 18개, SAH |
| InstanceSceneBaker → `SH_InstanceSceneBaker` | `InstanceSceneBaker` | 2.13 | `templates[0]` 비어 있음(mesh·행렬 없음) |

씬 루트에는 `HalfEdgeConversion`(2.1 A1, `targetMesh = Warrior_Body`)이 따로 있다. 루트 오브젝트라 이름을 바꾸지 않았다.

---

## 4. 추가한 것

### 4.1 하니스 7종

| 오브젝트 (월드 위치) | 컴포넌트 | 매뉴얼 장 | 설정 | 이 설정을 고른 이유 |
|---|---|---|---|---|
| `A1_ChartSegmentationDebugger` (-0.22, 1, -44.94) | `ChartSegmentationDebugger` | 2.1 A1 | `targetMesh = Capsule`(프리미티브) · 나머지 기본값 | 베이크 대상(`GameObject` 그룹)이 Cube/Capsule 이라 같은 메시로 분할을 본다. 기즈모는 이 Transform 기준이라 큐브 무리와 겹치지 않는 자리에 둠 |
| `A3_ParameterizationTestDebugger` (2.78, 1, -44.94) | `ParameterizationTestDebugger` | 2.2 A2/A3 | `targetMesh = Capsule` | 원통 + 반구 → Planar 가 실패하고 LSCM 을 타는 차트가 생긴다. 에디터 체크리스트 §3.6 "A3: 원통·구 메시 … methods 집계" 항목 그대로 |
| `A4_UVAssemblerTestDebugger` (5.78, 1, -44.94) | `MeshFilter(Capsule)` · `MeshRenderer`(URP 기본 머티리얼) · `UVAssemblerTestDebugger` | 2.2 UV 조립 | `targetMesh = Capsule` · `applyToMeshFilter`·`copyUV2ToUV0` 끔(기본) | 매뉴얼의 육안 확인(`applyToMeshFilter` + `copyUV2ToUV0`)은 같은 오브젝트에 MeshFilter 가 있어야 한다. `targetMesh` 를 따로 고정해 둬서, 조립 메시를 입힌 뒤 다시 돌려도 원본 Capsule 에서 출발한다 |
| `C2_InstancedSceneTester` (Debuggers 원점) | `InstancedSceneTester` | 2.6 C2 | `targets` = `GameObject` 그룹 MeshFilter 18개 | 비워 두면 자기+자식만 모아 "MeshFilter 없음" 으로 끝난다. 같은 메시(Cube_uv2 ×11, Capsule_uv2 ×7)를 공유해 BLAS 공유 경로까지 검증된다 |
| `B1_ProbeVolumeBaker` (-9.5, 1.1, -44.4) | `ProbeVolumeBaker` | 2.14 Track B · 2.4 C0 | 위치 = 그룹 바운드 중심 · `volumeSize (12,10,12)` · `probeSpacing (1,1,1)` → 13×11×13 = 1,859 프로브 · `staticSource = Roots` · `staticRoots = [GameObject]` · `sun = Directional Light` · `templates` 비움 | 그룹(약 8.6×7.7×7.3)을 덮는 최소 볼륨. 템플릿 없이도 정적 메시만으로 격자가 구워진다. 우클릭 **Collect Scene (C0)** 를 누르면 `staticSource = Collected` 로 바뀌어 체크리스트 §6(v15.19) 항목을 바로 확인할 수 있다 |
| `SH-1_SH9BakeTextureTest` (Debuggers 원점) | `SH9BakeTextureTest` | 2.13 SH | 기본값 | Run Self Tests 는 파일을 안 만든다. Bake SH & Write Texture 는 `Assets/CustomLightmapper/BakeOutput/SH9_BakeTest.png` 를 쓴다 |
| `SH-5_SHEvalProbeVerify` (Debuggers 원점) | `SHEvalProbeVerify` | 2.13 SH-5 | 기본값(`epsilon 0.002`, `projDirs 512`) | `SHEvalProbeGpuTests` 를 허용오차를 바꿔 단독 실행하는 용도 |

모두 Undo 로 되돌릴 수 있게 추가했다.

### 4.2 C0 표식

| 대상 | 컴포넌트 | 설정 | 이유 |
|---|---|---|---|
| `Debuggers` 루트 | `BakeSceneCollectOverride` | `mode = Exclude` · `includeChildren = true` | `A4_UVAssemblerTestDebugger` 의 Capsule 렌더러가 Collect Scene (C0) 에 Receiver/Occluder 로 끼어들지 않게 막는다. Static `Contribute GI` 규칙이 v15.21 에서 빠져 비정적이어도 Receiver 로 잡히므로 이 표식이 유일한 제외 수단이다. 앞으로 `Debuggers` 아래에 디버그 메시를 더 둬도 계속 제외된다. C0 리포트에 `UserExcluded 1` 이 찍히는 게 정상이다 |

### 4.3 추가하지 않은 것

| 항목 | 이유 |
|---|---|
| `HalfEdgeConversion` | 씬 루트에 이미 있다(Warrior_Body) |
| `ObjectRandomTranslator` | 테스트 씬 배치용 유틸. Undo 가 없어 샘플 씬 배치를 망가뜨릴 수 있다 |
| `MeshCombiner` | `CombineMeshes()` 가 미완성 스텁 |
| 테스트 클래스 38종 · RunAll 밖 테스트 | 컴포넌트가 필요 없다. `C2_LightmapEvaluateDebugger` 우클릭 메뉴와 `C2.5_AtlasApplyDebug` 우클릭 메뉴, 상단 `HuskyLibs/…`·`Husky/…` 메뉴로 실행한다 |

---

## 5. 실행 확인 (2026-10-28, 라이브 에디터)

| 실행 | 결과 |
|---|---|
| `A1_ChartSegmentationDebugger` — Run Segmentation | charts=7, faces=832 |
| `A1_ChartSegmentationDebugger` — Run Segmentation - Welded | charts=8, faces=832, 모든 차트 loops=1 |
| `A3_ParameterizationTestDebugger` — Run Parameterization Check | 8 차트 전부 OK · foldover 0 · degenerate 0 · **Planar 3 / LSCM 5 / MVC 0** · LSCM stretch 1.033~1.063 → 1.000~1.014 |
| `A4_UVAssemblerTestDebugger` — Run UV Assemble | 6/6 OK · 정점 524(= Σ차트) · 삼각형 832 보존 · 시임 그룹 96 |
| `C2_InstancedSceneTester` — Run Instanced Scene Test | **5/5 PASS** · 고유 메시 2, 인스턴스 18, BLAS 2, 삼각형 5,956 |
| `SH-1_SH9BakeTextureTest` — Run Self Tests | **4/4 passed** |
| `SH-5_SHEvalProbeVerify` — Verify GPU vs CPU SH | **10/10 PASS** (maxErr 0.00000) |
| `B1_ProbeVolumeBaker` | Bake 는 안 돌림(에셋 생성). 격자 13×11×13 = 1,859, staticRoots 1, sun 연결 확인 |

체크리스트 반영: `03_Progress_And_Validation/에디터검증_체크리스트_v15.12-v15.18.md` §1(Missing Script) · §2.3 · §3.6(A3) · §5 결과 기록.

**관찰**: Welded 분할에서 면 1개짜리 차트가 2개 나온다(면적 0.0014, Planar 로 처리됨). 오류는 아니지만 아틀라스에서 작은 차트로 자리를 차지한다 — 분할 결과를 볼 때 참고.

---

## 6. 챕터 ↔ `Debuggers` 커버리지

| 장 | 챕터 | 씬에서 쓰는 오브젝트 | 비고 |
|---|---|---|---|
| 2.1 | A1 메시 구조 · 차트 분할 | 루트 `HalfEdgeConversion` · `A1_ChartSegmentationDebugger` | |
| 2.2 | A2/A3 평탄화 · UV 조립 | `A3_ParameterizationTestDebugger` · `A4_UVAssemblerTestDebugger` | + 메뉴 `Husky/Tools/UV Layout Viewer` |
| 2.3 | A4 할당 · 텍셀 | `A4_TexelMapperDebug` · `A4_LightmapAllocatorDebug` | |
| 2.4 | C0 씬 수집 | `C2.5_AtlasApplyDebug` · `B1_ProbeVolumeBaker` 우클릭 Collect Scene (C0) · `BakeSceneCollectOverride`(Debuggers 루트) | |
| 2.5 | C1 BVH | `C1_BVHGizmo` · `C2_LightmapEvaluateDebugger`(BVH Mesh/Scene Test) | |
| 2.6 | C2 라이팅 코어 | `C2_LightmapEvaluateDebugger`(Evaluate) · `C2_InstancedSceneTester` | |
| 2.7 | 로컬 광원 | `C2.5_AtlasApplyDebug` · `SH_InstanceSceneBaker` · `B1_ProbeVolumeBaker` 의 로컬 광원 설정 | **씬에 Point/Spot/Area 라이트가 없다**(Directional 1개뿐) |
| 2.8~2.11 | Burst · GPU · Track A · 후처리 | `C2.5_AtlasApplyDebug` | `radianceBackend = CPU`(기본값) — 실사용은 Burst/Gpu |
| 2.12 | α 알파 컷아웃 | `C2.5_AtlasApplyDebug` | **씬에 알파 컷아웃 머티리얼이 없다** |
| 2.13 | SH 인스턴싱 | `SH-1_SH9BakeTextureTest` · `SH-4_SHBakeDemo` · `SH-5_SHEvalProbeVerify` · `SH_InstanceSceneBaker` | `SH_InstanceSceneBaker` 의 `templates[0]` 비어 있음 |
| 2.14 | Track B 프로브 볼륨 | `B1_ProbeVolumeBaker` | 템플릿(식생) 없음 — 정적 메시만 |
| §3 | 전체 회귀 38종 | `C2_LightmapEvaluateDebugger` 우클릭 | |

---

## 7. 남은 일 · 주의

1. **씬 저장 필요** — 작업 전에 이미 `isDirty = true` 였다(내 변경 전부터 저장 안 된 변경이 있음). 내용을 확인한 뒤 직접 Ctrl+S. 저장하면 패키지 서브모듈의 `Samples/Mesh_Struct_Sample_Scene.unity` 가 바뀐다.
2. **`B1_ProbeVolumeBaker` Bake 미실행** — 우클릭 Bake 시 `Assets/CustomLightmapper/BakeOutput/Mesh_Struct_Sample_Scene_B1_ProbeVolumeBaker_ProbeVolume.asset`(+EXR) 생성. 이어 Collect Scene (C0) → `staticSource = Collected` 로 Roots ≡ Collected 확인.
3. **이 씬으로 확인할 수 없는 챕터** — 로컬 광원(2.7)은 Point/Spot/Area 라이트를, α(2.12)·B1-6 은 알파 컷아웃 머티리얼(잎 카드)을 씬에 넣어야 실측된다. 테스트 클래스(`LocalLightBakeTests`·`AlphaCutoutTests` 등)는 임시 오브젝트로 돌기 때문에 이 씬과 무관하게 통과 여부를 볼 수 있다. 로컬 라이트를 추가하면 광원 출처 기본값이 Scene 이라 기존 베이크 결과가 달라진다.
4. **`SH_InstanceSceneBaker` / `SHRenderDemo` 입력 없음** — `templates[0].mesh` 와 `TestMeshes` 가 비어 있다. SHRenderDemo 는 데모 박스로 동작하지만 InstanceSceneBaker 는 템플릿을 채워야 Bake 가 의미 있다.
5. **내장 메시 주의(6000.7)** — `Resources.GetBuiltinResource<Mesh>("Capsule.fbx")` 는 프리미티브 캡슐이 아니라 `polySurface2`(410 정점, 2×4×2)를 돌려준다. 프리미티브 캡슐(`Capsule`, 550 정점, 1×2×1)은 `GameObject.CreatePrimitive(PrimitiveType.Capsule)` 의 `sharedMesh` 로 얻었다.
