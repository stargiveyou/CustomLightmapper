# Track B 식생 프로브 설계 · 진행 계획 (B1 → B2)

> 기준: 기획서 v15 §2 「Track B(식생): Light Probe/SH9(주) + 정점 베이크(보조), 텍스처 회피」 · §8 마일스톤 B1 · §9.1 결정 ②(프로브+정점) ⑥(적응형 프로브) · §11 다음 단계 5번.
> 선행: 공유 코어(2단 BVH·경로추적, CPU/Burst/GPU) ✅ · SH 트랙(SH9·`BurstSHBaker`/`GpuSHBaker` 1/다바운스·`EvaluateSH9.hlsl`·`InstanceSceneBaker`/`InstanceSHBakeAsset`) ✅.
> 원칙: **코어(BVH·순회·RadianceCore·SH 베이커) 무수정 재사용**, 신규는 별도 파일. (B1-6 은 원래 유일한 코어 변경 예정이었으나 D5 갱신으로 패키지 α any-hit 를 그대로 써서 코어 무수정.)
> **진행(2026-10-26)**: D1~D6 권장안 확정 · B1-1 `ProbeGrid` · B1-2 `BurstProbeValidity`(v15.7) · B1-3 `ProbeVolumeAsset`·`ProbeVolumeBaker`·인스턴스 적용(v15.8) · B1-4 `HuskyProbeVolume.hlsl`·`InstancedVegetation_URP`(v15.9) · B1-5 `VertexAOBake`(v15.10) · **B1-6 알파 잎 차폐(v15.13 — 패키지 α any-hit 연결)** — 테스트 6종, 에디터 실행 대기 · B1 완료 → 다음 B2(적응형). · **B2 지형 추종 2.5D**(v15.22 — `ProbeTerrainLayers`, `ProbeVolumeBaker.layout = TerrainLayers`).

---

## 1. 왜 Track B 인가 — SH 트랙과의 차이

| 축 | 인스턴싱 SH 트랙(현재) | Track B 공간 프로브(이 문서) |
|---|---|---|
| 프로브 위치 | 인스턴스마다 2개(상/하 대표점) | **공간 격자**(인스턴스와 무관) |
| 베이크 비용·저장 | ∝ 인스턴스 수 N (112B × 2 × N) | ∝ 볼륨 크기 / 간격 |
| 인스턴스 추가·이동·절차 배치 | 재베이크 필요 | **재베이크 없음**(런타임에 위치로 샘플) |
| 풀(수십만~수백만) | ✗ 버퍼 폭증(1M × 224B ≈ 224MB) | ✅ 픽셀/정점에서 볼륨 샘플 |
| 큰 나무 내부 음영 | 2-프로브 보간 한계 | 볼륨 + 정점 AO(보조) |
| 정확도(같은 점) | 그 점에서 직접 베이크 = 기준 | 격자 보간 오차(간격 의존) |

→ **역할 분담**: 소형 프롭 대량(고정 배치) = SH 트랙 유지 / 식생(나무·풀·꽃)·절차 배치 = Track B. 두 트랙은 같은 씬(2단 BVH)·같은 SH9 계약·같은 셰이더 평가식(`EvaluateSH9`)을 쓴다.

---

## 2. 재사용 자산 (무수정)

| 자산 | Track B 에서의 쓰임 |
|---|---|
| `TemplateInstanceSource.BuildScene` (Mesh[] + Matrix4x4[][] → 2단 BVH) | 볼륨 씬 구성(정적 MeshFilter 도 메시→템플릿·행렬로 변환해 투입) |
| `BurstSHBaker.Bake` / `BakeMultiBounce` (`NativeArray<Vector3> points` 입력) | **격자 위치를 그대로 points 로** — 프로브 베이커 신규 작성 불필요 |
| `GpuSHBaker.Bake` / `BakeMultiBounce` (`CSSHBake`/`CSSHBakeMulti`) | 대형 볼륨 GPU 베이크(동일 시드 규약) |
| `SH9`(Accumulate/Evaluate) · `SHPacked`(7×float4) · `SHInstancedBuffer` | 계수 저장·패킹·인스턴스 버퍼 |
| `EvaluateSH9.hlsl`(`SH9Coeffs`·`EvaluateSH9`·`EvaluateInstanceSH2Axis`) | 런타임 평가(볼륨 샘플 결과도 같은 함수로) |
| `InstancedSH_URP` + `InstancedSHRenderer` | B1-3 인스턴스 단위 적용(셰이더 변경 없음) |
| `ComputeProbePoints`(2-프로브 상/하 대표점) | B1-3 인스턴스별 샘플 위치 |
| `BurstAO.Compute`(points/normals) · `BurstRadianceBaker.Bake` | B1-5 정점 베이크(정점 = 점 배열) |
| `BakeOutputUtil`(EXR·폴더·임포트) · `[PreferBinarySerialization]` SO 패턴 | B1-3 저장(SO + EXR — §9.3 결정 그대로) |
| `PathTraceShader.Resolve` | GPU 셰이더 로드 |

반사 규약(ρ/π·E)·Linear·시드 규약(`seed + i*2654435761u`)·직사광 실시간(SH엔 간접+환경만)도 그대로 따른다.

---

## 3. 데이터 흐름

