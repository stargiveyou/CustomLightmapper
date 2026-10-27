---

> 📝 2026-10-27: 현재 코드 기준 사용법·테스트 실행법은 `06_Manual/챕터별_테스트_실행_매뉴얼.md` (이 가이드보다 최신).
> 📝 2026-10-26: UnityStudyProject 브랜치 병합으로 `AtlasApplyDebug` 가 `Runtime/` 으로 이동했고 `InstanceSceneBaker`·`ProbeVolumeBaker`(Track B)·결과 저장이 추가됐다. 이 가이드의 나머지 서술은 2026-10-14 기준 — 최신 구조는 `02_Development_And_Reference/커스텀_라이트매퍼_개발문서_v15.md`.
title: "CustomLightmapper 가이드 (2026-10-14) — 색인"
tags: [custom-lightmapper, guide, index]
---

# CustomLightmapper 가이드 (2026-10-14) — 색인

> 패키지 `com.huskylibs.customlightmapper` v0.14.0 (UPM embedded, Unity 6000.0+, URP 개발)
> 코드 기준: 패키지 저장소 `5a727ff` (SSAA) — 이후 코드 커밋 없음. 문서만 2026-08-18 C0 설계 추가.
> 이 가이드는 **처음 쓰는 사람이 바로 굽고, 코드를 찾아 들어갈 수 있게** 만든 실무용 요약이다.
> 설계 근거·시간순 이력은 기존 `Documentation~/01~04` 문서가 원본이다.

## 문서 구성

| # | 문서 | 이런 때 읽는다 |
|---|---|---|
| 01 | [개발 내용 정리](01_development_overview.md) | 무엇이 만들어졌고, 어디까지 검증됐고, 무엇이 남았나 |
| 02 | [툴 사용법](02_tool_usage_guide.md) | 씬에서 실제로 라이트맵·SH를 굽고 확인하는 방법, 튜닝, 문제 해결 |
| 03 | [스크립트 레퍼런스](03_script_reference.md) | 각 `.cs` / 셰이더 / 테스트 파일이 하는 일과 주요 API |

## 한눈에 보기

- **무엇**: Unity Progressive Lightmapper를 대체하는 자체 라이트맵 베이커. UV2 생성부터 경로추적 GI, 후처리, 인스턴싱 SH 프로브까지 직접 구현.
- **백엔드 3종**: CPU(정답) ≡ Burst ≡ GPU Compute — 같은 시드 규약으로 텍셀 단위 교차검증. GPU는 Burst 대비 **7.2×**.
- **진입점**: 씬에 `AtlasApplyDebug` 컴포넌트 → 인스펙터 우클릭 **Bake & Apply**. (실 씬 자동 수집기 C0는 설계만 완료)
- **회귀 테스트**: `LightmapEvaluateDebugger` → **Run All Tests (C1+C2)** = 109/109 PASS (2026-08-10 실측).

## 상태 요약

| 영역 | 상태 |
|---|---|
| UV 파라미터화(A1~A4) · 텍셀 복원 · SSAA · ST 할당 | ✅ (평탄화는 평면투영만 — LSCM/MVC 스텁) |
| BVH(C1) · 라이팅(C2: AO/Direct/Indirect) | ✅ 검증 |
| Burst(G0~G3) · GPU(G4~G6) 백엔드 | ✅ 검증 |
| 후처리(디노이즈 → 시임 스티칭 → 딜레이션) | ✅ |
| 알파 컷아웃 any-hit 그림자(α) · 태양 원반 소프트 섀도 | ✅ 검증 |
| SH 인스턴싱 프로브(SH-1~5, SH-G) | ✅ 검증 (URP) |
| **C0 씬 수집·분류** | 🔧 설계 완료 · 구현 미착수 ← 다음 작업 |
| B1 범용 프로브 격자 · Point/Spot/Area 광원 · LSCM/MVC | ✗ 미착수 |

## 원본 문서 위치 (`Documentation~/`)

| 폴더 | 내용 |
|---|---|
| `01_Planning_And_Design/` | 기획서 v14, C0 씬수집 설계, 알파컷아웃 설계, P2 출력분기 설계 |
| `02_Development_And_Reference/` | 개발문서 v14(§10 다음 단계), AtlasApplyDebug 레퍼런스, 디버그·테스트 도구 레퍼런스, C0 개발문서 |
| `03_Progress_And_Validation/` | 진행상황(C1/G0/C0), 실측 게이트 체크리스트 |
| `04_Complete_Dev_Docs/` | 완료 스냅샷 00~10 (대시보드, 트랙별 상세, 검증 매트릭스, 소스 인벤토리) |
| `05_Guide_2026-10-14/` | **이 가이드** |
