using System.Runtime.InteropServices;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    public enum BakeLightType { Point = 0, Spot = 1, Rect = 2, Disc = 3 }

    /// <summary>
    /// 베이크 로컬 광원(Point · Spot · Rect · Disc) — POD. CPU 배열 · Burst NativeArray · GPU StructuredBuffer(<see cref="Stride"/> = 112B,
    /// HLSL <c>GpuLight</c> 와 1:1)가 같은 구조체를 쓴다. 태양(<see cref="DirectionalLight"/>)은 별도 — 이 목록은 로컬 광원만.
    ///
    /// 조도 규약(<see cref="LightSampling"/>):
    ///  · Point/Spot = Color·Intensity·cosθ·감쇠. 감쇠 = 1/max(d², 1e-4) × 범위 창(saturate(1 − (d²/Range²)²)², Range = 0 이면 창 없음)
    ///    × 스포트 각 감쇠(saturate((cos − CosOuter)/(CosInner − CosOuter))²) — URP 실시간 라이트와 같은 식이라 베이크 ≈ 실시간.
    ///    Radius &gt; 0 이고 샘플 &gt; 1 이면 광원 원판(빛 방향에 수직)을 샘플링한 가시도 평균(반그림자).
    ///  · Rect/Disc = 한쪽 면(Direction 쪽)으로 방출하는 램버트 면광원, 방출 휘도 = Color·Intensity.
    ///    E = 면적 · 평균(cosθ_수신·cosθ_광원 / d² · 가시도), 물리 상한 π·휘도로 클램프. 범위 창 없음.
    ///  · 샘플 위치는 RNG 가 아닌 결정적 저불일치 수열(태양 원반과 같은 u1 층화 + 황금비 u2, 텍셀 위상 = 시드 회전) →
    ///    간접광 RNG 스트림을 건드리지 않는다(광원 0개면 기존 결과와 비트 동일).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BakeLight
    {
        public const int Stride = 112;
        /// <summary>1차 직사(라이트맵 텍셀이 광원을 직접 받음)에 포함. 끄면 간접(바운스)만 — Unity Mixed 대응.</summary>
        public const int FlagDirect = 1;
        /// <summary>그림자 레이 사용. 끄면 차폐 무시(Unity Shadow Type = None).</summary>
        public const int FlagShadows = 2;

        public int Type;            // BakeLightType
        public Vector3 Position;
        public Vector3 Direction;   // Spot 축 · 면광원 방출 방향(정규화)
        public Vector3 Right;       // 면광원 가로 축(정규화)
        public Vector3 Up;          // 면광원 세로 축(정규화)
        public Vector3 Color;       // Linear RGB(강도 제외)
        public float Intensity;
        public float Range;         // Point/Spot 범위 창(0 = 없음)
        public float Radius;        // Point/Spot 그림자 반경 · Disc 반지름
        public float Width, Height; // Rect 크기
        public float CosOuter, CosInner;   // Spot 바깥·안쪽 반각 cos
        public int Samples;         // 1차 직사 샘플 수(≥1). 바운스는 항상 1(중심)
        public int Flags;
        public int Pad0, Pad1, Pad2;

        public BakeLightType Kind => (BakeLightType)Type;
        public bool IsArea => Type >= (int)BakeLightType.Rect;

        public static BakeLight Point(Vector3 position, Vector3 color, float intensity, float range = 0f, float radius = 0f, int samples = 1,
                                      int flags = FlagDirect | FlagShadows)
            => new BakeLight
            {
                Type = (int)BakeLightType.Point, Position = position, Direction = Vector3.down, Right = Vector3.right, Up = Vector3.forward,
                Color = color, Intensity = intensity, Range = range, Radius = radius, CosOuter = -1f, CosInner = -1f,
                Samples = Mathf.Max(1, samples), Flags = flags,
            };

        /// <param name="outerAngleDeg">전체 원뿔 각(Unity spotAngle)</param>
        /// <param name="innerAngleDeg">안쪽 전체 각(Unity innerSpotAngle)</param>
        public static BakeLight Spot(Vector3 position, Vector3 direction, Vector3 color, float intensity, float range,
                                     float outerAngleDeg, float innerAngleDeg, float radius = 0f, int samples = 1, int flags = FlagDirect | FlagShadows)
        {
            var l = Point(position, color, intensity, range, radius, samples, flags);
            l.Type = (int)BakeLightType.Spot;
            l.Direction = direction.normalized;
            float outer = Mathf.Clamp(outerAngleDeg, 0.1f, 179.9f), inner = Mathf.Clamp(innerAngleDeg, 0f, outer);
            l.CosOuter = Mathf.Cos(outer * 0.5f * Mathf.Deg2Rad);
            l.CosInner = Mathf.Cos(inner * 0.5f * Mathf.Deg2Rad);
            return l;
        }

        /// <summary>Rect 면광원: forward 쪽으로 방출, 가로 = right·width, 세로 = up·height(중심 기준).</summary>
        public static BakeLight Rect(Vector3 center, Vector3 forward, Vector3 right, Vector3 up, float width, float height,
                                     Vector3 color, float intensity, int samples = 16, int flags = FlagDirect | FlagShadows)
            => new BakeLight
            {
                Type = (int)BakeLightType.Rect, Position = center, Direction = forward.normalized, Right = right.normalized, Up = up.normalized,
                Color = color, Intensity = intensity, Width = Mathf.Max(0f, width), Height = Mathf.Max(0f, height), CosOuter = -1f, CosInner = -1f,
                Samples = Mathf.Max(1, samples), Flags = flags,
            };

        public static BakeLight Disc(Vector3 center, Vector3 forward, Vector3 right, Vector3 up, float radius,
                                     Vector3 color, float intensity, int samples = 16, int flags = FlagDirect | FlagShadows)
        {
            var l = Rect(center, forward, right, up, 0f, 0f, color, intensity, samples, flags);
            l.Type = (int)BakeLightType.Disc;
            l.Radius = Mathf.Max(0f, radius);
            return l;
        }
    }

    /// <summary>그림자 레이 질의(CPU = IOccluder, Burst = BurstScene). 제네릭 struct 제약이라 Burst 에서도 가상 호출 없이 인라인.</summary>
    public interface IShadowQuery
    {
        bool Occluded(Vector3 o, Vector3 d, float maxDist);
    }

    /// <summary>CPU 그림자 질의 — 관리형 차폐자(BVH/BruteForce/TwoLevelBVH) 래퍼.</summary>
    public struct OccluderQuery : IShadowQuery
    {
        public IOccluder Occ;
        public OccluderQuery(IOccluder occ) { Occ = occ; }
        public bool Occluded(Vector3 o, Vector3 d, float maxDist) => Occ.Occluded(o, d, maxDist);
    }

    /// <summary>
    /// 로컬 광원 조도 — CPU·Burst 공용 단일 진실원(HLSL <c>DirectAll</c> 이 같은 식을 미러).
    /// 그림자 레이 원점 o = p + n·1e-3(태양과 같은 바이어스), 길이 = |q − o| − 1e-3(광원 위치·면 바로 앞까지 — 면광원이 천장과 같은 평면이어도 안 막힘).
    /// </summary>
    public static class LightSampling
    {
        public const float GoldenRatioFrac = 0.6180339887498949f;

        /// <summary>점 p(노멀 n)의 조도. samples: 1차 직사 = L.Samples, 바운스 = 1(중심 1발). rot: 텍셀 위상 [0,1).</summary>
        public static Vector3 Irradiance<TQ>(in BakeLight L, ref TQ q, Vector3 p, Vector3 n, int samples, float rot)
            where TQ : struct, IShadowQuery
        {
            if (L.Intensity <= 0f) return Vector3.zero;
            Vector3 o = p + n * 1e-3f;
            bool shadows = (L.Flags & BakeLight.FlagShadows) != 0;

            if (L.Type <= (int)BakeLightType.Spot)
            {
                Vector3 toL = L.Position - p;
                float d2 = Vector3.Dot(toL, toL);
                if (d2 <= 1e-12f) return Vector3.zero;
                float dist = Mathf.Sqrt(d2);
                Vector3 l = toL / dist;
                float ndl = Vector3.Dot(n, l);
                if (ndl <= 0f) return Vector3.zero;
                float atten = Attenuation(L, d2, l);
                if (atten <= 0f) return Vector3.zero;

                int N = (samples > 1 && L.Radius > 0f) ? samples : 1;
                Vector3 t = Vector3.zero, b = Vector3.zero;
                if (N > 1) RadianceCore.SunBasis(l, out t, out b);
                float acc = 0f;
                for (int i = 0; i < N; i++)
                {
                    Vector3 qp = L.Position;
                    if (N > 1)
                    {
                        Disk(i, N, rot, out float x, out float y);
                        qp = L.Position + t * (x * L.Radius) + b * (y * L.Radius);
                    }
                    Vector3 v = qp - o;
                    float dq = v.magnitude;
                    if (dq <= 1e-6f) continue;
                    if (!shadows || !q.Occluded(o, v / dq, dq - 1e-3f)) acc += 1f;
                }
                return L.Color * L.Intensity * (ndl * atten * (acc / N));
            }
            else
            {
                if (Vector3.Dot(L.Direction, p - L.Position) <= 0f) return Vector3.zero;   // 방출면 뒤
                int N = Mathf.Max(1, samples);
                bool rect = L.Type == (int)BakeLightType.Rect;
                float area = rect ? L.Width * L.Height : Mathf.PI * L.Radius * L.Radius;
                if (area <= 0f) return Vector3.zero;
                float acc = 0f;
                for (int i = 0; i < N; i++)
                {
                    float x = 0f, y = 0f;
                    if (N > 1) AreaSample(L, i, N, rot, out x, out y);
                    Vector3 qp = L.Position + L.Right * x + L.Up * y;
                    Vector3 w = qp - p;
                    float d2 = Vector3.Dot(w, w);
                    if (d2 <= 1e-12f) continue;
                    Vector3 wn = w / Mathf.Sqrt(d2);
                    float cosP = Vector3.Dot(n, wn);
                    float cosL = -Vector3.Dot(L.Direction, wn);
                    if (cosP <= 0f || cosL <= 0f) continue;
                    Vector3 v = qp - o;
                    float dq = v.magnitude;
                    if (dq <= 1e-6f) continue;
                    if (shadows && q.Occluded(o, v / dq, dq - 1e-3f)) continue;
                    acc += cosP * cosL / Mathf.Max(d2, 1e-4f);
                }
                return L.Color * L.Intensity * Mathf.Min(area * (acc / N), Mathf.PI);
            }
        }

        /// <summary>Point/Spot 감쇠: 역제곱 × 범위 창 × 스포트 각(URP 식). l = 수신점 → 광원 단위 벡터.</summary>
        public static float Attenuation(in BakeLight L, float d2, Vector3 l)
        {
            float atten = 1f / Mathf.Max(d2, 1e-4f);
            if (L.Range > 0f)
            {
                float f = d2 / (L.Range * L.Range);
                float s = Mathf.Clamp01(1f - f * f);
                atten *= s * s;
            }
            if (L.Type == (int)BakeLightType.Spot)
            {
                float sd = -Vector3.Dot(L.Direction, l);
                float s = Mathf.Clamp01((sd - L.CosOuter) / Mathf.Max(L.CosInner - L.CosOuter, 1e-4f));
                atten *= s * s;
            }
            return atten;
        }

        /// <summary>단위 원판 i/N 번째 점(u1 층화 반지름 √u1 · 황금비 각 + 회전).</summary>
        public static void Disk(int i, int n, float rot, out float x, out float y)
        {
            float u1 = (i + 0.5f) / n;
            float u2 = i * GoldenRatioFrac + rot;
            u2 -= Mathf.Floor(u2);
            float r = Mathf.Sqrt(u1);
            float phi = 2f * Mathf.PI * u2;
            x = r * Mathf.Cos(phi);
            y = r * Mathf.Sin(phi);
        }

        /// <summary>면광원 로컬 좌표(Right·Up 기준) 샘플: Rect = 층화 가로 × 황금비 세로, Disc = 원판 × Radius.</summary>
        public static void AreaSample(in BakeLight L, int i, int n, float rot, out float x, out float y)
        {
            if (L.Type == (int)BakeLightType.Rect)
            {
                float u1 = (i + 0.5f) / n;
                float u2 = i * GoldenRatioFrac + rot;
                u2 -= Mathf.Floor(u2);
                x = (u1 - 0.5f) * L.Width;
                y = (u2 - 0.5f) * L.Height;
            }
            else
            {
                Disk(i, n, rot, out x, out y);
                x *= L.Radius;
                y *= L.Radius;
            }
        }

        /// <summary>면광원 i/N 번째 월드 점(N = 1 이면 중심).</summary>
        public static Vector3 AreaPoint(in BakeLight L, int i, int n, float rot)
        {
            float x = 0f, y = 0f;
            if (n > 1) AreaSample(L, i, n, rot, out x, out y);
            return L.Position + L.Right * x + L.Up * y;
        }

        /// <summary>
        /// SH 프로브 직접광 투영(L2) — SH 는 '입사 복사휘도'를 투영하고 평가 때 코사인 로브로 컨볼루션하므로(SH9 규약):
        ///  · Point/Spot: 방향 l 의 델타 복사휘도, 세기 = 수직 조도 E⊥ = Color·I·감쇠 → c += E⊥·Y(ω_i)/N
        ///    (반경 &gt; 0·샘플 &gt; 1 이면 원판 샘플 방향 ω_i, 가시 샘플만) → 평가 E(n) ≈ E⊥·max(0, n·l) = 텍스처 경로 조도(L2 대역 근사).
        ///  · Rect/Disc: 방출 휘도 Color·I, 샘플 입체각 dΩ_i = (A/N)·cosθ_광원/max(d²,1e-4) → c += L·Y(ω_i)·dΩ_i.
        ///    총 입체각(가시도 무관 기하 합) 상한 2π — 근거리 중심 근사가 발산하지 않게(텍스처 경로의 π·L 클램프에 대응).
        ///  그림자 레이 원점 = p(프로브는 표면 위가 아님), 길이 = |q − p| − 1e-3. Direct 플래그 판정은 호출측.
        /// </summary>
        public static void ProjectSH<TQ>(ref SH9 sh, in BakeLight L, ref TQ q, Vector3 p, int samples, float rot)
            where TQ : struct, IShadowQuery
        {
            if (L.Intensity <= 0f) return;
            bool shadows = (L.Flags & BakeLight.FlagShadows) != 0;

            if (L.Type <= (int)BakeLightType.Spot)
            {
                Vector3 toL = L.Position - p;
                float d2 = Vector3.Dot(toL, toL);
                if (d2 <= 1e-12f) return;
                Vector3 l = toL / Mathf.Sqrt(d2);
                float atten = Attenuation(L, d2, l);
                if (atten <= 0f) return;
                Vector3 e = L.Color * L.Intensity * atten;
                int N = (samples > 1 && L.Radius > 0f) ? samples : 1;
                Vector3 t = Vector3.zero, b = Vector3.zero;
                if (N > 1) RadianceCore.SunBasis(l, out t, out b);
                float w = 1f / N;
                for (int i = 0; i < N; i++)
                {
                    Vector3 qp = L.Position;
                    if (N > 1)
                    {
                        Disk(i, N, rot, out float x, out float y);
                        qp = L.Position + t * (x * L.Radius) + b * (y * L.Radius);
                    }
                    Vector3 v = qp - p;
                    float dq = v.magnitude;
                    if (dq <= 1e-6f) continue;
                    Vector3 dir = v / dq;
                    if (!shadows || !q.Occluded(p, dir, dq - 1e-3f)) sh.Accumulate(dir, e, w);
                }
            }
            else
            {
                if (Vector3.Dot(L.Direction, p - L.Position) <= 0f) return;   // 방출면 뒤
                int N = Mathf.Max(1, samples);
                bool rect = L.Type == (int)BakeLightType.Rect;
                float area = rect ? L.Width * L.Height : Mathf.PI * L.Radius * L.Radius;
                if (area <= 0f) return;

                float gsum = 0f;   // 기하 입체각 합(가시도 무관) → 상한 스케일
                for (int i = 0; i < N; i++)
                {
                    Vector3 w = AreaPoint(L, i, N, rot) - p;
                    float d2 = Vector3.Dot(w, w);
                    if (d2 <= 1e-12f) continue;
                    float cosL = -Vector3.Dot(L.Direction, w / Mathf.Sqrt(d2));
                    if (cosL > 0f) gsum += cosL / Mathf.Max(d2, 1e-4f);
                }
                float scale = area / N;
                if (gsum * scale > 2f * Mathf.PI) scale = 2f * Mathf.PI / gsum;

                Vector3 rad = L.Color * L.Intensity;
                for (int i = 0; i < N; i++)
                {
                    Vector3 w = AreaPoint(L, i, N, rot) - p;
                    float d2 = Vector3.Dot(w, w);
                    if (d2 <= 1e-12f) continue;
                    float dist = Mathf.Sqrt(d2);
                    Vector3 dir = w / dist;
                    float cosL = -Vector3.Dot(L.Direction, dir);
                    if (cosL <= 0f) continue;
                    if (shadows && q.Occluded(p, dir, dist - 1e-3f)) continue;
                    sh.Accumulate(dir, rad, scale * (cosL / Mathf.Max(d2, 1e-4f)));
                }
            }
        }

        /// <summary>SH 직접광: Direct 플래그 광원만, 광원별 Samples, 위상 = SunConeRotation(프로브 인덱스 + 1). 광원 순서대로 더한다(HLSL CSSHDirect 와 같은 순서).</summary>
        public static void AddDirectSH<TQ>(ref SH9 sh, BakeLight[] lights, ref TQ q, Vector3 p, uint probeIndex)
            where TQ : struct, IShadowQuery
        {
            if (lights == null || lights.Length == 0) return;
            float rot = RadianceCore.SunConeRotation(probeIndex + 1u);
            for (int i = 0; i < lights.Length; i++)
                if ((lights[i].Flags & BakeLight.FlagDirect) != 0)
                    ProjectSH(ref sh, lights[i], ref q, p, lights[i].Samples, rot);
        }

        /// <summary>1차 직사: Direct 플래그 광원만, 광원별 Samples, 위상 = SunConeRotation(seed). direct 에 광원 순서대로 더한다(HLSL 과 같은 순서).</summary>
        public static Vector3 AddPrimary<TQ>(Vector3 direct, BakeLight[] lights, ref TQ q, Vector3 p, Vector3 n, uint seed)
            where TQ : struct, IShadowQuery
        {
            if (lights == null || lights.Length == 0) return direct;
            float rot = RadianceCore.SunConeRotation(seed);
            for (int i = 0; i < lights.Length; i++)
                if ((lights[i].Flags & BakeLight.FlagDirect) != 0)
                    direct += Irradiance(lights[i], ref q, p, n, lights[i].Samples, rot);
            return direct;
        }

        /// <summary>바운스 NEE: 전 광원(Direct 플래그 무관), 중심 1발.</summary>
        public static Vector3 AddBounce<TQ>(Vector3 eD, BakeLight[] lights, ref TQ q, Vector3 hp, Vector3 hn)
            where TQ : struct, IShadowQuery
        {
            if (lights == null || lights.Length == 0) return eD;
            for (int i = 0; i < lights.Length; i++)
                eD += Irradiance(lights[i], ref q, hp, hn, 1, 0f);
            return eD;
        }
    }
}