```
[씬 입력] 정적 MeshFilter(지형·건물) + 템플릿·행렬(나무·풀·프롭)
   └─ TemplateInstanceSource.BuildScene → 2단 BVH (+ BurstScene / GpuScene)
[프로브 배치]  ProbeGrid(origin, spacing, dims)  →  positions[]          (B1-1)
               └ B2 지형 추종: ProbeTerrainLayers(grid + 열 지면 높이) → positions[]   (v15.22, 같은 인덱스 공간)
[베이크]       BurstSHBaker / GpuSHBaker (1·다바운스)  →  SH9[]          (재사용)
[유효성]       ProbeValidity(backface 비율)  →  valid[] → dilation      (B1-2)
[저장]         ProbeVolumeAsset(SO: 메타·SH 패킹·valid + Texture3D×7) + EXR(z 슬라이스 타일)   (B1-3)
[런타임]       (a) 인스턴스 대표점에서 CPU 샘플 → SHInstancedBuffer(iid*14) → InstancedSH_URP   (B1-3)
               (b) 픽셀/정점에서 Texture3D 샘플 → EvaluateSH9                                (B1-4)
               × 정점 AO(템플릿 로컬, 보조)                                                  (B1-5)
```

---

## 4. 구성요소 설계

### 4.1 `ProbeGrid` (B1-1, 코어)
- `origin`(최소 코너), `spacing`(Vector3 — 수평/수직 분리 가능), `dims`(int3).
- 프로브 위치 `p(i,j,k) = origin + (i,j,k)·spacing` — 셀 **코너**에 배치(텍스처 텍셀 중심 ↔ 프로브 정렬: `uvw = ((p-origin)/spacing + 0.5) / dims`).
- 선형 인덱스 `idx = i + dims.x·(j + dims.y·k)`, `FromBounds(Bounds, spacing)` 헬퍼.
- CPU `Sample(SH9[] grid, bool[] valid, Vector3 pos)` — 8탭 trilinear(SH 계수는 위치에 대해 선형 결합해도 의미 유지). valid 가 주어지면 무효 탭 가중치 0 + 재정규화.

### 4.2 베이크 (B1-1, 재사용)
- positions 를 `NativeArray<Vector3>` 로 → `BurstSHBaker.Bake`(1바운스, 결정적) 또는 `BakeMultiBounce`(경로추적) / GPU 대응 커널. **신규 커널 없음.**
- 규모 감: 128×16×128 = 262,144 프로브 × 방향 256 ≈ 6,700만 1차 레이(+1바운스는 히트당 그림자 레이). 대형 볼륨은 GPU 기본.

### 4.3 유효성 · dilation (B1-2, 신규 `BurstProbeValidity`)
- 지형·건물 **내부**에 놓인 프로브는 뒷면만 보고 검정 → 보간 시 누수(검은 얼룩).
- 프로브당 K(=64) 피보나치 방향으로 `IntersectInstanced` → **뒤집지 않은 면 노멀**로 `dot(n_geo, d) > 0`(뒷면 히트) 비율 r. `r > τ`(기본 0.25) 이면 invalid.
  - `ClosestHit` 는 노멀을 레이 쪽으로 뒤집으므로 쓰지 않음(그래서 별도 잡 — 코어 무수정).
  - 양면 얇은 메시(잎 카드·단면 플레인)는 뒷면 판정이 무의미 → 템플릿 플래그 `countBackface=false` 로 판정 제외.
- dilation: invalid 프로브 = 유효 6-이웃 평균, 링 단위 반복(라이트맵 dilation 과 같은 방식). 런타임 샘플은 valid 가중 재정규화로 이중 방어.
- GPU 미러는 필요 시(검증 후) — 레이 수가 베이크의 1/4 수준이라 Burst 로 충분할 가능성 큼.

### 4.4 저장 · 컴포넌트 (B1-3)
- **`ProbeVolumeAsset`**(SO, `[PreferBinarySerialization]`): 격자 메타·SH 패킹 `Vector4[]`(프로브당 7, GPU 레이아웃 그대로)·valid·베이크 정보 + 서브에셋 `Texture3D`×7(RGBAHalf, Bilinear, Clamp) + EXR 사본(z 슬라이스를 가로로 타일한 RGBAFloat 2D).
  - 메모리 감: 262,144 프로브 × 7 × 8B(half) ≈ 14.7MB (float32 29.4MB).
- **`ProbeVolumeBaker`**(코어 컴포넌트, `InstanceSceneBaker` 패턴): 볼륨 Bounds·간격, 씬 입력(정적 루트 목록 + 템플릿·행렬 목록), 백엔드(Burst/GPU) × 바운스, 시드. ContextMenu: Bake / Save / Apply / Clear. OnEnable 에 에셋 적용.
- **인스턴스 적용(엔드투엔드 1차)**: 대상 인스턴스 집합(템플릿·행렬)에 대해 `ComputeProbePoints` 상/하 점에서 `ProbeGrid.Sample` → 2-프로브 SH → `SHInstancedBuffer.CreateFromPacked(…, 2)` → **기존 `InstancedSH_URP` 무수정**. 인스턴스 배치가 바뀌면 샘플만 다시(베이크 불필요).

### 4.5 픽셀 단위 샘플 셰이더 (B1-4)
- `HuskyProbeVolume.hlsl`(구현 시 이름 변경 — RP core APV 의 `ProbeVolume.hlsl` 과 구분, 식별자 `_HuskyProbeSH0..6`·`_HuskyPV_*`): Texture3D×7 HW trilinear → `UnpackSH9` → `EvaluateSH9`(공유 함수, 디링잉 동일). 볼륨 밖은 클램프 + 페이드(가장자리 → 하늘 앰비언트).
- 샘플 위치 = `worldPos + n·normalBias`(셀 크기 비례) — 얇은 벽·지면 누수 완화.
- `InstancedVegetation_URP.shader`: 알파 클립, 양면(백페이스 노멀 플립 — `InstancedSH_URP` 와 동일), **잎 투과** = `E(n) + t·E(-n)`(t = 투과율 프로퍼티), 직사광 실시간 + 그림자(알파 클립 ShadowCaster). 바람·LOD 는 범위 밖.
- 풀처럼 인스턴스가 극히 많은 경우 버텍스 셰이더에서 인스턴스 피벗 1회 샘플(정점당 아닌 인스턴스당) 옵션.

