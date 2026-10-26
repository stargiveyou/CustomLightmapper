---
title: "CustomLightmapper — 스크립트 레퍼런스"
tags: [custom-lightmapper, guide, reference]
---

# CustomLightmapper — 스크립트 레퍼런스

> 기준일 2026-10-14 · 현재 트리 실측(줄 수 포함). 경로는 패키지 루트 `Packages/com.huskylibs.customlightmapper/` 기준.
> 네임스페이스: `HuskyLibs.CustomLightmapper.Bake` (에디터 툴은 `.EditorTools`).
> 규모(.cs 기준): Runtime 48 · Editor 4 · Samples 17 · Tests 19 · Shaders 7 (+ asmdef 4)

---

## 1. Runtime — 베이크 코어

### 1.1 메시 구조 · 차트 분할 (A1)

| 파일 | 줄 | 주요 타입 / API | 설명 |
|---|---|---|---|
| `HalfEdge.cs` | 540 | `HalfEdge`, `WeldedHalfEdge`, `HalfEdge_Vertex/Edge/Face` | 삼각형 메시 → DCEL(하프에지). `WeldedHalfEdge`는 위치 기준 정점 용접 + 퇴화 삼각형 제거를 내장(생성자에서 실행). NativeArray 보유 → `Dispose()` 필요 |
| `ChartSegmentation.cs` | 338 | `SegmentationSettings`, `Chart`, `ChartSegmentationResult`, `ChartSegementer.GetResult()`, `DebugColorize()` | 노멀 각도 기반 best-first 영역성장 차트 분할(`MinHeap`). `SeamAngleDeg`(40)·`MaxChartAngleDeg`(60) |
| `ChartMesh.cs` | 146 | `ChartMesh`, `ChartMeshBuilder.Build/BuildAll` | 차트 하나를 독립 로컬 메시(정점·삼각형·UV)로 추출 |

### 1.2 평탄화 · UV 조립 (A2~A3)

| 파일 | 줄 | 주요 타입 / API | 설명 |
|---|---|---|---|
| `ChartFlatten/ChartFlattener.cs` | 48 | `FlattenMethod`, `Flatten`, `FlattenAll` | Planar → LSCM → MVC 순 디스패처. **실제로는 Planar만 동작** |
| `ChartFlatten/LSCMSolver.cs` | 29 | `LSCMSolver.Solve` | ⚠ **스텁** — `LogWarning("미구현 stub")` |
| `ChartFlatten/MVCFallback.cs` | 28 | `MVCFallback.Solve` | ⚠ **스텁** |
| `UVAssembly/PlanarProjector.cs` | 44 | `Projector`, `ProjectAll` | 차트 평균 노멀 평면으로 정사영 |
| `UVAssembly/UVValidator.cs` | 51 | `UVReport`, `Validate`, `HasFoldover` | 부호 면적으로 뒤집힌 삼각형(foldover) 검출 |
| `UVAssembly/UVPacker.cs` | 140 | `DensityNormalizer.Normalize`, `ShelfPacker.Pack` | 월드 면적 비례 UV 스케일 통일 → 셸프 방식 [0,1] 비겹침 배치 |
| `UVAssembly/UVAssembler.cs` | 73 | `SeamTable`, `UVAssembly.Assemble` | 차트들을 하나의 uv2(채널1) 메시로 조립 + 시임(같은 원본 정점의 분할) 테이블 |
| `UVAssembly/ParameterizationPipeline.cs` | 68 | `ParamResult`, `ParameterizationPipeline.Run(mesh, seg)` | A1~A3 드라이버: Half-Edge → 분할 → 차트 메시 → 평탄화 |

### 1.3 텍셀 · 할당 (A4~A5, SSAA)

