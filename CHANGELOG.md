# Changelog

## [Unreleased]

### Added
- 라이트맵 디노이즈(À-trous joint bilateral): `LightmapDenoise`(C# 직렬) + `LightmapDenoiseBurstJob`(Burst 병렬). 텍셀별 월드 노멀·위치·색(RGB L2) 가이드 에지 보존 필터 — MC 노이즈만 평활, 하드 엣지·차트 경계·그림자 경계 보존. 색 range 는 휘도 스칼라가 아닌 RGB 거리(단일 채널 엣지 과소평가 방지).
- `AtlasApplyDebug` 파이프라인에 Denoise 단계 배선(blit → **Denoise** → Seam Stitch → Dilate, Radiance/RadianceGI 전용) + 인스펙터 파라미터(iterations/normalPower/positionSigma/colorSigma/Burst 토글).
- `LightmapDenoiseCompare`: Burst≡Serial 일치 · 노이즈 RMS ≤0.5× · 그림자 엣지 대비 보존 · 원거리 차트 격리 + 성능 측정. 메뉴 `HuskyLibs/CustomLightmapper/PostCompare Denoise`.

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