### 4.6 정점 베이크 — 보조 (B1-5)
- **(a) 템플릿 로컬 정점 AO**(인스턴싱 공유 — 기본, ✅ v15.10 `VertexAOBake`): 템플릿 메시 단독 씬(항등 행렬 1인스턴스) → `BurstAO.Compute`(정점 위치·노멀) → 메시 사본 `uv3` = (앞면, 뒷면 AO — 양면 잎 카드는 뒷면 반구 별도)에 저장(`ProbeVolumeAsset` 서브메시). 런타임 `E_probe × AO_vertex` — 수관 안쪽 자기차폐 음영. 인스턴스 무관이라 저장 = 템플릿 수 × 정점 수.
- **(b) 히어로(비인스턴스) 정점 조도**(선택): `BurstRadianceBaker.Bake`(정점 위치·노멀 = 텍셀 배열과 동일 입력) → `MeshRenderer.additionalVertexStreams`. 인스턴스별 저장이라 대량에는 부적합.
- 텍스처 회피 원칙(기획 §2) 유지 — 식생엔 uv2/아틀라스 없음.

### 4.7 알파 잎 차폐 (B1-6) — ✅ v15.13: 패키지 텍스처 any-hit 연결(아래 원안은 기록용)
- **구현(D5 갱신)**: 머티리얼 알파 → `AlphaMaskBuilder`(사전 이진화 비트마스크) → `AlphaSceneData` → `BurstScene.Create(…, alpha, …)` → 같은 씬을 쓰는 프로브 SH(1·다바운스 Burst, GPU `CSSHBake`/`CSSHBakeMulti` — `GpuSHBaker` 가 `BindAlpha`) · 유효성(`IntersectInstanced`) · 정점 AO(`BurstAO` 의 `Occluded`)가 모두 투명 텍셀을 통과. 확률 통과가 아니라 **텍셀 단위 결정적 판정**이라 노이즈 없음.
- 잎 카드를 불투명 삼각형으로 트레이스하면 수관 내부·나무 밑 프로브가 과도하게 어두움.
- (원안) 방식: 삼각형별 평균 커버리지 ᾱ(알파 텍스처를 UV 삼각형 위에서 적분, 베이크 전 1회) → 순회 중 히트 시 결정적 난수 `hash(rayId, instId, triId) < ᾱ` 일 때만 히트(아니면 통과·계속 순회). 기대값으로 투과율 = 1-ᾱ.
- 변경 범위: `Tri`(또는 병렬 배열)에 ᾱ, `BurstScene`/`GpuScene` 평탄화, CPU `TwoLevelBVH`·Burst·`BvhTraverse/PathTrace.compute` 순회 3곳. ᾱ 미지정(=1) 경로는 **비트 동일**해야 함.
- 1차 대체(B1-1~5 동안): 템플릿별 `occluderMode = Opaque | Excluded` 토글.

### 4.8 적응형 프로브 (B2 — 결정 ⑥)
균일 격자는 하늘 빈 공간에 대부분을 낭비. 둘 중 택1(§5 D1):
- **지형 추종 2.5D 레이어**: XZ 격자 × 지면 높이 기준 K층(높이맵 Texture2D 1장 추가). 식생이 지면 근처에 몰리는 오픈월드에 가장 단순·효율적.
- **희소 브릭(APV식)**: 거친 격자 전역 + 지오메트리 근처만 4³ 브릭 세분, 인디렉션 Texture3D → 브릭 아틀라스 Texture3D. 건물 사이·다층 구조에 강함, 구현량 큼.

→ v15.22 에서 지형 추종 2.5D 를 구현(아래 B2 완료 메모). 희소 브릭은 다층 공간이 실제로 필요해질 때의 선택 항목:

**희소 브릭 설계 메모 (미착수 — 2026-10-29 검토)**
- **목적**: 2.5D 는 열마다 지면 하나 → 다리 위/아래·건물 층·동굴·절벽 밑 같은 다층 공간은 한 층만 표현(지면 메시에 다리가 들어가면 다리 밑 식생이 다리 위 빛을 읽음). 균일 격자로 덮으면 하늘까지 전부 프로브(256×32×256m 를 1m 간격 = 약 218만 개 ≈ 122MB, 기본 `maxProbeCount` 초과). 희소 브릭은 프로브를 3D 로 두되 지오메트리 근처만 촘촘히.
- **구조**: 브릭 = 프로브 4×4×4(셀 3×3×3, 경계 프로브를 이웃 브릭과 중복 → 브릭 안 HW trilinear 가 아틀라스의 다른 브릭으로 새지 않음). 레벨마다 간격 ×3(예 1·3·9m — 브릭 경계가 정렬되는 배율). 인디렉션 Texture3D(거친 칸 → 브릭 아틀라스 위치·레벨) + 브릭 아틀라스 Texture3D×7. 셰이더 = 인디렉션 Load 1회 → 브릭 안 좌표 → 아틀라스 trilinear.
- **재사용(B2 에서 깔림)**: 위치 배열 베이크 `ProbeGrid.BakeBurstAt`/`BakeGpuAt` · 유효성 `BurstProbeValidity.BackfaceRatio(positions)`. **새로 필요**: 세분 판정(칸 근처 지오메트리 — BVH 짧은 레이 또는 삼각형 AABB 겹침) · 브릭 사이를 건너가는 dilation · 인디렉션·아틀라스 에셋 · `HuskyProbeVolume.hlsl` 세 번째 경로(`_HuskyPV_Params.w` = 2) · CPU 미러 · **레벨 경계 이음매**(세밀 브릭 경계 프로브를 거친 쪽 보간값으로 맞춤 — 가장 까다로움).
- **비용**: 경계 중복 때문에 같은 영역을 덮는 데 균일 대비 64/27 ≈ 2.4배 프로브(대안: 중복 없이 셰이더 수동 8탭 — 셰이더 비용↑). 넓은 지형 카펫은 촘촘한 브릭이 땅을 다 덮어 2.5D 가 더 쌈 → 브릭 이득은 3D 로 띄엄띄엄한 공간(도시·실내·다층). 혼합안: 지형 = 2.5D, 구조물 주변만 브릭.
- **검증 계약(B2 와 같은 방식)**: 전부 최소 레벨 브릭 ≡ 같은 해상도 균일 격자(샘플 비트 동일) · GPU ≡ CPU 미러 · 레벨 경계 연속성 · 프로브 수·메모리 리포트.
- **범위 밖**: 벽 너머 누수(배치가 아니라 가시성 가중 — DDGI 식 깊이 모멘트, 별개 주제).
- **구현량**: B2 의 3~4배 — ① 브릭 배치·인디렉션·CPU 샘플(헤드리스) ② 세분 판정 ③ 베이크·dilation ④ 에셋·셰이더·GPU 테스트 ⑤ 레벨 경계 이음매.
- **착수 기준**: 다리 밑·건물 안·동굴·절벽 아래에 식생/인스턴스가 실제로 놓일 때. 넓은 지형 위 식생만이면 2.5D 로 충분.

