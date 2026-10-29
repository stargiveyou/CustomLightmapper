# Changelog

## [Unreleased]

### Docs — 희소 브릭(B4) 설계 문서 · Unity CLI 테스트 체크리스트 (2026-10-29)
- `Documentation~/01_Planning_And_Design/커스텀_라이트매퍼_희소브릭_설계.md` 신설 — 다층 공간용 희소 브릭을 Track B **B4**(선택, 미착수)로: 용어 · 구조(4³ 브릭·레벨 ×3·인디렉션 + 아틀라스) · 세분 판정 · 베이크 7단계(정수 격자 중복 제거·그래프 dilation·레벨 경계 이음매) · 저장 · 셰이더 계약(`_HuskyPV_Params.w = 2`) · 메모리 모델 · 단계 S1~S5 · 결정 항목 · 리스크 · 착수 기준. Track B 설계 §4.8 은 요약 + 링크.
- `Documentation~/03_Progress_And_Validation/UnityCLI_테스트_체크리스트.md` 신설 — `unity command eval`/`eval_file` 로 컴파일·compute·셰이더 메시지 → RunAll 40종 클래스별(B2 신규 2종 우선, 기대 443/443) → RunAll 밖 10개 → 실베이크 스모크(`ProbeVolumeBaker` Uniform → TerrainLayers 등) → 결과 기록. C# 스니펫(일괄 요약·로그 캡처·실베이크) 포함.
- 기획서 · 개발문서 · 00_INDEX · 매뉴얼 · 에디터검증 체크리스트에 링크. 기획서 단계 표의 B2 를 ✅ v15.22 로 고치고 B4 행 추가. 코드 변경 없음.

### Docs — 결정: B3(Unity 라이트 프로브 연동) 제외 · 희소 브릭 설계 메모 (2026-10-29)
- 기획서 §9.5 개발 원칙 신설: 커스텀 개발 — Unity GI 기능(라이트 프로브·APV·내장 라이트매퍼)을 쓰지 않는다. Track B 결정 D6 '자체 볼륨만, B3 보류' → **B3 제외**(기획서 · Track B 설계 · 개발문서 · 00_INDEX).
- Track B 설계 §4.8 에 희소 브릭(다층 공간용) 설계 메모 — 구조 · 재사용할 것 · 새로 필요한 것 · 비용 · 검증 계약 · 착수 기준. 코드 변경 없음.

### Added — Track B B2: 지형 추종 2.5D 프로브 (2026-10-28)
- `ProbeTerrainLayers`(Runtime/ProbeVolume): 결정 D1 의 적응형 배치 — XZ 균일 격자 × 열 (i,k) 지면 높이 + 층. 점 p 의 '열 격자'(origin.y 만 H(p.xz) + 첫 층 높이)로 위치·샘플·셰이더가 균일 규약을 그대로 쓴다. 열 높이 bilinear · 지면 레이(`ColumnHeights`, 미스 열은 이웃 링 평균) · 절감 리포트(`Measure`/`Describe`).
- `ProbeVolumeBaker.layout`(`Uniform` 기본 / `TerrainLayers`) · `groundOffset`(0.25) · `layerHeight`(6) · `groundRoots`(비우면 정적 입력 — 템플릿 제외) · Bake 로그 `layout:`(균일 환산 대비 프로브·메모리 절감률).
- `ProbeVolumeAsset.columnHeights` · `heightTexture`(RFloat, 서브에셋) · `layoutLog` · `ProbePosition` · `IsTerrainLayers`. `HuskyProbeVolume.hlsl` `_HuskyPV_Height` + `_HuskyPV_Params.w` · `ProbeVolumeShaderBinding` 높이 텍스처 바인딩·지형 추종 CPU 미러.
- `ProbeGrid` partial 분리 — `ProbeGrid.Bake.cs` 에 위치 배열판 `BakeBurstAt` · `BakeGpuAt`(격자판은 위임).
- 테스트 `ProbeTerrainLayersTests`(헤드리스 10/10) · `ProbeVolumeTerrainTests`(에디터) — RunAll 40종.