| 파일 | 줄 | 주요 타입 / API | 설명 |
|---|---|---|---|
| `TexelMapper/TexelMapper.cs` | 267 | `LumelMap`, `LumelSubsamples`, `Map`, `MapSubsamples`, `SubOffset` | uv2 삼각형 래스터 → 텍셀별 월드 위치·노멀·valid. `MapSubsamples`는 마스크된 텍셀만 S×S 서브샘플(층화+황금비 저불일치) |
| `TexelMapper/LightmapSSAA.cs` | 132 | `DetectEdges`, `TexelSeed`, `SubSeed`, `Luma` | 1패스 조도에서 엣지 텍셀 검출(상대 휘도차 + 노멀/거리 게이팅 + 링 확장). 시드 규약 `seed + li*2654435761u` 정의 |
| `LightmapAllocator/LightmapAllocator.cs` | 200 | `LightmapInstance`, `AllocationSettings`, `AllocationResult`, `InstanceLM`, `WorldArea`, `Allocate`, `RunSelfTests` | 인스턴스 월드 표면적 → 아틀라스 ST(ScaleOffset) + 페이지 배정. ⚠ `WorldArea`는 메시 배열을 복사하므로 비쌈 |

### 1.4 가속구조 (C1)

| 파일 | 줄 | 주요 타입 / API | 설명 |
|---|---|---|---|
| `LightmapEvaluate/Occluders/Occluder.cs` | 91 | `Tri`, `Hit`, `IOccluder`, `RayGeometry.RayTri/RayTriUV` | 공통 기하 타입. `IOccluder` = `Intersect` + `Occluded`. `RayTriUV`는 α용 무게중심 좌표 반환 오버로드 |
| `LightmapEvaluate/Occluders/BVH.cs` | 592 | `BVH`, `BuildQuality{Median,SAH}`, `Node`, `IntersectAlpha`, `OccludedAlpha`, `RayAABB`, `SahCost`, `MaxDepth` | 단일 레벨 BVH(SAH 12 bin). 알파 any-hit 변형 포함 |
| `LightmapEvaluate/Occluders/TwoLevelBVH.cs` | 404 | `TwoLevelBVH`, `Instance`, `InstancedHit`, `IntersectInstanced`, `TransformNormalToWorld`, `SetAlpha` | TLAS(인스턴스) / BLAS(유니크 메시) 2단. 같은 메시 인스턴스가 BLAS 공유 |
| `LightmapEvaluate/Occluders/BruteForceOccluder.cs` | 43 | `BruteForceOccluder` | 전수 검사 정답 — BVH 교차검증 기준 |

### 1.5 라이팅 코어 (C2)

| 파일 | 줄 | 주요 타입 / API | 설명 |
|---|---|---|---|
| `LightmapEvaluate/RadianceCore.cs` | 293 | `Rng`, `DirectionalLight`, `BakeQualitySettings`, `EvaluateAO`, `EvaluateDirect`, `EvaluateDirectSampled`, `EvaluateIndirect`, `EvaluateRadiance`, `CosineHemisphere`, `SunConeDirection` | **CPU 기준 구현(ground truth)**. 태양 원반 샘플링(`DirectSamples`, `AngularDiameterDeg`), 경로추적 + 러시안 룰렛 |
| `LightmapEvaluate/Sky.cs` | 30 | `ISky`, `UniformSky`, `GradientSky` | 미스 레이 하늘 복사휘도 |
| `LightmapEvaluate/RadianceScene.cs` | 91 | `IRadianceScene`, `RadianceScene` | `ClosestHit`(위치·노멀·알베도) 인터페이스 + 단일 레벨 구현 |
| `LightmapEvaluate/InstanceRadianceScene.cs` | 148 | `InstancedRadianceScene` | 2단 BVH 기반 씬. 알베도 모드 A(per-mesh) / B(per-instance submesh) |
| `LightmapEvaluate/SceneRadianceBuilder.cs` | 2 | — | ⚠ **빈 파일**. 기능은 `AtlasApplyDebug.BuildGiScene`에 있음 |

### 1.6 알파 컷아웃 (α)

