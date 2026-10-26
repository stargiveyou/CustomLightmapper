using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// GPU 후처리 계획(<see cref="LightmapPostPlan"/>) 검증 — 순수 C#(헤드리스).
    /// 계획 + CPU 모델(커널과 같은 분해)이 원본 CPU 판과 성분별 비트 동일함을 무작위 페이지로 고정한다.
    ///  A) Tier1: 겹침·그룹 안 중복·범위 밖·무효 텍셀이 섞인 무작위 그룹 → 웨이브 모델 ≡ LightmapSeamStitch.Stitch ·
    ///     같은 웨이브 그룹끼리 텍셀 비공유 · 텍셀을 공유하는 두 그룹은 원래 순서대로 웨이브 증가.
    ///  B) Tier2: 무작위 segment 그룹(2~4 segment, 길이·방향 다양) → 샘플·수집 모델 ≡ StitchEdges (반복 1·2·3).
    ///  C) Dilation: 모델 ≡ LightmapPostProcess.Dilate(직렬판) — 픽셀·valid 되쓰기 모두, 반복 1·3·6.
    ///  D) 전체 순서 T1 → T2 → dilate ≡ CPU 순서(AtlasApplyDebug 와 같은 호출 순).
    /// 호출: Debug.Log(LightmapPostPlanTests.RunAll());
    /// </summary>
    public static class LightmapPostPlanTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== GPU 후처리 계획 (시임 스티칭 · dilation) ≡ CPU 판 ===");
            int pass = 0, total = 0;
            TestTier1(sb, ref pass, ref total);
            TestTier2(sb, ref pass, ref total);
            TestDilate(sb, ref pass, ref total);
            TestChain(sb, ref pass, ref total);
            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ── 무작위 페이지 ──
        static void RandomPage(System.Random rng, int res, double validRatio, out Color[] px, out bool[] valid)
        {
            int n = res * res;
            px = new Color[n];
            valid = new bool[n];
            for (int i = 0; i < n; i++)
            {
                valid[i] = rng.NextDouble() < validRatio;
                px[i] = valid[i]
                    ? new Color((float)rng.NextDouble() * 3f, (float)rng.NextDouble(), (float)rng.NextDouble() * 0.5f, 1f)
                    : new Color(0.04f, 0.04f, 0.06f, 1f);
            }
        }

        static List<int[]> RandomGroups(System.Random rng, int res, int count)
        {
            int n = res * res;
            var groups = new List<int[]>();
            // 겹침이 잦도록 작은 '핫스팟' 텍셀 집합에서 많이 뽑는다
            var hot = new int[24];
            for (int i = 0; i < hot.Length; i++) hot[i] = rng.Next(n);
            for (int g = 0; g < count; g++)
            {
                int len = 2 + rng.Next(5);
                var arr = new int[len];
                for (int k = 0; k < len; k++)
                {
                    double u = rng.NextDouble();
                    arr[k] = u < 0.55 ? hot[rng.Next(hot.Length)]
                           : u < 0.9 ? rng.Next(n)
                           : u < 0.95 ? -1 - rng.Next(3)       // 범위 밖
                           : n + rng.Next(3);
                }
                if (len > 2 && rng.NextDouble() < 0.2) arr[1] = arr[0];   // 그룹 안 중복
                groups.Add(arr);
            }
            if (count > 3) { groups[1] = null; groups[2] = new[] { 5 }; }   // 무시돼야 하는 그룹
            return groups;
        }

        static List<LightmapSeamStitch.Seg[]> RandomSegs(System.Random rng, int res, int count)
        {
            var list = new List<LightmapSeamStitch.Seg[]>();
            for (int g = 0; g < count; g++)
            {
                int m = 2 + rng.Next(3);
                var segs = new LightmapSeamStitch.Seg[m];
                // 비슷한 곳을 지나는 segment 들(서로 텍셀을 공유하도록)
                float cx = (float)rng.NextDouble() * res, cy = (float)rng.NextDouble() * res;
                for (int s = 0; s < m; s++)
                {
                    float len = 1f + (float)rng.NextDouble() * res * 0.3f;
                    float ang = (float)(rng.NextDouble() * Math.PI * 2);
                    var a = new Vector2(cx + (float)rng.NextDouble() * 3f, cy + (float)rng.NextDouble() * 3f);
                    var b = new Vector2(a.x + (float)Math.Cos(ang) * len, a.y + (float)Math.Sin(ang) * len);
                    segs[s] = new LightmapSeamStitch.Seg(a, b);   // 페이지 밖은 Clamp
                }
                list.Add(segs);
            }
            if (count > 2) list[0] = new LightmapSeamStitch.Seg[] { new LightmapSeamStitch.Seg(Vector2.zero, Vector2.one) };   // 1개짜리 = 무시
            return list;
        }

        static bool Same(Color[] a, Color[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b || a[i].a != b[i].a) return false;
            return true;
        }

        static bool Same(bool[] a, bool[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static int Changed(Color[] before, Color[] after)
        {
            int c = 0;
            for (int i = 0; i < before.Length; i++)
                if (before[i].r != after[i].r || before[i].g != after[i].g || before[i].b != after[i].b) c++;
            return c;
        }

        // ── A) Tier1 ──
        static void TestTier1(StringBuilder sb, ref int pass, ref int total)
        {
            var rng = new System.Random(4242);
            bool ok = true, waveOk = true;
            int maxWaves = 0, touched = 0;
            for (int trial = 0; trial < 40; trial++)
            {
                int res = 16 + rng.Next(3) * 8;
                RandomPage(rng, res, 0.7, out var px, out var valid);
                var groups = RandomGroups(rng, res, 10 + rng.Next(60));

                var cpu = (Color[])px.Clone();
                LightmapSeamStitch.Stitch(cpu, (bool[])valid.Clone(), groups);
                var plan = LightmapPostPlan.BuildTier1(groups, valid);
                var gpu = (Color[])px.Clone();
                LightmapPostPlan.RunTier1(gpu, plan);
                ok &= Same(cpu, gpu);
                touched += Changed(px, cpu);
                maxWaves = Math.Max(maxWaves, plan.WaveStart.Length);

                // 웨이브 안 텍셀 비공유
                for (int w = 0; w < plan.WaveStart.Length; w++)
                {
                    var seen = new HashSet<int>();
                    for (int g = plan.WaveStart[w]; g < plan.WaveStart[w] + plan.WaveCount[w]; g++)
                    {
                        var mine = new HashSet<int>();
                        for (int k = 0; k < plan.GroupCount[g]; k++) mine.Add(plan.Texels[plan.GroupStart[g] + k]);
                        foreach (int t in mine) waveOk &= seen.Add(t);
                    }
                }
            }
            Check(sb, ref pass, ref total, $"A: Tier1 웨이브 모델 ≡ Stitch — 무작위 40 페이지(겹침·중복·범위 밖·무효), 최대 웨이브 {maxWaves}, 바뀐 텍셀 {touched} · 웨이브 안 텍셀 비공유",
                  ok && waveOk && maxWaves > 1 && touched > 0);
        }

        // ── B) Tier2 ──
        static void TestTier2(StringBuilder sb, ref int pass, ref int total)
        {
            var rng = new System.Random(777);
            bool ok = true;
            int samples = 0, touched = 0;
            for (int trial = 0; trial < 30; trial++)
            {
                int res = 24 + rng.Next(3) * 8;
                RandomPage(rng, res, 0.8, out var px, out var valid);
                var segs = RandomSegs(rng, res, 4 + rng.Next(20));
                foreach (int iters in new[] { 1, 2, 3 })
                {
                    var cpu = (Color[])px.Clone();
                    LightmapSeamStitch.StitchEdges(cpu, (bool[])valid.Clone(), res, segs, iters);
                    var plan = LightmapPostPlan.BuildTier2(segs, valid, res);
                    var gpu = (Color[])px.Clone();
                    LightmapPostPlan.RunTier2(gpu, plan, iters);
                    ok &= Same(cpu, gpu);
                    if (iters == 1) { samples += plan.SampleStart.Length; touched += Changed(px, cpu); }
                }
            }
            Check(sb, ref pass, ref total, $"B: Tier2 샘플·수집 모델 ≡ StitchEdges(반복 1·2·3) — 무작위 30 페이지, 샘플 {samples}, 바뀐 텍셀 {touched}",
                  ok && samples > 0 && touched > 0);
        }

        // ── C) Dilation ──
        static void TestDilate(StringBuilder sb, ref int pass, ref int total)
        {
            var rng = new System.Random(99);
            bool ok = true;
            int filled = 0;
            for (int trial = 0; trial < 12; trial++)
            {
                int res = 16 + rng.Next(4) * 8;
                RandomPage(rng, res, 0.15 + rng.NextDouble() * 0.4, out var px, out var valid);
                foreach (int it in new[] { 1, 3, 6 })
                {
                    var cp = (Color[])px.Clone(); var cv = (bool[])valid.Clone();
                    LightmapPostProcess.Dilate(cp, cv, res, res, it);
                    var gp = (Color[])px.Clone(); var gv = (bool[])valid.Clone();
                    LightmapPostPlan.RunDilate(gp, gv, res, res, it);
                    ok &= Same(cp, gp) && Same(cv, gv);
                    if (it == 1) filled += Changed(px, cp);
                }
            }
            Check(sb, ref pass, ref total, $"C: Dilation 모델 ≡ LightmapPostProcess.Dilate(반복 1·3·6, 픽셀·valid) — 채운 텍셀 {filled}", ok && filled > 0);
        }

        // ── D) 전체 순서 ──
        static void TestChain(StringBuilder sb, ref int pass, ref int total)
        {
            var rng = new System.Random(2026);
            bool ok = true;
            for (int trial = 0; trial < 10; trial++)
            {
                int res = 32;
                RandomPage(rng, res, 0.6, out var px, out var valid);
                var groups = RandomGroups(rng, res, 30);
                var segs = RandomSegs(rng, res, 12);

                var cp = (Color[])px.Clone(); var cv = (bool[])valid.Clone();
                LightmapSeamStitch.Stitch(cp, cv, groups);
                LightmapSeamStitch.StitchEdges(cp, cv, res, segs, 2);
                LightmapPostProcess.Dilate(cp, cv, res, res, 4);

                var gp = (Color[])px.Clone(); var gv = (bool[])valid.Clone();
                var p1 = LightmapPostPlan.BuildTier1(groups, gv);
                var p2 = LightmapPostPlan.BuildTier2(segs, gv, res);   // valid 는 스티칭 동안 불변 → 시작 마스크로 계획
                LightmapPostPlan.RunTier1(gp, p1);
                LightmapPostPlan.RunTier2(gp, p2, 2);
                LightmapPostPlan.RunDilate(gp, gv, res, res, 4);
                ok &= Same(cp, gp) && Same(cv, gv);
            }
            Check(sb, ref pass, ref total, "D: T1 → T2(반복 2) → dilate(4) 전체 ≡ CPU 순서 — 무작위 10 페이지", ok);
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