### Changed
- 기본 `layout = Uniform` 은 구 거동 그대로(위치 배열 = `grid.Positions()` — 비트 동일). `ProbeVolumeGpuSampleTests` 헬퍼 internal(B2 GPU 테스트가 재사용).

### Changed — 적응 샘플링 기본값 · 0 고착 방지 (2026-10-28)
- `AtlasApplyDebug.samplesPerPass` 기본 8 → **32**. 8 은 256 상한에서 패스 32회 × (디스패치 · readback · 판정) 오버헤드로 같은 평균 spp 의 균등 베이크보다 1.7배 느렸다(0.83s ↔ 0.48s). 32 에서 0.53s, 상대오차 relMean 0.0145 · P99 0.086(균등 75: 0.0202 · 0.177). 진행형 미리보기도 같은 필드라 패스가 줄고 빨라진다. 씬에 저장된 기존 값(8)은 그대로이니 필요하면 인스펙터에서 바꿀 것.
- `ProgressiveAccumulator`: 경로가 전부 0 인 점(m2 == 0)은 분산 0 → 첫 판정에서 바로 '수렴'해 접촉 그림자 텍셀이 0 으로 굳던 문제 → `MinSamples × ZeroPathMinFactor(4)`(≤ MaxSamples)까지 유지. 임시 씬에서 0 고착 84 → 3 텍셀(균등 75 와 같음), 비용 +2% spp. `Converged()` 자체는 그대로(순수 함수 · 테스트 D 불변).

### Changed — A4 셸프 패킹 차트 회전 (2026-10-28)
- `ShelfPacker.Pack`: 배치 후보 3종 — UV 그대로 / 차트별 최소 면적 bbox 방향(가로) / +90°(세로) — 중 균일 스케일(= 점유)이 가장 큰 것을 채택. LSCM·MVC 차트가 기울어진 채 나와 bbox 채움이 50% 로 떨어지던 문제(원통 인스턴스 점유 49.4% → 20.7%) 해소: 원통 **77.8%** · 구 39.0 → 43.2% · 캡슐 40.1%(그대로) · 큐브 65.4%.
- '그대로'가 첫 후보라 어떤 메시도 점유가 줄지 않고, 그대로가 뽑히면 UV 결과가 이전과 비트 동일. 회전이 뽑힌 메시는 uv2 배치가 바뀌므로 저장된 베이크 결과는 다시 구워야 맞는다.

### Changed — C0 Static ▸ Contribute GI 규칙 제외 (2026-10-28)
- `BakeSceneCollectPolicy`: Contribute GI 가 꺼진 오브젝트를 Excluded(또는 `NonGiAsOccluder` 면 OccluderOnly)로 빼던 판정을 주석 처리 — 이제 다른 규칙에 안 걸리면 Receiver 로 굽는다. 텍스처 라이트맵 · 프로브 볼륨 Collected 둘 다 해당. 빼려면 `BakeSceneCollectOverride`.
- 같이 주석: `BakeSceneCollectOptions.NonGiAsOccluder` · 메뉴 `Options/Non-GI Static As Occluder` · 리포트 조치 문구 · 테스트 T7 · T3/T9 의 ContributeGI 검사(`BakeSceneCollectPolicyTests` 21 → 19 검사). `CollectReason.NotContributeGI` 는 enum 에 남겨 둠(현재 안 붙음).

### Changed — C0 타입 이름 변경 (2026-10-28)
- `SceneCollect*` → `BakeSceneCollect*`: `BakeSceneCollector` · `BakeSceneCollectMenu`(Editor) · `BakeSceneCollectPolicy` · `BakeSceneCollection` · `BakeSceneCollectOptions` · `BakeSceneCollectOverride`(Runtime) · `BakeSceneCollectPolicyTests`(Tests). 결과가 라이트맵·프로브 볼륨 공용 "베이크 씬 참여자" 분류라 이름에 드러나게 했다.
- 파일 이름도 같이 바꿨고 `.meta` 를 옮겨 GUID 는 그대로 — 씬에 붙은 표식 컴포넌트 참조 유지. 메뉴 경로 · EditorPrefs 키 · `Collect*` enum/struct · 거동은 그대로. 이 아래 이전 항목은 당시 이름 그대로 둔다.

