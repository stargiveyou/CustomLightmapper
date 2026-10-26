# CustomLightmapper — 기획 · 개발 완료 정리 (소스 첨부)

> 패키지: `com.huskylibs.customlightmapper` v0.14.0 (UPM embedded)
> 정리 기준일: 2026-08-18 · 기준 커밋: `5a727ff` (SSAA) / `4553f0c` (α 컷아웃) / `6206368` (A-trous denoise)
> 원 문서: `01_Planning_And_Design/커스텀_라이트매퍼_기획서_v15.md` · `02_Development_And_Reference/커스텀_라이트매퍼_개발문서_v15.md`(2026-10-26 UnityStudyProject 브랜치 병합 후 최신 — v14 는 이력 보존)
> Track B(식생 프로브 볼륨) 설계·진행: `01_Planning_And_Design/커스텀_라이트매퍼_TrackB_식생프로브_설계.md` — 이 폴더(01~10)는 2026-08-18 스냅샷이라 병합분(SH 다바운스·RGBAHalf·결과 저장·Track B B1-1~5)과 C0 씬 수집(v15.12)은 v15 문서가 기준.

이 폴더는 **기획 단계별 결정**과 **실제 개발 완료된 코드**를 1:1로 대응시켜 정리한 문서 묶음이다.
기존 01~03 문서가 "시간순 변경 이력"이라면, 여기는 **"현재 트리에 실제로 존재하는 소스 기준의 완료 스냅샷"** 이다.
모든 항목은 실제 파일·줄번호 기준 코드 인용을 포함한다.

---

## 문서 구성

| # | 문서 | 내용 |
|---|------|------|
| 01 | [기획 단계별 정리](01_기획_단계별_정리.md) | 2-트랙 아키텍처, 공유 계약, 결정 ①~⑫, 마일스톤 A/C/G/SH 시간순 |
| 02 | [A트랙 — UV 파라미터화 A1~A4](02_A트랙_UV파라미터화_A1-A4.md) | Half-Edge → 차트분할 → 평탄화 → 패킹 → uv2 → 텍셀/SSAA/ST |
| 03 | [C1 — 가속구조 BVH](03_C1_가속구조_BVH.md) | 단일 BVH(Median/SAH) + 2단 TLAS/BLAS, IOccluder |
| 04 | [C2 — 라이팅 RadianceCore](04_C2_라이팅_RadianceCore.md) | AO / Direct(NEE) / Indirect(경로추적+RR), Sky, 씬 |
| 05 | [후처리 — 스티칭·디레이션·디노이즈](05_후처리_스티칭_디레이션_디노이즈.md) | 시임 Tier1/2, 노멀 게이팅, Dilation, À-trous 디노이즈 |
| 06 | [G0~G3 — Burst 백엔드](06_G0-G3_Burst_백엔드.md) | POD 평탄화(BurstScene) + AO/Direct/Indirect Job |
| 07 | [G4~G6 — GPU Compute](07_G4-G6_GPU_Compute.md) | GPU 2단 순회, 경로추적 커널, 실 백엔드 배선·최적화 |
| 08 | [SH 트랙 — 인스턴싱 프로브](08_SH트랙_인스턴싱_프로브.md) | SH-1~5 + SH-G, 어댑터, 패킹, 셰이더 |
| 09 | [검증 · 테스트 매트릭스](09_검증_테스트_매트릭스.md) | 등록 테스트 전수 + 실측 수치 + 미커버 갭 |
| 10 | [소스 인벤토리](10_소스_인벤토리.md) | 전체 파일 맵 · 어셈블리 구성 · 문서↔트리 차이 |

---

## 완료 상태 대시보드

### 오프라인 베이크 파이프라인 (기획서 §4)

