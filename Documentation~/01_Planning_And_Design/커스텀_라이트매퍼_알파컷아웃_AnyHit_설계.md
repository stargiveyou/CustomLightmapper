# CustomLightmapper — 알파 컷아웃 Any-Hit 차폐 설계 (α 트랙)

> 패키지: `com.huskylibs.customlightmapper` (UPM embedded)
> 작성일: 2026-08-10 · 기준 브랜치: `package/customlightmapper-upm`
> 상위 로드맵: `04_Complete_Dev_Docs/00_INDEX.md` 대시보드 **단계 1 (C0 수집·분류·알파 마스크)** 의 α 부분
> 관련: `02_Development_And_Reference/커스텀_라이트매퍼_개발문서_v14.md` §10

---

## 1. 문제 정의

식생(나무) 씬을 베이크하면 유니티 Progressive Lightmapper와 그림자 모양이 근본적으로 다르다.

| | 유니티 기본 라이트맵 | CustomLightmapper 현재 |
|---|---|---|
| 잎 그림자 | 잎 실루엣이 살아 있고 사이사이로 빛이 샌다 | 캐노피 전체가 **통짜 덩어리 그림자** |
| 나무 사이 틈 | 틈으로 빛이 통과 | 막힘 |
| 그림자 경계 | 잎 모양 디테일 | 볼록껍질에 가까운 뭉개진 외곽 |

원인은 **레이 차폐 판정에 알파 컷아웃 테스트가 존재하지 않기 때문**이다. 나뭇잎은
"큰 쿼드 + 알파 텍스처로 잎 모양만 남기는" 컷아웃 머티리얼인데, 현재 레이트레이서는
쿼드 전체를 불투명 판으로 취급한다.

### 1.1 코드 근거

- `Runtime/LightmapEvaluate/Occluders/Occluder.cs:6`
  ```csharp
  public struct Tri { public Vector3 V0, V1, V2; }   // 월드 공간 (alpha matId는 추후)
  ```
  삼각형이 **위치 3개만** 보유. UV도, 머티리얼 식별자도 없다 → 알파 샘플 자체가 불가능.

- `Runtime/LightmapEvaluate/Occluders/BVH.cs:275`
  ```csharp
  if (RayGeometry.RayTri(o, d, _tris[_triIdx[s]], 0f, maxDist, out _)) return true;
  ```
  히트 즉시 `true` 반환 = **무조건 차폐**.

- `Runtime/LightmapEvaluate/Burst/Occluders/BurstTwoLevelBVH.cs:128`, `Shaders/Resources/PathTrace.compute:281`
  Burst·GPU 백엔드도 동일한 즉시 반환 구조(세 백엔드가 서로의 미러이므로 당연).

- `Runtime/LightmapEvaluate/RadianceCore.cs:35`
  ```csharp
  // Indirect(RR path tracing, min-bounce) · point/area light · alpha-cutout any-hit
  ```
  기획 시점부터 미구현 항목으로 명시돼 있던 자리.

- `Samples/AtlasApplyDebug.cs:1135` `LocalTris(Mesh)` 는 `mesh.vertices` / `mesh.triangles` 만 읽는다.
  UV·서브메시·머티리얼 정보가 씬 구성 단계에서 이미 버려진다.

### 1.2 필요한 것 한 줄 요약

**Any-hit 알파 테스트** — 레이가 삼각형에 맞았을 때 그 지점의 UV를 보간해 머티리얼 알파를
조회하고, 컷오프 미만이면 **히트를 무시하고 순회를 계속**하는 기능. 이를 위해 삼각형이
UV와 머티리얼 식별자를 운반해야 하고, 머티리얼별 알파 마스크가 준비돼 있어야 한다.

---

## 2. 설계 목표 / 비목표

### 목표
1. 컷아웃 머티리얼의 그림자·AO·간접광이 잎 실루엣을 따르게 한다.
2. **CPU / Burst / GPU 세 백엔드가 정확히 같은 결과**를 내야 한다(프로젝트의 ε-교차검증 규약 유지).
3. **컷아웃 머티리얼이 하나도 없는 씬은 기존과 비트동일**이어야 한다(전 회귀 테스트 무손상).
4. `Tri` 레이아웃·BVH 빌드·GPU stride를 건드리지 않는다(G0~G6 자산 보존).

### 비목표 (이번 트랙 범위 밖)
- 반투명(연속 알파) 감쇠 그림자 — 확률적 투과는 후속. 이번엔 **이진 컷아웃만**.
- 굴절·유리 등 스펙큘러 투과.
- 잎 표면 자신이 리시버일 때의 텍셀 유효성(컷아웃된 텍셀 무효화) — §10.2 참조.
- 알파 블렌딩 머티리얼(`Transparent`) — 차폐 제외(통과) 정책으로 단순 처리.

---

## 3. 핵심 설계 결정

### 결정 ① — 알파 마스크는 **사전 이진화한 비트마스크**로 굽는다

부동소수 알파값을 런타임에 컷오프와 비교하지 않는다. 베이크 준비 단계에서
`alpha >= _Cutoff` 를 **1비트**로 확정해 `uint` 배열에 팩한다.

**이유:** 런타임 판정이 순수 정수 비트 연산이 되어 CPU/Burst/GPU가 **구조적으로 비트동일**해진다.
텍스처 필터링·색공간 변환·`_Cutoff` 부동소수 비교가 백엔드마다 미세하게 갈릴 여지를 원천 제거한다.
이는 SH-G에서 Fibonacci 방향셋을 CPU에서 계산해 업로드했던 것과 같은 전략이다
(`개발문서 v14` v14.9 — "GPU 재계산 금지 → 하드웨어 발산 제거").

### 결정 ② — `Tri` 는 불변, 속성은 **병렬 배열**로 추가

`Tri`(12B×3=36B)는 `GpuTri.Stride = 36`(`GpuScene.cs:40`)과 BVH 빌드 버퍼에 직결돼 있다.
여기에 필드를 추가하면 stride·정렬·빌드 경로가 전부 흔들린다.

대신 **같은 삼각형 인덱스로 병렬 인덱싱되는 별도 배열**을 추가한다.

```
blasTris[triBase + i]      ← 기존 (위치)
blasTriUV[triBase + i]     ← 신설 (UV0 ×3)
blasTriSubmesh[triBase + i]← 신설 (서브메시 인덱스)
```