### 4.9 ~~Unity LightProbes 연동 (B3, 선택)~~ — 제외 (2026-10-29)
> 커스텀 개발 원칙(기획서 §9.5): Unity GI 기능(라이트 프로브 등)을 쓰지 않는다 → B3 는 로드맵에서 제외. 아래는 검토 당시 기록.
- LightProbeGroup 으로 위치를 Unity 베이크로 확보 → 우리 볼륨을 그 위치에서 샘플 → `LightmapSettings.lightProbes.bakedProbes` 덮어쓰기(일반 MeshRenderer·SkinnedMesh 가 자동 사용).
- `SphericalHarmonicsL2` 와 우리 SH9 의 정규화·π·계수 순서 차이 → `AddAmbientLight`/`AddDirectionalLight` + `Evaluate` 왕복 테스트로 변환식 확정 후 진행.

---

## 5. 결정 항목 — ✅ 2026-10-25 권장안(굵게)으로 확정

| ID | 항목 | 선택지 | 권장 근거 |
|---|---|---|---|
| **D1** | 프로브 배치 1차 → 적응형 방식 | (**균일 3D 격자 → B2 에서 지형 추종 2.5D**) / 균일 → 희소 브릭 / 바로 적응형 | 균일 격자가 검증 기준(해석해·수렴 테스트). 식생은 지면 근처 → 2.5D 가 ⑥의 최소 구현 |
| **D2** | 런타임 보간 단위 | (**인스턴스 대표점 먼저(B1-3) → 픽셀 Texture3D(B1-4)**) / 픽셀만 | B1-3 은 셰이더 무변경으로 엔드투엔드 확보, 풀·큰 나무는 B1-4 필요 |
| **D3** | 런타임 저장 형식 | (**Texture3D×7 RGBAHalf**) / StructuredBuffer float4(수동 8탭) | HW trilinear·캐시 효율. SO 에는 패킹 원본도 보관 |
| **D4** | 정점 베이크 내용 | (**템플릿 로컬 AO**) / AO + bent normal / 히어로 정점 조도까지 | 인스턴싱과 호환되는 유일한 정점 데이터. bent normal 은 효과 확인 후 |
| **D5** | 알파 잎 차폐 | 1차 Opaque/Excluded 토글 → ~~삼각형 평균 커버리지 확률 통과~~ / (**텍스처 any-hit — 2026-10-26 갱신, B1-6 ✅ v15.13**) | (원 근거) 커버리지 방식은 순회에 float 1개·해시 1회 추가로 3백엔드 미러 용이. → 패키지 병합으로 any-hit 가 이미 3백엔드 구현·검증돼 있어 구현량 이점이 사라짐 + 텍셀 단위 정확·노이즈 없음 |
| **D6** | Unity LightProbes 연동 | (**자체 볼륨만 — B3 제외**, 2026-10-29 보류 → 제외) / ~~bakedProbes 내보내기 병행~~ | DrawMeshInstancedIndirect 는 Unity 프로브를 자동으로 못 받음 → 자체 볼륨이 본선. 2026-10-29: 커스텀 개발 — Unity GI 기능 미사용 방향(기획서 §9.5) |

고정(재확인만): SH9(L2) 계약 유지 · 직사광은 실시간(프로브엔 간접+환경) · 반사 규약 ρ/π·E · URP 우선.

---

## 6. 단계 분할 · 검증

