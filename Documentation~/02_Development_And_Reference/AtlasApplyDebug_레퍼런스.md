# AtlasApplyDebug 레퍼런스

> 파일: [Script/LightmapAllocator/AtlasApplyDebug.cs](../Script/LightmapAllocator/AtlasApplyDebug.cs)
> 네임스페이스: `HuskyLibs.CustomLightmapper.Bake`
> 타입: `MonoBehaviour` (`[ExecuteAlways]`)

## 개요

**A4(per-instance ST) + TexelMapper 통합 검증 컴포넌트.** 여러 MeshFilter 인스턴스를 모아 다음을 한 번에 수행하고 화면에서 매핑·베이크 정합을 눈으로 검증한다.

1. **할당** — 인스턴스별 월드 표면적 → ST(ScaleOffset) + 페이지 배정 (`LightmapAllocator`)
2. **베이크** — 각 인스턴스의 LumelMap 을 자기 ST 영역에 그대로 구워 `Texture2DArray` 아틀라스 생성
3. **적용** — 조립된 uv2 메시 + ST 를 렌더러에 입혀 `CustomLightmapper/LightmapDebug` 셰이더로 렌더

표면에 베이크 데이터가 정확히 정렬돼 보이면 ST 매핑·텍셀 복원이 일관된 것. ST 가 틀리면 옆 인스턴스 영역/깨진 색이 보인다.

**사용법**: 인스펙터 우클릭 컨텍스트 메뉴 → `Bake & Apply` 실행, `Restore Originals` 로 원복.

---

## 베이크 파이프라인 흐름 (`BakeAndApply`)

```
ResolveTargets()                    대상 MeshFilter 수집 (occluder 차집합 제외)
  │
  ├─ 1) 인스턴스별 WorldArea → LightmapAllocator.Allocate → ST + 페이지
  ├─ 2) 페이지 픽셀버퍼(slices) + validMask 를 배경색으로 초기화
  ├─ (mode별) 차폐 씬 구성:
  │      Radiance    → BuildWorldTris → BVH/BruteForce occluder + _sun
  │      RadianceGI  → BuildGiScene (InstancedRadianceScene + Burst/GPU 백엔드)
  ├─ 3) 인스턴스 루프:
  │      ParameterizationPipeline → DensityNormalizer → ShelfPacker
  │      → UVAssembly.Assemble → TexelMapper.Map → BlitRegion(+디노이즈 가이드 수집)
  │      → 시임 스티칭 입력 누적(Tier1 텍셀그룹 / Tier2 세그먼트)
  │      → 렌더러에 uv2 메시 + ST(MaterialPropertyBlock) 적용
  ├─ 3.3) Denoise (À-trous joint bilateral, Burst/C#) — Radiance/RadianceGI 전용, Seam Stitch 이전
  ├─ 3.4) Seam Stitch (Tier1 정점 / Tier2 모서리) — Dilation 이전
  ├─ 3.5) Dilation (Burst/C#) — 거터/배경 텍셀 확장
  ├─ 4) Texture2DArray 생성/갱신 + sharedMat 에 바인딩
  └─ 정리: occluder/GI 씬/GPU/Burst 리소스 Dispose
```

---

## 인스펙터 필드

### Input
| 필드 | 타입 | 기본값 | 설명 |
|---|---|---|---|
| `targets` | `MeshFilter[]` | — | 비우면 자기 자신+자식의 MeshFilter 를 모두 수집 |
| `occluders` | `MeshFilter[]` | — | 차폐 전용. 차폐 씬에만 참여, 베이크/스왑 대상 아님(지형·프롭·건물) |
| `segmentation` | `SegmentationSettings` | `.Default` | 차트 분할 설정 |