BVH는 `_triIdx` 만 재정렬하고 `_tris` 는 원본 순서를 유지하며 `TriIndex` 도 원본 인덱스를
반환한다(`BVH.cs:19` 계약). 따라서 병렬 배열은 **빌드 로직을 전혀 건드리지 않고** 성립한다.

### 결정 ③ — 머티리얼은 **per-instance 슬롯 테이블**로 해석

BLAS는 유니크 메시당 1개로 공유된다(`TwoLevelBVH.cs:68-73`). 같은 메시를 다른 머티리얼로
쓰는 인스턴스가 있으면 메시 단위 머티리얼 매핑은 틀린다. (현행 `meshAlbedo` 가 이미 이
한계를 갖는다 — `BurstScene.cs:37`, 첫 렌더러가 이김.)

DXR과 동일하게 **삼각형은 로컬 서브메시 인덱스만 들고, 인스턴스가 슬롯 테이블을 갖는다**:

```
matId = matSlot[ instMatBase[instanceIndex] + blasTriSubmesh[tri] ]
```

BLAS 중복 없이 인스턴스별 머티리얼 차이를 표현한다. `matId < 0` = 불투명.

### 결정 ④ — `RayGeometry.RayTri` 는 손대지 않고 **UV 출력 오버로드 신설**

`RayTri`(`Occluder.cs:23`)는 세 백엔드의 비트동일 기반이다. 내부적으로 이미 barycentric
`u`,`v`를 계산하지만 버린다. 여기에 `out` 파라미터를 붙여 시그니처를 바꾸면 모든 호출부와
GPU 미러가 연쇄 수정된다.

→ **본체를 복제한 `RayTriUV(..., out float hit, out float bu, out float bv)` 를 신설**한다.
연산 순서가 동일하므로 결과도 동일하다. 알파 경로만 이쪽을 쓴다. 기존 경로는 무손상.

보간식(고정 규약):
```
w0 = 1 - bu - bv ;  uv = uv0*w0 + uv1*bu + uv2*bv
```
(`RayTri` 가 `e1 = V1-V0`, `e2 = V2-V0` 기준이므로 `bu`↔V1, `bv`↔V2.)

### 결정 ⑤ — Any-hit 의미론: **거부 시 순회 계속**

| 함수 | 기존 | 변경 |
|---|---|---|
| `Occluded` / `OccludedBlas` | 히트 즉시 `return true` | 투명이면 **리프 루프·스택 순회 계속**, 불투명일 때만 `true` |
| `Intersect` / `IntersectBlas` | 히트마다 `best` 갱신 | 투명이면 `best` **미갱신**(가지치기 경계 `hT` 도 안 조임) |

`Occluded` 의 `maxDist` 는 원래 고정이므로 계속 순회해도 가지치기 손실이 없다.
`IntersectBlas` 는 채택된 히트로만 `hT` 를 조이므로 정확성이 유지된다.

### 결정 ⑥ — 컷아웃이 없으면 **완전 무비용**

`AlphaMaskSet.enabled == false`(컷아웃 머티리얼 0개) 이면 순회 코드가 기존과 동일한
분기를 타도록 단일 bool 게이트를 최상단에 둔다. 메시 단위로도
`blasHasCutout[mesh] == 0` 이면 그 BLAS는 early-exit 경로를 그대로 쓴다.

**이유:** 성능 회귀 방지 + 기존 회귀 테스트(BurstSceneTests / GpuBvhCompareTests /
BurstRadianceCompareTests / GpuRadianceCompareTests / GpuSHBakeCompareTests) 전부가
알파 비활성 상태에서 비트동일을 유지해야 착수 위험이 0에 가까워진다.

---

## 4. 데이터 계약

### 4.1 알파 마스크 (`AlphaMaskSet`, POD)

```csharp
public struct AlphaMaskSet : IDisposable
{
    public bool enabled;                        // 컷아웃 머티리얼 0개면 false → 전 경로 무비용
    [ReadOnly] public NativeArray<uint>    bits;      // 전 머티리얼 비트맵 concat (1bit/texel, LSB-first)
    [ReadOnly] public NativeArray<int>     maskWord;  // matId → bits[] 시작 워드 인덱스
    [ReadOnly] public NativeArray<int>     maskW;     // matId → 폭 (0 = 불투명 머티리얼)
    [ReadOnly] public NativeArray<int>     maskH;     // matId → 높이
    [ReadOnly] public NativeArray<Vector4> maskST;    // matId → (tiling.x, tiling.y, offset.x, offset.y)
}
```

판정 함수 — **세 백엔드가 문자 그대로 같은 식**을 쓴다:

```csharp
static bool AlphaOpaque(in AlphaMaskSet m, int matId, float u, float v)
{
    if (matId < 0) return true;                     // 불투명 슬롯
    int w = m.maskW[matId]; if (w == 0) return true;
    int h = m.maskH[matId];
    Vector4 st = m.maskST[matId];

    float uu = u * st.x; uu = uu + st.z;            // ⚠ mad 융합 금지 (HLSL 은 precise)
    float vv = v * st.y; vv = vv + st.w;

    int x = (int)Mathf.Floor(uu * w); x = ((x % w) + w) % w;   // Repeat wrap
    int y = (int)Mathf.Floor(vv * h); y = ((y % h) + h) % h;

    int bit  = y * w + x;
    uint word = m.bits[m.maskWord[matId] + (bit >> 5)];
    return (word & (1u << (bit & 31))) != 0;
}
```

> **부동소수 주의:** 유일한 float 연산은 `u*st.x + st.z` 와 `uu*w` 의 floor 다.
> HLSL 컴파일러가 `mad` 로 융합하면 CPU와 마지막 비트가 갈려 **텍셀 경계에서 1텍셀 차이**가
> 날 수 있다. GPU 미러에서는 해당 변수를 `precise float` 로 선언해 융합을 금지한다.
> ST가 항등(1,1,0,0)이면 이 위험 자체가 사라지므로, 빌더는 비항등 ST를 로그로 알린다.

### 4.2 삼각형 속성 (병렬 배열)

```csharp
public struct TriUV { public Vector2 UV0, UV1, UV2; }   // 24B  (GPU stride 24)
```