| 파일 | 줄 | 주요 타입 / API | 설명 |
|---|---|---|---|
| `LightmapEvaluate/Alpha/AlphaTypes.cs` | 137 | `TriUV`, `AlphaMath.InterpUV/TexelBit`, `AlphaSceneData` | 삼각형 UV 병렬 배열, 비트마스크 조회, per-instance 머티리얼 슬롯 테이블(`matBase + submesh`) |
| `LightmapEvaluate/Alpha/AlphaMaskBuilder.cs` | 396 | `AlphaMaskBuilder`, `TransparentPolicy`, `GetOrCreateMask`, `BuildScene` | 머티리얼 컷아웃 판별(`_AlphaClip`/`_Cutoff`/SpeedTree 셰이더 이름/강제 지정) → 알파 텍스처를 이진 비트마스크로 굽기. 삼각형 수 불일치 시 해당 메시 알파 비활성(정합 검사) |
| `LightmapEvaluate/Burst/BurstAlpha.cs` | 136 | `BurstAlpha`, `Create`, `CreateDisabled`, `HitOpaque` | `AlphaSceneData`의 Burst POD 미러. 알파 없는 씬도 `CreateDisabled`로 더미 할당 필요(잡 안전 시스템) |

### 1.7 Burst 백엔드 (G0~G3)

| 파일 | 줄 | 주요 타입 / API | 설명 |
|---|---|---|---|
| `LightmapEvaluate/Burst/BurstScene.cs` | 147 | `BurstScene.Create(bvh, albedo, alphaData, alloc)` | `TwoLevelBVH` → POD NativeArray 평탄화(BLAS 연결 + 오프셋), 알베도·알파 포함. ⚠ 새 NativeArray 필드는 **모든 Create 경로에서 할당**할 것 |
| `LightmapEvaluate/Burst/Occluders/BurstBVH.cs` | 103 | `BurstBVH.Intersect/Occluded` | 단일 레벨 static 순회 |
| `LightmapEvaluate/Burst/Occluders/BurstTwoLevelBVH.cs` | 304 | `IntersectInstanced`, `Occluded`, `ClosestHit`, `TransformNormalToWorld` | 2단 static 순회(스택 64) |
| `LightmapEvaluate/Burst/BurstAO.cs` | 83 | `BurstAO.Compute`, `AoJob` | AO `IJobParallelFor` |
| `LightmapEvaluate/Burst/BurstDirect.cs` | 134 | `BurstDirect.Compute`, `DirectJob` | Direct NEE(+태양 원반, 텍셀별 `Seeds`) |
| `LightmapEvaluate/Burst/BurstIndirect.cs` | 124 | `BurstIndirect.Compute`, `IndirectJob` | 경로추적 + RR |
| `LightmapEvaluate/Burst/BurstRadianceBaker.cs` | 42 | `Bake`(Burst), `BakeCPU` | Direct + Indirect 합성 진입점 |
| `LightmapEvaluate/Burst/BurstSky.cs` | 31 | `BurstSky.Uniform/Gradient/FromSky` | `ISky`의 POD 미러 |
| `LightmapEvaluate/Burst/BurstSHBaker.cs` | 122 | `BurstSHBaker.Bake`, `SHJob`, `FibonacchiSphere` | 프로브별 SH9 베이크(1바운스 + 하늘 앰비언트) |

### 1.8 GPU 백엔드 (G4~G6, SH-G)

| 파일 | 줄 | 주요 타입 / API | 설명 |
|---|---|---|---|
| `LightmapEvaluate/Gpu/GpuScene.cs` | 336 | `GpuScene`, `Bind`, `BindLighting`, `BindAlpha`, `GpuNode`(32B)/`GpuTri`(36B)/`GpuInstance`(64B)/`GpuInstNormal`(48B)/`GpuTriUV`(24B)/`GpuRay`/`GpuHit` | `BurstScene` → ComputeBuffer 재패킹. 행렬은 `GetRow(0..2)` 3행 업로드(열우선 함정 회피) |
| `LightmapEvaluate/Gpu/GpuSHBaker.cs` | 72 | `GpuSHBaker.Bake` | `CSSHBake` 디스패치 헬퍼. 방향셋은 CPU에서 계산해 업로드(초월함수 차이 제거) |

