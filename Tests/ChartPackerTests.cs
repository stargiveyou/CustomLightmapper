using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// A4 차트 패킹(<see cref="ChartPacker"/>: Shelf · MaxRects · Best) 검증 — 합성 차트 세트, 씬·GPU 불필요(헤드리스 .NET 실행 가능).
    ///  A) 계약(세 방식 × 전 세트): UV ⊂ [0,1]² · 차트 bbox 서로 gutter 이상 · [0,1] 경계와 gutter/2 이상 떨어짐 · 균일 스케일(차트별 면적비 동일) · 감기 방향 보존 · 유한
    ///  B) Shelf 모드 ≡ ShelfPacker.Pack(비트 동일) · Best 스케일 ≥ Shelf(전 세트) · Best 가 Shelf 를 고르면 Shelf 와 비트 동일
    ///  C) 점유 개선: 차트가 많은 세트(건물·유기체·대·소 혼합)에서 Best 점유 ≥ Shelf × 1.05
    ///  D) 결정성: 같은 입력 두 번 → 비트 동일
    ///  E) 퇴화: 차트 1개 · 면적 0 차트 · gutter 0 · 빈 배열 — 예외·NaN 없음 · 차트 수 상한 초과 → 셸프와 비트 동일
    ///  [표] 세트별 점유율 Shelf → MaxRects → Best · [time] 1000 차트
    /// 호출: Debug.Log(ChartPackerTests.RunAll());
    /// </summary>
    public static class ChartPackerTests
    {
        const float Gutter = 0.01f;

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== A4 Chart Packer (Shelf · MaxRects · Best) ===");
            int pass = 0, total = 0;

            var sets = Sets();
            var occ = new Dictionary<string, float[]>();   // 세트 → [Shelf, MaxRects, Best]
            var used = new Dictionary<string, ChartPacker.Method>();
            string contractFail = null;
            bool bestGeShelf = true, shelfSame = true, bestShelfSame = true;
            foreach (var kv in sets)
            {
                var o = new float[3];
                for (int m = 0; m < 3; m++)
                {
                    var method = m == 0 ? ChartPacker.Method.Shelf : m == 1 ? ChartPacker.Method.MaxRects : ChartPacker.Method.Best;
                    var charts = Clone(kv.Value);
                    var before = Clone(kv.Value);
                    var u = ChartPacker.Pack(charts, Gutter, method, out float scale);
                    if (m == 2) used[kv.Key] = u;
                    o[m] = Occupancy(charts);
                    string err = Contract(before, charts, Gutter, scale);
                    if (err != null && contractFail == null) contractFail = $"{kv.Key}/{method}: {err}";

                    if (m == 0)
                    {
                        var legacy = Clone(kv.Value);
                        ShelfPacker.Pack(legacy, Gutter);
                        shelfSame &= SameUV(legacy, charts);
                    }
                    if (m == 2 && u == ChartPacker.Method.Shelf)
                    {
                        var legacy = Clone(kv.Value);
                        ShelfPacker.Pack(legacy, Gutter);
                        bestShelfSame &= SameUV(legacy, charts);
                    }
                }
                bestGeShelf &= o[2] >= o[0] * (1f - 1e-5f);
                occ[kv.Key] = o;
            }

            Check(sb, ref pass, ref total, $"A: 계약(세 방식 × {sets.Count}세트) — [0,1]² · 비겹침 · gutter 간격 · 경계 여백 · 균일 스케일 · 감기 보존 · 유한{(contractFail != null ? " — " + contractFail : "")}",
                  contractFail == null);
            Check(sb, ref pass, ref total, $"B: Shelf ≡ ShelfPacker.Pack(비트) · Best ≥ Shelf(전 세트) · Best 가 Shelf 를 고르면 비트 동일",
                  shelfSame && bestGeShelf && bestShelfSame);

            string[] many = { "building40", "organic150", "mixed3+500" };
            bool improved = true;
            var gains = new StringBuilder();
            foreach (var k in many)
            {
                float g = occ[k][2] / Mathf.Max(1e-6f, occ[k][0]);
                gains.Append($" {k} ×{g:0.00}");
                improved &= g >= 1.05f;
            }
            Check(sb, ref pass, ref total, $"C: 차트 많은 세트 점유 Best ≥ Shelf × 1.05 —{gains}", improved);

            {
                var a = Clone(sets["organic150"]); var b = Clone(sets["organic150"]);
                ChartPacker.Pack(a, Gutter, ChartPacker.Method.Best);
                ChartPacker.Pack(b, Gutter, ChartPacker.Method.Best);
                Check(sb, ref pass, ref total, "D: 결정성 — 같은 입력 두 번 Best → 비트 동일", SameUV(a, b));
            }

            Check(sb, ref pass, ref total, $"E: 퇴화(차트 1개 · 면적 0 차트 · gutter 0 · 빈 배열 · 상한 {ChartPacker.MaxRectsChartLimit} 초과 → 셸프) — 예외·NaN 없음 {Degenerate(out string dmsg)}", dmsg == null);

            sb.AppendLine("  [표] 점유율(Σ차트 UV 면적 / [0,1]²) Shelf → MaxRects → Best(사용)");
            foreach (var kv in occ)
                sb.AppendLine($"    {kv.Key,-14} {kv.Value[0] * 100f,5:0.0}% → {kv.Value[1] * 100f,5:0.0}% → {kv.Value[2] * 100f,5:0.0}% ({used[kv.Key]}, 차트 {sets[kv.Key].Length})");

            {
                var big = RandomRects(1000, 77, 0.02f, 0.3f, true);
                var sw = Stopwatch.StartNew();
                var c1 = Clone(big); ChartPacker.Pack(c1, Gutter, ChartPacker.Method.Shelf);
                double tShelf = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                var c2 = Clone(big); var u2 = ChartPacker.Pack(c2, Gutter, ChartPacker.Method.Best, out _);
                double tBest = sw.Elapsed.TotalMilliseconds;
                sb.AppendLine($"  [time] 차트 1000개: Shelf {tShelf:0.0} ms ({Occupancy(c1) * 100f:0.0}%) · Best {tBest:0.0} ms ({Occupancy(c2) * 100f:0.0}%, {u2})");
            }

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ───────── 합성 세트(정규화 UV = 메시 면적 단위 — DensityNormalizer 를 거친 상태와 같게 positions = (u, 0, v)) ─────────

        static Dictionary<string, ChartMesh[]> Sets()
        {
            var d = new Dictionary<string, ChartMesh[]>();
            // Unity Cube: 1×1 면 6개(축 정렬 — Planar)
            var cube = new List<ChartMesh>();
            for (int i = 0; i < 6; i++) cube.Add(Poly(RectPts(1f, 1f), 0f, Vector2.zero));
            d["cube"] = cube.ToArray();
            // Unity Cylinder(r 0.5, h 2): 옆면 π×2 (LSCM 이 기울여 냄 23°) + 뚜껑 원 2개
            d["cylinder"] = new[] { Poly(RectPts(Mathf.PI, 2f), 23f * Mathf.Deg2Rad, Vector2.zero), Poly(Disk(0.5f, 20), 0f, Vector2.zero), Poly(Disk(0.5f, 20), 0f, Vector2.zero) };
            // 캡슐 유사: 옆면 π×1 기울어짐 + 반구 2개(원판 등가 r 0.7) + 가는 조각 4개
            var cap = new List<ChartMesh> { Poly(RectPts(Mathf.PI, 1f), -31f * Mathf.Deg2Rad, Vector2.zero), Poly(Disk(0.7f, 24), 0f, Vector2.zero), Poly(Disk(0.7f, 24), 0f, Vector2.zero) };
            for (int i = 0; i < 4; i++) cap.Add(Poly(RectPts(1.2f, 0.12f), (10f + 25f * i) * Mathf.Deg2Rad, Vector2.zero));
            d["capsule-like"] = cap.ToArray();
            // 구 유사: 크기 비슷한 불규칙 볼록 다각형 8개(무작위 회전)
            d["sphere-like"] = RandomPolys(8, 11, 0.6f, 0.9f);
            // 건물: 축 정렬 직사각형 40개(종횡비 1~8, 크기 로그 균등)
            d["building40"] = RandomRects(40, 5, 0.15f, 2f, false);
            // 유기체(바위·나무): 볼록 다각형 150개(크기 0.05~1, 무작위 회전)
            d["organic150"] = RandomPolys(150, 9, 0.05f, 1f);
            // 대·소 혼합: 큰 차트 3개 + 작은 차트 500개
            var mixed = new List<ChartMesh> { Poly(RectPts(2f, 1.5f), 0f, Vector2.zero), Poly(RectPts(1.6f, 1.6f), 0.3f, Vector2.zero), Poly(Disk(0.9f, 24), 0f, Vector2.zero) };
            mixed.AddRange(RandomRects(500, 13, 0.02f, 0.12f, true));
            d["mixed3+500"] = mixed.ToArray();
            return d;
        }

        static ChartMesh[] RandomRects(int n, int seed, float minSide, float maxSide, bool rotate)
        {
            var r = new System.Random(seed);
            var a = new ChartMesh[n];
            for (int i = 0; i < n; i++)
            {
                float s = minSide * Mathf.Pow(maxSide / minSide, (float)r.NextDouble());
                float aspect = 1f + 7f * (float)r.NextDouble() * (float)r.NextDouble();
                float w = s, h = s / aspect;
                if (r.NextDouble() < 0.5) (w, h) = (h, w);
                a[i] = Poly(RectPts(w, h), rotate ? (float)(r.NextDouble() * Mathf.PI) : 0f, new Vector2((float)r.NextDouble() * 5f, (float)r.NextDouble() * 5f));
            }
            return a;
        }

        static ChartMesh[] RandomPolys(int n, int seed, float minR, float maxR)
        {
            var r = new System.Random(seed);
            var a = new ChartMesh[n];
            for (int i = 0; i < n; i++)
            {
                float rad = minR * Mathf.Pow(maxR / minR, (float)r.NextDouble());
                int k = 5 + r.Next(8);
                var angs = new float[k];
                for (int j = 0; j < k; j++) angs[j] = (float)(r.NextDouble() * 2.0 * System.Math.PI);
                System.Array.Sort(angs);
                float sx = 0.5f + (float)r.NextDouble(), sy = 0.5f + (float)r.NextDouble();
                var pts = new Vector2[k];
                for (int j = 0; j < k; j++) pts[j] = new Vector2(Mathf.Cos(angs[j]) * rad * sx, Mathf.Sin(angs[j]) * rad * sy);   // 타원 위 점 → 볼록
                a[i] = Poly(pts, (float)(r.NextDouble() * Mathf.PI), new Vector2((float)r.NextDouble() * 3f, (float)r.NextDouble() * 3f));
            }
            return a;
        }

        static Vector2[] RectPts(float w, float h) => new[] { new Vector2(0f, 0f), new Vector2(w, 0f), new Vector2(w, h), new Vector2(0f, h) };

        static Vector2[] Disk(float r, int seg)
        {
            var p = new Vector2[seg];
            for (int i = 0; i < seg; i++) { float a = 2f * Mathf.PI * i / seg; p[i] = new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r); }
            return p;
        }

        // 반시계 볼록 다각형 → 팬 삼각형 차트(회전·이동 적용). positions = (u, 0, v) — UV 면적 = 메시 면적.
        static ChartMesh Poly(Vector2[] pts, float rot, Vector2 off)
        {
            float c = Mathf.Cos(rot), s = Mathf.Sin(rot);
            var uv = new Vector2[pts.Length];
            var pos = new Vector3[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                var p = pts[i];
                uv[i] = new Vector2(p.x * c - p.y * s + off.x, p.x * s + p.y * c + off.y);
                pos[i] = new Vector3(uv[i].x, 0f, uv[i].y);
            }
            var tri = new int[(pts.Length - 2) * 3];
            for (int i = 0; i < pts.Length - 2; i++) { tri[3 * i] = 0; tri[3 * i + 1] = i + 1; tri[3 * i + 2] = i + 2; }
            return new ChartMesh { positions = pos, Triangles = tri, UV = uv, Loops = new List<int[]>() };
        }

        internal static ChartMesh[] Clone(ChartMesh[] src)
        {
            var a = new ChartMesh[src.Length];
            for (int i = 0; i < src.Length; i++)
                a[i] = new ChartMesh { chartID = src[i].chartID, positions = src[i].positions, Triangles = src[i].Triangles, Loops = src[i].Loops, UV = (Vector2[])src[i].UV.Clone() };
            return a;
        }

        // ───────── 검사 ─────────

        static float SignedArea(ChartMesh c)
        {
            float a = 0f;
            var t = c.Triangles; var u = c.UV;
            for (int i = 0; i < t.Length; i += 3)
            {
                Vector2 A = u[t[i]], B = u[t[i + 1]], C = u[t[i + 2]];
                a += 0.5f * ((B.x - A.x) * (C.y - A.y) - (B.y - A.y) * (C.x - A.x));
            }
            return a;
        }

        internal static float Occupancy(ChartMesh[] charts)
        {
            double a = 0;
            foreach (var c in charts) a += System.Math.Abs(SignedArea(c));
            return (float)a;
        }

        // 위반이 있으면 사유, 없으면 null
        internal static string Contract(ChartMesh[] before, ChartMesh[] after, float gutter, float scale)
        {
            int n = after.Length;
            const float eps = 1e-5f;
            var mn = new Vector2[n]; var mx = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                foreach (var p in after[i].UV)
                {
                    if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.x) || float.IsInfinity(p.y)) return $"차트 {i} NaN/Inf";
                    if (p.x < -eps || p.y < -eps || p.x > 1f + eps || p.y > 1f + eps) return $"차트 {i} [0,1] 밖 ({p.x:0.000000},{p.y:0.000000})";
                }
                ShelfPacker.Bounds(after[i].UV, out mn[i], out mx[i]);
                float a0 = SignedArea(before[i]), a1 = SignedArea(after[i]);
                if (Mathf.Abs(a0) > 1e-9f)
                {
                    if (Mathf.Sign(a0) != Mathf.Sign(a1)) return $"차트 {i} 감기 방향 뒤집힘";
                    float ratio = a1 / a0, expect = scale * scale;
                    if (Mathf.Abs(ratio - expect) > 1e-3f * expect) return $"차트 {i} 스케일 불균일(면적비 {ratio:0.000000} ≠ {expect:0.000000})";
                }
            }
            float gap = gutter * scale - 1e-5f;
            float margin = 0.5f * gutter * scale - 1e-5f;   // 셀 안 gutter/2 오프셋 → [0,1] 경계와도 gutter/2 이상(셸프와 같은 계약)
            for (int i = 0; i < n; i++)
                if (Mathf.Min(Mathf.Min(mn[i].x, mn[i].y), Mathf.Min(1f - mx[i].x, 1f - mx[i].y)) < margin)
                    return $"차트 {i} 경계 여백 < gutter/2";
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    float sepX = Mathf.Max(mn[j].x - mx[i].x, mn[i].x - mx[j].x);
                    float sepY = Mathf.Max(mn[j].y - mx[i].y, mn[i].y - mx[j].y);
                    if (Mathf.Max(sepX, sepY) < gap) return $"차트 {i}·{j} bbox 간격 {Mathf.Max(sepX, sepY):0.000000} < gutter {gutter * scale:0.000000}";
                }
            return null;
        }

        static bool SameUV(ChartMesh[] a, ChartMesh[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].UV.Length != b[i].UV.Length) return false;
                for (int k = 0; k < a[i].UV.Length; k++)
                    if (a[i].UV[k].x != b[i].UV[k].x || a[i].UV[k].y != b[i].UV[k].y) return false;
            }
            return true;
        }

        static string Degenerate(out string msg)
        {
            msg = null;
            try
            {
                ChartPacker.Pack(new ChartMesh[0], Gutter, ChartPacker.Method.Best);

                var one = new[] { Poly(RectPts(2f, 0.5f), 0.4f, Vector2.zero) };
                ChartPacker.Pack(one, Gutter, ChartPacker.Method.Best, out float s1);
                if (Contract(new[] { Poly(RectPts(2f, 0.5f), 0.4f, Vector2.zero) }, one, Gutter, s1) is string e1) msg = "차트 1개: " + e1;

                var flat = new ChartMesh { positions = new Vector3[3], Triangles = new[] { 0, 1, 2 }, UV = new[] { Vector2.zero, Vector2.zero, Vector2.zero } };
                var withFlat = new[] { Poly(RectPts(1f, 1f), 0f, Vector2.zero), flat };
                ChartPacker.Pack(withFlat, Gutter, ChartPacker.Method.Best);
                foreach (var c in withFlat) foreach (var p in c.UV) if (float.IsNaN(p.x) || float.IsNaN(p.y)) msg ??= "면적 0 차트: NaN";

                var over = RandomRects(ChartPacker.MaxRectsChartLimit + 1, 3, 0.05f, 0.2f, true);
                var overShelf = Clone(over);
                ShelfPacker.Pack(overShelf, Gutter);
                if (ChartPacker.Pack(over, Gutter, ChartPacker.Method.Best, out _) != ChartPacker.Method.Shelf || !SameUV(over, overShelf))
                    msg ??= $"차트 {ChartPacker.MaxRectsChartLimit + 1}개(상한 초과)가 셸프와 다름";

                var noGutter = Clone(Sets()["sphere-like"]);
                var before = Clone(noGutter);
                ChartPacker.Pack(noGutter, 0f, ChartPacker.Method.Best, out float s0);
                if (Contract(before, noGutter, 0f, s0) is string e0) msg ??= "gutter 0: " + e0;
            }
            catch (System.Exception e) { msg = "예외 " + e.GetType().Name + ": " + e.Message; }
            return msg == null ? "" : "— " + msg;
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