### Fixed (2026-10-27)
- `SH9BakeTextureTest` "Bake SH & Write Texture": PNG 저장 경로가 구 저장소 경로 `Assets/Study/CustomLightmapper/Script/SH/` 로 고정돼 폴더가 없으면 쓰기가 실패하던 문제 → 새 필드 `outputFolder`(기본 `Assets/CustomLightmapper/BakeOutput`, 없으면 생성).
- 에디터 메뉴 `Husky/Tool/Custom Lightmapper//Allocate Selected (per-instance ST)` 경로의 `//` 제거 → `Husky/Tool/Custom Lightmapper/Allocate Selected (per-instance ST)`.

### Docs — 챕터별 테스트 · 실행 소스 사용 매뉴얼 (2026-10-27)
- `Documentation~/06_Manual/챕터별_테스트_실행_매뉴얼.md`: 챕터(A1~A4 · C0 · C1 · C2 · 로컬 광원 · G0~G6 · Track A · 후처리 · α · SH · Track B)별 실행 컴포넌트·메뉴 사용 순서, 테스트 실행 메뉴·요구 조건(Compute/URP/렌더)·확인 내용, RunAll 38종 순서표, RunAll 밖 테스트, 문제 해결. README · 00_INDEX · 05 가이드에서 링크.

### Added — Track B 정적 입력 C0 합류 (2026-10-27)
- `ProbeVolumeBaker.staticSource`(`Roots` 기본 / `Collected`) · `collectedStatics` · `SetCollected(MeshFilter[])` — 프로브 볼륨 정적 메시를 C0 씬 분류 결과(Receiver ∪ OccluderOnly)로 받는다. 텍스처 라이트맵의 targets ∪ occluders 와 같은 기준(LOD1+·Contribute GI 꺼짐·꺼진 렌더러·Read/Write 꺼짐 제외, `SceneCollectOverride` 반영).
- Collected 는 템플릿 인스턴스와 겹치는 물체를 자동 제외(① 템플릿 `captureSource` 자손 ② 메시 + 월드 행렬 일치, Excluded 템플릿 포함) — 같은 나무가 템플릿·정적으로 두 번 들어가는 것을 막는다.
- 에디터 우클릭 `CONTEXT/ProbeVolumeBaker/Collect Scene (C0)`·`… All Loaded Scenes (C0)`(옵션은 C0 메뉴 토글 공유, Undo 가능) · `SceneCollectPolicy.SceneMembers` · 에셋 `sceneLog`(Bake 로그 `scene:` 줄).
- `BakeLightUtil.FindSceneLights()` — 씬 라이트 탐색 공용 경로(Unity 6.5 `FindObjectsByType`·`GetEntityId` 분기). `AtlasApplyDebug` "From DirectionalLight Component" 도 이 경로(Directional 이 여럿이면 객체 ID 순 첫 번째 — 결정적).
- 테스트 `SceneCollectPolicyTests` T14(헤드리스) · `ProbeVolumeC0Tests`(에디터) — RunAll 38종.

### Changed
- 기본 `staticSource = Roots` 는 구 거동 그대로(비트 동일 — 테스트로 고정). Collected 를 고른 경우에만 결과가 달라질 수 있다.