| Phase | 내용 | 신규 파일(예정) | 검증(테스트) |
|---|---|---|---|
| **B1-1** ✅ | 격자 정의 + 베이크 연결 + CPU trilinear | `Script/ProbeVolume/ProbeGrid.cs` | `ProbeGridTests`: 인덱스↔위치 왕복 · **선형장 trilinear 정확**(c0 = a·x+b → 임의 내부점 오차 ε) · **격자 베이크 ≡ 같은 점 직접 `BurstSHBaker` 호출(비트 동일)** · 열린 바닥+균일 하늘 해석해(`BurstSHBakerTests` D 계열 재사용) |
| **B1-2** ✅ | 유효성(backface 비율) + dilation + valid 가중 샘플 | `ProbeVolume/BurstProbeValidity.cs` | 닫힌 박스 내부 프로브 invalid / 외부 valid · 단면 플레인(`countBackface=false`) 무영향 · dilation 후 invalid = 유효 이웃 평균 · valid 가중 샘플이 invalid 값 무시 |
| **B1-3** ✅ | 저장 + 베이커 컴포넌트 + 인스턴스 적용(엔드투엔드) | `ProbeVolume/ProbeVolumeAsset.cs`, `ProbeVolume/ProbeVolumeBaker.cs` | 에셋 왕복(패킹·Texture3D 텍셀 ≡ SH) · EXR 매직 · 인스턴스 샘플 ≡ CPU `Sample` · **수렴: 간격 ½ 마다 '직접 베이크(SH 트랙) 대비 오차' 단조 감소** |
| **B1-4** ✅ | 픽셀 샘플 셰이더 | `Shaders/HuskyProbeVolume.hlsl`, `Shaders/InstancedVegetation_URP.shader` | GPU 샘플 ≡ CPU `Sample`(렌더 컨텍스트, `SHEvalProbeGpuTests` 패턴 — 텍셀 중심 정렬·클램프 경계 포함) · glslang 컴파일 |
| **B1-5** ✅ | 정점 AO(템플릿 로컬) [히어로 정점 조도는 미착수] | `ProbeVolume/VertexAOBake.cs` | 정점 AO ≡ `BurstAO.Compute` 직접 호출 · 고립 평면 AO=1 · 무한 천장 아래 AO≈0 · (히어로) 정점 조도 ≡ 같은 점·노멀 `BurstRadianceBaker` |
| **B1-6** ✅ | 알파 잎 차폐 — 패키지 α any-hit 연결(코어 무수정) | `ProbeVolumeBaker`(알파 설정·`BuildAlphaData`)·`VertexAOBake`(alpha 인자) 수정 + `Tests/ProbeVolume/ProbeAlphaTests.cs` | `ProbeAlphaTests`: ᾱ=1(전부 불투명 마스크) ≡ 알파 없음 **비트 동일**(SH 1·다바운스·뒷면 비율) · ᾱ=0 ≡ 메시 제거(비트 동일) · 체커 ᾱ≈0.5 → E(up) 비율 ∈ [0.3,0.7] · 정점 AO 동일 3종 · 베이커 머티리얼 경로 · GPU ≡ Burst |
| **B2** ✅ | 적응형(D1 선택안 = 지형 추종 2.5D) | `ProbeVolume/ProbeTerrainLayers.cs`(+ `ProbeGrid.Bake.cs` 분리) · 에셋/베이커/셰이더/바인딩 수정 + `Tests/ProbeVolume/ProbeTerrainLayersTests.cs`(헤드리스)·`ProbeVolumeTerrainTests.cs` | 높이 0·상수 ≡ 같은 해상도 균일 격자(**비트 동일**) · 지면 기준 선형장 재현 · 지면 레이 · 에셋 ≡ 코어 직접 파이프라인 · GPU ≡ CPU · **프로브 수·메모리 절감률 리포트**(로그 `layout:`) |
| ~~B3~~ | ~~(선택) Unity LightProbes 내보내기~~ — **제외(2026-10-29, D6)** | — | — |

- 모든 신규 테스트는 Tests 어셈블리(`Script/Test/ProbeVolume/`) + `LightmapEvaluateDebugger.RunAllTests` 배선(코어 → 테스트 참조 0 유지).
- 이 환경(Unity 없음)에선 tree-sitter 구문·glslang 컴파일·정적 의존성 검사까지 — 에디터 실행 확인은 단계마다 체크리스트로 남김.

**B1-1 + B1-2 완료(v15.7)** — 코어 무수정, 신규 2파일 + 테스트 2종(A~F 각각). 구현 메모:
- `ProbeGrid` 는 `[Serializable]` 구조체(B1-3 SO 에 그대로 직렬화). `BakeBurst` 는 q.MaxBounces ≤1 → 1바운스 근사, ≥2 → 다바운스(`InstanceSceneBaker` 규약). `BakeGpu` 커널은 `GpuKernelName(q)`.
- `Sample(sh, valid, p)`: 무효 탭 가중치 0 + 재정규화, 유효 탭이 전무하면 무가중(= dilation 값) 폴백. `w==0` 탭은 건너뜀(축 위 점은 탭 2개만).
- `BurstProbeValidity.BackfaceRatio` 분모는 K(미스 포함) — 지면 바로 아래는 r=0.5, 닫힌 메시 내부는 r≈1. `Dilate` 는 반복 시작 스냅샷 기준 링 단위라 결과가 순회 순서와 무관.
- 에디터 확인: `LightmapEvaluateDebugger` 우클릭 "Run Probe Volume Tests (Track B B1-1 _ B1-2)" 또는 Run All Tests(20종).

**B1-3 완료(v15.8)** — `ProbeVolumeAsset`(SO + Texture3D×7 RGBAHalf + EXR) · `ProbeVolumeBaker` · 인스턴스 적용. 구현 메모:
- 볼륨 = 베이커 Transform 위치 중심 월드 축 정렬 박스(회전 무시) + 간격 → `ProbeGrid.FromBounds`. 프로브 수·Texture3D 축(2048) 가드.
- 씬 입력: 템플릿(행렬) + 정적 루트(MeshFilter, 메시 dedupe). 템플릿별 `Opaque`/`Excluded`(D5 1차) · `twoSided`(유효성 뒷면 제외) · `render`.
- 인스턴스 적용: 상/하 대표점(SH 트랙과 같은 규칙, 템플릿 단위 `ComputeProbePoints`) → CPU 샘플(valid 가중) → `InstancedSH_URP` 무수정. 배치만 바뀌면 'Rebuild Render'.
- 저장: 기존 에셋 파일이면 서브에셋(텍스처)만 교체해 GUID 유지. EXR = z 슬라이스 가로 타일 × 계수 7 세로 블록(32-bit).

