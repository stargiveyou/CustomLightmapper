# Changelog

## [Unreleased]

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