### 1.9 후처리

| 파일 | 줄 | 주요 타입 / API | 설명 |
|---|---|---|---|
| `Denoise/LightmapDenoise.cs` | 126 | `DenoiseSettings`, `LightmapDenoise.Denoise` | À-trous joint bilateral 직렬 레퍼런스(노멀·위치·RGB 가이드) |
| `Denoise/LightmapDenoiseBurstJob.cs` | 178 | `Denoise`, `DenoiseBurst`, `DenoiseJob : IJobFor` | Burst 병렬판(직렬과 픽셀 일치) |
| `LightmapStitch/Lightmapseamstitch.cs` | 205 | `Stitch`(Tier1), `StitchEdges`(Tier2), `Seg`, `Uv2ToTexelIndex`, `UvToTexelCoord` | 시임 스티칭 본체 + uv2→텍셀 매핑 |
| `LightmapStitch/SeamEdgeBuilder.cs` | 211 | `SeamEdge`, `SeamEdgeGroup`, `Build`, `BuildSegments`, `FilterByNormal` | 시임 모서리 유도 + 세그먼트 생성 + 노멀 각도 게이팅 |
| `Diliation/LightmapPostProcess.cs` | 75 | `LightmapPostProcess.Dilate` | Push-Pull 딜레이션 직렬판 |
| `Diliation/LightmapPostProcessBurstJob.cs` | 184 | `Dilate`, `DilateBurst`, `DiliateJob : IJobFor` | Burst 병렬판 |

### 1.10 SH 인스턴싱 (Track B)

| 파일 | 줄 | 주요 타입 / API | 설명 |
|---|---|---|---|
| `TemplateInstanceSource.cs` | 252 | `MatrixInstanceInput`, `AnchorMode`, `MatrixInstanceScene`, `BuildScene`, `ComputeLocalAnchor`, `MeshToLocalTris`, `ToBurstScene`, `Summary` | Mesh 템플릿 + 행렬 배열 → 인스턴스 씬 어댑터. 프로브 앵커 5모드(Auto · Center · LocalTopLift · WorldUpLift · SurfaceNormalOffset) |
| `SH/SH9.cs` | 120 | `SH9`, `Basis`, `Accumulate`, `Evaluate` | 9계수 SH 프로젝션·조도(Unity `ShadeSH9` 일치, L2 디링잉) |
| `SH/SHInstanceBuffer.cs` | 106 | `SHPacked`(7×float4=112B), `Pack/Unpacked`, `SHInstancedBuffer.Create/Bind` | GPU 정렬 패킹 + GraphicsBuffer(`probesPerInstance`) |
| `SH/InstancedSHRenderer.cs` | 104 | `InstancedSHRenderer`, `UpdateMatrices`, `Draw` | `DrawMeshInstancedIndirect`로 전체 서브메시 드로우, 템플릿별 알베도 MPB 주입 |

### 1.11 기타

| 파일 | 줄 | 설명 |
|---|---|---|
| `AssemblyInfo.cs` | 8 | `InternalsVisibleTo` 등 어셈블리 속성 |

---

## 2. Shaders

