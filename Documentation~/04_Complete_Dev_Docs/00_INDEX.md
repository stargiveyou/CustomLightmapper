# CustomLightmapper — 기획 · 개발 완료 정리 (소스 첨부)

> 패키지: `com.huskylibs.customlightmapper` v0.14.0 (UPM embedded)
> 정리 기준일: 2026-07-27 · 기준 커밋: `6206368` (A-trous denoise) / `f7ec4b9` (Initial import v0.14.0)
> 원 문서: `01_Planning_And_Design/커스텀_라이트매퍼_기획서_v14.md` · `02_Development_And_Reference/커스텀_라이트매퍼_개발문서_v14.md`

이 폴더는 **기획 단계별 결정**과 **실제 개발 완료된 코드**를 1:1로 대응시켜 정리한 문서 묶음이다.
기존 01~03 문서가 "시간순 변경 이력"이라면, 여기는 **"현재 트리에 실제로 존재하는 소스 기준의 완료 스냅샷"** 이다.
모든 항목은 실제 파일·줄번호 기준 코드 인용을 포함한다.

---

## 문서 구성

| # | 문서 | 내용 |
|---|------|------|
| 01 | [기획 단계별 정리](01_기획_단계별_정리.md) | 2-트랙 아키텍처, 공유 계약, 결정 ①~⑫, 마일스톤 A/C/G/SH 시간순 |
| 02 | [A트랙 — UV 파라미터화 A1~A4](02_A트랙_UV파라미터화_A1-A4.md) | Half-Edge → 차트분할 → 평탄화 → 패킹 → uv2 → 텍셀/ST |
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
| 1 | 수집·분류·알파 마스크 | ✗ | SceneCollector / MeshCleaner 미존재 |
| 2 | 메시 구조(클린업→Half-Edge) | ✅ | `HalfEdge.cs` (`WeldedHalfEdge` weld 내장) |
| 3 | 파라미터화(분할→UV2→정규화→패킹→통합) | ✅ | 평면투영 한정 (LSCM/MVC 스텁) |
| 4 | 텍셀 복원(uv2→worldPos/normal) | ✅ | `TexelMapper.cs` |
| 4' | per-instance ST 할당 | ✅ | `LightmapAllocator.cs` |
| 5 | 가속구조 BVH | ✅ 검증 | `BVH.cs` / `TwoLevelBVH.cs` |
| 6 | 라이팅(AO/Direct/Indirect) | ✅ 검증 | `RadianceCore.cs` |
| 6.5 | GI 베이크 연결 | ✅ | `AtlasApplyDebug.RadianceGI` |
| 7 | 프로브 베이크(Track B) | ◐ | SH 인스턴싱 경로만 완료, 범용 프로브 격자 미착수 |
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
| B1 | 범용 프로브 격자 / 정점 베이크 | ✗ 미착수 |
| C0 | SceneCollector · MeshCleaner | ✗ 미착수 |
| — | LSCM / MVC 솔버 본체 | ✗ 스텁 |

---

## 읽는 순서 권장

- **처음 보는 사람**: 01 → 02 → 03 → 04 (파이프라인 이해) → 09 (검증)
- **성능/백엔드 관심**: 01 → 06 → 07
- **인스턴싱·대량 프롭**: 01 → 08
- **코드 찾기**: 10

---

## 표기 규약

- ✅ 구현·검증 완료 / ◐ 부분 완료 / ✗ 미착수 / ⚠ 주의·차이점
- 코드 인용은 `파일경로:줄번호` 형식이며 실제 트리 기준이다.
- 문서(v14)와 실제 트리가 다른 지점은 각 문서 말미 「문서↔트리 차이」에 명시했다.