| 배열 | 인덱싱 | 비고 |
|---|---|---|
| `blasTriUV` | `blasTriStart[mesh] + triIdx` (= `blasTris` 와 동일) | 컷아웃 없는 메시는 미할당(§7.3) |
| `blasTriSubmesh` | 동일 | `byte`. 서브메시 256개 초과는 비지원(로그 후 clamp) |
| `blasHasCutout` | `mesh` | 0이면 해당 BLAS는 early-exit 경로 유지 |

### 4.3 인스턴스 머티리얼 테이블

| 배열 | 내용 |
|---|---|
| `instMatBase[inst]` | `matSlot` 내 시작 오프셋 |
| `matSlot[base + submesh]` | `matId` (마스크 인덱스), `-1` = 불투명 |

### 4.4 3층 미러 대응표

| 계층 | 보유처 | 신설 항목 |
|---|---|---|
| CPU (ground truth) | `TwoLevelBVH` / `BVH` | `AlphaMaskSet` + 속성 배열 참조를 **선택적 필드**로 보유. null이면 기존 경로 |
| Burst (POD) | `BurstScene` (`BurstScene.cs:20-37` 옆) | `blasTriUV`, `blasTriSubmesh`, `blasHasCutout`, `instMatBase`, `matSlot`, `AlphaMaskSet` |
| GPU | `GpuScene` (`GpuScene.cs`) | `_BlasTriUV`(24B), `_BlasTriSubmesh`(uint), `_BlasHasCutout`(uint), `_InstMatBase`, `_MatSlot`, `_MaskBits`, `_MaskWord`, `_MaskW`, `_MaskH`, `_MaskST`, uniform `_AlphaEnabled` |

> GPU 신규 버퍼는 **`Bind()` 가 아니라 신설 `BindAlpha()`** 로 배선한다.
> G4 순회 커널(`BvhTraverse.compute`)은 이 버퍼를 선언하지 않으므로 기존 검증이 그대로 산다.
> (`BindLighting` 이 G5에서 취한 방식과 동일 — `GpuScene.cs:214-220`.)

---

## 5. 알파 마스크 빌드 (C0-α)

신설: `Runtime/LightmapEvaluate/Alpha/AlphaMaskBuilder.cs`

### 5.1 컷아웃 머티리얼 판별

```
isCutout =  mat.GetTag("RenderType", false, "") == "TransparentCutout"
         || mat.IsKeywordEnabled("_ALPHATEST_ON")
         || (mat.HasProperty("_AlphaClip") && mat.GetFloat("_AlphaClip") > 0.5f)
cutoff   =  mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0.5f
tex      =  _BaseMap (URP) ?? _MainTex (BIRP)
st       =  mat.GetTextureScale/Offset(해당 프로퍼티)
```

알파 블렌딩(`RenderType == "Transparent"`)은 **차폐에서 제외**(전부 통과)한다 — 유리·물이
통짜 그림자를 만드는 현행 문제도 같이 해소된다. 인스펙터로 정책 전환 가능하게 둔다.

### 5.2 알파 채널 추출 (non-readable 텍스처 대응)

임포트 설정에 의존하지 않는다.

```
RenderTexture rt = GetTemporary(w, h, 0, ARGB32, RenderTextureReadWrite.Linear)
Graphics.Blit(tex, rt)                  // 축소는 point 필터로 (§5.3)
Texture2D.ReadPixels → GetPixels32()    // .a 만 사용
```

알파 채널은 색공간 변환 대상이 아니므로 `Linear` RT로 안전하다.

### 5.3 해상도 정책

- `maskResolution` (기본 **256**, 상한 1024) 과 원본 크기 중 **작은 쪽**.
- 축소는 **point 샘플** 후 `alpha >= cutoff` 이진화. bilinear 축소 후 임계하면 잎이
  두꺼워지거나(과다 차폐) 얇아진다.
- 마스크 해상도는 그림자 실루엣 디테일의 상한을 결정한다 → 품질 노브로 문서화.

### 5.4 캐시·중복 제거

`Dictionary<Material, int>` 로 공유 머티리얼당 마스크 1개. 빌드 로그에
`cutout mats = N, mask bytes = M, non-identity ST = K` 를 남긴다.

---

## 6. 씬 구성 경로 변경

`Samples/AtlasApplyDebug.cs`

### 6.1 `LocalTris` → `LocalTrisWithAttr`

현행(`AtlasApplyDebug.cs:1135`)은 `mesh.triangles` 를 통째로 읽는다. 이는 **서브메시 순서대로
연결된 배열**이므로, 서브메시별로 순회하며 같은 순서로 채우면 **삼각형 인덱스가 정확히 보존**된다
→ BVH 빌드 입력 불변 → 회귀 없음.

```
for s in 0..mesh.subMeshCount-1:
    SubMeshDescriptor d = mesh.GetSubMesh(s)
    triRange = [d.indexStart/3, (d.indexStart+d.indexCount)/3)
    → 이 구간의 blasTriSubmesh = s
uv0 = mesh.uv   (없으면 zero + 해당 메시 hasCutout=0 강제 + 경고)
```

### 6.2 머티리얼 슬롯 수집

`BuildGiScene`(`AtlasApplyDebug.cs:1033`)에서 인스턴스마다
`renderer.sharedMaterials[submesh]` → `AlphaMaskBuilder.GetOrCreate(mat)` → `matId`.
`instMatBase`/`matSlot` 을 누적한다.

### 6.3 인스펙터 신설

| 필드 | 기본 | 설명 |
|---|---|---|
| `alphaCutoutShadows` | `true` | α 트랙 마스터 스위치. false면 `AlphaMaskSet.enabled=false` |
| `alphaMaskResolution` | `256` | 마스크 해상도 상한 |
| `alphaBlendMode` | `Ignore` | `Transparent` 머티리얼 처리: `Ignore`(통과) / `Opaque`(기존 거동) |

---

## 7. 순회 변경 지점

### 7.1 변경 대상 함수 (세 백엔드 미러)

| 백엔드 | 파일 | 함수 |
|---|---|---|
| CPU | `Occluders/BVH.cs:223, 260` | `Intersect`, `Occluded` |
| CPU | `Occluders/TwoLevelBVH.cs:269, 305` | `Occluded`, `IntersectInstanced` |
| Burst | `Burst/Occluders/BurstTwoLevelBVH.cs:16, 110, 136, 60` | `IntersectBlas`, `OccludedBlas`, `Occluded`, `IntersectInstanced` |
| GPU | `Shaders/Resources/PathTrace.compute:217, 260, 290, 337` | `IntersectBlas`, `OccludedBlas`, `TlasClosest`, `TlasOccluded` |