| 단계 | 내용 | 상태 | 구현 |
|---|---|---|---|
| 1 | 수집·분류·알파 마스크 | ◕ | 알파 마스크 ✅(`Alpha/` + 3백엔드 any-hit) / C0 씬 수집·분류 구현(`Runtime/Collect/` + `Editor/SceneCollector`, v15.12 — 에디터 씬 게이트 대기) / MeshCleaner = `WeldedHalfEdge` 용접·퇴화제거 공개(진단만) |
| 2 | 메시 구조(클린업→Half-Edge) | ✅ | `HalfEdge.cs` (`WeldedHalfEdge` weld 내장) |
| 3 | 파라미터화(분할→UV2→정규화→패킹→통합) | ✅ | 평면투영 한정 (LSCM/MVC 스텁) |
| 4 | 텍셀 복원(uv2→worldPos/normal) | ✅ | `TexelMapper.cs` |
| 4* | SSAA 텍셀 내부 슈퍼샘플링(그림자 계단 제거) | ✅ | `TexelMapper.MapSubsamples` / `LightmapSSAA.cs` |
| 4' | per-instance ST 할당 | ✅ | `LightmapAllocator.cs` |
| 5 | 가속구조 BVH | ✅ 검증 | `BVH.cs` / `TwoLevelBVH.cs` |
| 6 | 라이팅(AO/Direct/Indirect) | ✅ 검증 | `RadianceCore.cs` |
| 6.5 | GI 베이크 연결 | ✅ | `AtlasApplyDebug.RadianceGI` |
| 7 | 프로브 베이크(Track B) | ◕ | SH 인스턴싱 + 공간 프로브 볼륨(B1-1~6: 격자·유효성·저장·픽셀 셰이더·정점 AO·알파 잎 차폐) — v15 문서·`Runtime/ProbeVolume/` · B2 적응형 남음 |
| 8 | 후처리(디노이즈/스티칭/디레이션/RGBAHalf) | ✅ | `Denoise/` `LightmapStitch/` `Diliation/` |
| 9 | 출력·런타임 적용 | ✅ | `LightmapDebug.shader` / `InstancedSH_URP.shader` |

### 백엔드 3종 (동일 알고리즘 · ε 교차검증)

| 백엔드 | 진입점 | 상태 | 실측 |
|---|---|---|---|
| CPU (ground truth) | `RadianceCore.EvaluateRadiance` | ✅ | 기준 |
| Burst | `BurstRadianceBaker.Bake` | ✅ | 77만 텍셀 mean≈0 (≡CPU) |
| GPU compute | `PathTrace.compute` `CSRadiance` | ✅ | 653k 텍셀 meanDiff 3e-7, **7.2× 가속** |

### 마일스톤

| ID | 내용 | 상태 |
|---|---|---|
| A1~A4 | 파라미터화 ~ 텍셀/ST | ✅ 완료 |
| C1 | BVH(단일 Median/SAH + 2단 TLAS/BLAS) | ✅ 완료·검증 |
| C2 | RadianceCore(AO/Direct/Indirect) + ISky | ✅ 완료·검증 |
| C2.5 | GI 베이크 연결 + 알베도 수집 | ✅ 완료 |
| G0~G3 | Burst Job화(순회 + AO/Direct/Indirect) | ✅ 완료·검증 |
| G4 | GPU 2단 BVH 순회 | ✅ 완료·검증 |
| G5 | GPU 경로추적(AO/Direct/Indirect) | ✅ 완료·검증 |
| G6 | 실 백엔드 배선 + 영구버퍼·AsyncReadback | ✅ 완료·검증 |
| SH-1~5 | 인스턴싱 SH9 엔드투엔드(URP) | ✅ 완료·검증 |
| SH-G | SH 베이크 GPU 가속 | ✅ 완료·검증 |
| 후처리 | 시임 Tier1+2 · Dilation · **Denoise** · RGBAHalf | ✅ 완료 |
| α0~α8 | 알파 컷아웃 Any-Hit 셰도우(CPU/Burst/GPU) | ✅ 완료·검증 |
| SSAA | 텍셀 내부 슈퍼샘플링(Adaptive/Full) | ✅ 완료·검증 |
| C0 | 씬 수집·분류 (알파 마스크는 완료) | ◕ C0-0~C0-4 구현(v15.12, 정책 테스트 20/20 헤드리스) · 에디터 씬 게이트 V3·V6·V8 대기 · C0-5(선택) 미착수 |
| B1 | 범용 프로브 격자 / 정점 베이크 | ✅ B1-1~6 구현(격자·유효성·저장·픽셀 셰이더·정점 AO v15.7~10 병합 · 알파 잎 차폐 v15.13) — 에디터 테스트 대기 |
| — | LSCM / MVC 솔버 본체 | ✗ 스텁 |
| — | Point / Spot / Area 광원 | ✗ 미착수 |

