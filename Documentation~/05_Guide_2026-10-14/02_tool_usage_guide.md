---
title: "CustomLightmapper — 툴 사용법"
tags: [custom-lightmapper, guide, usage]
---

# CustomLightmapper — 툴 사용법

> 기준일 2026-10-14 · 패키지 v0.14.0 · Unity 6000.0+ / URP
> 모든 메뉴·필드 이름은 현재 코드에서 그대로 옮겼다.

---

## 0. 준비

### 설치

패키지는 `Packages/com.huskylibs.customlightmapper/`에 **embedded**로 들어 있어 Unity가 자동 인식한다.
다른 프로젝트에서 쓰려면 `Packages/manifest.json`에 추가한다.

```json
"com.huskylibs.customlightmapper": "file:../../경로/com.huskylibs.customlightmapper"
```

의존 패키지: Burst 1.8.4 · Collections 2.1.0 · Mathematics 1.2.6 (자동 해결).

### 필수 체크

| 항목 | 이유 |
|---|---|
| 임포트 메시(FBX) **Read/Write Enabled** | 정점/삼각형을 CPU에서 읽는다. 꺼져 있으면 해당 메시는 건너뛰거나 에러 로그 |
| 씬에 **Directional Light** 1개 | `From DirectionalLight Component`로 방향·색·강도를 가져온다 |
| Compute Shader 지원 GPU | GPU 백엔드용. 미지원/로드 실패 시 자동으로 Burst로 폴백 |
| URP | `LightmapDebug.shader`, `InstancedSH_URP.shader`가 URP 기준 |

### 샘플 씬

`Packages/com.huskylibs.customlightmapper/Samples/Mesh_Struct_Sample_Scene.unity`

### 도구 종류와 호출 방식

| 종류 | 위치 | 호출 방법 |
|---|---|---|
| MonoBehaviour 하니스 | `Samples/` | GameObject에 컴포넌트 추가 → **인스펙터 우클릭(⋮) → 컨텍스트 메뉴** |
| static 테스트 | `Tests/` | 디버거 컨텍스트 메뉴 또는 코드 `Debug.Log(X.RunAll())` |
| 에디터 메뉴/윈도우 | `Editor/` | 상단 메뉴바 `Husky/…`, `HuskyLibs/…` |

---

## 1. 빠른 시작 — 라이트맵 굽기 (5분)

1. 굽고 싶은 메시들을 빈 GameObject의 **자식**으로 모은다(또는 나중에 `targets`에 직접 지정).
2. 그 부모에 **`AtlasApplyDebug`** 컴포넌트를 추가한다.
3. **매핑부터 확인**: `mode = PerInstanceColor` → 우클릭 **Bake & Apply**
   → 오브젝트마다 다른 단색이 겹침 없이 입혀지면 UV2·ST 할당이 정상.
4. 우클릭 **From DirectionalLight Component** → 씬 태양광 값이 `lightDirection/lightColor/lightIntensity`로 복사된다.
5. 실제 GI 베이크 설정:
   - `mode = RadianceGI`
   - `radianceBackend = Gpu` (또는 `Burst`) ← 기본값이 `CPU`라 반드시 바꿀 것
   - 처음엔 `atlasResolution = 256`, `indirectSamples = 16`, `maxBounces = 1`
6. 우클릭 **Bake & Apply** → 콘솔에 단계별 시간 로그, 오브젝트에 라이트맵이 입혀진다.
7. 원래대로 되돌리기: 우클릭 **Restore Originals** (원본 메시·머티리얼 복원).

> 결과 아틀라스는 인스펙터 Result의 `atlas`(Texture2DArray), 머티리얼은 `sharedMat`.

---

## 2. AtlasApplyDebug — 메인 베이크 도구

