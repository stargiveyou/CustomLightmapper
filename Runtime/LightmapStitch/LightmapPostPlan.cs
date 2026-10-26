using System.Collections.Generic;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// GPU 후처리(LightmapPost.compute)용 계획 — 순수 C#(헤드리스 검증 <c>LightmapPostPlanTests</c>).
    /// CPU 판(<see cref="LightmapSeamStitch"/>·<see cref="LightmapPostProcessBurstJob"/>)의 순서 의존 연산을
    /// '같은 덧셈 순서'를 지키는 병렬 단계로 분해한다. 텍셀 목록은 지오메트리·valid 만으로 정해지고
    /// (스티칭 중 valid 는 바뀌지 않음) 값 계산은 GPU 가 한다.
    ///
    /// Tier1(정점): CPU 는 그룹을 순서대로 평균 → 덮어쓰기라 텍셀을 공유하는 그룹끼리는 순서가 결과를 바꾼다.
    ///   그룹 g 의 웨이브 = 1 + (g 의 텍셀을 앞서 건드린 그룹들의 웨이브 최대). 같은 웨이브 그룹은 텍셀을 공유하지 않고,
    ///   텍셀을 공유하는 두 그룹은 원래 순서대로 웨이브가 증가 → 웨이브 순 병렬 실행 = 순차 실행(정확).
    /// Tier2(모서리, Jacobi): 샘플(그룹, t)마다 그 t 의 valid 텍셀 평균(A단계) → 텍셀마다 기여 샘플 평균을
    ///   CPU 누적 순서(샘플 순)대로 합산(B단계). 반복마다 A→B.
    /// Dilation: 텍셀 독립 더블버퍼(DiliateJob 과 같은 8-이웃 순서).
    /// <see cref="RunTier1"/>·<see cref="RunTier2"/>·<see cref="RunDilate"/> 는 커널과 같은 분해를 CPU 로 돌리는 모델 —
    /// 원본 CPU 판과 비트 동일함을 테스트로 고정하고, HLSL 은 이 모델을 그대로 옮긴다.
    /// </summary>
    public static class LightmapPostPlan
    {
        /// <summary>Tier1 계획: 그룹 = valid 텍셀 목록(원래 순서·중복 유지, ≥2). 그룹은 웨이브 순으로 정렬돼 있다.</summary>
        public sealed class Tier1
        {
            public int[] GroupStart, GroupCount, Texels;
            /// <summary>웨이브 w = 그룹 [WaveStart[w], WaveStart[w] + WaveCount[w]).</summary>
            public int[] WaveStart, WaveCount;
        }

        /// <summary>Tier2 계획: 샘플 = (그룹, t) 의 valid 텍셀(중복 제거·순서 유지, ≥2), 텍셀 = 갱신 대상(첫 등장 순)과 기여 샘플(샘플 순).</summary>
        public sealed class Tier2
        {
            public int[] SampleStart, SampleCount, SampleTexels;
            public int[] Texels;
            public int[] ContribStart, ContribCount, ContribSamples;
        }

        public static Tier1 BuildTier1(List<int[]> groups, bool[] valid)
        {
            var gStart = new List<int>();
            var gCount = new List<int>();
            var tex = new List<int>();
            var wave = new List<int>();
            var last = new Dictionary<int, int>();
            var tmp = new List<int>();
            int n = valid.Length;
            int maxWave = -1;

            if (groups != null)
            {
                foreach (var g in groups)
                {
                    if (g == null || g.Length < 2) continue;
                    tmp.Clear();
                    for (int i = 0; i < g.Length; i++)
                    {
                        int idx = g[i];
                        if (idx < 0 || idx >= n) continue;
                        if (valid[idx]) tmp.Add(idx);   // Stitch 가 읽고 쓰는 텍셀 = 범위 안 valid(중복 포함)
                    }
                    if (tmp.Count < 2) continue;

                    int w = 0;
                    foreach (int t in tmp)
                        if (last.TryGetValue(t, out int lw) && lw + 1 > w) w = lw + 1;
                    foreach (int t in tmp) last[t] = w;

                    gStart.Add(tex.Count);
                    gCount.Add(tmp.Count);
                    tex.AddRange(tmp);
                    wave.Add(w);
                    if (w > maxWave) maxWave = w;
                }
            }

            // 웨이브 순 안정 정렬(카운팅) — 웨이브 안 순서는 결과와 무관(텍셀 비공유)하지만 결정적으로 유지
            int waves = maxWave + 1;
            var waveCount = new int[waves];
            foreach (int w in wave) waveCount[w]++;
            var waveStart = new int[waves];
            for (int w = 1; w < waves; w++) waveStart[w] = waveStart[w - 1] + waveCount[w - 1];
            int groupsN = wave.Count;
            var order = new int[groupsN];
            var cursor = (int[])waveStart.Clone();
            for (int g = 0; g < groupsN; g++) order[cursor[wave[g]]++] = g;

            var plan = new Tier1
            {
                GroupStart = new int[groupsN],
                GroupCount = new int[groupsN],
                Texels = new int[tex.Count],
                WaveStart = waveStart,
                WaveCount = waveCount,
            };
            int k = 0;
            for (int s = 0; s < groupsN; s++)
            {
                int g = order[s];
                plan.GroupStart[s] = k;
                plan.GroupCount[s] = gCount[g];
                for (int i = 0; i < gCount[g]; i++) plan.Texels[k++] = tex[gStart[g] + i];
            }
            return plan;
        }

        public static Tier2 BuildTier2(List<LightmapSeamStitch.Seg[]> segmentGroups, bool[] valid, int res)
        {
            var sStart = new List<int>();
            var sCount = new List<int>();
            var sTex = new List<int>();
            var contrib = new Dictionary<int, List<int>>();
            var texOrder = new List<int>();
            var perT = new List<int>(8);
            int n = valid.Length;

            if (segmentGroups != null && res > 0)
            {
                foreach (var segs in segmentGroups)
                {
                    if (segs == null || segs.Length < 2) continue;

                    // StitchEdges 와 같은 샘플 수·같은 t·같은 텍셀 선택
                    float maxLen = 0;
                    for (int s = 0; s < segs.Length; s++)
                        maxLen = Mathf.Max(maxLen, Vector2.Distance(segs[s].A, segs[s].B));
                    int N = Mathf.Max(1, Mathf.CeilToInt(maxLen));

                    for (int i = 0; i <= N; i++)
                    {
                        perT.Clear();
                        float t = i / (float)N;
                        for (int s = 0; s < segs.Length; s++)
                        {
                            Vector2 p = Vector2.Lerp(segs[s].A, segs[s].B, t);
                            int tx = Mathf.Clamp(Mathf.FloorToInt(p.x), 0, res - 1);
                            int ty = Mathf.Clamp(Mathf.FloorToInt(p.y), 0, res - 1);
                            int idx = ty * res + tx;
                            if (idx < 0 || idx >= n || !valid[idx]) continue;
                            if (perT.Contains(idx)) continue;
                            perT.Add(idx);
                        }
                        if (perT.Count < 2) continue;

                        int j = sStart.Count;
                        sStart.Add(sTex.Count);
                        sCount.Add(perT.Count);
                        sTex.AddRange(perT);
                        foreach (int idx in perT)
                        {
                            if (!contrib.TryGetValue(idx, out var list))
                            {
                                list = new List<int>(2);
                                contrib[idx] = list;
                                texOrder.Add(idx);
                            }
                            list.Add(j);
                        }
                    }
                }
            }

            var plan = new Tier2
            {
                SampleStart = sStart.ToArray(),
                SampleCount = sCount.ToArray(),
                SampleTexels = sTex.ToArray(),
                Texels = texOrder.ToArray(),
                ContribStart = new int[texOrder.Count],
                ContribCount = new int[texOrder.Count],
            };
            var cs = new List<int>();
            for (int k = 0; k < texOrder.Count; k++)
            {
                var list = contrib[texOrder[k]];
                plan.ContribStart[k] = cs.Count;
                plan.ContribCount[k] = list.Count;
                cs.AddRange(list);
            }
            plan.ContribSamples = cs.ToArray();
            return plan;
        }

        // ───────────── CPU 모델(커널과 같은 분해·같은 연산 순서) ─────────────

        /// <summary>CSStitchT1 모델: 웨이브 순, 그룹마다 합(0 에서 순서대로) / 개수 → 그룹 텍셀 전부에 기록.</summary>
        public static void RunTier1(Color[] px, Tier1 plan)
        {
            for (int w = 0; w < plan.WaveStart.Length; w++)
            {
                for (int g = plan.WaveStart[w]; g < plan.WaveStart[w] + plan.WaveCount[w]; g++)
                {
                    int s = plan.GroupStart[g], c = plan.GroupCount[g];
                    Color sum = Color.clear;
                    for (int k = 0; k < c; k++) sum += px[plan.Texels[s + k]];
                    Color avg = sum / c;
                    for (int k = 0; k < c; k++) px[plan.Texels[s + k]] = avg;
                }
            }
        }

        /// <summary>CSStitchT2Sample → CSStitchT2Gather 모델(반복마다).</summary>
        public static void RunTier2(Color[] px, Tier2 plan, int iterations)
        {
            int S = plan.SampleStart.Length;
            var avg = new Color[S];
            for (int it = 0; it < Mathf.Max(1, iterations); it++)
            {
                for (int j = 0; j < S; j++)
                {
                    int s = plan.SampleStart[j], c = plan.SampleCount[j];
                    Color r = Color.clear;
                    for (int k = 0; k < c; k++) r += px[plan.SampleTexels[s + k]];
                    avg[j] = r / c;
                }
                for (int k = 0; k < plan.Texels.Length; k++)
                {
                    int s = plan.ContribStart[k], c = plan.ContribCount[k];
                    Color sum = avg[plan.ContribSamples[s]];   // 첫 기여는 대입(StitchEdges 의 sum[idx] = avg 와 같은 연산)
                    for (int q = 1; q < c; q++) sum = sum + avg[plan.ContribSamples[s + q]];
                    px[plan.Texels[k]] = sum / c;
                }
            }
        }

        /// <summary>CSDilate 모델(DiliateJob 과 같은 이웃 순서·더블버퍼). 채운 텍셀은 valid 로 되쓴다.</summary>
        public static void RunDilate(Color[] px, bool[] valid, int w, int h, int iterations)
        {
            if (iterations <= 0) return;
            var srcP = (Color[])px.Clone(); var srcV = (bool[])valid.Clone();
            var dstP = new Color[px.Length]; var dstV = new bool[valid.Length];
            for (int it = 0; it < iterations; it++)
            {
                for (int index = 0; index < px.Length; index++)
                {
                    if (srcV[index]) { dstP[index] = srcP[index]; dstV[index] = true; continue; }
                    int x = index % w, y = index / w;
                    float r = 0, g = 0, b = 0, a = 0;
                    int cnt = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= h) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = x + dx;
                            if (nx < 0 || nx >= w) continue;
                            int nidx = ny * w + nx;
                            if (!srcV[nidx]) continue;
                            Color c = srcP[nidx];
                            r += c.r; g += c.g; b += c.b; a += c.a;
                            cnt++;
                        }
                    }
                    if (cnt > 0) { dstP[index] = new Color(r / cnt, g / cnt, b / cnt, a / cnt); dstV[index] = true; }
                    else { dstP[index] = srcP[index]; dstV[index] = false; }
                }
                (srcP, dstP) = (dstP, srcP);
                (srcV, dstV) = (dstV, srcV);
            }
            System.Array.Copy(srcP, px, px.Length);
            System.Array.Copy(srcV, valid, valid.Length);
        }
    }
}
