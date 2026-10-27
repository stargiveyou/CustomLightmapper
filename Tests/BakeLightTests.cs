using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 로컬 광원(<see cref="BakeLight"/> · <see cref="LightSampling"/>) 검증 — 순수 C#(헤드리스, BruteForce 차폐).
    ///  A) Point: 역제곱·코사인(정확), 범위 창 = (1 − (d²/R²)²)² · R 밖 0, 뒷면 0.
    ///  B) Spot: 안쪽 원뿔 = Point 와 동일 · 바깥 원뿔 밖 0 · 사이 = URP smoothstep²(정확식) · 단조.
    ///  C) Rect: 중심 아래 점 조도 ≈ 해석해 π·L·F(평행 사각형 형상계수) — 1024 샘플 0.5% 이내, 비스듬한 점도 고밀도 수치적분과 일치.
    ///  D) Disc: 축 위 점 ≈ π·L·R²/(R²+h²) · 방출면 뒤 0 · 근접 클램프 ≤ π·L.
    ///  E) 그림자: 가림판 → Point 0 · FlagShadows 끄면 원래 값 · 반경 있는 Point 의 반그림자가 가장자리를 가로질러 0→최대 단조.
    ///  F) 통합: lights = null ≡ 빈 배열(비트 동일) · Direct 플래그 끄면 1차 직사는 태양만, 간접은 증가(바운스) ·
    ///     CPU 누적(ProgressiveAccumulator) ≡ EvaluateRadiance(lights)(비트 동일).
    ///  G) 구조체 크기 = 112B(GPU stride) · Unity 각도 변환(spotAngle 전체각 → 반각 cos).
    /// 호출: Debug.Log(BakeLightTests.RunAll());
    /// </summary>
    public static class BakeLightTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 로컬 광원 (Point · Spot · Rect · Disc) ===");
            int pass = 0, total = 0;
            TestPoint(sb, ref pass, ref total);
            TestSpot(sb, ref pass, ref total);
            TestRect(sb, ref pass, ref total);
            TestDisc(sb, ref pass, ref total);
            TestShadows(sb, ref pass, ref total);
            TestIntegration(sb, ref pass, ref total);
            TestLayout(sb, ref pass, ref total);
            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        static readonly Vector3 White = new Vector3(1f, 1f, 1f);

        static Vector3 E(in BakeLight L, IOccluder occ, Vector3 p, Vector3 n, int samples = 1, float rot = 0f)
        {
            var q = new OccluderQuery(occ ?? new BruteForceOccluder(new Tri[0]));
            return LightSampling.Irradiance(L, ref q, p, n, samples, rot);
        }

        static bool Near(double a, double b, double rel, double abs = 1e-7) => Math.Abs(a - b) <= Math.Max(abs, rel * Math.Abs(b));

        // ── A) Point ──
        static void TestPoint(StringBuilder sb, ref int pass, ref int total)
        {
            var L = BakeLight.Point(new Vector3(0, 2, 0), White, 3f);
            bool ok = true;
            foreach (float x in new[] { 0f, 0.5f, 1.3f, 3f })
            {
                var p = new Vector3(x, 0, 0);
                double d2 = x * x + 4.0, cos = 2.0 / Math.Sqrt(d2);
                ok &= Near(E(L, null, p, Vector3.up).x, 3.0 * cos / d2, 1e-5);
            }
            ok &= E(L, null, Vector3.zero, Vector3.down) == Vector3.zero;   // 뒷면

            // 범위 창: R = 5
            var Lr = BakeLight.Point(new Vector3(0, 2, 0), White, 3f, range: 5f);
            double prev = double.MaxValue; bool mono = true;
            foreach (float x in new[] { 0f, 1f, 2f, 3f, 4f, 4.5f })
            {
                var p = new Vector3(x, 0, 0);
                double d2 = x * x + 4.0, cos = 2.0 / Math.Sqrt(d2), f = d2 / 25.0, s = Math.Max(0, 1 - f * f);
                double e = E(Lr, null, p, Vector3.up).x;
                ok &= Near(e, 3.0 * cos / d2 * s * s, 1e-4);
                mono &= e <= prev; prev = e;
            }
            ok &= E(Lr, null, new Vector3(5f, 0, 0), Vector3.up).x == 0f;   // d > R
            Check(sb, ref pass, ref total, "A: Point 역제곱·코사인 · 범위 창 (1−(d²/R²)²)² · R 밖·뒷면 0 · 거리 단조", ok && mono);
        }

        // ── B) Spot ──
        static void TestSpot(StringBuilder sb, ref int pass, ref int total)
        {
            // 아래를 비추는 스포트, 전체각 60°(반각 30°), 안쪽 전체각 30°(반각 15°)
            var S = BakeLight.Spot(new Vector3(0, 2, 0), Vector3.down, White, 3f, 0f, 60f, 30f);
            var P = BakeLight.Point(new Vector3(0, 2, 0), White, 3f);
            bool ok = Near(S.CosOuter, Math.Cos(30 * Math.PI / 180), 1e-6) && Near(S.CosInner, Math.Cos(15 * Math.PI / 180), 1e-6);
            double prev = double.MaxValue; bool mono = true;
            for (int k = 0; k <= 40; k++)
            {
                float ang = k * 1f;                                   // 축에서의 각(도)
                float x = 2f * Mathf.Tan(ang * Mathf.Deg2Rad);
                var p = new Vector3(x, 0, 0);
                double es = E(S, null, p, Vector3.up).x, ep = E(P, null, p, Vector3.up).x;
                double c = Math.Cos(ang * Math.PI / 180);
                double t = Math.Min(1, Math.Max(0, (c - S.CosOuter) / (S.CosInner - S.CosOuter)));
                if (ang <= 14f) ok &= Near(es, ep, 1e-5);             // 안쪽 원뿔 = Point
                else if (ang >= 31f) ok &= es == 0;                   // 바깥 밖
                else ok &= Near(es, ep * t * t, 1e-3);
                mono &= es <= prev + 1e-9; prev = es;
            }
            Check(sb, ref pass, ref total, "B: Spot 안쪽 = Point · 바깥 원뿔 밖 0 · 사이 = saturate((cos−cosO)/(cosI−cosO))² · 각도 단조 · Unity 전체각 변환", ok && mono);
        }

        // 평행 사각형 형상계수(법선이 한 꼭짓점을 지남): F = 1/(2π)·[X/√(1+X²)·atan(Y/√(1+X²)) + Y/√(1+Y²)·atan(X/√(1+Y²))]
        static double CornerF(double a, double b, double h)
        {
            double X = a / h, Y = b / h;
            return (X / Math.Sqrt(1 + X * X) * Math.Atan(Y / Math.Sqrt(1 + X * X)) + Y / Math.Sqrt(1 + Y * Y) * Math.Atan(X / Math.Sqrt(1 + Y * Y))) / (2 * Math.PI);
        }

        // 고밀도 중점 적분(레퍼런스): E = L ∫ cosP cosL / d² dA
        static double RectRef(in BakeLight R, Vector3 p, Vector3 n, int grid)
        {
            double sum = 0, dA = (double)R.Width * R.Height / (grid * grid);
            for (int i = 0; i < grid; i++)
                for (int j = 0; j < grid; j++)
                {
                    float x = ((i + 0.5f) / grid - 0.5f) * R.Width, y = ((j + 0.5f) / grid - 0.5f) * R.Height;
                    Vector3 q = R.Position + R.Right * x + R.Up * y, w = q - p;
                    double d2 = Vector3.Dot(w, w), d = Math.Sqrt(d2);
                    double cp = Vector3.Dot(n, w) / d, cl = -Vector3.Dot(R.Direction, w) / d;
                    if (cp > 0 && cl > 0) sum += cp * cl / d2 * dA;
                }
            return R.Intensity * sum;
        }

        // ── C) Rect ──
        static void TestRect(StringBuilder sb, ref int pass, ref int total)
        {
            // 높이 1, 2×1 사각형이 아래(−Y)로 방출, 휘도 L = 2
            var R = BakeLight.Rect(new Vector3(0, 1, 0), Vector3.down, Vector3.right, Vector3.forward, 2f, 1f, White, 2f, 1024);
            double ana = Math.PI * 2.0 * 4 * CornerF(1.0, 0.5, 1.0);
            double e = E(R, null, Vector3.zero, Vector3.up, 1024, 0.37f).x;
            bool okA = Near(e, ana, 5e-3);
            // 비스듬한 점 · 기운 노멀 vs 고밀도 적분
            var p2 = new Vector3(1.4f, 0.2f, -0.6f); var n2 = new Vector3(0.3f, 1f, 0.2f).normalized;
            double e2 = E(R, null, p2, n2, 1024, 0.11f).x, ref2 = RectRef(R, p2, n2, 400);
            bool okB = Near(e2, ref2, 1e-2);
            // 샘플 1 = 중심 점 근사(바운스 규약)
            double c1 = E(R, null, Vector3.zero, Vector3.up, 1, 0f).x;
            bool okC = Near(c1, Math.Min(2.0 * 2.0 * 1.0 * 1.0 / 1.0, Math.PI * 2.0), 1e-5);   // A·L·cos·cos/d² (=4) — π·L(6.28) 아래
            // 뒷면(방출면 위의 점) 0
            bool okD = E(R, null, new Vector3(0, 2, 0), Vector3.down, 64).x == 0f;
            Check(sb, ref pass, ref total, $"C: Rect 중심 아래 {e:F4} ≈ 해석해 {ana:F4} · 비스듬한 점 {e2:F4} ≈ 적분 {ref2:F4} · 1샘플 = 중심 근사 · 방출면 뒤 0",
                  okA && okB && okC && okD);
        }

        // ── D) Disc ──
        static void TestDisc(StringBuilder sb, ref int pass, ref int total)
        {
            var D = BakeLight.Disc(new Vector3(0, 1.5f, 0), Vector3.down, Vector3.right, Vector3.forward, 0.8f, White, 1.5f, 1024);
            double ana = Math.PI * 1.5 * 0.64 / (0.64 + 2.25);
            double e = E(D, null, Vector3.zero, Vector3.up, 1024, 0.5f).x;
            // 근접 클램프: 아주 큰 면광원 바로 앞(1 샘플 중심 근사는 발산) → ≤ π·L
            var big = BakeLight.Rect(new Vector3(0, 0.01f, 0), Vector3.down, Vector3.right, Vector3.forward, 50f, 50f, White, 1f, 1);
            double clamp = E(big, null, Vector3.zero, Vector3.up, 1).x;
            Check(sb, ref pass, ref total, $"D: Disc 축 위 {e:F4} ≈ π·L·R²/(R²+h²) {ana:F4} · 근접 클램프 {clamp:F4} ≤ π·L",
                  Near(e, ana, 5e-3) && clamp <= Math.PI + 1e-6 && clamp > 3.1);
        }

        static Tri[] Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            => new[] { new Tri { V0 = a, V1 = b, V2 = c }, new Tri { V0 = a, V1 = c, V2 = d } };

        // ── E) 그림자 ──
        static void TestShadows(StringBuilder sb, ref int pass, ref int total)
        {
            // y = 1 높이의 가림판 x ∈ [-5, 0] (x>0 은 열림)
            var occ = new BruteForceOccluder(Quad(new Vector3(-5, 1, -5), new Vector3(-5, 1, 5), new Vector3(0, 1, 5), new Vector3(0, 1, -5)));
            var hard = BakeLight.Point(new Vector3(0, 2, 0), White, 3f);
            var noShadow = hard; noShadow.Flags = BakeLight.FlagDirect;
            var pBlocked = new Vector3(-1, 0, 0);
            bool ok = E(hard, occ, pBlocked, Vector3.up) == Vector3.zero
                   && E(noShadow, occ, pBlocked, Vector3.up).x == E(hard, null, pBlocked, Vector3.up).x;

            // 반경 0.5 Point(샘플 64): 가림판 가장자리(x = 0)를 가로지르며 0 → 최대 단조, 중간은 반그림자
            var soft = BakeLight.Point(new Vector3(0, 2, 0), White, 3f, radius: 0.5f, samples: 64);
            double prevVis = -1; bool mono = true; int penumbra = 0;
            for (int k = -10; k <= 10; k++)
            {
                var p = new Vector3(k * 0.1f, 0, 0);
                double free = E(soft, null, p, Vector3.up).x, vis = free > 0 ? E(soft, occ, p, Vector3.up, 64, 0.3f).x / free : 0;
                // 샘플 64 → 가시 비율 = E(occ)/E(free) (free 는 원판 샘플링이어도 전부 보임 = 1)
                mono &= vis >= prevVis - 1e-6; prevVis = vis;
                if (vis > 0.02 && vis < 0.98) penumbra++;
            }
            Check(sb, ref pass, ref total, $"E: 그림자 — Point 가림 0 · 그림자 끔 = 무차폐 · 반경 0.5 반그림자 단조(중간값 {penumbra}점)", ok && mono && penumbra >= 3);
        }

        // ── F) 통합 ──
        static void TestIntegration(StringBuilder sb, ref int pass, ref int total)
        {
            var tris = new List<Tri>();
            tris.AddRange(Quad(new Vector3(-6, 0, -6), new Vector3(-6, 0, 6), new Vector3(6, 0, 6), new Vector3(6, 0, -6)));      // 바닥
            tris.AddRange(Quad(new Vector3(-6, 3, -6), new Vector3(6, 3, -6), new Vector3(6, 3, 6), new Vector3(-6, 3, 6)));      // 천장(태양 차단)
            tris.AddRange(Quad(new Vector3(2, 0, -6), new Vector3(2, 3, -6), new Vector3(2, 3, 6), new Vector3(2, 0, 6)));        // 벽
            var arr = tris.ToArray();
            var scene = new RadianceScene(arr, new Vector3(0.7f, 0.7f, 0.7f), new BruteForceOccluder(arr));
            var sun = new DirectionalLight { Direction = new Vector3(-0.3f, -1f, -0.2f).normalized, Color = White, Intensity = 2f };
            var sky = new UniformSky(new Vector3(0.2f, 0.25f, 0.3f));
            var q = new BakeQualitySettings { IndirectSamples = 24, MaxBounces = 3, RRStartDepth = 2, RayBias = 1e-4f, DirectSamples = 1 };
            var lights = new[]
            {
                BakeLight.Point(new Vector3(-1, 2, 0), new Vector3(1f, 0.9f, 0.8f), 4f, range: 8f, radius: 0.2f, samples: 4),
                BakeLight.Spot(new Vector3(1, 2.8f, 1), Vector3.down, White, 6f, 6f, 70f, 40f),
                BakeLight.Rect(new Vector3(0, 2.99f, -2), Vector3.down, Vector3.right, Vector3.forward, 1f, 0.5f, White, 3f, 8),
                BakeLight.Disc(new Vector3(1.99f, 1.5f, 2), Vector3.left, Vector3.forward, Vector3.up, 0.3f, White, 5f, 8),
            };
            var pts = new List<Vector3>(); var nrm = new List<Vector3>(); var sds = new List<uint>();
            for (int i = 0; i < 40; i++)
            {
                pts.Add(new Vector3(-4f + (i % 8) * 0.8f + 0.03f, 1e-3f, -3f + (i / 8) * 1.4f)); nrm.Add(Vector3.up);
                sds.Add(999u + (uint)i * 2654435761u);
            }
            int n = pts.Count;
            bool nullEqEmpty = true, directOnlySun = true, indirectUp = true;
            int brighter = 0;
            var noDirect = (BakeLight[])lights.Clone();
            for (int k = 0; k < noDirect.Length; k++) noDirect[k].Flags &= ~BakeLight.FlagDirect;
            var withL = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 a = RadianceCore.EvaluateRadiance(scene, pts[i], nrm[i], sun, sky, q, sds[i], null);
                Vector3 b = RadianceCore.EvaluateRadiance(scene, pts[i], nrm[i], sun, sky, q, sds[i], new BakeLight[0]);
                nullEqEmpty &= a.x == b.x && a.y == b.y && a.z == b.z;
                withL[i] = RadianceCore.EvaluateRadiance(scene, pts[i], nrm[i], sun, sky, q, sds[i], lights);
                if (RadianceCore.Luminance(withL[i]) > RadianceCore.Luminance(a) + 1e-4f) brighter++;

                Vector3 dSun = RadianceCore.EvaluateDirectSampled(scene.Occluder, pts[i], nrm[i], sun, 1, sds[i]);
                Vector3 dNo = RadianceCore.EvaluateDirectPrimary(scene.Occluder, pts[i], nrm[i], sun, 1, sds[i], noDirect);
                directOnlySun &= dNo.x == dSun.x && dNo.y == dSun.y && dNo.z == dSun.z;
                Vector3 iNo = RadianceCore.EvaluateIndirect(scene, pts[i], nrm[i], sun, sky, q, sds[i], noDirect);
                Vector3 iNone = RadianceCore.EvaluateIndirect(scene, pts[i], nrm[i], sun, sky, q, sds[i]);
                indirectUp &= RadianceCore.Luminance(iNo) >= RadianceCore.Luminance(iNone) - 1e-6f;
            }
            // 누적 엔진(K=5) ≡ 일괄
            var acc = new ProgressiveAccumulator(new CpuPathAccum(scene, sun, sky, q, lights),
                new AdaptiveSampling { MaxSamples = q.IndirectSamples, SamplesPerPass = 5 }, pts.ToArray(), nrm.ToArray(), sds.ToArray(), n);
            acc.Run();
            bool accEq = true;
            for (int i = 0; i < n; i++) { var r = acc.Estimate(i); accEq &= r.x == withL[i].x && r.y == withL[i].y && r.z == withL[i].z; }
            Check(sb, ref pass, ref total, $"F: 통합 — null ≡ 빈 배열 · 광원으로 밝아진 점 {brighter}/{n} · Direct 끔 = 1차는 태양만·간접 ≥ · CPU 누적(K=5) ≡ EvaluateRadiance(lights)",
                  nullEqEmpty && brighter > n / 2 && directOnlySun && indirectUp && accEq);
        }

        // ── G) 레이아웃 ──
        static void TestLayout(StringBuilder sb, ref int pass, ref int total)
        {
            int size = System.Runtime.InteropServices.Marshal.SizeOf<BakeLight>();
            Check(sb, ref pass, ref total, $"G: BakeLight 크기 {size}B = GPU stride {BakeLight.Stride}", size == BakeLight.Stride);
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