### Allocation (LightmapAllocator)
| 필드 | 타입 | 기본값 | 설명 |
|---|---|---|---|
| `atlasResolution` | `int` | `1024` | 아틀라스 한 변 해상도 |
| `texelsPerWorldUnit` | `float` | `16` | 월드 단위당 텍셀 밀도 |
| `gutterTexels` | `int` | `2` | 인스턴스 간 거터 텍셀 |
| `maxPages` | `int` | `8` | 최대 페이지(슬라이스) 수 |

### Bake
| 필드 | 타입 | 기본값 | 설명 |
|---|---|---|---|
| `mode` | `BakeMode` | `PerInstanceColor` | 베이크 모드(아래 enum 참조) |
| `chartGutter` | `float` (0~0.05) | `0.01` | 차트 패킹 거터(UV 공간) |
| `background` | `Color` | `(0.04,0.04,0.06)` | 빈 텍셀 배경색(거터 가시화) |
| `checkerSize` | `int` (≥1) | `8` | Checker 모드 격자 크기 |

### Post Process (Dilation)
| 필드 | 타입 | 기본값 | 설명 |
|---|---|---|---|
| `dilate` | `bool` | `true` | 거터/배경 텍셀을 인접 valid 평균으로 확장(검은 시임 제거) |
| `dilateIterations` | `int` (≥0) | `4` | Dilation 패스 수(=확장 링 수). `gutterTexels` 근처 권장 |
| `dilateBurst` | `bool` | `true` | Burst Job 병렬판 사용. 끄면 순수 C# 직렬판(비교용) |
| `atlasFilter` | `FilterMode` | `Point` | 아틀라스 샘플링 필터. Point=블록 / Bilinear=보간 |

### Denoise (À-trous Joint Bilateral — Radiance/RadianceGI 전용, Seam Stitch 직전)
| 필드 | 타입 | 기본값 | 설명 |
|---|---|---|---|
| `denoise` | `bool` | `true` | 몬테카를로 노이즈(그레인) 평활. 텍셀별 월드 노멀·위치·색 가이드의 에지 보존 필터(`LightmapDenoise`/`LightmapDenoiseBurstJob`) |
| `denoiseIterations` | `int` (1~5) | `3` | À-trous 반복 수(step=1,2,4…). 3이면 유효 반경 ≈17텍셀 |
| `denoiseNormalPower` | `float` (1~128) | `32` | 노멀 가중 지수 `pow(max(0,n·n'),p)`. 클수록 각진 면 분리 강함(하드 엣지 보존) |
| `denoisePositionSigmaTexels` | `float` (0.5~8) | `2` | 월드 위치 가우시안 σ(텍셀 단위, `texelsPerWorldUnit` 로 월드 변환). 월드에서 먼 차트 간 bleed 차단 |
| `denoiseColorSigma` | `float` (0.01~1) | `0.25` | 색 range σ(Linear RGB L2 거리). 작을수록 그림자 경계 등 라이팅 엣지 보존 강함. 휘도가 아닌 색 거리 — 단일 채널만 다른 엣지도 보존 |
| `denoiseBurst` | `bool` | `true` | Burst Job 병렬판 사용. 끄면 순수 C# 직렬판(비교용) |

### Seam Stitch (시임 스티칭, Dilation 직전)
| 필드 | 타입 | 기본값 | 설명 |
|---|---|---|---|
| `seamStitchTier1` | `bool` | `true` | Tier1(정점): 같은 원본 정점에서 갈라진 경계 텍셀 그룹 평균 |
| `seamStitchTier2` | `bool` | `true` | Tier2(모서리): 경계 모서리를 공유 t로 DDA 순회, 양쪽 텍셀 평균 |
| `seamStitchIterations` | `int` (≥1) | `1` | Tier2 Jacobi 반복 수 |
| `seamMaxAngleDeg` | `float` (1~180) | `45` | 시임 양쪽 노멀 각도 ≤ 이 값일 때만 스티칭(하드 엣지 rim 방지) |

