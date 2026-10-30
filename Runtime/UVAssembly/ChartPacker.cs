using System.Collections.Generic;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// A4 차트 패킹 진입점: 정규화된 차트들을 메시-아틀라스 [0,1]² 에 배치(차트 간 gutter, 균일 스케일 — 텍셀 밀도 균일 유지).
    /// 할당기(<see cref="LightmapAllocator"/>)가 인스턴스마다 정사각 영역을 주므로 목표 = '모든 차트 bbox 를 담는 가장 작은 정사각형'
    /// (스케일 = 1/변 — 클수록 같은 영역에서 텍셀 밀도↑).
    ///  - <see cref="Method.Shelf"/>   : <see cref="ShelfPacker"/>(높이순 선반 + 회전 후보 3종) — 구 거동, 결과 비트 동일.
    ///  - <see cref="Method.MaxRects"/>: MaxRects(Best Short Side Fit, 차트마다 0°/90° 선택) + 정사각 변 이분 탐색.
    ///                                   차트 ≤ 256 이면 bbox 방향 2종(그대로 / 최소 면적 bbox) × 배치 순서 2종(긴 변 / 면적)을 모두 풀어 가장 작은 변,
    ///                                   그보다 많으면 최소 면적 bbox × 긴 변 순서 하나. 실패하면 셸프.
    ///  - <see cref="Method.Best"/>(기본): 둘 다 계산해 스케일이 '엄격히' 큰 쪽. 동률이면 셸프 → 어떤 메시도 이전보다 점유가 줄지 않는다.
    /// 회전은 모두 고유 회전(det +1)이라 삼각형 감기 방향·면적이 보존된다. 결정적(정렬·동률 규칙 고정).
    /// </summary>
    public static class ChartPacker
    {
        public enum Method { Best, Shelf, MaxRects }

        /// <summary>
        /// MaxRects 를 시도하는 차트 수 상한. 넘으면 Best·MaxRects 모두 셸프. MaxRects 비용 ≈ 차트 수² × 이분 탐색(~9회) —
        /// 헤드리스 측정(.NET, 무작위 볼록 차트): 150개 ≈ 10 ms · 500개 ≈ 40 ms · 1000개 ≈ 150 ms · 2000개 ≈ 600 ms. 베이커는 인스턴스마다 패킹하므로 1024 에서 자른다.
        /// </summary>
        public const int MaxRectsChartLimit = 1024;

        /// <summary>차트 UV 를 제자리에서 [0,1]² 로 배치한다.</summary>
        public static void Pack(ChartMesh[] charts, float gutter = 0.01f, Method method = Method.Best) => Pack(charts, gutter, method, out _);

        /// <summary>배치하고 실제로 쓴 방식(Shelf/MaxRects)을 반환. scale = 정규화 UV → [0,1] 균일 스케일(점유율 = scale² × Σ 정규화 UV 면적).</summary>
        public static Method Pack(ChartMesh[] charts, float gutter, Method method, out float scale)
        {
            scale = 0f;
            if (charts == null || charts.Length == 0) return Method.Shelf;

            // 셸프는 싸다(차트 1000개 ≈ 8 ms) — 모드와 무관하게 먼저 풀어 MaxRects 탐색 상한으로 쓴다(Best 와 MaxRects 단독이 같은 탐색 → 같은 결과).
            float shelfScale = ShelfPacker.Choose(charts, gutter, out int shelfBest, out float[] angle);

            if (method != Method.Shelf && charts.Length <= MaxRectsChartLimit)
            {
                var plan = MaxRectsPacker.Plan(charts, gutter, angle, shelfScale > 0f ? 1f / shelfScale : 0f);
                if (plan != null && (method == Method.MaxRects || plan.Scale > shelfScale * (1f + 1e-5f)))
                {
                    MaxRectsPacker.Apply(charts, gutter, angle, plan);
                    scale = plan.Scale;
                    return Method.MaxRects;
                }
            }

            ShelfPacker.Apply(charts, gutter, shelfBest, angle);
            scale = shelfScale;
            return Method.Shelf;
        }

        /// <summary>점유율 = Σ 차트 UV 면적(패킹 뒤 [0,1]² 기준 — 1 이면 빈틈 없음). 진단·뷰어용.</summary>
        public static float Occupancy(ChartMesh[] charts)
        {
            double a = 0;
            if (charts == null) return 0f;
            foreach (var c in charts)
            {
                var t = c.Triangles; var u = c.UV;
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    Vector2 A = u[t[i]], B = u[t[i + 1]], C = u[t[i + 2]];
                    a += System.Math.Abs(0.5 * ((B.x - A.x) * (C.y - A.y) - (B.y - A.y) * (C.x - A.x)));
                }
            }
            return (float)a;
        }
    }

    /// <summary>
    /// MaxRects(Jylänki, "A Thousand Ways to Pack the Bin") — 빈 공간을 서로 겹칠 수 있는 최대 자유 사각형 목록으로 관리하고,
    /// 사각형마다 Best Short Side Fit(남는 짧은 변 최소, 동률이면 긴 변)으로 자리·회전(0°/90°)을 고른다.
    /// 정사각 빈의 변 L 을 이분 탐색해 전부 들어가는 최소 L 을 찾는다(휴리스틱이라 L 에 단조가 아님 — 찾은 것 중 가장 작은 성공을 쓴다).
    /// 단위는 정규화 UV(gutter 포함 셀) — 셸프와 같다: 셀 = bbox + gutter, 차트는 셀 안 gutter/2 오프셋.
    /// 비용 절감(결과 불변): 남은 셀 중 가장 짧은 변보다 좁은 자유 사각형은 다시 쓸 일이 없어 버린다(배치 순서상 그 하한은 줄지 않음).
    /// </summary>
    internal static class MaxRectsPacker
    {
        internal sealed class Result
        {
            public float Scale;        // 1 / max(실제 사용 폭, 높이)
            public bool MinBoxBase;    // true = 차트별 최소 면적 bbox 방향(ShelfPacker.MinBoxAngle)으로 돌린 뒤 배치
            public float[] X, Y;       // 셀 왼쪽 아래(정규화 UV)
            public bool[] Rot;         // 셀을 90° 돌려 배치(폭 ↔ 높이)
        }

        const int SearchSteps = 16;
        const float SearchRelTol = 1e-3f;          // 변 상대 정밀도 → 점유 손실 ≤ 0.2%
        // 후보를 늘리는 차트 수 상한: '그대로' 방향 · 면적 순서. 헤드리스 측정에서 최소 bbox × 긴 변 순서가 대부분 이기고(후보 4종 최선과
        // 기하평균 차 ≈ 0.8%), 후보마다 비용이 그대로 더해져 차트가 적을 때만 본다.
        const int ExtraCandidateChartLimit = 256;

        /// <param name="hiHint">탐색 상한 후보(셸프의 변 = 1/셸프 스케일). 0 이면 하한에서 1.25배씩 키워 찾는다.</param>
        internal static Result Plan(ChartMesh[] charts, float gutter, float[] angle, float hiHint = 0f)
        {
            int n = charts.Length;
            bool extra = n <= ExtraCandidateChartLimit;
            Result best = null;
            for (int b = extra ? 0 : 1; b < 2; b++)
            {
                var w = new float[n];
                var h = new float[n];
                for (int i = 0; i < n; i++)
                {
                    Vector2 mn, mx;
                    if (b == 0) ShelfPacker.Bounds(charts[i].UV, out mn, out mx);
                    else ShelfPacker.RotatedBounds(charts[i].UV, angle[i], out mn, out mx);
                    w[i] = (mx.x - mn.x) + gutter;
                    h[i] = (mx.y - mn.y) + gutter;
                }
                for (int byArea = 0; byArea < (extra ? 2 : 1); byArea++)
                {
                    var r = SolveSquare(w, h, hiHint, byArea == 1);
                    if (r == null) continue;
                    r.MinBoxBase = b == 1;
                    if (best == null || r.Scale > best.Scale) best = r;   // 동률이면 먼저 푼 후보('그대로' · 긴 변 순서) 유지
                }
            }
            return best;
        }

        /// <summary>계획대로 UV 를 돌리고 옮긴다. 회전은 ShelfPacker.Rotate(계획의 bbox 와 같은 식) + 셀 90° 는 (dx, dy) → (dy, w − dx).</summary>
        internal static void Apply(ChartMesh[] charts, float gutter, float[] angle, Result plan)
        {
            float half = gutter * 0.5f;
            for (int i = 0; i < charts.Length; i++)
            {
                var uv = charts[i].UV;
                if (plan.MinBoxBase) ShelfPacker.Rotate(uv, angle[i]);
                ShelfPacker.Bounds(uv, out var mn, out var mx);
                float w = mx.x - mn.x;
                float gx = plan.X[i] + half, gy = plan.Y[i] + half;
                for (int k = 0; k < uv.Length; k++)
                {
                    float dx = uv[k].x - mn.x, dy = uv[k].y - mn.y;
                    float x = plan.Rot[i] ? dy : dx;
                    float y = plan.Rot[i] ? w - dx : dy;   // 시계 방향 90°(det +1 — 감기 방향 보존)
                    uv[k] = new Vector2((x + gx) * plan.Scale, (y + gy) * plan.Scale);
                }
            }
        }

        // 정사각 빈 최소 변 탐색. 하한 = max(√Σ셀 면적, 가장 긴 셀 변) — 그 아래는 불가능. 실패하면 null.
        internal static Result SolveSquare(float[] w, float[] h, float hiHint = 0f, bool byArea = false)
        {
            int n = w.Length;
            double area = 0;
            float longest = 0f;
            for (int i = 0; i < n; i++) { area += (double)w[i] * h[i]; longest = Mathf.Max(longest, Mathf.Max(w[i], h[i])); }
            float lo = Mathf.Max((float)System.Math.Sqrt(area), longest);
            if (!(lo > 1e-12f) || float.IsInfinity(lo)) return null;

            // 큰 셀부터(긴 변 → 면적 → 인덱스, byArea 면 면적 → 긴 변 → 인덱스) — 결정적 순서
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            System.Array.Sort(order, (a, b) =>
            {
                float la = Mathf.Max(w[a], h[a]), lb = Mathf.Max(w[b], h[b]);
                float aa = w[a] * h[a], ab = w[b] * h[b];
                if (byArea && aa != ab) return ab.CompareTo(aa);
                if (la != lb) return lb.CompareTo(la);
                if (aa != ab) return ab.CompareTo(aa);
                return a.CompareTo(b);
            });

            // 배치 k 이후 남은 셀들의 짧은 변·긴 변 최솟값 — 이보다 작은 자유 사각형은 쓸모없음
            var minShort = new float[n + 1];
            var minLong = new float[n + 1];
            minShort[n] = minLong[n] = float.MaxValue;
            for (int k = n - 1; k >= 0; k--)
            {
                int i = order[k];
                minShort[k] = Mathf.Min(minShort[k + 1], Mathf.Min(w[i], h[i]));
                minLong[k] = Mathf.Min(minLong[k + 1], Mathf.Max(w[i], h[i]));
            }

            var x = new float[n]; var y = new float[n]; var rot = new bool[n];
            Result best = null;
            void Keep(float L)
            {
                float ext = 0f;
                for (int i = 0; i < n; i++)
                {
                    float cw = rot[i] ? h[i] : w[i], ch = rot[i] ? w[i] : h[i];
                    ext = Mathf.Max(ext, Mathf.Max(x[i] + cw, y[i] + ch));
                }
                float s = 1f / ext;
                if (best == null || s > best.Scale)
                    best = new Result { Scale = s, X = (float[])x.Clone(), Y = (float[])y.Clone(), Rot = (bool[])rot.Clone() };
            }

            bool F(float L) => Fits(L, w, h, order, minShort, minLong, x, y, rot);
            if (F(lo)) { Keep(lo); return best; }

            float hi = hiHint > lo ? hiHint : lo * 1.25f;
            bool ok = F(hi);
            for (int g = 0; g < 64 && !ok; g++)
            {
                lo = hi;
                hi *= 1.25f;
                ok = F(hi);
            }
            if (!ok) return null;
            Keep(hi);

            for (int step = 0; step < SearchSteps && (hi - lo) > SearchRelTol * hi; step++)
            {
                float mid = 0.5f * (lo + hi);
                if (F(mid)) { hi = mid; Keep(mid); }
                else lo = mid;
            }
            return best;
        }

        struct Rect { public float x, y, w, h; public Rect(float x, float y, float w, float h) { this.x = x; this.y = y; this.w = w; this.h = h; } }

        // L×L 빈에 order 순서로 배치. 하나라도 못 넣으면 false. 성공하면 x/y/rot 에 셀 위치.
        static bool Fits(float L, float[] w, float[] h, int[] order, float[] minShort, float[] minLong, float[] px, float[] py, bool[] rot)
        {
            var free = new List<Rect> { new Rect(0f, 0f, L, L) };
            var fresh = new List<Rect>();
            for (int k = 0; k < order.Length; k++)
            {
                int i = order[k];
                if (!FindBssf(free, w[i], h[i], minShort[k], minLong[k], out Rect node, out bool r)) return false;
                px[i] = node.x; py[i] = node.y; rot[i] = r;

                fresh.Clear();
                for (int f = free.Count - 1; f >= 0; f--)
                    if (Split(free[f], node, fresh)) RemoveSwap(free, f);   // 뒤에서부터 — 옮겨 온 원소는 이미 본 것

                // 가지치기: 새 자유 사각형만 검사하면 된다 — 새 것은 지워진 자유 사각형의 일부라 기존(서로 포함 없음)을 포함할 수 없다.
                for (int a = 0; a < fresh.Count; a++)
                {
                    var ra = fresh[a];
                    bool drop = ra.w <= 0f || ra.h <= 0f || Useless(ra, minShort[k + 1], minLong[k + 1]);
                    for (int b = 0; !drop && b < free.Count; b++) drop = Contains(free[b], ra);
                    for (int b = 0; !drop && b < fresh.Count; b++)
                        if (b != a && Contains(fresh[b], ra) && (!Same(fresh[b], ra) || b < a)) drop = true;   // 같은 것 둘이면 앞쪽만 남김
                    if (!drop) free.Add(ra);
                }
            }
            return true;
        }

        // Best Short Side Fit: 남는 짧은 변 최소, 동률이면 긴 변 최소. 0° 먼저 → 동률이면 회전하지 않음.
        // 훑으면서 이제 쓸모없어진 자유 사각형(남은 셀 하한보다 좁음)을 지운다 — 어떤 셀도 못 들어가므로 선택 결과 불변.
        static bool FindBssf(List<Rect> free, float w, float h, float minShort, float minLong, out Rect node, out bool rotated)
        {
            node = default; rotated = false;
            float bestShort = float.MaxValue, bestLong = float.MaxValue;
            bool found = false;
            for (int f = free.Count - 1; f >= 0; f--)
            {
                var fr = free[f];
                if (Useless(fr, minShort, minLong)) { RemoveSwap(free, f); continue; }
                if (fr.w >= w && fr.h >= h)
                {
                    float lw = fr.w - w, lh = fr.h - h;
                    float sh = Mathf.Min(lw, lh), lg = Mathf.Max(lw, lh);
                    if (sh < bestShort || (sh == bestShort && lg < bestLong))
                    { node = new Rect(fr.x, fr.y, w, h); rotated = false; bestShort = sh; bestLong = lg; found = true; }
                }
                if (fr.w >= h && fr.h >= w)
                {
                    float lw = fr.w - h, lh = fr.h - w;
                    float sh = Mathf.Min(lw, lh), lg = Mathf.Max(lw, lh);
                    if (sh < bestShort || (sh == bestShort && lg < bestLong))
                    { node = new Rect(fr.x, fr.y, h, w); rotated = true; bestShort = sh; bestLong = lg; found = true; }
                }
            }
            return found;
        }

        // used 와 겹치면 fr 의 used 밖 부분(최대 4개, 서로 겹칠 수 있음)을 out 에 넣고 true.
        static bool Split(Rect fr, Rect used, List<Rect> outRects)
        {
            if (used.x >= fr.x + fr.w || used.x + used.w <= fr.x || used.y >= fr.y + fr.h || used.y + used.h <= fr.y) return false;
            if (used.x < fr.x + fr.w && used.x + used.w > fr.x)
            {
                if (used.y > fr.y && used.y < fr.y + fr.h) outRects.Add(new Rect(fr.x, fr.y, fr.w, used.y - fr.y));
                if (used.y + used.h < fr.y + fr.h) outRects.Add(new Rect(fr.x, used.y + used.h, fr.w, fr.y + fr.h - (used.y + used.h)));
            }
            if (used.y < fr.y + fr.h && used.y + used.h > fr.y)
            {
                if (used.x > fr.x && used.x < fr.x + fr.w) outRects.Add(new Rect(fr.x, fr.y, used.x - fr.x, fr.h));
                if (used.x + used.w < fr.x + fr.w) outRects.Add(new Rect(used.x + used.w, fr.y, fr.x + fr.w - (used.x + used.w), fr.h));
            }
            return true;
        }

        static bool Useless(Rect r, float minShort, float minLong) => Mathf.Min(r.w, r.h) < minShort || Mathf.Max(r.w, r.h) < minLong;

        static void RemoveSwap(List<Rect> list, int i)
        {
            int last = list.Count - 1;
            list[i] = list[last];
            list.RemoveAt(last);
        }

        static bool Contains(Rect outer, Rect inner) =>
            inner.x >= outer.x && inner.y >= outer.y && inner.x + inner.w <= outer.x + outer.w && inner.y + inner.h <= outer.y + outer.h;

        static bool Same(Rect a, Rect b) => a.x == b.x && a.y == b.y && a.w == b.w && a.h == b.h;
    }
}