### 7.2 간접 영향 호출부 (수정 불필요, 거동만 개선)

`RadianceCore.cs:88`(AO) · `:123`(Direct NEE) · `:158`(Indirect) ·
`BurstAO.cs:49` · `BurstDirect.cs:45` · `BurstIndirect.cs:48,81` ·
`BurstSHBaker.cs:52,69` · `PathTrace.compute` 전 커널.

→ **AO·직접광·간접광·SH 프로브가 한 번에 알파를 존중**하게 된다.

### 7.3 BLAS 단위 게이트 (성능)

`blasHasCutout[mesh] == 0` 이면 `OccludedBlas` 는 기존 early-exit 본문을 그대로 실행한다.
컷아웃 메시만 any-hit 루프를 탄다. 나무만 컷아웃이고 지형·건물은 불투명한
실제 씬에서 회귀 폭을 크게 줄인다.

---

## 8. 단계별 작업 계획

각 단계는 **이전 단계를 ground truth 로 삼아 교차검증**한다(G 트랙과 동일 규약).

| 단계 | 내용 | 검증 게이트 | 위험 |
|---|---|---|---|
| **α0** | `AlphaMaskSet` · `TriUV` · `AlphaMaskBuilder` · `RayTriUV` 신설. 순회 미변경 | `AlphaMaskBuilderTests`: 합성 체커 텍스처 → 비트 == 기대값. `RayTriUV` 의 `hit` 가 `RayTri` 와 **비트동일** | 낮음 |
| **α1** | 씬 구성 확장(서브메시·UV·머티리얼 슬롯). 순회 미변경 | 기존 전 테스트 **비트동일**(속성만 추가, 순회 무변경) | 낮음 |
| **α2** | **CPU any-hit** — `BVH`/`TwoLevelBVH` | `AlphaOccluderTests`: 컷아웃 쿼드 격자에 레이 → 마스크 예측값과 일치. `enabled=false` 시 기존 결과 비트동일 | 중간 |
| **α3** | **Burst 미러** | `AlphaOccludedCompareTests`: ≡CPU, **불일치 0건**(정수 비트 판정이므로 ε 아님) | 중간 |
| **α4** | **GPU 미러** (`precise` UV) | ≡Burst 불일치 0건. dxc/fxc 컴파일 + **X4714 재발 없음** 확인 | 높음 |
| **α5** | `AtlasApplyDebug` 배선 + 인스펙터 | 나무 씬 "Bake & Apply" 육안(잎 실루엣·빛샘) + "RadianceGI Backend Diff" MATCH + **성능 실측 기록** | 중간 |
| **α6**(옵션) | 성능 최적화 — 마스크 계층(coarse 전부불투명 블록), UV 버퍼 조건부 업로드 | 회귀(≡α5) + 시간 단축 폭 | 낮음 |

### 8.1 α4 GPU 주의사항

- `int stack[BVH_STACK]` 은 **32 유지**. 알파 테스트는 스택을 늘리지 않지만 UV fetch·비트 연산이
  VGPR을 추가로 잡는다. v14.5에서 해소한 X4714가 재발하지 않는지 fxc `/T cs_5_0` 로 커널별 확인.
- `OccludedBlas` 의 early-exit 제거는 **루프 종료 조건 변화**다. HLSL에서 조기 `return` 이
  사라지면 컴파일러 언롤 전략이 바뀔 수 있으니 컴파일 경고를 반드시 재확인.

---

## 9. 검증 매트릭스

| ID | 대상 | 방법 | 합격 기준 |
|---|---|---|---|
| α-V1 | 마스크 빌드 | 합성 체커/원형 알파 텍스처 | 비트 전수 일치 |
| α-V2 | `RayTriUV` | 무작위 5000 삼각형·레이 | `hit` 비트동일, `bu+bv<=1` |
| α-V3 | CPU any-hit | 컷아웃 쿼드 격자 관통 레이 | 해석적 기대값과 일치 |
| α-V4 | Burst ≡ CPU | 동일 씬·레이 5000 | **불일치 0** |
| α-V5 | GPU ≡ Burst | 동일 씬·레이 5000 | **불일치 0** (텍셀 경계 근접 케이스 별도 집계) |
| α-V6 | 회귀 | 컷아웃 0개 씬에서 기존 테스트 전수 | 전부 **비트동일** |
| α-V7 | 실 씬 | 나무 씬 Bake & Apply, 실시간 라이트 OFF | 잎 실루엣 그림자 · 캐노피 사이 빛샘 육안 확인 |
| α-V8 | 성능 | 동일 씬 알파 on/off | 시간 비율 기록(회귀 폭 문서화) |

α-V6가 이 설계의 **안전망**이다. 결정 ⑥(무비용 게이트)이 제대로 구현됐다면 자동으로 통과한다.

---

## 10. 리스크 · 미결 사항

### 10.1 성능 회귀 (가장 큰 리스크)

캐노피는 삼각형 밀도가 가장 높은 부위인데, any-hit 은 **early-exit 을 포기**한다.
그림자 레이가 잎을 여러 장 통과하며 전 캐노피를 훑을 수 있다.

- 완화 1: BLAS 단위 게이트(§7.3) — 불투명 메시는 무영향.
- 완화 2: 마스크 계층(coarse 레벨에서 "이 블록 전부 불투명"이면 즉시 확정) — α6.
- 완화 3: GPU 백엔드가 이미 7.2× 여유가 있음.
- **측정 없이 판단하지 않는다** — α-V8에서 실측 후 α6 착수 여부 결정.

### 10.2 잎 자신이 리시버일 때

컷아웃으로 잘려나간 텍셀도 라이트맵 아틀라스에 자리를 차지하고 베이크된다.
`validMask` 에 알파를 반영해 무효 텍셀로 만들면 디레이션·스티칭이 잘린 영역을 침범하지 않는다.
**이번 범위 밖**이지만 후처리 품질에 영향이 있으므로 후속 항목으로 남긴다.

### 10.3 텍셀 해상도 — 알파와 무관한 병행 이슈