### Radiance (mode=Radiance 전용)
| 필드 | 타입 | 기본값 | 설명 |
|---|---|---|---|
| `lightDirection` | `Vector3` | `(-0.3,-1,-0.2)` | 빛 진행 방향 |
| `lightColor` | `Color` | `white` | 광원색(sRGB) |
| `lightIntensity` | `float` (≥0) | `1` | 광원 강도 |
| `ambient` | `Color` | `(0.2,0.25,0.35)` | 환경광(AO 로 변조), Linear RGB |
| `aoSamples` | `int` (≥1) | `32` | AO 반구 샘플 수(비쌈 — 16~32 시작 권장) |
| `seed` | `uint` | `12345` | 텍셀 결정적 시드 기준값 |
| `surfaceBias` | `float` (≥0) | `0` | 노멀 방향 추가 바이어스(self-occlusion 방지) |
| `occluderKind` | `OccluderKind` | `BVH` | 차폐 백엔드. BVH=가속 / BruteForce=정답(느림, 교차검증) |
| `bvhQuality` | `BVH.BuildQuality` | `Median` | BVH 분할 품질 |

### Radiance GI (mode=RadianceGI — 경로추적 간접광)
| 필드 | 타입 | 기본값 | 설명 |
|---|---|---|---|
| `indirectSamples` | `int` (≥1) | `32` | 텍셀당 간접 경로 수(spp). 매우 비쌈 — 16~32 시작 |
| `maxBounces` | `int` (≥1) | `2` | 표면 바운스 상한 |
| `skyColor` | `Color` | `(0.3,0.35,0.45)` | 하늘(미스 레이) 복사휘도, Linear RGB |
| `defaultAlbedo` | `Color` | `(0.6,0.6,0.6)` | 머티리얼 색 못 읽을 때 per-mesh 기본 알베도(Linear) |
| `radianceBackend` | `RadianceBackend` | `CPU` | GI 베이크 경로. Gpu=컴퓨트(대형 씬 최고속) / Burst=병렬 / CPU=기존 |

### Result (read-only, `[SerializeField]`)
| 필드 | 타입 | 설명 |
|---|---|---|
| `instanceCount` | `int` | 처리된 인스턴스 수 |
| `pageCount` | `int` | 생성된 페이지 수 |
| `utilization` | `float` (0~1) | 아틀라스 점유율 |
| `overflow` | `bool` | 페이지 초과(클램프) 발생 여부 |
| `atlas` | `Texture2DArray` | 생성된 라이트맵 아틀라스 |
| `sharedMat` | `Material` | LightmapDebug 머티리얼 |

---

## 내부 런타임 상태 (private)

| 필드 | 타입 | 용도 |
|---|---|---|
| `_occluder` | `IOccluder` | Radiance 모드 차폐자(BVH/BruteForce). NativeArray 보유 → Dispose 필요 |
| `_sun` | `DirectionalLight` | 광원 POD(방향/색/강도). `BlitRegion` 이 참조 |
| `_ambientLin` | `Vector3` | 환경광 Linear 변환 캐시 |
| `_giScene` | `IRadianceScene` | RadianceGI 2단 인스턴싱 경로추적 씬 |
| `_giBvh` | `TwoLevelBVH` | GI 씬의 공유 TLAS(Burst/GPU 백엔드가 재사용) |
| `_giMeshAlbedo` | `Vector3[]` | per-mesh 알베도(Linear) |
| `_sky` | `ISky` | 하늘(미스 레이) 복사휘도 소스 |
| `_giQ` | `BakeQualitySettings` | GI 품질 설정 POD(AO/spp/bounce/RR/bias) |
| `_burstScene` | `BurstScene` | Burst 백엔드 평탄화 씬(POD). Dispose 필요 |
| `_burstSky` | `BurstSky` | Burst 백엔드용 하늘 uniform |
| `_burstReady` | `bool` | Burst 백엔드 구성 완료 플래그 |
| `_gpuScene` | `GpuScene` | GPU 백엔드 씬(ComputeBuffer). Dispose 필요 |
| `_pathCS` | `ComputeShader` | PathTrace.compute |
| `_kRadiance` | `int` | `CSRadiance` 커널 인덱스(-1=미구성) |
| `_gpuReady` | `bool` | GPU 백엔드 구성 완료 플래그 |
| `_gpuIo` | `GpuIoBuffers` | 재사용 GPU I/O 버퍼 홀더(`_gpuScene` 과 동일 수명) |
| `_appliedFilters` | `MeshFilter[]` | 원복용 — 적용한 렌더러 |
| `_originalMeshes` | `Mesh[]` | 원복용 — 원본 메시 |
| `_originalMats` | `Material[]` | 원복용 — 원본 머티리얼 |