`Runtime/LightmapAllocator/AtlasApplyDebug.cs`(2026-10-26 병합으로 Samples → Runtime) · `[ExecuteAlways]` · 텍스처 라이트맵 엔드투엔드 베이크 진입점(SH 는 `InstanceSceneBaker`, 식생 프로브 볼륨은 `ProbeVolumeBaker`).

### 2.1 컨텍스트 메뉴

| 메뉴 | 동작 |
|---|---|
| **Bake & Apply** | 할당 → 파라미터화 → 텍셀 복원 → 라이팅 → SSAA → 후처리 → 아틀라스 생성 → 렌더러 적용 |
| **Restore Originals** | 적용한 uv2 메시·머티리얼·프로퍼티블록을 원본으로 복원 |
| **From DirectionalLight Component** | 씬의 첫 Directional Light 값을 필드로 복사 |
| Radiance Diff Test (BruteForce vs BVH) | `Radiance` 모드 차폐 백엔드 픽셀 차이 측정(비트 일치 확인) |
| RadianceGI Backend Diff (CPU vs Burst) | GI 베이크 CPU↔Burst 텍셀 대조 |
| RadianceGI Backend Diff (Burst vs GPU) | GI 베이크 Burst↔GPU 텍셀 대조 + 시간 비교 |
| SSAA Self-Tests | `LightmapSSAATests` 실행(레이 없는 기하/로직 검증) |
| Alpha Diagnose (알파 효과 측정) | 같은 레이를 알파 ON/OFF로 쏴서 판정이 뒤집힌 수를 셈. 0이면 알파가 작동 안 하는 것 |

### 2.2 베이크 모드 (`mode`)

| 모드 | 용도 |
|---|---|
| `PerInstanceColor` | 인스턴스별 고유색 — ST/아틀라스 매핑 검증 |
| `WorldNormal` | 월드 노멀 색 — 텍셀 복원 검증 |
| `Checker` | 체커 패턴(`checkerSize`) — 텍셀 밀도·왜곡 검증 |
| `Radiance` | 구 경로: 월드 삼각형 + 단일 BVH, AO + Direct. **알파 미지원** |
| **`RadianceGI`** | **프로덕션 경로**: 2단 BVH 경로추적 Direct + Indirect, 3백엔드, 알파·SSAA 지원 |

### 2.3 인스펙터 필드

**Input**

| 필드 | 기본값 | 설명 |
|---|---|---|
| `targets` | — | 라이트맵을 받을 MeshFilter. 비우면 자기+자식 전부 |
| `occluders` | — | **그림자만 드리우고 굽지 않는** 오브젝트(지형·건물·나무 등) |
| `segmentation` | Default | 차트 분할: `SeamAngleDeg`(40°, 이면각 초과 시 절단) · `MaxChartAngleDeg`(60°, 차트 평균 노멀 허용 편차) |

**Allocation**

| 필드 | 기본값 | 설명 |
|---|---|---|
| `atlasResolution` | 1024 | 아틀라스 한 변 해상도 |
| `texelsPerWorldUnit` | 16 | 월드 1유닛당 텍셀 수 — 해상도/품질의 핵심 |
| `gutterTexels` | 2 | 인스턴스 간 여백 |
| `maxPages` | 8 | 최대 페이지(Texture2DArray 슬라이스) |

**Bake**: `mode`, `chartGutter`(0~0.05, 차트 간 UV 여백), `background`(빈 텍셀 색), `checkerSize`

**Radiance / Light**

| 필드 | 기본값 | 설명 |
|---|---|---|
| `lightDirection` | (-0.3,-1,-0.2) | 빛 진행 방향 |
| `lightColor` / `lightIntensity` | white / 1 | 태양 색·강도 |
| `ambient` | (0.2,0.25,0.35) | `Radiance` 모드 환경광 |
| `aoSamples` | 32 | `Radiance` 모드 AO 샘플 |
| `seed` | 12345 | 결정적 시드 기준 |
| `surfaceBias` | 0 | 자기 그림자(acne) 방지 바이어스 |
| `occluderKind` / `bvhQuality` | BVH / Median | `Radiance` 모드 차폐 백엔드 |