### Added — 로컬 광원 L2: SH·프로브 (2026-10-27)
- `LightSampling.ProjectSH`/`AddDirectSH`(+ Burst `BurstLights.AddDirectSH`): Direct 플래그 로컬 광원이 프로브에 직접 주는 빛을 SH 에 투영(Point/Spot 델타 E⊥, 면광원 입체각 가중·2π 상한).
- `BurstSHBaker` `SHJob`·`SHPathJob`: 히트점 NEE 에 로컬 광원 + 직접광 투영. GPU `ShIncoming`/`ShIncomingPath` → `DirectAll`, 신규 커널 `CSSHDirect`(`GpuSHBaker` 가 광원 있을 때 디스패치).
- 공용 `LocalLightSource` enum · `LocalLightSettings` → `ProbeVolumeBaker.localLights`·`InstanceSceneBaker.localLights`(기본 Scene), `TemplateInstanceSource.ToBurstScene(alloc, lights)`, 에셋 `lightLog`.
- 테스트 `SHLightProjectionTests`(헤드리스) · `SHLocalLightTests`(에디터) — RunAll 37종.

### Changed
- `AtlasApplyDebug.LocalLightSource` → 최상위 `LocalLightSource`(직렬화 값 불변). 프로브·인스턴스 SH 베이크가 씬의 로컬 라이트를 포함(구 거동 = `localLights.source = None`). 광원이 없으면 결과 비트 동일.
- 기존 베이커 테스트 8종은 로컬 광원 `None` 으로 고정(열린 씬과 격리).

### Added — 로컬 광원 L1: Point · Spot · Area (2026-10-27)
- `BakeLight`(POD 112B: Point/Spot/Rect/Disc · `FlagDirect`/`FlagShadows` · 광원별 `Samples`) + `LightSampling`(CPU·Burst 공용 제네릭 `Irradiance`, URP 감쇠·스포트 식, 면광원 결정적 저불일치 샘플링·π·L 클램프) + `BakeLightUtil`(Unity `Light` 변환·씬 수집).
- 텍스처 라이트맵 1차 직사 + 바운스 NEE 에 로컬 광원: CPU `RadianceCore`(`EvaluateRadiance`/`EvaluateDirectPrimary`/`TracePath`/`AccumulateIndirect` 의 `lights` 인자), Burst `BurstScene.lights`(+`BurstLights`), GPU `GpuScene.Lights` + `PathTrace.compute` `DirectAll`(태양 포함 통합 루프).
- `AtlasApplyDebug`: `localLightSource`(기본 Scene)·`localLights`·`localLightsFollowBakeType`·`areaLightSamples`·`softLightSamples`, 로그 `lights=…`.
- 테스트 `BakeLightTests`(헤드리스) · `LocalLightBakeTests`(에디터) — RunAll 35종 + 메뉴 "Run Local Light Tests (Point _ Spot _ Area)".

### Changed
- 씬에 Point/Spot/Area 라이트가 있으면 `AtlasApplyDebug` 재베이크 결과가 바뀜(기본 `localLightSource = Scene`). 구 거동은 `None`. 광원이 없으면 결과 비트 동일.
- GPU 직사 함수 `DirectNEESampled` → `DirectAll`(태양 + 로컬 광원, 그림자 레이 호출부 1곳). 광원 0개면 같은 연산.

### Added — GPU 잔여: 적응 샘플링 · 진행형 누적 · GPU 후처리 (2026-10-26)
- **적응 샘플링**: `AtlasApplyDebug.adaptiveSampling`(기본 off)·`samplesPerPass`(8)·`adaptiveMinSamples`(16)·`adaptiveThreshold`(0.03) — RadianceGI 간접광을 패스로 이어 누적하고, 텍셀별 표준오차 ≤ threshold × 휘도면 멈춤(`indirectSamples` = 상한). CPU/Burst/GPU 공통, 판정은 관리 코드라 배치·진행형과 무관하게 결정적.
- **진행형 미리보기**: 우클릭 "Bake Progressive (Preview)" / "Cancel Progressive Bake", `progressivePreviewEveryPasses`, 공개 `BeginProgressive`/`ProgressiveStep`/`IsProgressiveRunning`. 저 spp 결과를 즉시 적용 → 패스마다 누적·재합성 → SSAA·저장. 최종 = 같은 설정의 `BakeAndApply`.
- **이어 굽기 코어**: `RadianceCore.TracePath`/`AccumulateIndirect`/`Luminance`, `Rng.State`, `BurstIndirect.TracePath`/`AccumJob`/`Accumulate`, `PathTrace.compute` `TracePath`·`CSRadianceAccum`, `PathTraceShader.BindRadiance`. 엔진 `ProgressiveAccumulator` + `IPathAccumBackend`(`CpuPathAccum`·`BurstPathAccum`·`GpuPathAccum`).
- **GPU 후처리**: `AtlasApplyDebug.gpuPostProcess`·`postShader` — 시임 스티칭(Tier1 웨이브 · Tier2 샘플→수집) + dilation 을 `LightmapPost.compute`(`LightmapPostGpu`, 계획 `LightmapPostPlan`)로. 실패 시 CPU 폴백.
- 테스트 `ProgressiveAccumulatorTests`·`LightmapPostPlanTests`(헤드리스) · `ProgressiveBakeTests`(에디터) — RunAll 33종 + 메뉴 "Run Adaptive _ Progressive _ GPU Post Tests (G6-5 _ G6-6)".