---

## 열거형 (enum)

| enum | 값 | 설명 |
|---|---|---|
| `BakeMode` | `PerInstanceColor` | 인스턴스별 고유색(황금비 분산) — 순수 매핑 검증 |
| | `WorldNormal` | 월드 노멀 시각화 |
| | `Checker` | 체커 패턴(텍셀 정합 검증) |
| | `Radiance` | 텍셀별 AO+Direct+그림자 베이크(실제 라이트맵) |
| | `RadianceGI` | 경로추적 간접광 포함 |
| `OccluderKind` | `BruteForce` / `BVH` | 차폐 질의 백엔드 |
| `RadianceBackend` | `CPU` / `Burst` / `Gpu` | RadianceGI 베이크 실행 경로 |

---

## 메서드

### 컨텍스트 메뉴 (public, 인스펙터 우클릭)

| 메서드 | 설명 |
|---|---|
| `BakeAndApply()` | **메인 진입점.** 할당→베이크→적용 전체 파이프라인 실행 |
| `RestoreOriginals()` | 적용된 메시/머티리얼/프로퍼티블록을 원본으로 복원 |
| `GetDirectionLightFromLightComponent()` | 씬의 첫 Directional Light 에서 방향/색/강도를 필드로 복사 |
| `RadianceDiffTest()` | **검증.** 같은 텍셀에서 BruteForce vs BVH radiance 픽셀 차이 측정(비트 일치 확인) |
| `RadianceGiBackendDiffTest()` | **검증.** CPU vs Burst GI 베이크 텍셀 대조(백엔드 동등성) |
| `RadianceGiBackendDiffTestGpu()` | **검증.** Burst vs GPU GI 베이크 대조 + Stopwatch 시간 비교 |

### 베이크 헬퍼 (private)

| 메서드 | 반환 | 설명 |
|---|---|---|
| `BlitRegion(slice, valid, res, ox, oy, sidePx, lm, tint, giRadiance, guideNormal, guidePos)` | `void` | LumelMap 을 아틀라스 페이지의 (ox,oy) 영역에 blit. valid 텍셀만, 모드별 색 산출. guideNormal/guidePos 가 non-null 이면 디노이즈 가이드(월드 노멀·위치)도 동일 매핑으로 기록 |
| `BakeGiLumelsBurst(lm)` | `Vector3[]` | valid lumel 을 NativeArray 로 모아 `BurstRadianceBaker.Bake` 병렬 처리 → li 인덱스 산란 |
| `BakeGiLumelsGpu(lm)` | `Vector3[]` | Burst 미러. `DispatchRadianceGpu` 한 디스패치 + readback → li 인덱스 산란 |
| `BuildGiScene(filters)` | `void` | 유니크 메시+알베도+인스턴스 → `InstancedRadianceScene` + (선택)Burst/GPU 백엔드 구성 |
| `EnsureMaterial()` | `void` | `CustomLightmapper/LightmapDebug` 셰이더로 `sharedMat` 지연 생성 |

### GPU 정적 헬퍼