| 파일 | 줄 | 커널 / 패스 | 설명 |
|---|---|---|---|
| `Resources/PathTrace.compute` | 864 | `CSAO`, `CSDirect`, `CSIndirect`, `CSRadiance`, `CSSHBake` | GPU 경로추적 전체. `CSRadiance` = Direct+Indirect 1-디스패치(텍셀별 `_Seeds`). 알파 any-hit는 `(matBase, alphaOn)` 매개변수 분기(X4714 회피). `BVH_STACK=32` |
| `Resources/BvhTraverse.compute` | 358 | `CSClosestHit`, `CSOccluded` | G4 2단 BVH 순회(검증용) |
| `EvaluateSH9.hlsl` | 112 | — | SH9 디코드 공유 include. `EvaluateInstanceSH2Axis`(2-프로브 상하 보간, 임의 축) |
| `InstancedSH_URP.shader` | 174 | ForwardLit, ShadowCaster | **SH-5 주 셰이더**. `Cull Off` + 뒷면 노멀 뒤집기, 정점에서 인스턴스 up 축 유도 |
| `InstancedSH_BuiltIn.shader` | 141 | — | BIRP판(1-프로브 전제, **보류**) |
| `SHEvalProbe.shader` | 37 | — | SH GPU 디코드 검증용(1×1 RT) |
| `LightmapDebug.shader` | 70 | URP Unlit | 라이트맵 아틀라스(Texture2DArray) + 인스턴스 ST 적용 |

> compute는 `Shaders/Resources/`에 있어 `Resources.Load<ComputeShader>("PathTrace")`로 에디터·빌드 공통 로드된다.

---

## 3. Editor

| 파일 | 줄 | 메뉴 | 설명 |
|---|---|---|---|
| `UVLayoutViewer.cs` | 302 | `Husky/Tools/UV Layout Viewer` | `EditorWindow`. A1~A3 차트 UV를 2D 캔버스에 그림, foldover 빨강 강조, A4 패킹 토글·거터·차트 선택 |
| `LightmapAllocatorDebugTool.cs` | 97 | `Husky/Tool/Custom Lightmapper/Allocate Selected (per-instance ST)`, `…/Run Allocator Self-Tests` | 선택 오브젝트 ST 할당 / 셀프테스트 |
| `LightmapDenoiseCompareMenu.cs` | 18 | `HuskyLibs/CustomLightmapper/PostCompare Denoise (Burst vs Serial + Quality)` | `LightmapDenoiseCompare.RunAll()` 실행 |
| `LightmapPostProcessCompareMenu.cs` | 17 | `HuskyLibs/CustomLightmapper/PostCompare Dilate (Burst vs Serial)` | `LightmapPostProcessCompare.RunAll()` 실행 |

---

## 4. Samples — 하니스 · 데모