### Changed
- `AtlasApplyDebug.BakeAndApply` 를 단계 메서드(`BeginRun`·`PrepareInstance`·`FlushBatch`·`FinishInstance`·`Compose`·`EndRun`)로 분해 — 연산 순서·결과 불변. 일괄 간접광 경로(CPU/Burst/HLSL)가 공유 `TracePath` 를 부르도록 정리(결과 불변).

### Added — GPU 추가 최적화 (2026-10-26)
- **배치 베이크(G6-3b)**: `AtlasApplyDebug.bakeBatchLumels`(기본 1<<20) — 인스턴스들의 lumel 을 모아 1패스·SSAA 2패스를 한 번에(GPU 1 업로드·1 readback, Burst 1 잡). 0 = 인스턴스마다(구 거동). 결과는 배치와 무관하게 비트 동일. 에디터 진행 표시.
- **분할 디스패치**: `AtlasApplyDebug.gpuDispatchChunk`(기본 65536) — `CSRadiance` 를 `_Base` 오프셋으로 나눠 연속 디스패치. 한 디스패치 4.19M 점 한도 해소, 긴 디스패치 TDR 위험 완화.
- **순회 스택 가드**: `GpuBakeLimits`(Tiles · MaxLeafDepth · StackUsage) + `GpuScene.RequiredStack`/`TraversalFits` — GPU 스택(32)에 안 맞는 BVH 는 AtlasApplyDebug·ProbeVolumeBaker·InstanceSceneBaker·SHRenderDemo 가 Burst 로 폴백.
- 테스트 `GpuBakeLimitsTests`(헤드리스) · `AtlasBatchBakeTests`(배치·분할 비트 동일) — RunAll 배선 + 메뉴 "Run GPU Batch Tests (G6-3b _ G6-4)".

### Fixed
- 한 인스턴스(또는 SSAA Full 서브샘플)가 4.19M 점을 넘으면 GPU 디스패치가 그룹 한도를 넘던 잠재 문제(분할로 해소).

### Added — A3 LSCM / MVC 솔버 본체 (2026-10-26)
- `LSCMSolver`(Lévy 2002 등각 최소제곱, 외곽 루프 2점 고정, Jacobi CG) · `MVCFallback`(Floater 평균값 좌표, 원 경계, 구멍 가상 부채꼴, BiCGSTAB/Gauss–Seidel) · `SparseSolver`(CSR·CG·BiCGSTAB·GS, double·결정적).
- `ChartFlattener`: `FlattenSettings`(Planar 유지 L2 stretch 임계 기본 1.005, LSCM/MVC 토글, 반복·허용치) + `FlattenStats`, 3단 디스패치(겹침 또는 고왜곡 → LSCM 이 더 나을 때만 · 둘 다 겹치면 MVC). `FlattenSettings.FoldoverOnly` = 구 거동.
- `UVValidator.Stretch`(L2 stretch, 면적 정규화). `ParameterizationPipeline.Run(mesh, seg, flatten)` + `ParamResult.Stats`. `AtlasApplyDebug.flatten`. `ParameterizationTestDebugger` 리포트에 stretch·방법 집계.
- `Tests/ChartFlattenTests`(헤드리스 17 체크) — RunAll 배선 + 메뉴 "Run Chart Flatten Tests (A3 LSCM_MVC)".

