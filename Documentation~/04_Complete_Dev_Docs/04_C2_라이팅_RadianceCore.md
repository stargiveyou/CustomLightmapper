# 04 — C2: 라이팅 (RadianceCore / Sky / Scene)

> 상태: **✅ 완료 · 검증** (결정 ⑨⑪⑫)
> 검증: `RadianceIndirectTests` a~e (π·L / 하늘0 / 흑박스0 / 에너지보존 / RR 무편향), `LightmapEvaluateTests`

---

## 0. 계약 (모든 백엔드 공통)

| 계약 | 내용 |
|---|---|
| 저장량 | **조도(irradiance)**. 점 알베도는 런타임에 곱한다. 바운스 표면 알베도만 베이크에 들어간다 |
| 색 공간 | Linear 전 구간 |
| 진입점 | `EvaluateRadiance(scene, p, n, sun, sky, q, seed)` = `Direct(NEE) + Indirect(경로추적+RR)` |
| 난수 | xorshift32, per-texel 시드 `seed + i·2654435761u` → **결정적·재현 가능** |
| 샘플링 | 코사인 중요도 → 히트에서 π·(1/π) 상쇄, **π는 하늘(미스) 항에만** |

이 "π 규약"이 CPU/Burst/GPU 3백엔드가 ε 수준으로 일치하는 핵심 근거다.

---

## 1. 난수 — `Rng` (xorshift32)

```csharp
// Runtime/LightmapEvaluate/RadianceCore.cs:10
public struct Rng
{
    private uint _s;
    public Rng(uint seed) { _s = seed == 0 ? 1u : seed; }

    public float Next()
    {
        _s ^= _s << 13;
        _s ^= _s >> 17;
        _s ^= _s << 5;
        return (_s & 0xFFFFFF) / 16777216f;   // 분모 2^24 → fp32 정확
    }
}
```

분자를 24비트로 마스크하고 분모를 2^24로 둔 것은 **fp32에서 나눗셈이 정확**하도록 하기 위함이다.
GPU HLSL 미러(`PathTrace.compute:401`)가 이 상수를 그대로 쓴다 → **비트동일**.

---

## 2. 코사인 반구 샘플

```csharp
// Runtime/LightmapEvaluate/RadianceCore.cs:100
public static Vector3 CosineHemisphere(Vector3 n, ref Rng rng)
{
    float r1 = rng.Next();          // ← 호출 순서 고정: r1 먼저, r2 다음
    float r2 = rng.Next();
    float st  = Mathf.Sqrt(r1);
    float phi = 2 * Mathf.PI * r2;

    float lx = st * Mathf.Cos(phi);
    float ly = st * Mathf.Sin(phi);
    float lz = Mathf.Sqrt(Mathf.Max(0f, 1f - r1));

    Vector3 up = Mathf.Abs(n.x) < 0.9f ? Vector3.right : Vector3.up;   // TBN 기저
    Vector3 t  = Vector3.Cross(n, up).normalized;
    Vector3 b  = Vector3.Cross(n, t);
    return (t * lx + b * ly + n * lz).normalized;
}
```

> **`r1` → `r2` 호출 순서와 `up` 선택 임계값(0.9)이 계약이다.**
> 하나라도 바뀌면 Burst/GPU와 난수 스트림이 어긋나 교차검증이 깨진다.

---

## 3. 광원 · 하늘

```csharp
// Runtime/LightmapEvaluate/RadianceCore.cs:27
public struct DirectionalLight
{
    public Vector3 Direction;   // 광원이 '향하는' 방향 (조명 → 표면)
    public Vector3 Color;
    public float   Intensity;
}
```

```csharp
// Runtime/LightmapEvaluate/Sky.cs:8
public interface ISky { Vector3 Radiance(Vector3 dir); }

public struct UniformSky : ISky           // 반구 적분 시 조도 = π·Radiance
{ public Vector3 L; public Vector3 Radiance(Vector3 dir) => L; }

public struct GradientSky : ISky
{
    public Vector3 Top, Bottom;
    public Vector3 Radiance(Vector3 dir)
    {
        float t = Mathf.Clamp01(dir.y * 0.5f + 0.5f);
        return Vector3.Lerp(Bottom, Top, t);
    }
}
```

> ⑪ **정정**: 원 기획의 `ILight` 인터페이스는 채택되지 않았다.
> 광원은 `DirectionalLight` struct 1종 + `ISky` 조합. **Point / Spot / Area 및 `ILight.Sample` 은 미구현.**