**Radiance GI**

| 필드 | 기본값 | 설명 |
|---|---|---|
| `indirectSamples` | 32 | 텍셀당 간접 경로 수(spp) |
| `maxBounces` | 2 | 바운스 상한 |
| `skyColor` | (0.3,0.35,0.45) | 하늘 복사휘도 |
| `defaultAlbedo` | (0.6,0.6,0.6) | 머티리얼 색을 못 읽을 때 |
| `radianceBackend` | **CPU** | `CPU` / `Burst` / `Gpu` — **Gpu 권장** |

**Direct Shadow (태양 원반)**

| 필드 | 기본값 | 설명 |
|---|---|---|
| `directSamples` | 1 | 텍셀당 그림자 레이. 1 = 하드(점묘 노이즈 가능), 8~16 권장 |
| `sunAngularDiameterDeg` | 0 | 태양 각지름. 실제 태양 **0.53°** 권장. 1° 이상은 잎 형태가 뭉개짐 |

**SSAA**

| 필드 | 기본값 | 설명 |
|---|---|---|
| `ssaaMode` | Off | `Off` / `Adaptive`(권장) / `Full`(레퍼런스, S²배 비용) |
| `ssaaFactor` | 2 | 한 축 서브샘플 수 S (2~4) |
| `ssaaEdgeThreshold` | 0.10 | 엣지 판정 상대 휘도차. 낮을수록 재샘플 텍셀↑ |
| `ssaaEdgeNormalAngleDeg` | 45 | 이 각도보다 꺾인 이웃은 비교 제외 |
| `ssaaEdgeDilate` | 1 | 엣지 마스크 확장 링 수(1 권장) |
| `ssaaDebugMask` | off | 재샘플 텍셀을 마젠타로 표시(튜닝 전용) |

**Alpha Cutout (α)**

| 필드 | 기본값 | 설명 |
|---|---|---|
| `alphaCutoutShadows` | on | 컷아웃 알파를 그림자 판정에 반영 |
| `alphaMaskResolution` | 256 | 머티리얼당 마스크 해상도 상한 |
| `alphaTransparentPolicy` | Ignore | 반투명(유리·물) 머티리얼은 차폐 제외 |
| `alphaForceCutout` | — | 자동 판별이 실패한 머티리얼 강제 지정 |
| `alphaForceCutoutNames` | — | 머티리얼/셰이더 이름 부분일치 강제(예: `Leaf`, `Tree_N_`) |
| `alphaDefaultCutoff` | 0.5 | 컷오프 프로퍼티가 없을 때 임계(SpeedTree는 0.3333 자동) |

**Denoise** (`Radiance`/`RadianceGI` 전용)

| 필드 | 기본값 | 설명 |
|---|---|---|
| `denoise` | on | 몬테카를로 노이즈 평활 |
| `denoiseIterations` | 3 | 1~5. 3이면 유효 반경 ≈17텍셀 |
| `denoiseNormalPower` | 32 | 클수록 각진 면 분리 강함 |
| `denoisePositionSigmaTexels` | 2 | 월드 거리 가중 — 먼 차트끼리 섞임 차단 |
| `denoiseColorSigma` | 0.25 | 작을수록 그림자 경계 보존 강함 |
| `denoiseBurst` | on | 끄면 C# 직렬판(비교용) |

**Seam Stitch**: `seamStitchTier1`(정점) / `seamStitchTier2`(모서리) / `seamStitchIterations` / `seamMaxAngleDeg`(45, 이보다 꺾인 하드 엣지는 제외)

**Post Process**: `dilate`(on) / `dilateIterations`(4, `gutterTexels` 근처) / `dilateBurst` / `atlasFilter`(Point=블록, Bilinear=보간)

