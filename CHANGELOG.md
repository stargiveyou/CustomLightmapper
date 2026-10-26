# Changelog

## [Unreleased]

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
