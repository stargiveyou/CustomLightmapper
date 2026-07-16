using System.Diagnostics;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Burst 판(LightmapPostProcessBurstJob.Dilate) vs 직렬판(LightmapPostProcess.Dilate) 비교.
    ///  1) 결과 일치: 동일 입력·iterations 에서 두 결과가 픽셀단위 동일(eps).
    ///  2) 성능: 큰 그리드에서 소요시간. Burst 첫 호출은 컴파일 포함이라 '본 측정과 동일 iters' 로 워밍업,
    ///     이후 best-of-N(노이즈 최소화)로 측정. 관리형(Color[]) 경로와 NativeArray 직결 경로를 분리 측정.
    /// 호출: Debug.Log(LightmapPostProcessCompare.RunAll());
    /// 주의: 에디터에서 Burst 진짜 속도를 보려면 Jobs ▸ Burst ▸ Safety Checks Off, Leak Detection Off 권장.
    /// </summary>
    public static class LightmapPostProcessCompare
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Dilate Compare: Burst vs Serial ===");
            int pass = 0, total = 0;

            // ── 1) 결과 일치 (대표 입력: 흩뿌린 시드 + 거터 박힌 사각 영역) ──
            {
                int w = 64, h = 64, iters = 8;
                MakeInput(w, h, out Color[] basePx, out bool[] baseValid);

                var pxA = (Color[])basePx.Clone(); var vA = (bool[])baseValid.Clone();
                var pxB = (Color[])basePx.Clone(); var vB = (bool[])baseValid.Clone();

                LightmapPostProcessBurstJob.Dilate(pxA, vA, w, h, iters); // Burst
                LightmapPostProcess.Dilate(pxB, vB, w, h, iters);         // 직렬

                float maxDiff = 0; int validMismatch = 0;
                for (int i = 0; i < w * h; i++)
                {
                    maxDiff = Mathf.Max(maxDiff, Mathf.Abs(pxA[i].r - pxB[i].r));
                    maxDiff = Mathf.Max(maxDiff, Mathf.Abs(pxA[i].g - pxB[i].g));
                    maxDiff = Mathf.Max(maxDiff, Mathf.Abs(pxA[i].b - pxB[i].b));
                    maxDiff = Mathf.Max(maxDiff, Mathf.Abs(pxA[i].a - pxB[i].a));
                    if (vA[i] != vB[i]) validMismatch++;
                }
                Check(sb, ref pass, ref total, $"결과 일치: 색 maxDiff={maxDiff:0.000000} < 1e-4", maxDiff < 1e-4f);
                Check(sb, ref pass, ref total, $"결과 일치: valid 마스크 동일 (mismatch={validMismatch})", validMismatch == 0);
            }

            // ── 2) 성능 (큰 그리드, 동일 iters 워밍업 후 best-of-N) ──
            {
                int w = 512, h = 512, iters = 8, reps = 7;
                MakeInput(w, h, out Color[] basePx, out bool[] baseValid);

                // 워밍업: 본 측정과 같은 iters 로 1회씩 (Burst 컴파일 + 캐시 흡수)
                { var p = (Color[])basePx.Clone(); var v = (bool[])baseValid.Clone(); LightmapPostProcessBurstJob.Dilate(p, v, w, h, iters); }
                { var p = (Color[])basePx.Clone(); var v = (bool[])baseValid.Clone(); LightmapPostProcess.Dilate(p, v, w, h, iters); }

                double burstMs = MeasureManaged(basePx, baseValid, w, h, iters, reps, useBurst: true);
                double serialMs = MeasureManaged(basePx, baseValid, w, h, iters, reps, useBurst: false);
                double nativeMs = MeasureNative(basePx, baseValid, w, h, iters, reps);

                sb.AppendLine($"  [time] {w}×{h}, {iters} iters (best of {reps})");
                sb.AppendLine($"    Serial                   = {serialMs:0.00} ms");
                sb.AppendLine($"    Burst (managed Color[])  = {burstMs:0.00} ms  (serial 대비 ≈{serialMs / Mathx(burstMs):0.00}×)");
                sb.AppendLine($"    Burst (NativeArray 직결) = {nativeMs:0.00} ms  (변환 제외, serial 대비 ≈{serialMs / Mathx(nativeMs):0.00}×)");
                // 성능은 환경마다 달라 PASS/FAIL 대신 정보로만 출력.
            }

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // 관리형 진입점(Color[]) 측정 — Color↔float4 변환 + TempJob 할당 포함(공개 API 실측). 입력은 매 rep 새로 복제(인플레이스라).
        static double MeasureManaged(Color[] basePx, bool[] baseValid, int w, int h, int iters, int reps, bool useBurst)
        {
            var sw = new Stopwatch();
            double best = double.MaxValue;
            for (int r = 0; r < reps; r++)
            {
                var px = (Color[])basePx.Clone();    // 측정 밖
                var v = (bool[])baseValid.Clone();   // 측정 밖
                sw.Restart();
                if (useBurst) LightmapPostProcessBurstJob.Dilate(px, v, w, h, iters);
                else LightmapPostProcess.Dilate(px, v, w, h, iters);
                sw.Stop();
                best = System.Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            return best;
        }

        // NativeArray 직결(DilateBurst) 측정 — Color 변환 제외, 순수 Job 커널 비용. 데이터 채우기는 측정 밖.
        static double MeasureNative(Color[] basePx, bool[] baseValid, int w, int h, int iters, int reps)
        {
            int n = w * h;
            var px = new NativeArray<float4>(n, Allocator.Persistent);
            var v = new NativeArray<bool>(n, Allocator.Persistent);
            var sw = new Stopwatch();
            double best = double.MaxValue;
            try
            {
                for (int r = 0; r < reps; r++)
                {
                    for (int i = 0; i < n; i++)   // 측정 밖: 입력 리필
                    {
                        Color c = basePx[i];
                        px[i] = new float4(c.r, c.g, c.b, c.a);
                        v[i] = baseValid[i];
                    }
                    sw.Restart();
                    LightmapPostProcessBurstJob.DilateBurst(px, v, w, h, iters);
                    sw.Stop();
                    best = System.Math.Min(best, sw.Elapsed.TotalMilliseconds);
                }
            }
            finally { px.Dispose(); v.Dispose(); }
            return best;
        }

        // 0 나눗셈 가드(아주 빠른 환경에서 best=0 방지).
        static double Mathx(double ms) => ms < 1e-4 ? 1e-4 : ms;

        // 결정적 입력: 좌상 사각 영역을 valid(그라디언트 색)로, 그 밖 흩뿌린 시드 몇 개. 나머지 무효(배경0).
        static void MakeInput(int w, int h, out Color[] px, out bool[] valid)
        {
            px = new Color[w * h];
            valid = new bool[w * h];

            int rx0 = w / 8, rx1 = w / 2, ry0 = h / 8, ry1 = h / 2; // 차트-유사 사각 영역
            for (int y = ry0; y < ry1; y++)
                for (int x = rx0; x < rx1; x++)
                {
                    int i = y * w + x;
                    px[i] = new Color((float)x / w, (float)y / h, 0.5f, 1f);
                    valid[i] = true;
                }

            var rng = new System.Random(12345);
            for (int s = 0; s < 12; s++) // 흩뿌린 시드
            {
                int x = rng.Next(w), y = rng.Next(h);
                int i = y * w + x;
                px[i] = new Color((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble(), 1f);
                valid[i] = true;
            }
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        {
            total++;
            if (ok) pass++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }
    }
}