**Result (읽기 전용)**: `instanceCount`, `pageCount`, `utilization`, `overflow`(페이지 초과), `atlas`, `sharedMat`

### 2.4 권장 프리셋

| 목적 | 설정 |
|---|---|
| **미리보기** | `atlasResolution 256` · `texelsPerWorldUnit 8` · `RadianceGI` · `Burst` · `indirectSamples 16` · `maxBounces 1` · SSAA Off |
| **표준** | `1024` · `16` · `Gpu` · `indirectSamples 32` · `maxBounces 2` · Denoise on · SSAA `Adaptive` |
| **고품질** | `2048` · `Gpu` · `indirectSamples 64` · `directSamples 8~16` · `sunAngularDiameterDeg 0.53` · SSAA `Adaptive`(S=3) |
| **나무/잎 씬** | 위 + `alphaCutoutShadows on` · 나무를 `occluders`에 · `denoiseIterations 1` 정도로 낮춤(잎 실루엣 보존) |

### 2.5 시나리오별 사용법

**바닥만 굽고 나무·건물은 그림자만**
→ 바닥을 `targets`, 나무·건물을 `occluders`에 넣는다. occluders는 차폐 씬에만 참여하고 메시·머티리얼이 바뀌지 않는다.

**잎이 통짜 그림자로 나올 때**
1. `mode = RadianceGI` 인지 확인(구 `Radiance`는 알파 미지원).
2. 우클릭 **Alpha Diagnose** → 뒤집힌 레이 수가 0이면 머티리얼 판별 실패.
3. SpeedTree/커스텀 셰이더면 `alphaForceCutoutNames`에 셰이더·머티리얼 이름 일부를 넣는다.

**그림자 외곽이 계단처럼 각질 때**
→ 알파 문제가 아니라 **텍셀 밀도** 문제. `texelsPerWorldUnit`을 올리거나 `ssaaMode = Adaptive`. `ssaaDebugMask`로 재샘플 위치 확인 후 끈다.

**잎 경계에 점묘(자글자글) 노이즈**
→ 그림자 레이 1발의 이진 판정. `directSamples 8~16` + `sunAngularDiameterDeg 0.53`.

**백엔드 결과가 같은지 확인**
→ **RadianceGI Backend Diff (Burst vs GPU)** — `meanDiff`가 1e-6대, `over(1/255)`가 0.001% 수준이면 정상.

---

## 3. SH 인스턴싱 데모 (Track B)

### SHRenderDemo — 대량 인스턴스 SH 렌더

`Samples/SHRenderDemo.cs` (SHDebugView 자동 요구)

1. 빈 GameObject에 `SHRenderDemo` 추가 → `SHDebugView`도 함께 붙는다.
2. 필요 시 `sun`에 Directional Light 지정(없으면 `sunDirFallback`).
3. 우클릭 **Bake + Render SH** → 데모 박스 2종 × `perTemplate`개 인스턴스가 SH 조명으로 그려진다.
4. 자기 메시로 하려면 `TestMeshes`에 메시를 넣는다(Read/Write 필요). 임의 메시는 `anchorMode = SurfaceNormalOffset`, `liftRelativeToBounds = on` 권장.
5. **Capture Screenshot**으로 `Assets/SHRender_HHmmss.png` 저장.

| 필드 | 설명 |
|---|---|
| `perTemplate` | 템플릿당 인스턴스 수(기본 300) |
| `shDir` | 프로브당 방향 샘플 수(기본 512) |
| `tiltMaxDeg` | 0 = Y회전만, >0 = 임의 3축 기울기 테스트 |
| `anchorMode` / `surfaceLift` / `liftRelativeToBounds` | 프로브 대표점 배치(솔리드 내부에 갇히지 않게 띄움) |
| `skyTop` / `skyBottom` / `sunIntensity` | 하늘·태양 |
| `exposure` | 표시 노출(기본 0.5) |
| `shBakeBackend` | `Burst` / `Gpu` |