| 메서드 | 반환 | 설명 |
|---|---|---|
| `DispatchRadianceGpu(gpuScene, cs, kernel, sun, sky, q, pts, nrm, seeds, n, io)` | `Vector3[]` | `CSRadiance` 디스패치 공통. grow-on-demand 버퍼(`io`), 앞 n개만 업로드/AsyncGPUReadback |
| `LoadPathCompute()` | `ComputeShader` | `Resources.Load<ComputeShader>("PathTrace")` (Shaders/Resources 배치, 에디터·빌드 공통) |

### 지오메트리/유틸 정적 헬퍼

| 메서드 | 반환 | 설명 |
|---|---|---|
| `LocalTris(mesh)` | `Tri[]` | 변환 없는 로컬 삼각형(BLAS 입력) |
| `BuildWorldTris(filters)` | `Tri[]` | 모든 타깃 삼각형을 월드공간으로 평탄화(차폐자 입력) |
| `NormalsAgree(group, normals, cosThresh)` | `bool` | 시임 그룹 노멀이 모두 임계각 이내 = 부드러운 시임 판정(하드 엣지 제외) |
| `LinColor(c)` | `Vector3` | 인스펙터 sRGB Color → Linear Vector3 |
| `ToColor(lin)` | `Color` | 선형 RGB → 아틀라스 저장값(클램프만, LDR) |

### 대상 해석 헬퍼

| 메서드 | 반환 | 설명 |
|---|---|---|
| `ResolveTargets()` | `MeshFilter[]` | targets(없으면 자식) 수집 후 occluder 차집합 제외 |
| `ResolveOccluderUnion(receivers)` | `MeshFilter[]` | receivers ∪ occluders(중복 제거) — 차폐 씬 구성용 |
| `BuildSettings()` | `AllocationSettings` | 인스펙터 필드 → 클램프된 할당 설정 POD |

---

## 중첩 타입: `GpuIoBuffers`

`System.IDisposable` 재사용 GPU I/O 버퍼 홀더 (grow-on-demand). per-instance 반복 `DispatchRadianceGpu` 에서 ComputeBuffer 5개(pts/nrm/valid/seed/radiance)를 매번 생성/해제하던 것을 제거.

| 멤버 | 설명 |
|---|---|
| `Points`, `Normals`, `Valid`, `Seeds`, `Radiance` | `ComputeBuffer` 5종 |
| `ValidScratch` | `uint[]` 항상 1u — 재할당 시 1회만 채움(매 호출 `new uint[n]` GC 제거) |
| `Ensure(n)` | 요청 n 을 담도록 보장. `capacity < n` 일 때만 다음 2^k 로 재할당(그 외 no-op) |
| `Dispose()` | 버퍼 5개 해제 + capacity 리셋 |

**수명**: `BakeGiLumelsGpu` 경로는 인스턴스 필드(`_gpuIo`, `_gpuScene` 과 동일 수명), Backend Diff 메뉴는 로컬 생성 후 해제.

---

## 핵심 불변식(정합 규약)

- **텍셀 시드**: 모든 백엔드(CPU/Burst/GPU)가 `seed + li * 2654435761u` 동일 → 교차검증 성립
- **평가 원점**: `worldPos + worldNormal * surfaceBias` 로 통일
- **색공간**: 아틀라스는 Linear 텍스처. Radiance/GI 는 이미 선형이라 그대로 저장, 디버그 표시색(sRGB)만 `.linear` 변환 → 프레임버퍼가 linear→sRGB 인코딩 1회 수행
- **처리 순서**: Denoise(노이즈 평활) → Seam Stitch(경계값 일치) → Dilation(경계값 확장) 순서 필수 — 경계값을 안정화한 뒤 스티칭하고, 그 값을 거터로 확장한다
- **리소스 해제**: `BakeAndApply` 종료 시 occluder/GI 씬/GPU/Burst 리소스를 모두 Dispose