---

## 4. AO — `EvaluateAO`

```csharp
// Runtime/LightmapEvaluate/RadianceCore.cs:80
public static float EvaluateAO(IOccluder occluder, Vector3 point, Vector3 normal,
                               int samples, uint seed, float maxDist = float.MaxValue)
{
    var rng = new Rng(seed);
    Vector3 o = point + normal * 1e-3f;   // self-hit 방지 바이어스 (하드코드 1e-3)
    int occ = 0;
    for (int s = 0; s < samples; s++)
    {
        Vector3 d = CosineHemisphere(normal, ref rng);
        if (occluder.Occluded(o, d, maxDist)) occ++;
    }
    return 1f - (float)occ / samples;     // 1 = 열림, 0 = 막힘
}
```

코사인 가중 샘플 + 단순 평균 = **π가 상쇄된 가시성 평균**. 별도 정규화가 없는 이유다.
오프셋 `1e-3f`는 `BakeQualitySettings.RayBias`(1e-4)와 **다른 하드코드**다 — Burst/GPU 미러도 동일하게 1e-3을 쓴다.

---

## 5. 직접광 — `EvaluateDirect` (NEE)

```csharp
// Runtime/LightmapEvaluate/RadianceCore.cs:118
public static Vector3 EvaluateDirect(IOccluder occluder, Vector3 p, Vector3 n, DirectionalLight sun)
{
    Vector3 L = -sun.Direction.normalized;      // 광원을 '향하는' 방향
    float ndl = Vector3.Dot(L, n);
    if (ndl <= 0f) return Vector3.zero;         // 백페이스
    if (occluder.Occluded(p + n * 1e-3f, L, 1e30f)) return Vector3.zero;   // 그림자
    return sun.Color * sun.Intensity * ndl;
}
```

무작위가 없다 → Burst/GPU 미러와 **타이트하게 일치**해야 한다(실측: GPU mean 8.3e-10).

---

## 6. 간접광 — `EvaluateIndirect` (경로추적 + Russian Roulette) ★

C2의 핵심. 이 루프가 Burst(`BurstIndirect.IndirectJob`)와 GPU(`EvalIndirect`)에서 그대로 미러된다.

```csharp
// Runtime/LightmapEvaluate/RadianceCore.cs:143
public static Vector3 EvaluateIndirect(IRadianceScene scene, Vector3 p, Vector3 n,
                                       DirectionalLight sun, ISky sky,
                                       in BakeQualitySettings q, uint seed)
{
    var rng = new Rng(seed);
    Vector3 sum = Vector3.zero;

    for (int i = 0; i < q.IndirectSamples; i++)
    {
        Vector3 acc = Vector3.zero;
        Vector3 tp  = Vector3.one;                         // throughput
        Vector3 dir = CosineHemisphere(n, ref rng);
        Vector3 o   = p + n * q.RayBias;

        for (int b = 0; ; b++)
        {
            if (!scene.ClosestHit(o, dir, 0f, float.MaxValue, out var hp, out var hn, out var alb))
            {
                // 미스: 하늘. 반구 적분의 π는 '여기에만' 남는다.
                acc += Vector3.Scale(tp, sky.Radiance(dir)) * Mathf.PI;
                break;
            }

            // 바운스 표면의 직접 조도(태양)를 알베도로 반사 → tp · ρ · E_direct
            Vector3 eD = EvaluateDirect(scene.Occluder, hp, hn, sun);
            acc += Vector3.Scale(tp, Vector3.Scale(alb, eD));

            tp = Vector3.Scale(tp, alb);                   // throughput *= ρ

            if (b + 1 >= q.MaxBounces) break;

            if (b + 1 >= q.RRStartDepth)
            {
                float pSurv = Mathf.Clamp(Mathf.Max(tp.x, Mathf.Max(tp.y, tp.z)), 0.05f, 1f);
                if (rng.Next() > pSurv) break;
                tp /= pSurv;                               // 무편향 보정
            }

            dir = CosineHemisphere(hn, ref rng);
            o   = hp + hn * q.RayBias;
        }
        sum += acc;
    }
    return sum / (float)q.IndirectSamples;
}
```

### 6.1 왜 π가 하늘 항에만 붙는가

반구 조도 적분은 `E = ∫ L(ω)·cosθ dω`.
코사인 중요도 샘플의 pdf는 `cosθ/π`이므로 몬테카를로 추정량은 `L·cosθ / (cosθ/π) = π·L`.

