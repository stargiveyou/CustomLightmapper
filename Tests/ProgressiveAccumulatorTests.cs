using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 적응 샘플링 · 진행형 누적 엔진 검증 — 순수 C#(헤드리스, BruteForce 차폐 씬).
    ///  A) 패스 일정(가짜 백엔드): 패스 수 = ⌈N/K⌉ · 마지막 패스는 남은 만큼 · First 1회 + Next · 활성 목록 오름차순 · Estimate = direct + sum/count.
    ///  B) 비트 동일: CPU 누적(Adaptive=false, K = 1·3·7·N·N+5) 최종값 ≡ RadianceCore.EvaluateRadiance(같은 seed·N spp) —
    ///     RR(바운스 3, RR 2부터)·태양 원반 4발 포함, 성분별 정확 비교.
    ///  C) 적응: 하늘 점(위에 지오메트리 없음 → 경로 전부 미스 → 분산 0)은 MinSamples 에서 멈추고, 상자 근처 점은 더 쏜다 · count ∈ [min, N] ·
    ///     전체 경로 &lt; 상한 · 같은 입력 두 번 = 동일 · 점 집합을 반으로 나눠 따로 돌려도 점별 결과·count 동일(배치 무관).
    ///  D) Converged: 분산 0 → 참 · N&lt;2 → 거짓 · 표준오차 경계 앞뒤 · 어두운 텍셀 하한.
    ///  E) 적응 + 부분 패스: N=20·K=8·min=16 → 8, 16(판정), 20(끝).
    /// 호출: Debug.Log(ProgressiveAccumulatorTests.RunAll());
    /// </summary>
    public static class ProgressiveAccumulatorTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 적응 샘플링 · 진행형 누적 (ProgressiveAccumulator) ===");
            int pass = 0, total = 0;
            TestSchedule(sb, ref pass, ref total);
            TestBitExact(sb, ref pass, ref total);
            TestAdaptive(sb, ref pass, ref total);
            TestConverged(sb, ref pass, ref total);
            TestPartialAdaptive(sb, ref pass, ref total);
            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ── A) 패스 일정 ──
        sealed class FakeBackend : IPathAccumBackend
        {
            public int Firsts, Nexts, N;
            public bool Sorted = true;
            int[] _k;
            public void Bind(Vector3[] p, Vector3[] n, uint[] s, int count) { N = count; _k = new int[count]; }
            static Vector3 F(int i, int k) => new Vector3((i * 7 + k * 3) % 11, (i + 2 * k) % 5, k % 3);
            public void First(int samples, Vector3[] direct, Vector3[] sum, float[] m2)
            {
                Firsts++;
                for (int i = 0; i < N; i++)
                {
                    direct[i] = new Vector3(i, 0, 1);
                    sum[i] = Vector3.zero; m2[i] = 0;
                    for (int q = 0; q < samples; q++) sum[i] += F(i, _k[i]++);
                }
            }
            public void Next(int[] active, int count, int samples, Vector3[] sum, float[] m2)
            {
                Nexts++;
                for (int j = 0; j < count; j++)
                {
                    if (j > 0 && active[j] <= active[j - 1]) Sorted = false;
                    int i = active[j];
                    for (int q = 0; q < samples; q++) sum[i] += F(i, _k[i]++);
                }
            }
            public void Dispose() { }
        }

        static void TestSchedule(StringBuilder sb, ref int pass, ref int total)
        {
            bool ok = true;
            foreach (var (N, K) in new[] { (20, 8), (16, 16), (5, 1), (7, 100), (64, 3) })
            {
                var fb = new FakeBackend();
                var acc = new ProgressiveAccumulator(fb, new AdaptiveSampling { MaxSamples = N, SamplesPerPass = K }, new Vector3[9], new Vector3[9], new uint[9], 9);
                var passSizes = new List<int>();
                int before = 0;
                while (true)
                {
                    bool more = acc.Step();
                    passSizes.Add(acc.SamplesDone - before);
                    before = acc.SamplesDone;
                    if (!more) break;
                }
                int k = Math.Min(K, N);
                int expectPasses = (N + k - 1) / k;
                ok &= acc.Passes == expectPasses && passSizes.Count == expectPasses && fb.Firsts == 1 && fb.Nexts == expectPasses - 1;
                ok &= passSizes[passSizes.Count - 1] == N - k * (expectPasses - 1) && acc.Finished && !acc.Step();
                for (int i = 0; i < 9; i++)
                {
                    ok &= acc.Count[i] == N;
                    // 기대값 직접 계산: direct + Σ F / N
                    Vector3 s = Vector3.zero;
                    for (int q = 0; q < N; q++) s += new Vector3((i * 7 + q * 3) % 11, (i + 2 * q) % 5, q % 3);
                    Vector3 e = new Vector3(i, 0, 1) + s / (float)N, r = acc.Estimate(i);
                    ok &= r.x == e.x && r.y == e.y && r.z == e.z;
                }
                ok &= acc.TotalSamples == 9L * N && fb.Sorted;
            }
            var empty = new ProgressiveAccumulator(new FakeBackend(), new AdaptiveSampling { MaxSamples = 8, SamplesPerPass = 2 }, null, null, null, 0);
            ok &= empty.Finished && !empty.Step() && empty.Result().Length == 0;
            Check(sb, ref pass, ref total, "A: 패스 일정 — ⌈N/K⌉ 패스·마지막 = 남은 만큼·First 1 + Next·활성 오름차순·Estimate = direct + sum/N·빈 입력", ok);
        }

        // ── 테스트 씬: 바닥(20×20) + 떠 있는 상자(1×1×1, y 0.5~1.5) ──
        static RadianceScene MakeScene()
        {
            var tris = new List<Tri>();
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                tris.Add(new Tri { V0 = a, V1 = b, V2 = c });
                tris.Add(new Tri { V0 = a, V1 = c, V2 = d });
            }
            Quad(new Vector3(-10, 0, -10), new Vector3(-10, 0, 10), new Vector3(10, 0, 10), new Vector3(10, 0, -10));
            float x0 = -0.5f, x1 = 0.5f, y0 = 0.5f, y1 = 1.5f, z0 = -0.5f, z1 = 0.5f;
            Quad(new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0)); // top
            Quad(new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x0, y0, z1)); // bottom
            Quad(new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y0, z0)); // -z
            Quad(new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1)); // +z
            Quad(new Vector3(x0, y0, z0), new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0)); // -x
            Quad(new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x1, y0, z1)); // +x
            var arr = tris.ToArray();
            return new RadianceScene(arr, new Vector3(0.6f, 0.6f, 0.6f), new BruteForceOccluder(arr));
        }

        // 지면 격자 점(상자 아래·근처·먼 곳) + 상자 윗면 점 + 하늘 점
        static void MakePoints(out Vector3[] p, out Vector3[] n, out uint[] s, int grid = 9, float extent = 6f)
        {
            var pl = new List<Vector3>(); var nl = new List<Vector3>();
            for (int a = 0; a < grid; a++)
                for (int b = 0; b < grid; b++)
                {
                    float x = -extent + 2 * extent * a / (grid - 1), z = -extent + 2 * extent * b / (grid - 1);
                    pl.Add(new Vector3(x * 0.37f + 0.05f, 1e-3f, z * 0.37f - 0.03f)); nl.Add(Vector3.up);   // 가까운 고리
                    pl.Add(new Vector3(x, 1e-3f, z)); nl.Add(Vector3.up);                                   // 먼 곳 포함
                }
            pl.Add(new Vector3(0.1f, 1.5f + 1e-3f, 0.2f)); nl.Add(Vector3.up);
            // 하늘 점: 씬 위 높이 50 — 위 반구에 지오메트리가 없어 모든 경로가 미스(경로 기여 상수 = 분산 0)
            for (int k = 0; k < 6; k++) { pl.Add(new Vector3(k * 1.7f - 4f, 50f, 2f - k)); nl.Add(Vector3.up); }
            p = pl.ToArray(); n = nl.ToArray();
            s = new uint[p.Length];
            for (int i = 0; i < s.Length; i++) s[i] = 12345u + (uint)i * 2654435761u;
        }

        static BakeQualitySettings Q(int spp) => new BakeQualitySettings
        {
            AoSamples = 4, IndirectSamples = spp, MaxBounces = 3, RRStartDepth = 2, RayBias = 1e-4f, DirectSamples = 4,
        };

        static DirectionalLight Sun => new DirectionalLight
        {
            Direction = new Vector3(-0.3f, -1f, -0.2f).normalized, Color = new Vector3(1f, 0.95f, 0.9f), Intensity = 2f, AngularDiameterDeg = 2f,
        };

        static bool Same(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;

        // ── B) 비트 동일 ──
        static void TestBitExact(StringBuilder sb, ref int pass, ref int total)
        {
            var scene = MakeScene();
            var sky = new UniformSky(new Vector3(0.3f, 0.35f, 0.45f));
            var sun = Sun;
            MakePoints(out var p, out var n, out var s);
            const int N = 24;
            var q = Q(N);
            var refv = new Vector3[p.Length];
            for (int i = 0; i < p.Length; i++) refv[i] = RadianceCore.EvaluateRadiance(scene, p[i], n[i], sun, sky, q, s[i]);

            bool ok = true;
            int nonTrivial = 0;
            foreach (int K in new[] { 1, 3, 7, N, N + 5 })
            {
                var acc = new ProgressiveAccumulator(new CpuPathAccum(scene, sun, sky, q),
                    new AdaptiveSampling { MaxSamples = N, SamplesPerPass = K, Adaptive = false }, p, n, s, p.Length);
                acc.Run();
                for (int i = 0; i < p.Length; i++) ok &= Same(acc.Estimate(i), refv[i]) && acc.Count[i] == N;
            }
            // 비교가 상수끼리가 아님: 점마다 값이 달라야(그림자·차폐 변화)
            for (int i = 1; i < p.Length; i++) if (!Same(refv[i], refv[0])) nonTrivial++;
            Check(sb, ref pass, ref total, $"B: CPU 누적(K=1·3·7·N·N+5) ≡ EvaluateRadiance(N={N}, RR·원반 4발) — 점 {p.Length}개 성분별 정확(서로 다른 값 {nonTrivial})",
                  ok && nonTrivial > p.Length / 2);
        }

        // ── C) 적응 ──
        static void TestAdaptive(StringBuilder sb, ref int pass, ref int total)
        {
            var scene = MakeScene();
            var sky = new UniformSky(new Vector3(0.3f, 0.35f, 0.45f));
            var sun = Sun;
            MakePoints(out var p, out var n, out var s, grid: 7, extent: 9f);
            const int N = 128, K = 8, MIN = 16;
            var q = Q(N);
            var st = new AdaptiveSampling { MaxSamples = N, SamplesPerPass = K, Adaptive = true, MinSamples = MIN, Threshold = 0.03f };

            ProgressiveAccumulator Run(Vector3[] pp, Vector3[] nn, uint[] ss)
            {
                var a = new ProgressiveAccumulator(new CpuPathAccum(scene, sun, sky, q), st, pp, nn, ss, pp.Length);
                a.Run();
                return a;
            }
            var acc = Run(p, n, s);
            var acc2 = Run(p, n, s);

            bool ok = true;
            int far = 0, farAtMin = 0, near = 0, nearMore = 0;
            for (int i = 0; i < p.Length; i++)
            {
                int c = acc.Count[i];
                ok &= c >= MIN && c <= N && (c % K == 0 || c == N);
                ok &= c == acc2.Count[i] && Same(acc.Estimate(i), acc2.Estimate(i));
                // 하늘 점(위 반구에 지오메트리 없음 → 분산 0)은 첫 판정(MinSamples)에서 멈춘다
                float dxz = Mathf.Max(Mathf.Abs(p[i].x), Mathf.Abs(p[i].z));
                if (p[i].y > 10f) { far++; if (c == MIN) farAtMin++; }
                if (p[i].y < 0.01f && dxz < 1.2f) { near++; if (c > MIN) nearMore++; }
            }
            long budget = (long)p.Length * N;
            ok &= acc.TotalSamples < budget && far > 0 && farAtMin == far && near > 0 && nearMore > 0;

            // 배치 무관: 앞/뒤 절반을 따로 돌려도 점별 결과·count 동일
            int h = p.Length / 2;
            var a1 = Run(p[..h], n[..h], s[..h]);
            var a2 = Run(p[h..], n[h..], s[h..]);
            bool split = true;
            for (int i = 0; i < p.Length; i++)
            {
                var part = i < h ? a1 : a2; int j = i < h ? i : i - h;
                split &= part.Count[j] == acc.Count[i] && Same(part.Estimate(j), acc.Estimate(i));
            }
            Check(sb, ref pass, ref total, $"C: 적응 — 하늘 점 {farAtMin}/{far} 이 min {MIN} 에서 멈춤 · 상자 근처 {nearMore}/{near} 더 샘플 · 경로 {100.0 * acc.TotalSamples / budget:F1}% · 결정적 · 반씩 나눠도 동일",
                  ok && split);
        }

        // ── D) Converged ──
        static void TestConverged(StringBuilder sb, ref int pass, ref int total)
        {
            var d = new Vector3(0.5f, 0.5f, 0.5f);   // 직사 휘도 0.5
            // 분산 0: 경로 기여가 전부 (1,1,1) 이면 m2 = N·1
            bool zeroVar = ProgressiveAccumulator.Converged(d, Vector3.one * 16, 16f, 16, 0.001f);
            bool small = !ProgressiveAccumulator.Converged(d, Vector3.one, 1f, 1, 1f);
            // 경로 휘도 0 과 2 반반(N=16): mean 1, var = 16/15·1, se = √(var/16) ≈ 0.2582 · 기준 = thr × (0.5 + 1)
            Vector3 sum = Vector3.one * 16f;   // Σ l = 16
            float m2 = 8 * 4f;                 // 8 개가 2² = 4
            double se = Math.Sqrt((16.0 / 15.0) / 16.0);
            bool atEdge = ProgressiveAccumulator.Converged(d, sum, m2, 16, (float)(se / 1.5 * 1.001))
                       && !ProgressiveAccumulator.Converged(d, sum, m2, 16, (float)(se / 1.5 * 0.999));
            // 어두운 텍셀: 휘도 0 이면 기준 = thr × 하한
            bool dark = ProgressiveAccumulator.Converged(Vector3.zero, Vector3.zero, 0f, 4, 0.01f);
            Check(sb, ref pass, ref total, "D: Converged — 분산 0 참 · N<2 거짓 · 표준오차 경계(±0.1%) · 어두운 텍셀 하한", zeroVar && small && atEdge && dark);
        }

        // ── E) 적응 + 부분 패스 ──
        static void TestPartialAdaptive(StringBuilder sb, ref int pass, ref int total)
        {
            var scene = MakeScene();
            var sky = new UniformSky(new Vector3(0.3f, 0.35f, 0.45f));
            MakePoints(out var p, out var n, out var s, grid: 5, extent: 9f);
            var acc = new ProgressiveAccumulator(new CpuPathAccum(scene, Sun, sky, Q(20)),
                new AdaptiveSampling { MaxSamples = 20, SamplesPerPass = 8, Adaptive = true, MinSamples = 16, Threshold = 0.03f }, p, n, s, p.Length);
            var done = new List<int>();
            while (true) { bool more = acc.Step(); done.Add(acc.SamplesDone); if (!more) break; }
            bool ok = true;
            for (int i = 0; i < p.Length; i++) ok &= acc.Count[i] == 16 || acc.Count[i] == 20;
            bool sched = done.Count >= 2 && done[0] == 8 && done[1] == 16 && (done.Count == 2 || (done.Count == 3 && done[2] == 20));
            Check(sb, ref pass, ref total, $"E: 적응 + 부분 패스 — 누적 {string.Join("→", done)} · count ∈ {{16, 20}}", ok && sched);
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