### Changed
- 곡면 차트(평면투영 L2 stretch > 1.005)의 uv2 가 LSCM 결과로 바뀔 수 있음(더 나을 때만). 평면 차트는 비트 동일.

### Fixed
- `ProbeVolumeBakerTests`·`ProbeAlphaTests` 의 비트 동일 판정이 Unity `Vector4 !=`(ε 근사)를 쓰던 것을 성분별 정확 비교로.

### Added — Track B B1-6 알파 잎 차폐 (2026-10-26)
- `ProbeVolumeBaker`: 컷아웃 머티리얼의 투명 텍셀을 프로브 SH(Burst·GPU, 1·다바운스)·유효성·정점 AO 베이크 레이가 통과(패키지 α any-hit 재사용 — 코어 무수정). 설정 `alphaCutout`(기본 on)·`alphaMaskResolution`·`alphaTransparentPolicy`·`alphaForceCutout`·`alphaForceCutoutNames`·`alphaDefaultCutoff`, 템플릿 `alphaMaterials`(서브메시별, 비우면 표시 머티리얼). 머티리얼이 없으면 기존과 비트 동일.
- `VertexAOBake.Compute`/`Bake` 에 선택 인자 `AlphaSceneData alpha` + `VertexAOBake.BuildAlpha`.
- `ProbeVolumeAsset.alphaCutout`·`alphaLog`, `VertexAOEntry.alphaCutout`.
- `Tests/ProbeVolume/ProbeAlphaTests`(ᾱ=1 ≡ 알파 없음 · ᾱ=0 ≡ 메시 제거 비트 동일 · 체커 · 정점 AO · 베이커 머티리얼 경로 · GPU) — RunAll 배선.

### Added — C0 씬 수집·분류 (2026-10-26)
파이프라인 단계 1(씬에서 무엇을 굽을지 고르는 층). 베이크 경로 무변경 — `AtlasApplyDebug` 의 targets/occluders 를 채우는 입력 공급기. 상세 `Documentation~/02_Development_And_Reference/커스텀_라이트매퍼_개발문서_v15.md` §5.8 · §12 v15.12.
- `Runtime/Collect/`: `SceneCollectPolicy`(순수 함수 — Receiver / OccluderOnly / Excluded + 사유, 판정 순서 = 사유 우선순위, 정렬 없음) · `SceneCollection`(결과 + `Report`) · `SceneCollectOverride`(재수집에도 남는 OccluderOnly/Exclude 표식).
- `Editor/SceneCollector`(씬 순회 · Static ContributeGI · LODGroup LOD1+ · receiveGI · 배열 복사 없는 메시 메타데이터) · `Editor/SceneCollectMenu`(`CONTEXT/AtlasApplyDebug/Collect Scene (C0)` · 전체 로드 씬 · Report Only · Verify Determinism · Diagnose Selected Meshes · 옵션 토글).
- `AtlasApplyDebug.SetCollected(receivers, occluders)`. R/W 꺼진 메시는 이제 수집 단계에서 receiver·occluder 양쪽 제외 + 조치 문구(이전엔 아틀라스 슬롯만 받고 그림자가 조용히 사라짐).
- `WeldedHalfEdge.WeldVertices`·`RemoveDegenerateTriangles`·`WeldEpsilon` 공개(로직·값 무변경 — 진단 전용).
- `Tests/SceneCollectPolicyTests`(헤드리스 20 체크) — "Run All Tests" 배선 + "Run Scene Collect Tests (C0)".