2번 스크린샷의 그림자 외곽이 계단식으로 각진 것은 알파와 **별개로** 라이트맵 텍셀 밀도 문제다.
알파 컷아웃을 넣어도 잎 그림자의 표현 가능 해상도는 리시버의 texel density가 결정한다.
나무 그림자를 받는 바닥은 `texelsPerUnit` 을 올려야 유니티 수준의 디테일이 나온다.
α-V7 육안 검증 시 이 둘을 혼동하지 않도록 **알파 on/off 를 같은 해상도에서** 비교한다.

### 10.4 서브메시 256개 초과

`blasTriSubmesh` 를 `byte` 로 잡았다. 실무상 충분하지만 초과 시 경고 후 clamp.
필요해지면 `ushort` 로 승격(메모리 2배, GPU stride 무관 — 별도 버퍼라 영향 국소적).

### 10.5 비항등 텍스처 ST

`mad` 융합 문제(§4.1)는 ST가 항등일 때 사라진다. 빌더가 비항등 ST 개수를 로그로 알리고,
α-V5에서 비항등 ST 케이스를 별도 집계해 백엔드 불일치가 실제로 발생하는지 확인한다.

---

## 11. 상위 로드맵과의 관계

- 이 트랙은 대시보드 **단계 1 (C0: 수집·분류·알파 마스크)** 중 "알파 마스크" 부분의 완결이다.
  `SceneCollector`/`MeshCleaner` 는 별도.
- **지형 QuadTree 라이트맵 본계획의 선행 조건**이다. QuadTree 설계는 나무·프롭을
  occluder 인스턴스로 취급하는데(v14.11 Occluder/Receiver 분리 개념검증 완료),
  그 occluder 대부분이 컷아웃 식생이다. 알파 없이는 QuadTree를 구현해도
  그림자 품질이 지금과 같다.
- **Track B(식생 프로브)** 도 같은 레이 엔진을 쓰므로 자동으로 수혜를 받는다.

→ **권장 순서: α 트랙 → 지형 QuadTree 본계획 → Track B**

---

## 12. 신설·수정 파일 목록

### 신설
```
Runtime/LightmapEvaluate/Alpha/AlphaMaskSet.cs          (POD + AlphaOpaque 판정)
Runtime/LightmapEvaluate/Alpha/AlphaMaskBuilder.cs      (머티리얼 → 비트마스크)
Tests/AlphaMaskBuilderTests.cs                          (α-V1)
Tests/AlphaOccluderTests.cs                             (α-V2, α-V3)
Tests/AlphaOccludedCompareTests.cs                      (α-V4, α-V5)
```

### 수정
```
Runtime/LightmapEvaluate/Occluders/Occluder.cs          (+ TriUV, RayTriUV)
Runtime/LightmapEvaluate/Occluders/BVH.cs               (any-hit 오버로드)
Runtime/LightmapEvaluate/Occluders/TwoLevelBVH.cs       (any-hit 오버로드)
Runtime/LightmapEvaluate/Burst/BurstScene.cs            (+ 속성/머티리얼 배열)
Runtime/LightmapEvaluate/Burst/Occluders/BurstTwoLevelBVH.cs
Runtime/LightmapEvaluate/Gpu/GpuScene.cs                (+ BindAlpha)
Shaders/Resources/PathTrace.compute                     (any-hit + AlphaOpaque)
Samples/AtlasApplyDebug.cs                              (씬 구성 + 인스펙터)
Samples/LightmapEvaluateDebugger.cs                     (RunAll 에 α 테스트 배선)
```

`Shaders/Resources/BvhTraverse.compute` 는 **건드리지 않는다**(G4 검증 자산 보존).

---

## 13. 구현 결과 (2026-08-10, α0~α5 코드 완료)

### 13.1 설계 대비 변경된 결정 — GPU만 '복제' 대신 '분기'

설계 §3 결정 ⑤는 세 백엔드 모두 순회 함수를 **복제**하는 것이었다. CPU·Burst 는 그대로 복제했지만
(`BVH.IntersectAlpha`/`OccludedAlpha`, `BurstTwoLevelBVH.IntersectBlasAlpha`/`OccludedBlasAlpha`),
**GPU 는 기존 `IntersectBlas`/`OccludedBlas` 에 `(int matBase, bool alphaOn)` 매개변수를 추가하는
방식으로 바꿨다.**

이유: 복제하면 `int stack[BVH_STACK]` 가 하나 더 live 가 되어 v14.5 에서 해소한
**X4714(레지스터 압박)가 재발**한다. 한 함수 안에서 분기하면 스택이 늘지 않고,
`alphaOn=false` 일 때 `RayTriUV` 가 `RayTri` 와 동일 연산이므로 결과는 비트동일이다.
→ fxc `/T cs_5_0` 로 5개 커널 전수 확인, **X4714 없음**.

### 13.2 배선 구조 (설계보다 단순해진 지점)

`BurstAlpha` 를 **`BurstScene` 의 필드로** 넣었다. 잡(AoJob/DirectJob/IndirectJob/SHJob)은 이미
`BurstScene` 을 통째로 들고 있으므로 **잡 4종의 시그니처를 하나도 바꾸지 않고** 알파가 배선된다.
알파가 꺼진 씬도 `BurstAlpha.CreateDisabled` 로 1원소 더미를 할당한다 — 잡 안전 시스템이
NativeArray 필드의 '할당됨'을 요구하기 때문.

### 13.2b 부수 수정 — `meshAlbedo` 미할당 (α와 무관하지만 α가 드러냄)

`Run All Tests` 실행 시 `BurstRadianceCompareTests.DirectEquiv` 에서 예외가 났다:

```
InvalidOperationException: The UNKNOWN_OBJECT_TYPE DirectJob.scene.meshAlbedo
has not been assigned or constructed. All containers must be valid when scheduling a job.
```

원인은 α가 아니라 **`BurstScene.Create(bvh, allocator)`(알베도 없는 2인자 오버로드)가
`meshAlbedo` 를 아예 생성하지 않은 것**이다. 잡 안전 시스템은 중첩 구조체 안의 NativeArray 까지
'생성됨'을 요구하는데(=α에서 `BurstAlpha.CreateDisabled` 더미를 둔 것과 같은 이유),
이 필드만 `default` 로 남아 있었다.