- **히트 항**: 히트 표면의 조도 `E_direct`를 알베도로 반사한 값이 이미 `ρ·E/π · π = ρ·E` 로 상쇄된다 → **알베도만 곱한다**.
- **미스 항**: 하늘은 라디언스 `L`이므로 `π·L`이 그대로 남는다 → **`* Mathf.PI`**.

이 규약은 SH 트랙의 `A0 = π` 상수와도 자체정합한다
(v13.3에서 "1/π 누락 의심"이 **오귀인**으로 배제된 근거).

### 6.2 Russian Roulette

```
pSurv = clamp(max(tp.x, tp.y, tp.z), 0.05, 1)
생존 시 tp /= pSurv    ← 무편향 보정
```

하한 0.05로 조기 종료 폭주를 막고, 생존 시 나눠줘서 기댓값을 보존한다.
`RadianceIndirectTests` (e)가 이 무편향성을 검증한다.

---

## 7. 통합 진입점

```csharp
// Runtime/LightmapEvaluate/RadianceCore.cs:192
/// 전체 경로추적 라이트맵 값(조도) = Direct(sun) + Indirect(scene, sky).
public static Vector3 EvaluateRadiance(IRadianceScene scene, Vector3 p, Vector3 n,
                                       DirectionalLight sun, ISky sky,
                                       in BakeQualitySettings q, uint seed)
{
    Vector3 direct   = EvaluateDirect(scene.Occluder, p, n, sun);
    Vector3 indirect = EvaluateIndirect(scene, p, n, sun, sky, q, seed);
    return direct + indirect;
}
```

레거시 오버로드도 유지된다(`Direct + ambient × AO`, `AtlasApplyDebug.BakeMode.Radiance` 경로):

```csharp
// Runtime/LightmapEvaluate/RadianceCore.cs:132
public static Vector3 EvaluateRadiance(IOccluder occluder, Vector3 p, Vector3 n,
                                       DirectionalLight sun, Vector3 ambient, int aoSamples, uint seed)
    => EvaluateDirect(occluder, p, n, sun) + ambient * EvaluateAO(occluder, p, n, aoSamples, seed);
```

> **RNG 소비 순서 계약**: `Direct`는 난수를 쓰지 않고 `Indirect`가 `new Rng(seed)`로 새로 시작한다.
> GPU `CSRadiance`도 동일하게 "Direct 무소비 → Indirect `RngInit(_Seeds[i])`" 순서를 지킨다.

---

## 8. 씬 추상화

```csharp
// Runtime/LightmapEvaluate/RadianceScene.cs:11
public interface IRadianceScene
{
    IOccluder Occluder { get; }   // NEE 그림자 레이용
    bool ClosestHit(Vector3 o, Vector3 d, float tmin, float tmax,
                    out Vector3 pos, out Vector3 nrm, out Vector3 albedo);
}
```

### 8.1 단일레벨 — `RadianceScene`

월드 공간 `Tri[]` 기준. 면노멀은 빌드 시 1회 계산. 알베도는 균일 또는 per-tri.

```csharp
// Runtime/LightmapEvaluate/RadianceScene.cs:69
public bool ClosestHit(Vector3 o, Vector3 d, float tmin, float tmax,
                       out Vector3 pos, out Vector3 nrm, out Vector3 albedo)
{
    Hit h = _occ.Intersect(o, d, tmin, tmax);
    if (!h.Valid) { pos = default; nrm = default; albedo = default; return false; }
    pos = o + d * h.T;
    Vector3 fn = _faceN[h.TriIndex];
    if (Vector3.Dot(fn, d) > 0f) fn = -fn;      // 레이를 향하도록 flip
    nrm = fn;
    albedo = _albedo != null ? _albedo[h.TriIndex] : _uniform;
    return true;
}
```

### 8.2 2단 인스턴싱 — `InstancedRadianceScene`