### Merged — UnityStudyProject 브랜치(v14.12~v15.10) 병합 (2026-10-26)
패키지 0.14.0 이후 UnityStudyProject 에서 따로 진행된 작업을 패키지로 합침(3-way, 상세 `Documentation~/02_Development_And_Reference/커스텀_라이트매퍼_개발문서_v15.md` §12 v15.11).
- **SH 트랙**: SH-2 다바운스(Burst `BurstSHBaker.BakeMultiBounce` + GPU `CSSHBakeMulti`), 1바운스 반사 규약을 물리 규약(ρ/π·E)으로 통일, 실사용 컴포넌트 `InstanceSceneBaker` + 결과 `InstanceSHBakeAsset`(SO) + EXR, 2-프로브 대표점 `TemplateInstanceSource.ComputeProbePoints`(씬·템플릿 단위), `SHInstanceIndexGpuTests`·`InstanceSceneBakerTests`·`BurstSHBakerTests` D~H.
- **텍스처 라이트맵**: 최종 인코딩 RGBAHalf(`hdrOutput`), 결과 저장 `AtlasBakeAsset`(SO: 아틀라스·머티리얼·uv2 메시 + 엔트리) + 페이지 EXR, OnEnable 에셋 적용(`applyOnEnable`), 원본 추적(`OriginalOf`), `BakeOutputUtil`, `PathTraceShader` 로더, `AtlasHdrOutputTests`·`AtlasBakeAssetTests`.
- **Track B 식생 프로브 볼륨(신규 `Runtime/ProbeVolume/`)**: `ProbeGrid`(균일 격자·trilinear·베이크 연결) · `BurstProbeValidity`(뒷면 비율 유효성·dilation) · `ProbeVolumeAsset`(SO + Texture3D×7 RGBAHalf + EXR) · `ProbeVolumeBaker`(볼륨 베이크·저장·인스턴스 적용·기즈모) · `ProbeVolumeShaderBinding` + `Shaders/HuskyProbeVolume.hlsl` + `Shaders/InstancedVegetation_URP.shader`(픽셀 볼륨 샘플·알파 클립·잎 투과) · `VertexAOBake`(템플릿 로컬 정점 AO, uv3). 테스트 `Tests/ProbeVolume/` 5종. 설계 `Documentation~/01_Planning_And_Design/커스텀_라이트매퍼_TrackB_식생프로브_설계.md`.
- 셰이더: `InstancedSH_URP` 에 선택 키워드 `_HUSKY_VERTEX_AO`(기본 꺼짐), 검증용 `SHEvalInstance.shader`·`ProbeVolumeSampleTest.shader`.
- 문서: 기획서·개발문서 v15 추가(+ v15.11 병합 기록 — 2026-08-18 로드맵을 v15 §10/§11 로 합침), v14 는 이력 보존, Track B 설계 추가.

### Changed (병합)
- **`AtlasApplyDebug` 를 `Samples/` → `Runtime/LightmapAllocator/`** 로 이동(텍스처 베이크 드라이버·결과 저장/런타임 적용 = 실사용 코드, GUID 유지). 내장 Diff 3종은 `Tests/AtlasApplyDiffTests` + `Editor/AtlasApplyDiffTestsMenu`(같은 CONTEXT 메뉴명)로, `SSAA Self-Tests` 메뉴도 Editor CONTEXT 메뉴로.
- `LightmapAllocator.RunSelfTests` → `Tests/LightmapAllocatorTests.RunSelfTests`.
- 베이크 결과 기본 저장 폴더: `Assets/CustomLightmapper/BakeOutput`(소비 프로젝트 Assets 하위).
- `InstancedSHRenderer`: SH 버퍼 선택(null 허용 — 볼륨 셰이더용) + `Properties`(MPB) 공개.