수정: 2인자 `Create` 가 `meshAlbedo` 를 **길이 max(1, meshCount)로 항상 할당하고 0.5 로 채운다.**
0.5 는 `BurstTwoLevelBVH.ClosestHit` 가 쓰던 fallback 값과 동일하므로 **값 거동은 불변**이다.
알베도 오버로드는 재할당 대신 그 위에 덮어쓴다(누수 방지).

부수 효과로 GPU 쪽 잠재 버그도 사라졌다 — 2인자 경로로 만든 `GpuScene` 은 `_MeshAlbedo` 를
1원소만 업로드해서 `mesh>0` 인덱싱이 범위 밖이었다.

### 13.3 보수적 게이트

`MeshHasCutout[mesh]` 는 **그 메시를 쓰는 인스턴스 중 하나라도 컷아웃이면 1** 이다(보수적).
per-instance 게이트가 더 정밀하지만, 보수적 쪽은 절대 필요한 판정을 건너뛰지 않으므로 안전하다.

### 13.4 검증 현황

| 항목 | 방법 | 결과 |
|---|---|---|
| C# 컴파일 | `dotnet build` (Runtime+Tests+Samples, Unity 6000.5.2 참조) | **0 errors**, 신규 경고 0 |
| HLSL 문법 | dxc `-T cs_5_0` × 5커널 | 전부 rc=0 |
| 레지스터 압박 | fxc `/T cs_5_0` × 5커널 | **X4714 없음** (X3556 정수나머지 권고만, 느린 경로 한정) |
| α-V2~V4, V6 | `AlphaCutoutTests` | **16/16 PASS** (에디터 실측) |
| α-V5 | `AlphaGpuCompareTests` | **4/4 PASS** — GPU Direct ≡ Burst miss=0/256, 체커보드 해석적 일치 |
| **α-V6 회귀** | `Run All Tests (C1+C2)` 전수 | **109/109 PASS · 수치 드리프트 0** (아래 13.4b) |
| α-V1 | `AlphaMaskBuilder` 실 텍스처 | **미작성** — 실 씬 육안(α-V7)으로 대체 확인 |
| **α-V7 실 씬** | SpeedTree 버드나무 씬 + `Alpha Diagnose` 정량 측정 | **통과 — 수치 확정**(§13.7) |
| **α-V8 성능** | 알파 on/off × 원반샘플링 베이크 시간 | **측정 완료**(§13.9) |

### 13.4b 회귀 무영향 증거 (2026-08-10 에디터 실측)

α 도입 전 문서에 기록된 수치와 **자릿수까지 동일**하다 → 결정 ⑥(컷아웃 없으면 기존 경로)이
실제로 성립했음을 보여준다.

| 항목 | α 이전 (문서 v14.x) | α 이후 실측 | 판정 |
|---|---|---|---|
| G2 Direct ≡ CPU | 정확 일치 | mean=0, max=0, mism=0 | 동일 |
| G4 순회 T 오차 | maxErr≈1.1e-5, near-tie 0 | maxErr=1.1e-5, hard-miss=0 | 동일 |
| G5 Direct | mean 8.3e-10 | mean 8.28e-10 | 동일 |
| G5 AO | mean 0 (exact) | mean 0 (exact) | 동일 |
| G5 Indirect | mean 2.5e-9 | mean 2.48e-9 | 동일 |
| CSRadiance ≡ CSDirect+CSIndirect | mean 0 | mean 1.66e-9, over(1e-4)=0 | 동등 |
| SH-G ≡ BurstSHBaker | mean 1.0e-8, over 0/432 | mean 9.69e-9, over 0/432 | 동일 |

`AlphaWrapFloor` 에 빠른 경로(`0<=i<n` 이면 나머지 연산 생략)를 CPU/HLSL 양쪽에 동일하게 넣었다.
결과는 불변이고 핫패스에서 정수 나머지를 피한다.

### 13.5 미적용 범위

- **구 `Radiance` 모드**(월드 Tri + 단일레벨 BVH 경로)는 알파 미배선. 프로덕션 경로인
  `RadianceGI`(2단 인스턴싱)만 적용했다. 필요해지면 단일 '메시'로 보는 별도 레이아웃이 필요하다.
- §10.2(잎 자신이 리시버일 때 `validMask` 반영), §10.1 성능 최적화(α6)는 계획대로 후속.
### 13.6 실 씬 검증에서 드러난 것 — **머티리얼 판별이 진짜 난관이었다**

순회·마스크·백엔드 정합은 합성 씬에서 한 번에 통과했지만, **실제 에셋의 컷아웃 머티리얼을
알아보는 일**에서 두 번 헛돌았다. 실 씬 검증의 가치가 여기에 있었다.

**대상**: SpeedTree 8 에셋 (`Tree_N_LandscapePlant_H_01.st`, `_MainTex` 1024×1024 RGBA)

| 시도 | 판별 조건 | 결과 |
|---|---|---|
| 1차 | `RenderType=="TransparentCutout"` / `_ALPHATEST_ON` / `_AlphaClip>0.5` | 전부 실패 → `cutout mats=0` |
| 2차 | `_AlphaClip` 없으면 `_Cutoff` 유무로 넓게 | **여전히 실패** — `_Cutoff` 자체가 없다고 나옴 |
| 3차 | **셰이더 이름에 "SpeedTree"** → 컷아웃, 컷오프 0.3333 하드코딩 | **성공** |

2차가 실패한 이유가 핵심이다. `.mat` 파일에는 `_Cutoff: 0.333` 이 분명히 직렬화돼 있는데
`Material.HasProperty("_Cutoff")` 는 **false** 를 돌려준다.

> **`Material.HasProperty` 는 셰이더가 선언한 프로퍼티만 본다. `.mat` 에 남은 직렬화 값은 보지 않는다.**
> SpeedTree 임포터가 `_Cutoff` 를 써 넣긴 했지만 Unity 의 SpeedTree8 셰이더는 그것을 프로퍼티로
> 노출하지 않고 셰이더 코드에서 `clip(alpha - 0.3333)` 으로 **하드코딩**한다.
> → SpeedTree 는 **어떤 프로퍼티 기반 판별로도 잡을 수 없다.** 셰이더 이름으로 판별해야 한다.

**현재 판별 우선순위** (`AlphaMaskBuilder.GetOrCreateMask`)