> ⚠ **Load Real Templates (Adonis_Snackbar)** 는 `Resources/Adonis_Snackbar_A/B` FBX가 필요한데 현재 프로젝트에 없다 — 실행 시 "못 찾음" 에러가 정상.

### 기타 SH 도구

| 컴포넌트 | 메뉴 | 용도 |
|---|---|---|
| `SHBakeDemo` | Bake SH (Demo) | SH-1~4 엔드투엔드(렌더 없이 기즈모만) |
| `SHDebugView` | — | 대표점에 SH 평가색 기즈모(Point/Cross/Sphere) |
| `SH9BakeTextureTest` | Bake SH & Write Texture / Run Self Tests | SH9 수식 검증 + equirect 텍스처 출력 |
| `SHEvalProbeVerify` | Verify GPU vs CPU SH | 셰이더 SH 디코드 ≡ CPU (렌더 컨텍스트 필요) |

---

## 4. 단계별 디버거 (파이프라인 중간 결과 보기)

| 단계 | 컴포넌트 | 메뉴 | 보는 것 |
|---|---|---|---|
| A1 | `HalfEdgeConversion` | Rebuild HalfEdge | 하프에지 화살표·경계 에지 |
| A1 | `ChartSegmentationDebugger` | Run Segmentation / - Welded / Clear Result | 차트 분할 색 |
| A1~A3 | `ParameterizationTestDebugger` | Run Parameterization Check / Clear Result | 분할→평탄화 결과·foldover |
| A3 | `UVAssemblerTestDebugger` | Run UV Assemble / Clear Result | uv2 조립·SeamTable |
| A4 | `LightmapAllocatorDebug` | Run Self Tests / Run Full Tests / Run Mesh Test | ST 할당·비겹침 |
| A5 | `TexelMapperDebug` | Run Texel Map / Save Preview PNG / Clear Result | 루멜 맵(WorldNormal/WorldPos/ValidMask) |
| C1 | `BVHGizmoDebugger` | Build BVH / Create Ray Handles / Clear | BVH 노드 AABB, 레이 순회(방문/컬링 노드) 라이브 |
| C2 | `LightmapEvaluateDebugger` | Evaluate / Clear | 정점별 AO/Direct/Radiance 색 구 |
| C2 | `InstancedSceneTester` | Run Instanced Scene Test | 2단 인스턴싱 ≡ brute 교차검증 |

> 기즈모형 디버거는 **오브젝트를 선택했을 때만** 표시되고, 필드를 바꾸면 **다시 실행**해야 갱신된다.

### 유틸

| 컴포넌트 | 메뉴 | 용도 |
|---|---|---|
| `MeshCombiner` | — | 자식 메시 병합 |
| `ObjectRandomTranslator` | Random Translate | 오브젝트 무작위 이동(테스트 씬 구성) |

---

## 5. 에디터 메뉴

| 메뉴 경로 | 동작 |
|---|---|
| `Husky/Tools/UV Layout Viewer` | 차트 UV 2D 캔버스 창 — foldover(빨강)·겹침 확인, A4 패킹 토글 |
| `Husky/Tool/Custom Lightmapper/Allocate Selected (per-instance ST)` | 선택 오브젝트에 ST 할당 |
| `Husky/Tool/Custom Lightmapper/Run Allocator Self-Tests` | 할당 셀프테스트 |
| `HuskyLibs/CustomLightmapper/PostCompare Dilate (Burst vs Serial)` | Dilation Burst ≡ 직렬 + 성능 |
| `HuskyLibs/CustomLightmapper/PostCompare Denoise (Burst vs Serial + Quality)` | Denoise 일치 + 노이즈 RMS·엣지 보존·성능 |

---

## 6. 테스트 실행

### 전체 회귀 (코드 수정 후 항상)

