using System;
using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// L2 — 로컬 광원 SH 직접광 투영(<see cref="LightSampling.ProjectSH{TQ}"/>) 검증, 순수 C#(헤드리스, BruteForce 차폐).
    ///  A) Point: 투영 ≡ Accumulate(l, E⊥, 1)(비트 동일) · 가림판 → 0 · 그림자 끔 = 무차폐 · 스포트 원뿔 밖 0.
    ///  B) Rect: 투영 계수 ≈ 독립 격자 적분 ∫ L·Y(ω)·cosθL/d² dA (600² 중점) — 9계수 최대 오차 1%·c0.
    ///  C) Disc: 같은 비교(극좌표 격자 적분).
    ///  D) 입체각 상한: 아주 가까운 큰 면광원 1샘플 → c0 = L·k0·2π · 방출면 뒤 0.
    ///  E) 규약 일치: 넓은 면광원의 SH 평가 조도 E_sh(n) ≈ 텍스처 경로 Irradiance(n)(L2 대역 근사 — 여러 노멀 15% 이내) ·
    ///     반경 있는 Point 의 위상(프로브 인덱스) 바뀌어도 c0 거의 같음 · AddDirectSH 는 Direct 플래그 없는 광원 무시.
    /// 호출: Debug.Log(SHLightProjectionTests.RunAll());
    /// </summary>
    public static class SHLightProjectionTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== L2 로컬 광원 SH 직접광 투영 ===");
            int pass = 0, total = 0;
            TestPoint(sb, ref pass, ref total);
            TestRect(sb, ref pass, ref total);
            TestDisc(sb, ref pass, ref total);
            TestCap(sb, ref pass, ref total);
            TestConvention(sb, ref pass, ref total);
            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        static readonly Vector3 White = new Vector3(1f, 1f, 1f);
        static readonly BruteForceOccluder Empty = new BruteForceOccluder(new Tri[0]);

        static SH9 Project(in BakeLight L, IOccluder occ, Vector3 p, int samples, float rot = 0.3f)
        {
            var sh = new SH9();
            var q = new OccluderQuery(occ ?? Empty);
            LightSampling.ProjectSH(ref sh, L, ref q, p, samples, rot);
            return sh;
        }

        static Vector3[] C(SH9 s) => new[] { s.c0, s.c1, s.c2, s.c3, s.c4, s.c5, s.c6, s.c7, s.c8 };

        static bool Same(SH9 a, SH9 b)
        {
            var x = C(a); var y = C(b);
            for (int i = 0; i < 9; i++) if (x[i].x != y[i].x || x[i].y != y[i].y || x[i].z != y[i].z) return false;
            return true;
        }

        static bool IsZero(SH9 a) { foreach (var v in C(a)) if (v.x != 0f || v.y != 0f || v.z != 0f) return false; return true; }

        // 9계수 최대 오차 / |c0|
        static double RelErr(SH9 a, SH9 refv)
        {
            var x = C(a); var y = C(refv);
            double m = 0, c0 = Math.Abs(y[0].x);
            for (int i = 0; i < 9; i++) m = Math.Max(m, Math.Abs(x[i].x - y[i].x));
            return m / Math.Max(1e-12, c0);
        }

        static Tri[] Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            => new[] { new Tri { V0 = a, V1 = b, V2 = c }, new Tri { V0 = a, V1 = c, V2 = d } };

        // ── A) Point ──
        static void TestPoint(StringBuilder sb, ref int pass, ref int total)
        {
            var P = BakeLight.Point(new Vector3(0.7f, 2f, -0.4f), new Vector3(1f, 0.8f, 0.6f), 3f, range: 6f);
            var p = new Vector3(0.1f, 0.5f, 0.2f);
            Vector3 toL = P.Position - p;
            float d2 = Vector3.Dot(toL, toL);
            Vector3 l = toL / Mathf.Sqrt(d2);
            var manual = new SH9();
            manual.Accumulate(l, P.Color * P.Intensity * LightSampling.Attenuation(P, d2, l), 1f);
            bool ok = Same(Project(P, null, p, 1), manual);

            var occ = new BruteForceOccluder(Quad(new Vector3(-5, 1.2f, -5), new Vector3(-5, 1.2f, 5), new Vector3(5, 1.2f, 5), new Vector3(5, 1.2f, -5)));
            ok &= IsZero(Project(P, occ, p, 1));
            var noShadow = P; noShadow.Flags = BakeLight.FlagDirect;
            ok &= Same(Project(noShadow, occ, p, 1), manual);
            var S = BakeLight.Spot(P.Position, Vector3.up, White, 3f, 6f, 40f, 20f);   // 위를 비춤 → 아래 프로브는 원뿔 밖
            ok &= IsZero(Project(S, null, p, 1));
            Check(sb, ref pass, ref total, "A: Point 투영 ≡ Accumulate(l, E⊥, 1)(비트 동일) · 가림 0 · 그림자 끔 = 무차폐 · 스포트 원뿔 밖 0", ok);
        }

        // ── B) Rect vs 격자 적분 ──
        static void TestRect(StringBuilder sb, ref int pass, ref int total)
        {
            var R = BakeLight.Rect(new Vector3(0.3f, 1.2f, -0.2f), new Vector3(0.2f, -1f, 0.1f).normalized,
                                   Vector3.Cross(Vector3.forward, new Vector3(0.2f, -1f, 0.1f).normalized).normalized,
                                   Vector3.Cross(new Vector3(0.2f, -1f, 0.1f).normalized, Vector3.Cross(Vector3.forward, new Vector3(0.2f, -1f, 0.1f).normalized).normalized),
                                   1.6f, 0.9f, White, 2f, 4096);
            var p = new Vector3(-0.2f, 0f, 0.1f);
            var sh = Project(R, null, p, 4096, 0.41f);
            var refv = new SH9();
            const int G = 600;
            float dA = R.Width * R.Height / (G * G);
            for (int i = 0; i < G; i++)
                for (int j = 0; j < G; j++)
                {
                    float x = ((i + 0.5f) / G - 0.5f) * R.Width, y = ((j + 0.5f) / G - 0.5f) * R.Height;
                    Vector3 q = R.Position + R.Right * x + R.Up * y, w = q - p;
                    float dd = Vector3.Dot(w, w), dist = Mathf.Sqrt(dd);
                    Vector3 dir = w / dist;
                    float cosL = -Vector3.Dot(R.Direction, dir);
                    if (cosL > 0f) refv.Accumulate(dir, R.Color * R.Intensity, dA * cosL / dd);
                }
            double err = RelErr(sh, refv);
            Check(sb, ref pass, ref total, $"B: Rect 투영 ≈ 격자 적분(600²) — 9계수 최대 오차 {err:P2}·c0 (c0 {sh.c0.x:F4} vs {refv.c0.x:F4})", err < 0.01);
        }

        // ── C) Disc vs 극좌표 적분 ──
        static void TestDisc(StringBuilder sb, ref int pass, ref int total)
        {
            var fwd = new Vector3(-0.3f, -1f, 0.2f).normalized;
            var right = Vector3.Cross(Vector3.forward, fwd).normalized;
            var up = Vector3.Cross(fwd, right);
            var D = BakeLight.Disc(new Vector3(0.5f, 1.5f, 0.3f), fwd, right, up, 0.8f, White, 1.5f, 2048);
            var p = Vector3.zero;
            var sh = Project(D, null, p, 2048, 0.77f);
            var refv = new SH9();
            const int NR = 400, NP = 800;
            for (int i = 0; i < NR; i++)
                for (int j = 0; j < NP; j++)
                {
                    float r0 = (float)i / NR * D.Radius, r1 = (float)(i + 1) / NR * D.Radius, rm = 0.5f * (r0 + r1);
                    float ph = (j + 0.5f) / NP * 2f * Mathf.PI;
                    float dA = 0.5f * (r1 * r1 - r0 * r0) * (2f * Mathf.PI / NP);
                    Vector3 q = D.Position + D.Right * (rm * Mathf.Cos(ph)) + D.Up * (rm * Mathf.Sin(ph)), w = q - p;
                    float dd = Vector3.Dot(w, w);
                    Vector3 dir = w / Mathf.Sqrt(dd);
                    float cosL = -Vector3.Dot(D.Direction, dir);
                    if (cosL > 0f) refv.Accumulate(dir, D.Color * D.Intensity, dA * cosL / dd);
                }
            double err = RelErr(sh, refv);
            Check(sb, ref pass, ref total, $"C: Disc 투영 ≈ 극좌표 적분 — 9계수 최대 오차 {err:P2}·c0", err < 0.01);
        }

        // ── D) 입체각 상한 · 방출면 뒤 ──
        static void TestCap(StringBuilder sb, ref int pass, ref int total)
        {
            var big = BakeLight.Rect(new Vector3(0, 0.01f, 0), Vector3.down, Vector3.right, Vector3.forward, 50f, 50f, White, 1f, 1);
            var sh = Project(big, null, Vector3.zero, 1);
            double expect = 0.2820948 * 2 * Math.PI;   // L·k0·2π (중심 방향 하나에 2π 전부)
            bool okCap = Math.Abs(sh.c0.x - expect) < 1e-4;
            bool behind = IsZero(Project(big, null, new Vector3(0, 1f, 0), 16));
            Check(sb, ref pass, ref total, $"D: 입체각 상한 2π — c0 {sh.c0.x:F5} = L·k0·2π {expect:F5} · 방출면 뒤 0", okCap && behind);
        }

        // ── E) 규약 일치 · 위상 · Direct 플래그 ──
        static void TestConvention(StringBuilder sb, ref int pass, ref int total)
        {
            // 넓은 면광원(저주파) — SH 평가 조도 vs 텍스처 경로 조도
            var R = BakeLight.Rect(new Vector3(0, 1f, 0), Vector3.down, Vector3.right, Vector3.forward, 3f, 3f, White, 1f, 4096);
            var sh = Project(R, null, Vector3.zero, 4096, 0.2f);
            double worst = 0;
            foreach (var n in new[] { Vector3.up, new Vector3(0.5f, 1f, 0).normalized, new Vector3(0, 1f, -0.8f).normalized, new Vector3(1f, 0.4f, 0.3f).normalized })
            {
                var q = new OccluderQuery(Empty);
                double eTex = LightSampling.Irradiance(R, ref q, Vector3.zero, n, 4096, 0.2f).x;
                double eSh = sh.Evaluate(n).x;
                worst = Math.Max(worst, Math.Abs(eSh - eTex) / Math.Max(1e-6, eTex));
            }
            bool okConv = worst < 0.15;

            // 반경 Point: 프로브 인덱스(위상)만 바뀌면 c0 거의 같음, 같은 인덱스면 비트 동일
            var soft = BakeLight.Point(new Vector3(0, 2, 0), White, 3f, radius: 0.4f, samples: 16);
            var a = new SH9(); var b = new SH9(); var c = new SH9();
            var qq = new OccluderQuery(Empty);
            var lights = new[] { soft };
            LightSampling.AddDirectSH(ref a, lights, ref qq, Vector3.zero, 3);
            LightSampling.AddDirectSH(ref b, lights, ref qq, Vector3.zero, 3);
            LightSampling.AddDirectSH(ref c, lights, ref qq, Vector3.zero, 11);
            bool okRot = Same(a, b) && Math.Abs(a.c0.x - c.c0.x) < 1e-3 * a.c0.x && !Same(a, c);

            // Direct 플래그 없는 광원은 직접광 투영에서 제외
            var mixed = soft; mixed.Flags = BakeLight.FlagShadows;
            var m = new SH9();
            LightSampling.AddDirectSH(ref m, new[] { mixed }, ref qq, Vector3.zero, 0);
            Check(sb, ref pass, ref total, $"E: 넓은 면광원 SH 평가 ≈ 텍스처 조도(최대 {worst:P1}, L2 근사) · 위상 다르면 c0 ≈ 같음·같으면 비트 동일 · Direct 없음 제외",
                  okConv && okRot && IsZero(m));
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