1. 사용자 강제 지정(`alphaForceCutout` 인스펙터 배열) — 미지의 서드파티 셰이더 탈출구
2. 셰이더 이름에 `SpeedTree` → 컷오프 0.3333
3. `_AlphaClip` 보유(URP Lit 계열) → 그 값이 정답
4. 그 외 → `RenderType` / `_ALPHATEST_ON` / `_Cutoff` 유무로 후보

3번이 2번보다 뒤에 오면 안 된다. URP Lit 은 클립이 꺼져 있어도 `_Cutoff` 를 항상 노출하므로
4번만으로 판단하면 불투명 머티리얼을 전부 오탐한다.

**오탐 안전망**: 후보로 잡아 마스크를 구운 뒤 **전부 불투명이면 폐기**한다(`opaqueCount == mw*mh`).
전부 불투명한 마스크는 아무 일도 하지 않으므로, 판별을 넓게 잡아도 결과를 바꿀 수 없다.
실제로 같은 SpeedTree 셰이더를 쓰는 줄기 머티리얼(`DouglasFirBark`)이 여기서 자동으로 걸러졌다.

**진단 로그**: 머티리얼별로 `shader=... RenderType=... _AlphaClip=... _Cutoff=...` 를 남긴다.
1차 실패 때 셰이더 이름을 안 찍은 것이 한 번 헛돈 직접 원인이었다.

### 13.7 α-V7 정량 측정 — `Alpha Diagnose` (2026-08-12)

육안만으로는 "캐노피가 겹쳐서 원래 진한 것"과 "알파가 죽은 것"을 구분할 수 없다.
그래서 **같은 그림자 레이 집합을 알파 ON/OFF 로 두 번 쏴서 판정이 뒤집힌 개수를 세는**
진단 메뉴를 만들었다(`AtlasApplyDebug` → "Alpha Diagnose").

```
[마스크 빌드] cutout mats=1, transparent(ignored)=0, 불투명 스킵=1, mask bytes=131072, non-identity ST=0
    · 'Lit' 불투명 취급 (shader='Universal Render Pipeline/Lit', RenderType=Opaque, _AlphaClip=0, _Cutoff=있음)
    · 'DouglasFirBark' [_MainTex 256×1024] 알파 전부 불투명 → 스킵
    · 'Tree_N_LandscapePlant_H_01_LOD0' [_MainTex] 1024×1024 cutoff=0.333 불투명=32.2%
인스턴스 9, 유니크 메시 2, alpha.Enabled=True
  mesh[1] 'Tree_..._LOD0' tris=6353, submesh=2, uv0=있음, 컷아웃=예
그림자 레이 40000발 (리시버 상단 200×200 격자, 태양 방향)
  알파 OFF 차폐 = 2801 (7.0%)
  알파 ON  차폐 = 2225 (5.6%)
  → 알파로 뚫린 레이 = 576 (1.4%)
```

**해석 — 세 갈래 판별이 전부 의도대로 동작했다:**

| 머티리얼 | 경로 | 결과 |
|---|---|---|
| `Lit`(바닥) | `_AlphaClip=0` (URP Lit 계열, 결정 ②) | 불투명 — 오탐 없음 |
| `DouglasFirBark`(줄기) | 후보로 잡혔으나 **all-opaque 폐기** 안전망 | 스킵 — 넓은 판별이 결과를 바꾸지 못함을 실증 |
| `Tree_..._LOD0`(잎) | **SpeedTree 이름 판별**, 컷오프 0.3333 하드코딩 fallback | 마스크 1024², 불투명 32.2% |

컷오프 `0.333` 은 하드코딩 fallback 값이다 — 앞선 실행에서 이 머티리얼이 `_Cutoff=없음` 으로
보고됐으므로 `HasProperty("_Cutoff")` 는 false 이고, §13.6 의 분석과 일치한다.

**핵심 수치**: 전체 격자의 1.4% 가 뚫렸는데, 이는 **원래 그림자였던 레이(2801발)의 20.6%**다.
즉 캐노피 그림자 면적의 1/5 이 잎 사이 틈으로 열렸다. 나머지 격자(93%)는 애초에 나무 밖이라
분모에 넣으면 안 된다.

### 13.8 헤맨 지점 — 진단이 모호하면 라운드가 늘어난다

α-V7 확정까지 여러 차례 왕복했고, 마지막 한 번은 **인스펙터의 `alphaCutoutShadows` 토글이 꺼져
있던 것**이 원인이었다. 진단 출력의 `[마스크 빌드]` 줄이 **빈 문자열**로 나온 것이 단서였다
(마스크 빌더를 건너뛰는 경로가 그 토글 하나뿐이므로).

교훈을 코드에 반영했다:
- `AlphaDiagnose` 는 **토글이 꺼져 있으면 즉시 경고 후 중단**한다(모호한 출력 금지).
- 완료 로그 한 줄에 `alpha=ON(masks=N, KB)` / `alpha=DISABLED(컷아웃 머티리얼 0)` / `alpha=OFF(토글)` 를 항상 붙인다.
- 자동 판별이 실패하는 서드파티 셰이더용 탈출구: `alphaForceCutout`(에셋 참조) +
  `alphaForceCutoutNames`(머티리얼/셰이더 **이름 부분일치**). 임포터가 만든 임베드 서브에셋
  머티리얼은 폴더의 동명 `.mat` 과 다른 오브젝트라 참조 지정이 안 통할 수 있어서 이름 매칭을 함께 뒀다.

### 13.9 α-V8 성능 실측 (2026-08-12)

씬: 리시버 플레인 1 + **occluder 나무 64그루**, 아틀라스 2048², RadianceGI, denoise 3×, dilate 4×.

| # | alpha | direct | total | scene(BVH+마스크) | **bake(레이트레이싱)** | post |
|---|---|---|---|---|---|---|
| 1 | ON | 1× | 23.66s | 0.10s | **17.56s** | 5.94s |
| 2 | OFF | 1× | 13.09s | 0.05s | **10.10s** | 2.70s |
| 3 | ON | 16×(1.0°) | 20.99s | 0.10s | **18.10s** | 2.74s |

**결론 두 가지**

1. **알파 any-hit 비용 = bake 10.10 → 17.56s (약 1.74×).**
   early-exit 포기가 원인이며 §10.1에서 예상한 그대로다. 마스크 빌드(scene 0.05→0.10s)는 무시 가능.