```csharp
// Runtime/LightmapEvaluate/InstanceRadianceScene.cs:99
public bool ClosestHit(Vector3 o, Vector3 d, float tmin, float tmax,
                       out Vector3 pos, out Vector3 nrm, out Vector3 albedo)
{
    TwoLevelBVH.InstancedHit h = _bvh.IntersectInstanced(o, d, tmin, tmax);
    if (!h.Valid) { pos = default; nrm = default; albedo = default; return false; }

    pos = o + d * h.T;                                        // T는 변환 불변

    Vector3 localN = _meshFaceN[h.MeshIndex][h.MeshTriIndex];
    Vector3 wn     = _bvh.TransformNormalToWorld(h.InstanceIndex, localN);   // (M⁻¹)ᵀ
    if (Vector3.Dot(wn, d) > 0f) wn = -wn;
    nrm = wn;

    albedo = LookupAlbedo(h.InstanceIndex, h.MeshIndex, h.MeshTriIndex);
    return true;
}
```

**알베도 모드 2종**:

| 모드 | 소스 | 용도 |
|---|---|---|
| **A** per-mesh | `meshAlbedo[meshIndex]` | 테스트·단순. **현재 GI 베이크가 사용** |
| **B** per-instance·submesh | `instanceSubmeshAlbedo[inst][submesh]` (submesh는 `meshTriSubmesh[mesh][tri]`로 역참조) | 머티리얼 충실도↑ |

모드 B 조회는 전 단계 경계 검사 + `(uint)` 캐스트로 음수 인덱스까지 한 번에 거른다:

```csharp
// Runtime/LightmapEvaluate/InstanceRadianceScene.cs:119
Vector3 LookupAlbedo(int instance, int mesh, int tri)
{
    if (_instanceSubMeshAlbedo != null)
    {
        int sm = 0;
        if (_meshTriSubmesh != null && (uint)mesh < (uint)_meshTriSubmesh.Length)
        {
            int[] triSm = _meshTriSubmesh[mesh];
            if (triSm != null && (uint)tri < (uint)triSm.Length) sm = triSm[tri];
        }
        if ((uint)instance < (uint)_instanceSubMeshAlbedo.Length)
        {
            var arr = _instanceSubMeshAlbedo[instance];
            if (arr != null && (uint)sm < (uint)arr.Length) return arr[sm];
        }
        return Fallback;                                       // (0.5, 0.5, 0.5)
    }
    return (_meshAlbedo != null && (uint)mesh < (uint)_meshAlbedo.Length) ? _meshAlbedo[mesh] : Fallback;
}
```

`_ownsBvh` 플래그로 **BVH 소유권**을 관리한다(외부 주입 시 이중 Dispose 방지).

---

## 9. C2.5 — GI 베이크 연결 (`AtlasApplyDebug`)

`AtlasApplyDebug.BakeMode` 5종 중 `RadianceGI`가 완전 경로추적 경로다.

```
BakeMode:  PerInstanceColor | WorldNormal | Checker | Radiance | RadianceGI
                                            └ Direct+ambient×AO   └ Direct + 경로추적 Indirect
```

### 9.1 씬 구성 — `BuildGiScene`

메시 dedup → 유니크 로컬 `Tri[][]` + per-mesh 알베도 + 인스턴스 → `TwoLevelBVH` → `InstancedRadianceScene`(**모드 A**).

```csharp
// Samples/AtlasApplyDebug.cs:1135  — 변환 없는 로컬 Tri[] (BLAS 입력)
static Tri[] LocalTris(Mesh mesh)
{
    var v = mesh.vertices; var t = mesh.triangles;
    var tris = new Tri[t.Length / 3];
    for (int i = 0; i < tris.Length; i++)
        tris[i] = new Tri { V0 = v[t[i*3]], V1 = v[t[i*3+1]], V2 = v[t[i*3+2]] };
    return tris;
}
```

알베도 수집 — 머티리얼에서 읽고 **Linear 변환 + 클램프**:

```csharp
// Samples/AtlasApplyDebug.cs:1123
Vector3 ReadAlbedo(MeshFilter mf)
{
    var m = mf.GetComponent<MeshRenderer>()?.sharedMaterial;
    Color col = defaultAlbedo;
    if      (m != null && m.HasProperty("_BaseColor")) col = m.GetColor("_BaseColor");  // URP Lit
    else if (m != null && m.HasProperty("_Color"))     col = m.GetColor("_Color");      // 레거시
    Color lin = col.linear;
    return new Vector3(Mathf.Clamp01(lin.r), Mathf.Clamp01(lin.g), Mathf.Clamp01(lin.b));
}
```

`Radiance` 모드(레거시)는 월드 삼각형 단일 BVH를 쓴다:

```csharp
// Samples/AtlasApplyDebug.cs:1146
static Tri[] BuildWorldTris(MeshFilter[] filters)   // 전 메시 삼각형을 월드로 평탄화
```