### Added
- **SSAA(텍셀 내부 슈퍼샘플링)** — 그림자 경계의 텍셀 계단 제거. 기존 래스터는 텍셀 정중앙 1점만 복원해 서브텍셀 커버리지 정보가 없었고, 태양 원반 샘플링(`DirectSamples`+`AngularDiameterDeg`)으로 반그림자를 만들어도 그 폭이 텍셀보다 작으면 다시 한 텍셀에 뭉개졌다.
  - `TexelMapper.MapSubsamples` + `LumelSubsamples`: 요청한 텍셀만 S×S 서브샘플로 재래스터(압축 슬롯 저장). `TexelMapper.SubOffset` 은 u=층화·v=황금비 저불일치 → 두 축 모두 S² 단계 계조(정규격자는 한 축이 S 단계뿐).
  - `LightmapSSAA.DetectEdges`: 1패스 조도에서 상대 휘도차로 엣지 텍셀 검출. 월드 노멀/거리 게이팅으로 차트 경계·각진 면의 **정상 불연속**은 제외, 1링 확장으로 계단 양쪽을 함께 재샘플.
  - `AtlasApplyDebug`: `ssaaMode`(Off/Adaptive/Full) · `ssaaFactor` · `ssaaEdgeThreshold` · `ssaaEdgeNormalAngleDeg` · `ssaaEdgeDilate` · `ssaaDebugMask`. Adaptive 는 엣지 텍셀만 재샘플해 총 레이 ≈1.1~1.3배. 완료 로그에 재샘플 비율·레이 배수 출력.
  - 아틀라스 해상도·`valid` 마스크·ST 매핑을 건드리지 않으므로 Denoise/Seam Stitch/Dilation 단계는 불변. `ssaaMode=Off` 는 기존 경로와 비트 동일.
  - `LightmapSSAATests` + `AtlasApplyDebug` 컨텍스트 메뉴 `SSAA Self-Tests`(레이 없는 기하/로직 검증).
- 라이트맵 디노이즈(À-trous joint bilateral): `LightmapDenoise`(C# 직렬) + `LightmapDenoiseBurstJob`(Burst 병렬). 텍셀별 월드 노멀·위치·색(RGB L2) 가이드 에지 보존 필터 — MC 노이즈만 평활, 하드 엣지·차트 경계·그림자 경계 보존. 색 range 는 휘도 스칼라가 아닌 RGB 거리(단일 채널 엣지 과소평가 방지).
- `AtlasApplyDebug` 파이프라인에 Denoise 단계 배선(blit → **Denoise** → Seam Stitch → Dilate, Radiance/RadianceGI 전용) + 인스펙터 파라미터(iterations/normalPower/positionSigma/colorSigma/Burst 토글).
- `LightmapDenoiseCompare`: Burst≡Serial 일치 · 노이즈 RMS ≤0.5× · 그림자 엣지 대비 보존 · 원거리 차트 격리 + 성능 측정. 메뉴 `HuskyLibs/CustomLightmapper/PostCompare Denoise`.

### Changed
- `AtlasApplyDebug`: Radiance 모드도 `BlitRegion` 인라인 평가가 아니라 `BakeLumels` 선-베이크로 통일(SSAA 가 1패스 결과를 봐야 엣지를 찾을 수 있음). 시드·평가 원점·호출 함수가 동일해 값은 불변, 인라인 경로는 폴백으로 유지.
- `BakeGiLumelsBurst`/`BakeGiLumelsGpu` → `BakeLumels` + `BakePoints`(임의 점 리스트 진입점)로 일반화. SSAA 2패스가 백엔드 분기를 재작성하지 않고 CPU/Burst/GPU 를 그대로 탄다.

## [0.14.0] - 2026-07-14

### Added
- UPM 패키지화: `Assets/Study/CustomLightmapper` → `Packages/com.huskylibs.customlightmapper` (embedded).
- 어셈블리 4분할: Runtime / Editor / Tests / Samples.
- `package.json` 매니페스트 (Burst/Collections/Mathematics 의존).

### Changed
- 컴퓨트 셰이더 로드를 하드코딩 `AssetDatabase` 절대경로 → `Resources.Load` 로 통일 (`Shaders/Resources/` 배치).
- 데모/테스트 코드를 코어(Runtime) 어셈블리에서 분리. Tests/Samples 는 Editor 플랫폼 전용 → 소비자 빌드 제외.

### Removed
- 미사용 `BruteForceOccluderTester` (Runtime→Tests 역참조 제거).