| 파일 | 줄 | 컨텍스트 메뉴 | 설명 |
|---|---|---|---|
| **`AtlasApplyDebug.cs`** | **1633** | Bake & Apply · Restore Originals · From DirectionalLight Component · Radiance Diff Test · RadianceGI Backend Diff (CPU vs Burst / Burst vs GPU) · SSAA Self-Tests · Alpha Diagnose — *2026-10-26 병합: `Runtime/LightmapAllocator/` 로 이동, Diff 3종·SSAA Self-Tests 는 같은 이름의 Editor CONTEXT 메뉴(`AtlasApplyDiffTestsMenu`), 결과 저장(`AtlasBakeAsset`)·RGBAHalf 추가* | ★ **엔드투엔드 베이커**. 대상 해석(`ResolveTargets`/`ResolveOccluderUnion`) → 할당 → 인스턴스 루프(파라미터화·텍셀·`BakeLumels`/`BakePoints`) → SSAA 2패스 → Denoise → Seam Stitch → Dilation → `Texture2DArray` → 렌더러 적용. GPU I/O는 `GpuIoBuffers`(grow-on-demand) + AsyncGPUReadback. 5모드 · 3백엔드 |
| `SHRenderDemo.cs` | 393 | Bake + Render SH · Load Real Templates · Capture Screenshot | SH 인스턴싱 실 드로우 데모(Burst/GPU 베이크 토글, 기울기 테스트) |
| `SHBakeDemo.cs` | 110 | Bake SH (Demo) | SH-1~4 엔드투엔드(기즈모만) |
| `SHDebugView.cs` | 134 | — | SH 대표점 기즈모(Point/Cross/Sphere) |
| `SH9BakeTextureTest.cs` | 250 | Bake SH & Write Texture · Run Self Tests | SH9 항등식 검증 + equirect 텍스처. `RunAll`은 회귀에도 포함 |
| `SHEvalProbeVerify.cs` | 22 | Verify GPU vs CPU SH | `SHEvalProbeGpuTests` 얇은 래퍼 |
| `LightmapEvaluateDebugger.cs` | 301 | Run All Tests (C1+C2) 외 14개 개별 테스트 · Evaluate · Clear | ★ **회귀 테스트 허브** + 정점별 라이팅 기즈모 |
| `InstancedSceneTester.cs` | 206 | Run Instanced Scene Test | 2단 인스턴싱 씬 ≡ brute 교차검증 |
| `BVHGizmoDebug/BVHGizmoDebugger.cs` | 311 | Build BVH · Create Ray Handles · Clear | BVH 노드 AABB 시각화 + 레이 순회 라이브 |
| `ChartSegmentationDebugger.cs` | 271 | Run Segmentation · Run Segmentation - Welded · Clear Result | 차트 분할 시각화 |
| `HalfEdgeConversion.cs` | 242 | Rebuild HalfEdge | 하프에지 구조 시각화 |
| `ParameterizationTestDebugger.cs` | 147 | Run Parameterization Check · Clear Result | A1~A3 구동·검증 |
| `UVAssemblerTestDebugger.cs` | 176 | Run UV Assemble · Clear Result | uv2 조립·SeamTable 검증 |
| `TexelMapperDebug.cs` | 205 | Run Texel Map · Save Preview PNG · Clear Result | 루멜 맵 프리뷰 |
| `LightmapAllocatorDebug.cs` | 112 | Run Self Tests · Run Full Tests · Run Mesh Test | ST 할당 검증 |
| `MeshCombiner.cs` | 66 | — | 자식 메시 병합 유틸 |
| `ObjectRandomTranslator.cs` | 41 | Random Translate | `parent` 자식들을 `range` 구 안에 무작위 배치 |
| `Mesh_Struct_Sample_Scene.unity` | — | — | 샘플 씬 |

---

## 5. Tests — 자체 검증 (static `RunAll() → string`)

| 파일 | 줄 | 대상 | RunAll 포함 |
|---|---|---|---|
| `LightmapEvaluateTests.cs` | 170 | C2 토대: RayTri · BruteForce · CosineHemisphere · AO · Direct (해석적 정답) | ✅ |
| `BVHTests.cs` | 324 | C1: 단일/2단 BVH ≡ brute (Median/SAH). `RunMeshTest`, `RunSceneTest` | ✅ |
| `RadianceIndirectTests.cs` | 138 | Indirect: 빈 씬=π·L, 흑체=0, 에너지 보존, RR 무편향 | ✅ |
| `InstancedSceneModeBTests.cs` | 225 | 알베도 모드 B(서브메시) 정확성·경계 안전 | ✅ |
| `BurstSceneTests.cs` | 186 | G0: Burst POD 순회 ≡ 관리형 TwoLevelBVH | ✅ |
| `BurstRadianceCompareTests.cs` | 234 | G2/G3: Burst Direct/Indirect ≡ RadianceCore | ✅ |
| `TemplateInstanceSourceTests.cs` | 271 | 템플릿 어댑터 ≡ MeshFilter 경로, 앵커 모드 | ✅ |
| `BurstSHBakerTests.cs` | 103 | SH-2: 빈 씬·그라디언트·hit 반사 | ✅ |
| `SHPackedTests.cs` | 70 | SH-3: Pack↔Unpack 왕복 | ✅ |
| `SHEvalProbeGpuTests.cs` | 109 | SH-5: 셰이더 디코드 ≡ CPU(렌더 컨텍스트 필요) | ✅ |
| `GpuBvhCompareTests.cs` | 224 | G4: GPU 순회 ≡ Burst | ✅ |
| `GpuRadianceCompareTests.cs` | 324 | G5: GPU AO/Direct/Indirect ≡ Burst | ✅ |
| `GpuSHBakeCompareTests.cs` | 176 | SH-G: GPU SH ≡ BurstSHBaker | ✅ |
| `AlphaCutoutTests.cs` | 282 | α: RayTriUV≡RayTri, CPU 해석적, Burst≡CPU, OFF 회귀 | ✅ |
| `AlphaGpuCompareTests.cs` | 162 | α: GPU any-hit ≡ Burst | ✅ |
| `LightmapSSAATests.cs` | 237 | SSAA 기하/로직(레이 없음) | ❌ → `AtlasApplyDebug` "SSAA Self-Tests" |
| `LightmapAllocatorTests.cs` | 170 | ST 할당 확장 11케이스 | ❌ → `LightmapAllocatorDebug` "Run Full Tests" |
| `LightmapPostProcessCompare.cs` | 155 | Dilation Burst ≡ 직렬 + 성능 | ❌ → 메뉴 PostCompare Dilate |
| `LightmapDenoiseCompare.cs` | 190 | Denoise Burst ≡ 직렬 · RMS · 엣지 보존 · 격리 | ❌ → 메뉴 PostCompare Denoise |