**B1-3 에디터 확인 체크리스트**
1. 재컴파일 에러 0 → `LightmapEvaluateDebugger` 우클릭 "Run Probe Volume Tests (Track B B1-1 ~ B1-3)" 전부 PASS(특히 F 수렴 수치).
2. 빈 GameObject 에 `ProbeVolumeBaker` → `volumeSize`·`probeSpacing` 지정 → 템플릿(Read/Write 켠 메시 + 'Capture Matrices From Sources') · 지면 등은 `staticRoots`.
3. 우클릭 Bake → `BakeOutput/<씬>_<이름>_ProbeVolume.asset`(+ Texture3D 7 서브에셋) · `.exr` 생성, 콘솔에 프로브 수·무효/dilation 수.
4. 선택 시 기즈모: 볼륨 박스 · 프로브 구(위쪽 조도 색 — 그늘·지면 근처 어두움) · 지면/건물 내부 프로브 빨강.
5. 인스턴스가 볼륨 조명으로 표시(InstancedSH_URP) → 인스턴스를 옮기고 'Rebuild Render' 만으로 새 위치 조명 반영(재베이크 없이).
6. 씬 저장·재오픈 / 플레이 모드에서 베이크 없이 에셋으로 표시.

**B1-4 완료(v15.9)** — 픽셀 단위 볼륨 샘플. 구현 메모:
- 셰이더 계약은 `ProbeVolumeShaderBinding`(C#) ↔ `HuskyProbeVolume.hlsl` 1:1 — CPU 미러 `EvaluateIrradiance` 가 GPU 테스트 기준. HW trilinear 는 valid 가중 불가 → dilation 값 의존.
- 볼륨 밖: 경계 클램프 + `edgeFadeCells` 동안 폴백 SH(베이크 때 저장한 하늘만 SH)로 lerp. 비활성(바인딩 없음) → 폴백만.
- 식생 셰이더: 알파 클립(그림자 포함)·양면·잎 투과(간접 E(n)+t·E(-n), 직사 뒷면 t 배)·`_HUSKY_PV_PIVOT`(풀 — 정점에서 피벗 1회 샘플). 바람·LOD 는 범위 밖.
- 베이커: 템플릿별 `ShadingMode.PerPixelVolume` → SH 버퍼 없는 렌더러 + MPB 볼륨 바인딩. `bindGlobal` 로 다른 렌더러도 볼륨 사용.

**B1-4 에디터 확인 체크리스트**
1. 재컴파일 에러 0(셰이더 3종 포함) → "Run Probe Volume Tests (Track B B1-1 ~ B1-4)" 전부 PASS — 특히 `ProbeVolumeGpuSampleTests` G1~G6 의 max|Δ|.
2. `ProbeVolumeBaker` 템플릿 하나를 `PerPixelVolume` 으로 → Rebuild Render → 같은 인스턴스 안에서도 높이·위치별로 GI 가 달라지는지(InstanceProbes 대비).
3. 잎 텍스처 머티리얼(InstancedVegetation_URP, `_BaseMap` 알파) 지정 → 알파 클립·그림자 구멍·뒷면 투과 확인.
4. 볼륨 경계 밖으로 인스턴스 이동 → `edgeFadeCells` 동안 하늘 GI 로 부드럽게 전환, 끊김 없음.
5. 지면 접지부 누수: `normalBiasCells` 0 ↔ 0.25 비교(지면 아래 프로브 영향 감소).
6. 풀(대량): `_SamplePivot` 켜고 프레임 시간 비교.

**B1-5 완료(v15.10)** — 템플릿 로컬 정점 AO. 구현 메모:
- `VertexAOBake.Compute`: 메시 단독 씬 → `BurstAO.Compute`(코어 무수정). 앞면 = 노멀 반구, 뒷면 = 반대 반구(twoSided 템플릿만, 아니면 앞면 사본). 레이 길이 = 0.5 × bounds 대각선(기본), 시작 오프셋 = 1e-4 × 대각선(+ BurstAO 1e-3).
- 저장: 메시 사본 uv3 = (앞, 뒤) → `ProbeVolumeAsset.vertexAO`(서브에셋). 같은 메시를 쓰는 템플릿은 1개(twoSided OR).
- 표시: AO 메시 + `_HUSKY_VERTEX_AO` 머티리얼(식생·InstancedSH 둘 다) — 간접광에만 곱함, 백페이스는 뒷면 AO. 런타임 머티리얼은 AO 판/비-AO 판 분리(uv3 없는 메시에 키워드 = 간접광 0 방지).

**B1-5 에디터 확인 체크리스트**
1. "Run Probe Volume Tests (Track B B1-1 ~ B1-5)" 전부 PASS(특히 `VertexAOBakeTests` C·D 수치).
2. 실 나무 템플릿(Read/Write) → Bake → 에셋에 `<메시>_AO` 서브메시 생성, 콘솔 경고 없음.
3. 수관 안쪽·가지 밑이 어두워지는지 `vertexAOStrength` 0 ↔ 1 비교(직사광 그림자는 그대로).
4. 양면 잎 카드(twoSided) 뒷면에서 AO 가 따로 적용되는지.
5. 정점 수 많은 메시의 베이크 시간(정점 × samples 레이) — 필요 시 samples·maxDistanceRelative 조정.

**B1-6 완료(v15.13)** — 알파 잎 차폐(패키지 α any-hit). 구현 메모:
- 베이커 `alphaCutout`(기본 on) + `alphaMaskResolution`·`alphaTransparentPolicy`·`alphaForceCutout`·`alphaForceCutoutNames`·`alphaDefaultCutoff`(= `AtlasApplyDebug` 알파 설정과 같은 의미).
- 알파 머티리얼: 템플릿 `alphaMaterials`(서브메시별, 베이크 전용) → 없으면 **표시 머티리얼**(`material`, 없으면 모드별 공용 `vegetationMaterial`/`material` — 지정된 것만)을 전 서브메시에 → 정적 루트는 렌더러 `sharedMaterials`(인스턴스별). 머티리얼이 하나도 없거나 컷아웃이 없으면 Disabled → 기존과 비트 동일.
- 주의(거동 변화): 알파 블렌딩(Transparent) 머티리얼은 `alphaTransparentPolicy = Ignore`(기본, `AtlasApplyDebug` 와 동일)면 베이크 레이가 통과 — 정적 루트의 유리·물이 더는 차폐하지 않음. 이전 거동이 필요하면 `Opaque` 정책.
- 정점 AO: 메시 단독 씬에도 같은 규칙으로 알파(`VertexAOBake.BuildAlpha` → `Compute(…, alpha)`) — 잎 카드끼리의 자기차폐가 구멍을 통과. 같은 메시를 쓰는 템플릿은 처음 템플릿의 머티리얼.
- 기록: `ProbeVolumeAsset.alphaCutout`·`alphaLog`, `VertexAOEntry.alphaCutout`. Bake 로그에 alpha ON/OFF + 마스크 요약.
- `OccluderMode` 는 그대로(Opaque / Excluded) — Opaque 가 이제 '컷아웃 구멍은 통과'. Excluded 는 여전히 베이크 씬에서 제외.

**B1-6 에디터 확인 체크리스트**
1. "Run Probe Volume Tests (Track B B1-1 ~ B1-6)" 전부 PASS — 특히 `ProbeAlphaTests` A·B(비트 동일)·C/D 비율·E(그래픽 디바이스 필요)·F(compute 필요).
2. 실 나무 템플릿(잎 = 컷아웃 머티리얼, `material` 또는 `alphaMaterials`) → Bake → 콘솔 `alpha=ON` + `cutout mats=N`. 0 이면 `alphaForceCutout`/`alphaForceCutoutNames`(SpeedTree 는 셰이더 이름으로 자동).
3. 수관 아래 프로브(기즈모 색)가 `alphaCutout` off(통짜) 대비 밝아지고, Excluded(완전 통과)보다는 어두운지.
4. 정점 AO 메시: 잎 안쪽 AO 가 off 대비 덜 어두운지(`vertexAOStrength` 1).
5. GPU 백엔드로 같은 결과(평균 오차 작음)인지.

**정적 입력 C0 합류 완료(v15.19)** — §7 씬 입력 이원화 해소(정적 메시). 구현 메모:
- `staticSource`: `Roots`(기본 — `staticRoots` 하위 전부, 구 거동 비트 동일) / `Collected`(`collectedStatics` — C0 의 Receiver ∪ OccluderOnly, Entries 순서 = 텍스처 경로 targets ∪ occluders 와 같은 집합). 프로브엔 굽기/차폐만 구분이 없어 한 목록.
- 채우기: `ProbeVolumeBaker` 우클릭 'Collect Scene (C0)' / '… All Loaded Scenes (C0)'(옵션 = 상단 C0 메뉴 토글 공유, Undo) 또는 코드 `SetCollected(MeshFilter[])`.
- 템플릿 중복 제외(Collected 만): 템플릿 `captureSource` 자손 · (메시, 월드 행렬) 일치 — Excluded 템플릿 포함. 로그·에셋 `sceneLog` 에 제외 수.
- 템플릿(식생) 입력은 그대로(행렬 캡처/코드). 같은 메시 MeshFilter 묶음을 템플릿으로 만드는 자동화는 범위 밖.

**정적 입력 C0 에디터 확인 체크리스트**
1. "Run Probe Volume Tests (Track B B1-1 ~ B1-6 + C0)" 전부 PASS — 특히 `ProbeVolumeC0Tests` A(Roots 비트 동일)·B(템플릿 중복).
2. 실씬: `ProbeVolumeBaker` 우클릭 'Collect Scene (C0)' → 콘솔 리포트 + `정적 입력 N (receiver + occluder)` · 인스펙터 `staticSource = Collected`.
3. 같은 씬에서 `AtlasApplyDebug` 'Collect Scene (C0)' 의 targets + occluders 수 = 볼륨 정적 입력 수(같은 옵션일 때).
4. 나무 템플릿을 captureSource 로 캡처한 씬 → Bake 로그 `scene:` 줄에 '템플릿 중복 −N'(나무 MeshFilter 수만큼) · Roots 로 굽던 결과와 차이가 나면 `scene:` 줄의 static 수 차이(LOD1+·Contribute GI 꺼짐 등 C0 제외분)로 설명되는지.
5. Undo 로 `collectedStatics`·`staticSource` 복원.

**B2 완료(v15.22)** — 지형 추종 2.5D(결정 D1). 구현 메모:
- 배치: `ProbeVolumeBaker.layout = TerrainLayers` → XZ = 볼륨의 균일 격자 그대로, 열 (i,k) 마다 지면 높이 h, 층 j 의 y = (h + `groundOffset`) + j·`probeSpacing.y`, 층 수 = ⌈`layerHeight` / 간격⌉ + 1. 에셋 `grid.origin.y` = 첫 층 높이(오프셋), `columnHeights`(+ 높이맵 `heightTexture`).
- 지면 높이: 지면 메시(`groundRoots` 하위, 비우면 정적 입력 — **템플릿 제외**라 수관·식생이 층을 들어 올리지 않음)에 볼륨 위에서 아래로 레이(범위 = `volumeSize.y`). 미스 열 = 이웃 링 평균, 전부 미스면 볼륨 바닥.
- 샘플: 점 p 의 '열 격자' = origin.y 만 H(p.xz) + 오프셋(H = 열 높이 bilinear). CPU(`ProbeTerrainLayers.Sample`) · 셰이더(`HuskyPV_GridCoord`, `_HuskyPV_Params.w = 1`) 같은 식 — 층 좌표가 지면 기준이라 경사에서도 '지면 위 1m' 는 같은 층.
- 균일과의 관계: 높이 없음 = 균일 그 자체, 높이 상수 c = origin.y 를 c + 오프셋으로 둔 균일과 비트 동일. 계수·valid·dilation(6-이웃)·Texture3D·EXR 은 인덱스 공간이라 무변경.
- 절감: 로그 `layout: TerrainLayers nx×K×nz = N 프로브 (MB) · 지면 a~b · 균일 환산 nx×K'×nz = N' (MB) → −x%`(K' = ⌈(지면 높이차 + (K−1)·간격)/간격⌉ + 1).
- 한계: 2.5D — 지붕·다리 아래 같은 다층 공간은 한 층만(지면 메시 선택으로 우회, 근본은 희소 브릭). 급경사에선 층이 기울어 보간이 비등방.

**B2 에디터 확인 체크리스트**
1. "Run Probe Volume Tests (Track B B1-1 ~ B1-6 + C0 + B2)" 전부 PASS — 특히 `ProbeVolumeTerrainTests` B(코어 직접 ≡ 비트 동일)·G(GPU ≡ CPU, 렌더 컨텍스트).
2. 실지형: `layout = TerrainLayers` · `groundOffset` 0.25 · `layerHeight` = 식생 최대 높이 → Bake → 로그 `layout:` 줄의 절감률 · 미스 열 수 확인(많으면 `volumeSize.y` 가 지면을 다 덮는지).
3. 기즈모: 프로브 층이 지면을 따라 휘는지 · 무효(빨강)가 지면 바로 아래에 몰리지 않는지(몰리면 `groundOffset` ↑).
4. 식생(PerPixelVolume): 경사·언덕에서 균일 대비 밝기 분포가 자연스러운지 · 볼륨 가장자리 페이드.
5. `groundRoots` 를 지형 메시만으로 지정했을 때 건물 지붕 위로 층이 올라가지 않는지.

다음: (선택) 희소 브릭(다층 구조 — §4.8 설계 메모, 다층 공간이 실제로 필요해질 때). B3 Unity LightProbes 연동은 제외(D6 · 기획서 §9.5).

---

## 7. 리스크

- **누수(leak)**: trilinear 는 벽을 넘어 보간 → 얇은 벽·지면 경계 번짐. B1-2(유효성)·B1-4(normalBias)로 완화, 근본 해결(가시성 가중 — DDGI 식 깊이 모멘트)은 범위 밖.
- ~~**알파 잎 과차폐**~~: B1-6(v15.13)으로 해소 — 컷아웃 머티리얼이 지정된 경우. 머티리얼이 없거나 판별 실패하면 여전히 통짜(로그 `alpha=OFF` 로 확인).
- **볼륨 해상도 vs 메모리**: 균일 격자는 빈 공간 낭비 → ✅ B2(v15.22) 지형 추종으로 완화(로그에 절감률). 간격이 크면 풀 접지부 음영 손실(정점 AO 가 일부 보완). 다층 구조(지붕·다리 아래)는 2.5D 한계 — 희소 브릭이 근본.
- ~~**씬 입력 이원화**~~: v15.19 로 정적 메시는 해소 — `staticSource = Collected` 면 볼륨이 텍스처 경로와 같은 C0 분류를 쓴다(기본 Roots 는 구 거동이라 사용자가 고를 것). 메시→BLAS 변환 코드는 아직 두 벌(`AtlasApplyDebug.BuildGiScene` 은 적용된 uv2 메시의 원본 추적 때문에 별도) — 같은 식(로컬 Tri · `_BaseColor`/`_Color` Linear 알베도)이라 결과는 같다.
- ~~**B1-6 코어 변경**~~: D5 갱신으로 코어 무수정(패키지 any-hit 재사용). ᾱ=1 비트 동일 게이트는 `ProbeAlphaTests` A 로 유지.
- **SH 링잉**: 고대비(하늘↔지면) 볼륨에서 L2 링잉 — 기존 디링잉(W2=0.5) 공유, 필요 시 볼륨 전용 윈도우.
- 공통: 에디터 미검증 상태로 쌓이는 변경 → 기획서 §11-1 에디터 게이트를 B1-3 전에 한 번 통과 권장.

---

## 8. 다음 액션
1. ✅ §5 **D1~D6** 확정(권장대로, 2026-10-25).
2. ✅ **B1-1 + B1-2**: `ProbeGrid`·`BurstProbeValidity` + `ProbeGridTests`·`ProbeValidityTests` + RunAll 배선 + 문서 v15.7 (에디터 테스트 실행 대기).
3. ✅ **B1-3**: `ProbeVolumeAsset` · `ProbeVolumeBaker` · 인스턴스 적용 + `ProbeVolumeBakerTests` + 문서 v15.8 (에디터 확인 — §6 체크리스트).
4. ✅ **B1-4**: `HuskyProbeVolume.hlsl` + `InstancedVegetation_URP.shader` + `ProbeVolumeShaderBinding` + `ProbeVolumeGpuSampleTests` + 문서 v15.9 (에디터 확인 — §6 체크리스트).
5. ✅ **B1-5**: `VertexAOBake` + 에셋·베이커 연동 + 셰이더 `_HUSKY_VERTEX_AO` + `VertexAOBakeTests` + 문서 v15.10 (에디터 확인 — §6 체크리스트).
7. ✅ **B1-6 구현(v15.13)**: 베이커·정점 AO 알파 연결 + `ProbeAlphaTests` + 문서 (에디터 확인 — §6 체크리스트).
6. ✅ **B1-6 방식 결정(2026-10-26)**: 패키지 병합(v15.11)을 기준으로 함 → D5 를 '삼각형 평균 커버리지' 대신 **패키지 텍스처 any-hit**(CPU/Burst/GPU + `AlphaCutoutTests`·`AlphaGpuCompareTests`)으로 변경. B1-6 = 템플릿 머티리얼 → `AlphaSceneData`(`AlphaMaskBuilder`) → 볼륨 베이크·유효성·정점 AO 씬에 `SetAlpha`/`BurstScene.Create(…, alpha, …)`/`GpuScene.BindAlpha`. 코어 순회 변경 불필요(이미 3백엔드 구현·검증).