`LightmapEvaluateDebugger` 컴포넌트 → 우클릭 **Run All Tests (C1+C2)**
→ 콘솔에 클래스별 `n/n PASS`. 기대값 **109/109**, BVH 교차검증 `miss=0`.

포함 테스트(16): C2 토대 · BVH · Indirect · 알베도 모드B · Burst 씬(G0) · Burst Radiance(G2/G3) · 템플릿 어댑터 · SH9 · BurstSHBaker · SHPacked · SH GPU 디코드 · GPU BVH(G4) · GPU Radiance(G5) · GPU SH(SH-G) · 알파 컷아웃 · 알파 GPU.

> GPU·SH 디코드 테스트는 **렌더 컨텍스트가 필요** — 에디터에서 실행(헤드리스 불가).
> `LightmapSSAATests`, Dilate/Denoise 비교는 RunAll에 없음 → 각각 `SSAA Self-Tests`, `PostCompare` 메뉴로.

### 개별 실행 메뉴 (`LightmapEvaluateDebugger`)

Run Self Tests · Run Indirect Tests · Run Mode-B Albedo Tests · Run BVH Cross Tests · Run Burst Scene Tests (G0) · Run Burst Radiance Compare (G2 _ G3) · Run SH GPU Decode Test (SH-5) · Run GPU BVH Compare (G4) · Run GPU Radiance Compare (G5) · Run GPU SH Bake Compare (SH-G) · Run Alpha Cutout Tests (a) · Run Alpha GPU Compare (a) · Run BVH Mesh Test · Run BVH Scene Test (multi-mesh)

### 권장 검증 순서

1. **Run All Tests** — 어느 계층이 깨졌는지 먼저 격리.
2. 단계별 시각 확인 — 차트(A1~A3) → TexelMapperDebug → AtlasApplyDebug `WorldNormal`/`Checker`.
3. 라이팅 — `RadianceGI` 베이크 후 Backend Diff로 백엔드 일치 확인.

---

## 7. 문제 해결

| 증상 | 원인 / 조치 |
|---|---|
| 메시가 베이크에서 빠짐, "Read/Write" 에러 | FBX 임포트 설정 **Read/Write Enabled** 켜기 |
| 베이크가 매우 느림 | `radianceBackend`가 기본값 `CPU` → `Gpu`/`Burst`로. 해상도·spp 낮춰 시작 |
| 차트 경계에 검은 선 | `dilate` on, `dilateIterations ≥ gutterTexels` |
| 모서리에 밝은 테두리(rim) | `seamMaxAngleDeg`를 낮춤(하드 엣지 스티칭 제외) |
| 그림자 경계가 뭉개짐 | `denoiseColorSigma`↓, `denoiseIterations`↓ |
| 화면이 자글자글(그레인) | `indirectSamples`↑ 또는 Denoise on |
| 잎 그림자가 통짜 | `RadianceGI` 모드인지, **Alpha Diagnose**, `alphaForceCutoutNames` |
| 그림자 외곽 계단 | `texelsPerWorldUnit`↑ 또는 SSAA `Adaptive` |
| 밝은 면이 하얗게 잘림 | 아틀라스가 RGBA32(LDR) — 알려진 한계(RGBAHalf 미구현) |
| `overflow = true` | 페이지 초과 → `maxPages`↑ 또는 `texelsPerWorldUnit`↓ |
| GPU 결과가 안 나옴 | 콘솔에 폴백 로그 확인. compute 미지원이면 Burst로 자동 전환 |
| 새 테스트 클래스가 IDE에서 "없음" | Unity 재컴파일 전 정상 |
| 곡면이 늘어나 보임 | (v15.14 부터 LSCM 자동) `flatten.PlanarStretchThreshold` 를 낮추면 더 많은 차트가 LSCM 비교를 탐 · 그래도 남으면 `segmentation.MaxChartAngleDeg` 를 낮춰 차트를 잘게. `ParameterizationTestDebugger` 로 차트별 stretch·방법 확인 |