> `SH9BakeTextureTest`(Samples)도 RunAll에 포함 → 총 16개 클래스, 109/109 PASS(2026-08-10).

---

## 6. 어셈블리 정의

| asmdef | 참조 | includePlatforms |
|---|---|---|
| `Runtime/HuskyLibs.CustomLightmapper` | Burst, Collections, Mathematics | 전체 |
| `Editor/HuskyLibs.CustomLightmapper.Editor` | Runtime, Tests | `Editor` |
| `Tests/HuskyLibs.CustomLightmapper.Tests` | Runtime | 전체 ⚠ (Editor 한정 권장) |
| `Samples/HuskyLibs.CustomLightmapper.Samples` | Runtime, Tests | 전체 ⚠ (Editor 한정 권장) |

모든 asmdef `allowUnsafeCode: true`. **Samples는 Editor를 참조하지 않는다** — 에디터 전용 기능(MenuItem 등)은 `Editor/`에 둘 것(C0 `SceneCollectMenu`도 같은 이유로 Editor에 배치 예정).

---

## 7. 핵심 불변식 (코드 수정 시 깨지면 안 되는 것)

| 불변식 | 위치 | 깨지면 |
|---|---|---|
| 텍셀 시드 `seed + li * 2654435761u` | `LightmapSSAA.TexelSeed`, 3백엔드 | CPU/Burst/GPU 교차검증 무효 |
| 평가 원점 `worldPos + normal * surfaceBias` | `AtlasApplyDebug` | 백엔드 간 차이 발생 |
| 인스턴스 순서 = 수집 순서 | `ResolveTargets` | ST·시드가 바뀌어 비트 동일 비교 불가 |
| `Tri`/`GpuTri` 36B 불변, α는 병렬 배열 | `Occluder.cs`, `GpuScene.cs` | GPU stride 불일치, 알파 정합 실패 |
| 차폐 씬 삼각형 = `mesh.triangles` 순서 | `AlphaMaskBuilder` 정합 검사 | 해당 메시 알파가 조용히 꺼짐 |
| 후처리 순서 Denoise → Stitch → Dilate | `AtlasApplyDebug.BakeAndApply` | 시임 불일치·거터 오염 |
| GPU 순회 함수 복제 금지(매개변수 분기) | `PathTrace.compute` | X4714 레지스터 압박 재발 |
| `BurstScene`의 모든 NativeArray는 모든 `Create` 경로에서 할당 | `BurstScene.cs` | 잡 스케줄 시 "not been assigned" 예외 |