---

## 다음 단계 (2026-10-26 갱신)

우선순위와 작업 단위는 **[개발문서 v15 §10](../02_Development_And_Reference/커스텀_라이트매퍼_개발문서_v15.md)** 이 기준이다(2026-10-26 병합 반영).

| 순위 | 항목 | 왜 |
|---|---|---|
| 1 | **에디터 확인 게이트** | 병합(v15.11) + C0(v15.12) + B1-6(v15.13)은 에디터 밖에서만 검증(패키지 전체 타입 검사 통과) — 재컴파일 0 · Run All Tests 27종 · C0 씬 게이트(V3 비트동일·V6 α 정합) → [C0 진행](../03_Progress_And_Validation/진행상황_C0-SceneCollector_2026-08-18.md) |
| — | ~~C0 — 씬 수집·분류~~ | ◕ 구현(v15.12) — [설계](../01_Planning_And_Design/커스텀_라이트매퍼_C0_씬수집_설계.md) · [개발문서](../02_Development_And_Reference/커스텀_라이트매퍼_C0_SceneCollector_개발문서.md) §11 구현 차이 |
| — | ~~B1-6 — Track B 알파 잎 차폐~~ | ✅ 구현(v15.13) — 패키지 α any-hit 연결, 코어 무수정 → [Track B 설계](../01_Planning_And_Design/커스텀_라이트매퍼_TrackB_식생프로브_설계.md) §6 체크리스트 |
| 2 | Track B B2 — 적응형 프로브 | D1: 균일 격자 → 지형 추종 2.5D. 빈 하늘 공간 낭비 해소 |
| 3 | LSCM / MVC 솔버 본체 | 리스크 표에서 유일하게 미해소 — 곡면 차트가 평면투영만 받음 |
| 4 | Point / Spot / Area 광원 | `ILight.Sample` 계약 확정 후 3백엔드 미러링 |
| 5 | ~~SH-2 다바운스 · `iid*7` 인덱싱 검증~~ | ✅ 완료(v14.12·v14.14·v15.2 — 병합으로 반영). SH 트랙 잔여는 실씬 노출 튜닝 |
| 6 | G6-3b 단일 글로벌 디스패치 · 타일/적응 샘플링 | GPU 추가 가속(현재 7.2×) |
| — | ~~지형 QuadTree 라이트맵~~ | **2026-08-18 로드맵 제외**. `AtlasApplyDebug.occluders` 는 occluder 전용 기능으로 존치 |
| 보류 | BIRP 포팅 | 2026-07-09 결정 — SH 트랙 URP 확정 |

---

## 읽는 순서 권장

- **처음 보는 사람**: 01 → 02 → 03 → 04 (파이프라인 이해) → 09 (검증)
- **성능/백엔드 관심**: 01 → 06 → 07
- **인스턴싱·대량 프롭**: 01 → 08
- **코드 찾기**: 10

---

## 표기 규약

- ✅ 구현·검증 완료 / ◐ 부분 완료 / 🔧 설계 완료·구현 미착수 / ✗ 미착수 / ⚠ 주의·차이점
- 코드 인용은 `파일경로:줄번호` 형식이며 실제 트리 기준이다.
- 문서(v14)와 실제 트리가 다른 지점은 각 문서 말미 「문서↔트리 차이」에 명시했다.