2. **태양 원반 샘플링 16발 = +0.54s (+3%).** 직사광 레이가 16배가 됐는데도 거의 공짜다 —
   간접광(spp 32 × 2바운스)이 텍셀당 수십~수백 레이라 직사광 1→16발은 전체에서 미미하다.

**⚠ 측정 오염 주의**: #1의 `post`가 5.94s 로 #2·#3(2.7s)의 **2.2배**다. 후처리는 알파와 아무
관계가 없고 해상도·반복수도 같으므로 이 차이는 **첫 실행의 Burst JIT 컴파일**로 봐야 한다.
같은 이유로 #1의 `bake` 도 부풀려졌을 가능성이 있어 **알파 비용 1.74× 는 상한**이다.
(#3이 #1보다 일을 더 하는데 total 이 더 짧은 것도 같은 정황.)
→ 정확한 값이 필요하면 **워밍업 베이크 1회 후** 또는 **역순으로** 재측정할 것.

**α6(마스크 계층) 착수 판단**: 보류가 타당하다. 비용의 본질은 "any-hit 이 early-exit 을
포기하는 것"이라 coarse 블록 계층으로 줄일 수 있는 몫이 크지 않다. 잎 마스크가 32% 불투명이라
'전부 불투명' 블록이 드물어 조기 확정이 잘 걸리지 않는다. 1.7× 는 식생 그림자의 정확성 대비
수용 가능한 대가로 본다. 나무 수를 더 늘렸을 때 초선형으로 나빠지는지만 추후 확인.

---

## 14. 후속 — 직사광 태양 원반 샘플링 (2026-08-12)

α 가 동작하자 **새 문제**가 드러났다: 잎 경계에 **점묘(salt-and-pepper) 노이즈**.
몬테카를로 노이즈가 아니라 **이진 판정 에일리어싱**이다 — 텍셀당 그림자 레이가 1발이라
가려짐/안 가려짐이 반반인 경계에서 인접 텍셀이 무작위로 0/1 로 갈린다.
텍셀 밀도를 올려도 잘아질 뿐 사라지지 않고, 디노이저는 엣지 보존 필터라 이 점묘를
'진짜 그림자 경계'로 인식해 오히려 살린다.

**해법**: 태양을 점이 아니라 **원반**으로 보고 그 안에서 방향을 흔들어 N 발 쏜 뒤 평균 →
가시도가 0~1 연속값이 되고 반그림자(penumbra)가 생긴다.

| 파라미터 | 위치 | 기본 | 의미 |
|---|---|---|---|
| `DirectSamples` | `BakeQualitySettings` | 1 | 텍셀당 그림자 레이 수. **1 이면 기존 경로와 비트동일** |
| `AngularDiameterDeg` | `DirectionalLight` | 0 | 태양 각지름(도). 0 = 하드. 실제 태양 **0.53°** |

### 설계 요점

- **RNG 를 쓰지 않는다.** 인덱스만으로 정해지는 저불일치 수열(u1 층화 + 황금비 회전)을 쓰고
  텍셀별로 **위상만** 시드로 돌린다. SH-G 가 피보나치 방향셋을 CPU 계산해 업로드한 것과 같은
  이유 — 세 백엔드가 같은 방향셋을 쓰게 만들어 발산 여지를 줄인다.
- **1차 직사광에만 적용.** 바운스 NEE(간접광 내부)는 1 발 유지 → G5 Indirect 검증 수치 보존.
- **GPU 만 구조가 다르다.** `DirectNEESampled` 안에서 하드/소프트를 **하나의 루프**로 합쳤다.
  분기마다 `DirectNEE` 를 따로 호출하면 `TlasOccluded` 가 두 번 인라인되어 순회 스택이 2벌 더
  live → **X4714 재발**(실제로 1차 구현에서 재발했고 fxc 로 잡았다). 이른 return 제거로
  fxc X4000 false-positive 도 함께 해소. CPU/Burst 는 인라인 압박이 없어 위임 구조 유지.
- `soft=false` 일 때 N=1·d=L(정규화 재적용 없음)·오프셋 동일 → `DirectNEE` 와 비트동일.

### 회귀 (2026-08-12 실측, 109/109 PASS)

기본값(`DirectSamples=1`)에서 G2 Direct `mean=0/max=0`, G5 Direct `8.28e-10`, AO `0`,
Indirect `2.48e-9`, SH-G `9.69e-9`, α GPU `miss=0/256` — 전부 도입 전과 동일.

단 `G6-2 CSRadiance ≡ CSDirect+CSIndirect` 만 `1.66e-9 → 2.48e-9` 로 바뀌었다(정합성 개선).
이전에는 `CSDirect` 가 직사광을 커널에 인라인으로 갖고 `CSRadiance` 는 `DirectNEE` 를 호출해
같은 식인데도 스케줄링이 미세하게 달랐다. 이제 **둘 다 `DirectNEESampled` 를 호출**하므로
직사광 부분이 완전히 같아졌고, 잔차가 순수 간접광 MC 발산만 남아 **G5 Indirect 값과 정확히
일치**하게 됐다. `over(1e-4)=0` 불변.

### 튜닝 지침 (실측 기반)

`DirectSamples=16, AngularDiameterDeg=1.0` 으로 구웠더니 점묘는 완전히 사라졌으나
**잎 그림자 형태까지 뭉개졌다.** 원인은 각지름 과다다.

반그림자 폭 ≈ (차폐물까지 거리) × tan(각지름). 캐노피가 지면 10 m 위면 1° 에서 약 **17 cm** 로
잎 덩어리 크기와 맞먹어, 16 발이 서로 다른 잎 사이를 지나며 평균 투과율로 수렴한다.

| 목적 | 권장값 |
|---|---|
| 물리적 정확 | `AngularDiameterDeg = 0.53`(실제 태양), `DirectSamples = 8~16` |
| 디테일 우선 | 0.2~0.3, 8~16 |
| 부드러운 연출 | 1~2, 24~32 |

각지름이 좁을수록 원뿔이 작아 **적은 샘플로도 충분**하다. 디노이저(`denoiseIterations`)가
그 위에 또 뭉개므로, 원반 샘플링을 켠 뒤에는 디노이즈를 0~1 로 낮추는 편이 낫다.

---

