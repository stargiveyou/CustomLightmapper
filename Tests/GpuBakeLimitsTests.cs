using System;
using System.Collections.Generic;
using System.Text;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// G6-4 검증: <see cref="GpuBakeLimits"/> — 순수 C#(헤드리스).
    ///  A) Tiles: [0,n) 를 빈틈·겹침 없이 덮음 · 구간 ≤ chunk · ≤ 디스패치 한도(그룹 ≤ 65535) · chunk ≤ 0 = 기본 · n = 0 → 없음.
    ///  B) MaxLeafDepth: 리프 1개 = 0 · 완전 이진트리 깊이 d · 사슬 · BLAS 오프셋(start) 무관 · 잘못된 인덱스/순환 → 예외.
    ///  C) StackUsage: 완전 트리 = 깊이+1 · 오른쪽 깊은 사슬 = 깊이+1 · 왼쪽 깊은 사슬 = 2(깊이 무관) ·
    ///     무작위 트리 300개에서 '독립 참조 구현(재귀로 같은 순서 재현)' 과 일치하고 ≤ 깊이+1.
    /// 호출: Debug.Log(GpuBakeLimitsTests.RunAll());
    /// </summary>
    public static class GpuBakeLimitsTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== G6-4 GPU 베이크 한계 (분할 디스패치 · 순회 스택) ===");
            int pass = 0, total = 0;
            TestTiles(sb, ref pass, ref total);
            TestDepth(sb, ref pass, ref total);
            TestStack(sb, ref pass, ref total);
            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ── A) Tiles ──
        static void TestTiles(StringBuilder sb, ref int pass, ref int total)
        {
            bool ok = true;
            foreach (int n in new[] { 1, 63, 64, 65, 1000, 65536, 65537, 1_000_003 })
                foreach (int chunk in new[] { 64, 977, 65536, 0, -5, int.MaxValue })
                {
                    var t = GpuBakeLimits.Tiles(n, chunk);
                    int eff = chunk > 0 ? Math.Min(chunk, GpuBakeLimits.MaxPointsPerDispatch) : GpuBakeLimits.DefaultChunk;
                    int next = 0;
                    foreach (var (start, count) in t)
                    {
                        ok &= start == next && count > 0 && count <= eff && GpuBakeLimits.Groups(count) <= GpuBakeLimits.MaxGroupsPerDispatch;
                        next = start + count;
                    }
                    ok &= next == n && t.Count == (n + eff - 1) / eff;
                }
            ok &= GpuBakeLimits.Tiles(0, 100).Count == 0 && GpuBakeLimits.Tiles(-3, 100).Count == 0;
            var big = GpuBakeLimits.Tiles(10_000_000, int.MaxValue);
            ok &= big.Count == 3 && big[0].count == GpuBakeLimits.MaxPointsPerDispatch;
            Check(sb, ref pass, ref total, $"A: Tiles — 빈틈·겹침 없음 · ≤ chunk · 그룹 ≤ {GpuBakeLimits.MaxGroupsPerDispatch} · 기본 chunk · 1000만 점 → 한도 {GpuBakeLimits.MaxPointsPerDispatch}씩 {big.Count}회", ok);
        }

        // ── B) MaxLeafDepth ──
        static void TestDepth(StringBuilder sb, ref int pass, ref int total)
        {
            var leaf = Complete(0);
            var c5 = Complete(5);
            var rc = Chain(40, rightDeep: true);
            var lc = Chain(40, rightDeep: false);
            bool ok = GpuBakeLimits.MaxLeafDepth(leaf.lf, leaf.ct, 0, leaf.lf.Length) == 0
                   && GpuBakeLimits.MaxLeafDepth(c5.lf, c5.ct, 0, c5.lf.Length) == 5
                   && GpuBakeLimits.MaxLeafDepth(rc.lf, rc.ct, 0, rc.lf.Length) == 40
                   && GpuBakeLimits.MaxLeafDepth(lc.lf, lc.ct, 0, lc.lf.Length) == 40
                   && GpuBakeLimits.MaxLeafDepth(new int[0], new int[0], 0, 0) == -1;

            // BLAS 이어 붙이기: 앞에 다른 트리(노드 7개)를 두고 start 오프셋으로 같은 트리
            var pre = Complete(2);
            var lf2 = new int[pre.lf.Length + c5.lf.Length];
            var ct2 = new int[lf2.Length];
            Array.Copy(pre.lf, lf2, pre.lf.Length); Array.Copy(pre.ct, ct2, pre.ct.Length);
            Array.Copy(c5.lf, 0, lf2, pre.lf.Length, c5.lf.Length); Array.Copy(c5.ct, 0, ct2, pre.ct.Length, c5.ct.Length);
            ok &= GpuBakeLimits.MaxLeafDepth(lf2, ct2, pre.lf.Length, c5.lf.Length) == 5
               && GpuBakeLimits.StackUsage(lf2, ct2, pre.lf.Length, c5.lf.Length) == 6;

            bool threwIdx = false, threwCycle = false;
            try { GpuBakeLimits.MaxLeafDepth(new[] { 5 }, new[] { 0 }, 0, 1); } catch (ArgumentOutOfRangeException) { threwIdx = true; }
            try { GpuBakeLimits.StackUsage(new[] { 1, 0, 0 }, new[] { 0, 0, 1 }, 0, 3); } catch (InvalidOperationException) { threwCycle = true; }   // 노드 1 이 다시 0·1 을 가리킴
            Check(sb, ref pass, ref total, "B: MaxLeafDepth — 리프 0 · 완전 5 · 사슬 40 · 빈 트리 -1 · BLAS 오프셋 무관 · 잘못된 인덱스/순환 예외",
                  ok && threwIdx && threwCycle);
        }

        // ── C) StackUsage ──
        static void TestStack(StringBuilder sb, ref int pass, ref int total)
        {
            var c5 = Complete(5);
            var rc = Chain(40, rightDeep: true);
            var lc = Chain(40, rightDeep: false);
            int sc = GpuBakeLimits.StackUsage(c5.lf, c5.ct, 0, c5.lf.Length);
            int sr = GpuBakeLimits.StackUsage(rc.lf, rc.ct, 0, rc.lf.Length);
            int sl = GpuBakeLimits.StackUsage(lc.lf, lc.ct, 0, lc.lf.Length);
            Check(sb, ref pass, ref total, $"C: 완전 트리 깊이 5 → {sc}(=6) · 오른쪽 깊은 사슬 40 → {sr}(=41 > GPU {GpuBakeLimits.TraversalStack}) · 왼쪽 깊은 사슬 40 → {sl}(=2)",
                  sc == 6 && sr == 41 && sl == 2 && sr > GpuBakeLimits.TraversalStack);

            var rng = new Random(20261026);
            bool same = true, bounded = true;
            int maxSeen = 0;
            for (int t = 0; t < 300; t++)
            {
                var tree = RandomTree(rng, 1 + rng.Next(400));
                int got = GpuBakeLimits.StackUsage(tree.lf, tree.ct, 0, tree.lf.Length);
                int refv = ReferenceUsage(tree.lf, tree.ct);
                int depth = GpuBakeLimits.MaxLeafDepth(tree.lf, tree.ct, 0, tree.lf.Length);
                same &= got == refv;
                bounded &= got <= GpuBakeLimits.RequiredStack(depth) && got >= 1;
                maxSeen = Math.Max(maxSeen, got);
            }
            Check(sb, ref pass, ref total, $"C: 무작위 트리 300개 — StackUsage ≡ 재귀 참조 구현 · ≤ 깊이+1 (최대 {maxSeen})", same && bounded);
        }

        // ───────────────────────── 트리 생성 (BVH 평탄 배치: 내부 노드 자식 = leftFirst, leftFirst+1) ─────────────────────────

        struct Tree { public int[] lf, ct; }

        static Tree Complete(int depth)
        {
            var lf = new List<int>(); var ct = new List<int>();
            lf.Add(0); ct.Add(0);
            var frontier = new List<(int node, int d)> { (0, 0) };
            while (frontier.Count > 0)
            {
                var next = new List<(int, int)>();
                foreach (var (node, d) in frontier)
                {
                    if (d == depth) { ct[node] = 1; continue; }
                    int l = lf.Count;
                    lf[node] = l;
                    lf.Add(0); ct.Add(0); lf.Add(0); ct.Add(0);
                    next.Add((l, d + 1)); next.Add((l + 1, d + 1));
                }
                frontier = next;
            }
            return new Tree { lf = lf.ToArray(), ct = ct.ToArray() };
        }

        // 사슬: 매 층에서 한 자식만 내부(rightDeep = left+1 쪽이 내부 — HLSL 이 먼저 꺼내는 쪽)
        static Tree Chain(int depth, bool rightDeep)
        {
            var lf = new List<int> { 0 }; var ct = new List<int> { 0 };
            int cur = 0;
            for (int d = 0; d < depth; d++)
            {
                int l = lf.Count;
                lf[cur] = l;
                lf.Add(0); ct.Add(0); lf.Add(0); ct.Add(0);
                int inner = rightDeep ? l + 1 : l, leafN = rightDeep ? l : l + 1;
                ct[leafN] = 1;
                cur = inner;
            }
            ct[cur] = 1;
            return new Tree { lf = lf.ToArray(), ct = ct.ToArray() };
        }

        // 무작위 이진트리(리프 n 개): 재귀 분할을 BFS 로 평탄화
        static Tree RandomTree(Random rng, int leaves)
        {
            var lf = new List<int> { 0 }; var ct = new List<int> { 0 };
            var q = new Queue<(int node, int leaves)>();
            q.Enqueue((0, leaves));
            while (q.Count > 0)
            {
                var (node, k) = q.Dequeue();
                if (k == 1) { ct[node] = 1 + rng.Next(4); continue; }
                int a = 1 + rng.Next(k - 1);
                int l = lf.Count;
                lf[node] = l;
                lf.Add(0); ct.Add(0); lf.Add(0); ct.Add(0);
                q.Enqueue((l, a)); q.Enqueue((l + 1, k - a));
            }
            return new Tree { lf = lf.ToArray(), ct = ct.ToArray() };
        }

        // 참조: HLSL 순서(push left, push left+1 → 나중 것 먼저)를 재귀로 재현. 노드를 꺼낼 때 스택에 남은 '대기' 수를 들고 내려간다.
        static int ReferenceUsage(int[] lf, int[] ct)
        {
            int max = 1;
            void Visit(int node, int pendingBelow)   // pendingBelow = 이 노드를 꺼낸 뒤 스택에 남아 있는 칸 수
            {
                if (ct[node] > 0) return;
                int l = lf[node];
                max = Math.Max(max, pendingBelow + 2);
                Visit(l + 1, pendingBelow + 1);   // 먼저 꺼냄 — 형제 l 이 아래에 대기
                Visit(l, pendingBelow);
            }
            Visit(0, 0);
            return max;
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