### 9.2 Occluder / Receiver 분리 (v14.11)

지형 QuadTree 기획의 개념검증. **`occluders` 배열은 차폐 씬에만 참여**하고 베이크·머티리얼 스왑에서는 제외된다.

```csharp
// Samples/AtlasApplyDebug.cs:28
public MeshFilter[] occluders;    // occluder-only
```

- `ResolveTargets()` — 반환 전 `occluders`를 receiver 집합에서 **차집합 제외**(HashSet).
  자식 폴백 시 occluder가 receiver로 오분류되는 사고를 막는다. null/빈 배열이면 no-op → **기존 거동 비트동일**.
- `ResolveOccluderUnion(receivers)` — `receivers ∪ occluders` → `BuildWorldTris` / `BuildGiScene` 입력.
- 할당·베이크 루프·머티리얼 스왑 배열은 **receiver만** 사용 → 무변경.

검증(에디터): receiver 플레인 + occluder 나무 → `Bake & Apply`(RadianceGI) →
플레인 라이트맵에 **나무 그림자가 텍셀 단위로 구워지고**, **나무 자체는 원본 머티리얼 유지**(안 구워짐).

### 9.3 시드 규약

```
seed_i = seed + li * 2654435761u        // li = lumel(텍셀) 인덱스
```

`BlitRegion` / `RadianceDiffTest` / `BakeGiLumelsBurst` / `BakeGiLumelsGpu` 가 **전부 이 식을 공유**한다.
→ 백엔드 교차검증이 텍셀 단위로 성립하는 근거.

### 9.4 백엔드 토글

```csharp
// Samples/AtlasApplyDebug.cs:107
public enum RadianceBackend { CPU, Burst, Gpu }
public RadianceBackend radianceBackend = RadianceBackend.CPU;
```

호출부는 `_gpuReady ? GPU : (_burstReady ? Burst : CPU)` 폴백 체인.
자세한 내용은 [06](06_G0-G3_Burst_백엔드.md) / [07](07_G4-G6_GPU_Compute.md).

---

## 10. 검증

### `RadianceIndirectTests` ([`Tests/RadianceIndirectTests.cs`](../../Tests/RadianceIndirectTests.cs), 138줄)

| # | 케이스 | 기대 |
|---|---|---|
| a | 빈 씬 + 균일 하늘 L | `E ≈ π·L` (π 규약 검증) |
| b | 하늘 0 | `E = 0` |
| c | 닫힌 흑박스 | `E = 0` |
| d | 무광원 | `E = 0` (에너지 보존) |
| e | RR on/off | 기댓값 동일 (**무편향**) |

### `LightmapEvaluateTests` ([`Tests/LightmapEvaluateTests.cs`](../../Tests/LightmapEvaluateTests.cs), 170줄)

`RayTri`(히트/미스/tmax컬/평행/백페이스) · `BruteForce`(최근접·차폐 maxDist) ·
`CosineHemisphere`(상반구 · `E[cosθ] ≈ 2/3`) · `AO`(0/1/결정성) · `Direct`(그림자/백페이스) · Radiance 합성.

### `InstancedSceneModeBTests` ([`Tests/InstancedSceneModeBTests.cs`](../../Tests/InstancedSceneModeBTests.cs), 225줄)

모드 B(인스턴스·submesh) 알베도 ≡ per-tri ground truth + 경계 폴백.

---

## 11. 문서 ↔ 트리 차이 ⚠

| 항목 | v14 문서 | 실제 트리 |
|---|---|---|
| **`SceneRadianceBuilder.cs`** | "✅ 씬 월드 삼각형 + per-submesh 상수 알베도(sRGB→Linear, 폴백 0.5)" | **빈 파일(2줄, `using UnityEngine;` 만).** 해당 기능은 `AtlasApplyDebug`의 `BuildWorldTris` / `LocalTris` / `ReadAlbedo` / `LinColor` 로 구현돼 있다 |
| `ILight` 인터페이스 | ⑪에 언급 | 미도입 (`DirectionalLight` struct + `ISky`) |
| GI 알베도 모드 | 모드 A 사용 | 동일 (모드 B로 전환은 미착수 — 개발문서 §10-5 낮은 우선순위) |

> `SceneRadianceBuilder.cs` 는 **삭제 또는 실제 구현 이관** 대상이다.
> 현재는 빈 파일이 트리에 남아 있어 문서와 불일치를 만든다.
