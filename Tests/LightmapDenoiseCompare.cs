using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Denoise 검증 — Burst 판(LightmapDenoiseBurstJob) vs 직렬판(LightmapDenoise) + 품질 게이트.
    ///  1) 백엔드 일치: 동일 입력·settings 에서 두 결과가 픽셀단위 동일(eps).
    ///  2) 노이즈 감소: 평탄면(정답=상수)에 노이즈를 얹고 → 디노이즈 후 RMS 오차가 크게 감소.
    ///  3) 라이팅 엣지 보존: 그림자 경계(계단 신호)의 대비가 색 range 커널로 유지.
    ///     (단일 채널 R 계단 — 휘도 스칼라 커널이면 0.7 계단이 휘도 0.15로 과소평가돼 번지는 회귀 케이스.)
    ///  4) 차트 격리: 아틀라스에서 인접하지만 월드에서 먼 두 영역이 서로 bleed 하지 않음(위치 가이드).
    ///  5) 성능: 큰 그리드 소요시간(워밍업 후 best-of-N, 정보 출력).
    /// 호출: Debug.Log(LightmapDenoiseCompare.RunAll());
    /// </summary>
    public static class LightmapDenoiseCompare
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Denoise Compare: Burst vs Serial + Quality ===");
            int pass = 0, total = 0;
            var s = DenoiseSettings.Default;

            // ── 1) 백엔드 일치 (노이즈 낀 평탄면 + 그림자 엣지 + 이웃 차트 혼합 입력) ──
            {
                int w = 64, h = 64;
                MakeSceneInput(w, h, out var basePx, out var valid, out var nrm, out var pos, out _);

                var pxA = (Color[])basePx.Clone();
                var pxB = (Color[])basePx.Clone();
                LightmapDenoiseBurstJob.Denoise(pxA, valid, nrm, pos, w, h, s); // Burst
                LightmapDenoise.Denoise(pxB, valid, nrm, pos, w, h, s);         // 직렬

                float maxDiff = 0;
                for (int i = 0; i < w * h; i++)
                {
                    maxDiff = Mathf.Max(maxDiff, Mathf.Abs(pxA[i].r - pxB[i].r));
                    maxDiff = Mathf.Max(maxDiff, Mathf.Abs(pxA[i].g - pxB[i].g));
                    maxDiff = Mathf.Max(maxDiff, Mathf.Abs(pxA[i].b - pxB[i].b));
                    maxDiff = Mathf.Max(maxDiff, Mathf.Abs(pxA[i].a - pxB[i].a));
                }
                Check(sb, ref pass, ref total, $"백엔드 일치: 색 maxDiff={maxDiff:0.000000} < 1e-4", maxDiff < 1e-4f);
            }

            // ── 2) 노이즈 감소 + 3) 엣지 보존 + 4) 차트 격리 (직렬판 기준 — 1)에서 Burst 동등 확인) ──
            {
                int w = 64, h = 64;
                MakeSceneInput(w, h, out var px, out var valid, out var nrm, out var pos, out var clean);

                double rmsBefore = Rms(px, clean, valid, w, 0, 0, w, h / 2);
                LightmapDenoise.Denoise(px, valid, nrm, pos, w, h, s);
                double rmsAfter = Rms(px, clean, valid, w, 0, 0, w, h / 2);
                Check(sb, ref pass, ref total,
                    $"노이즈 감소: RMS {rmsBefore:0.0000} → {rmsAfter:0.0000} (≤ 0.5×)", rmsAfter <= rmsBefore * 0.5);

                // 엣지 보존: 아래 절반(y<h/2) 좌/우에 0.15 vs 0.85 계단 — 경계 양쪽 2텍셀 밖 평균 대비 유지.
                double dark = MeanR(px, valid, w, 4, 2, w / 2 - 3, h / 2 - 2);
                double bright = MeanR(px, valid, w, w / 2 + 3, 2, w - 4, h / 2 - 2);
                double contrast = bright - dark; // 원 신호 대비 0.7
                Check(sb, ref pass, ref total,
                    $"엣지 보존: 그림자 경계 대비 {contrast:0.000} ≥ 0.49 (원신호 0.7의 70%)", contrast >= 0.49);

                // 차트 격리: 위 절반은 월드에서 100 유닛 떨어진 별도 차트(순수 G). 아래 차트(R 계열) bleed 없어야 함.
                double gLeak = 0;
                for (int y = h / 2 + 1; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (!valid[i]) continue;
                        gLeak = System.Math.Max(gLeak, System.Math.Abs(px[i].r - clean[i].r));
                    }
                Check(sb, ref pass, ref total,
                    $"차트 격리: 원거리 차트 R-채널 오염 max={gLeak:0.0000} < 0.02", gLeak < 0.02);
            }

            // ── 5) 성능 (큰 그리드, 워밍업 후 best-of-N) ──
            {
                int w = 512, h = 512, reps = 5;
                MakeSceneInput(w, h, out var basePx, out var valid, out var nrm, out var pos, out _);

                { var p = (Color[])basePx.Clone(); LightmapDenoiseBurstJob.Denoise(p, valid, nrm, pos, w, h, s); }
                { var p = (Color[])basePx.Clone(); LightmapDenoise.Denoise(p, valid, nrm, pos, w, h, s); }

                double burstMs = Measure(basePx, valid, nrm, pos, w, h, s, reps, useBurst: true);
                double serialMs = Measure(basePx, valid, nrm, pos, w, h, s, reps, useBurst: false);
                sb.AppendLine($"  [time] {w}×{h}, iters={s.Iterations} (best of {reps})");
                sb.AppendLine($"    Serial = {serialMs:0.00} ms");
                sb.AppendLine($"    Burst  = {burstMs:0.00} ms  (serial 대비 ≈{serialMs / System.Math.Max(1e-4, burstMs):0.00}×)");
            }

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        static double Measure(Color[] basePx, bool[] valid, Vector3[] nrm, Vector3[] pos,
                              int w, int h, in DenoiseSettings s, int reps, bool useBurst)
        {
            var sw = new Stopwatch();
            double best = double.MaxValue;
            for (int r = 0; r < reps; r++)
            {
                var px = (Color[])basePx.Clone(); // 측정 밖(인플레이스라 리필)
                sw.Restart();
                if (useBurst) LightmapDenoiseBurstJob.Denoise(px, valid, nrm, pos, w, h, s);
                else LightmapDenoise.Denoise(px, valid, nrm, pos, w, h, s);
                sw.Stop();
                best = System.Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            return best;
        }

        // 결정적 입력 — 라이트맵 유사 장면(모든 좌표 valid, 가장자리 2텍셀 무효 거터):
        //  아래 절반(y<h/2): 차트 A — 평면(노멀 +Y, 위치 = 텍셀그리드×1/16 월드), R 채널 계단(좌 0.15/우 0.85) + 노이즈 ±0.1
        //  위 절반        : 차트 B — 평면(노멀 +Y, 월드에서 +100 유닛 이격), 상수 G=0.5 + 노이즈 ±0.1
        //  clean = 노이즈 없는 정답(품질 게이트 기준).
        static void MakeSceneInput(int w, int h, out Color[] px, out bool[] valid,
                                   out Vector3[] nrm, out Vector3[] pos, out Color[] clean)
        {
            int n = w * h;
            px = new Color[n];
            clean = new Color[n];
            valid = new bool[n];
            nrm = new Vector3[n];
            pos = new Vector3[n];
            const float texel = 1f / 16f; // texelsPerWorldUnit=16 가정(PositionSigma 기본값과 정합)

            var rng = new System.Random(12345);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    bool gutter = x < 2 || y < 2 || x >= w - 2 || y >= h - 2 || (y == h / 2); // 차트 사이 1줄 거터
                    valid[i] = !gutter;
                    nrm[i] = Vector3.up;
                    bool chartB = y > h / 2;
                    pos[i] = new Vector3(x * texel, chartB ? 100f : 0f, y * texel); // 차트 B는 월드서 멀리

                    float noise = ((float)rng.NextDouble() * 2f - 1f) * 0.1f;
                    Color c0 = chartB
                        ? new Color(0f, 0.5f, 0f, 1f)                          // 차트 B: 상수 G
                        : new Color(x < w / 2 ? 0.15f : 0.85f, 0f, 0f, 1f);    // 차트 A: R 계단(그림자 경계)
                    clean[i] = c0;
                    px[i] = gutter ? new Color(0, 0, 0, 1) : new Color(
                        Mathf.Clamp01(c0.r + (c0.r > 0 ? noise : 0f)),
                        Mathf.Clamp01(c0.g + (c0.g > 0 ? noise : 0f)),
                        c0.b, 1f);
                }
            }
        }

        // 사각 영역 [x0,x1)×[y0,y1) 의 valid 텍셀 RMS 오차(clean 대비, R 채널).
        static double Rms(Color[] px, Color[] clean, bool[] valid, int w, int x0, int y0, int x1, int y1)
        {
            double sum = 0; long cnt = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = y * w + x;
                    if (!valid[i]) continue;
                    double d = px[i].r - clean[i].r;
                    sum += d * d; cnt++;
                }
            return cnt > 0 ? System.Math.Sqrt(sum / cnt) : 0;
        }

        // 사각 영역 valid 텍셀 R 평균.
        static double MeanR(Color[] px, bool[] valid, int w, int x0, int y0, int x1, int y1)
        {
            double sum = 0; long cnt = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = y * w + x;
                    if (!valid[i]) continue;
                    sum += px[i].r; cnt++;
                }
            return cnt > 0 ? sum / cnt : 0;
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        {
            total++;
            if (ok) pass++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }
    }
}
