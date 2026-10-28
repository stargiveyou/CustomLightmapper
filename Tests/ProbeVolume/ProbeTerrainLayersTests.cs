using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Track B B2 검증: <see cref="ProbeTerrainLayers"/>(지형 추종 2.5D 배치) — 순수 수학이라 씬·GPU 불필요(헤드리스).
    ///  A) 배치 격자: XZ = 같은 볼륨 균일 격자 · y = (오프셋, 층 수) · LayerCount 규칙(FromBounds 축 규칙과 같은 식) · 간격 0 예외.
    ///  B) 높이 없음(null·빈 배열) ≡ 균일 격자 — 위치·샘플(valid 유/무, 볼륨 밖 포함) 비트 동일.
    ///  C) 높이 전부 c ≡ origin.y = c + 오프셋 인 균일 격자 — 위치·샘플 비트 동일.
    ///  D) 열 높이 bilinear: 열 중심 = 그 열 높이 · 선형 지면 재현 · 볼륨 밖 경계 클램프 · 1열 축.
    ///  E) 경사 지형 샘플: 프로브 위치 = 그 프로브 · 격자 좌표 선형장(지면 기준 층 좌표)을 내부 임의점에서 재현 · 지면 위 같은 높이 = 같은 층.
    ///  F) 지면 레이(<see cref="ProbeTerrainLayers.ColumnHeights"/>, BruteForceOccluder): 경사면 높이 정확 · 메시 밖 열 = 미스 → 이웃 보간 ·
    ///     지면 없음 = 전부 bottom · 감김 방향 무관.
    ///  G) <see cref="ProbeTerrainLayers.FillMissing"/>: 4-이웃 평균 · 링 순서(스냅샷) · 닿지 않으면 폴백.
    ///  H) 절감 리포트: 평지 0% · 경사 = 균일 환산 대비 비율 · 로그 문자열.
    /// 호출: Debug.Log(ProbeTerrainLayersTests.RunAll());
    /// </summary>
    public static class ProbeTerrainLayersTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Track B 지형 추종 2.5D 배치 (B2) ===");
            int pass = 0, total = 0;
            TestGrid(sb, ref pass, ref total);
            TestZeroHeights(sb, ref pass, ref total);
            TestConstantHeights(sb, ref pass, ref total);
            TestColumnHeight(sb, ref pass, ref total);
            TestSlopeSample(sb, ref pass, ref total);
            TestGroundRays(sb, ref pass, ref total);
            TestFill(sb, ref pass, ref total);
            TestStats(sb, ref pass, ref total);
            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        static ProbeGrid TestGridDef() => new ProbeGrid(new Vector3(-1.3f, 0.2f, 2.1f), new Vector3(0.75f, 0.5f, 1.25f), new Vector3Int(6, 4, 5));

        // ── A) 배치 격자 ──
        static void TestGrid(StringBuilder sb, ref int pass, ref int total)
        {
            var vol = new Bounds(new Vector3(1.3f, 4f, -2.1f), new Vector3(12f, 8f, 9f));
            var sp = new Vector3(2f, 1f, 1.5f);
            var g = ProbeTerrainLayers.Grid(vol, sp, 0.25f, 5f);
            var u = ProbeGrid.FromBounds(vol, sp);
            bool xz = g.origin.x == u.origin.x && g.origin.z == u.origin.z && g.dims.x == u.dims.x && g.dims.z == u.dims.z
                      && g.spacing.Equals(sp) && g.origin.y == 0.25f && g.dims.y == 6;
            Check(sb, ref pass, ref total, $"A: Grid — XZ = 균일 FromBounds({u.dims.x}×·×{u.dims.z}) · origin.y = 오프셋 0.25 · 층 {g.dims.y}(높이 5 / 간격 1 → 6)", xz);

            bool threw = false;
            try { ProbeTerrainLayers.LayerCount(1f, 0f); } catch (ArgumentException) { threw = true; }
            int l0 = ProbeTerrainLayers.LayerCount(0f, 1f), l1 = ProbeTerrainLayers.LayerCount(4.99995f, 1f);
            int l2 = ProbeTerrainLayers.LayerCount(5.2f, 1f), l3 = ProbeTerrainLayers.LayerCount(-3f, 1f), l4 = ProbeTerrainLayers.LayerCount(3f, 0.5f);
            Check(sb, ref pass, ref total, $"A: LayerCount — 0→{l0} · 4.99995→{l1} · 5.2→{l2} · 음수→{l3} · 3/0.5→{l4} · 간격 0 → 예외",
                  l0 == 1 && l1 == 6 && l2 == 7 && l3 == 1 && l4 == 7 && threw);
        }

        // ── B) 높이 없음 ≡ 균일 ──
        static void TestZeroHeights(StringBuilder sb, ref int pass, ref int total)
        {
            var g = TestGridDef();
            var sh = Field(g, new System.Random(3));
            var valid = RandomMask(g.Count, new System.Random(4));
            var rng = new System.Random(5);
            bool posOk = true, smpOk = true;
            foreach (var heights in new[] { null, Array.Empty<float>() })
            {
                var pu = g.Positions();
                var pl = ProbeTerrainLayers.Positions(g, heights);
                posOk &= pu.Length == pl.Length;
                for (int i = 0; posOk && i < pu.Length; i++)
                    posOk &= pu[i].Equals(pl[i]) && ProbeTerrainLayers.Position(g, heights, i).Equals(pu[i]);
                for (int s = 0; s < 300; s++)
                {
                    Vector3 p = AroundVolume(rng, g, g.origin.y, 1.5f);
                    var m = s % 2 == 0 ? valid : null;
                    smpOk &= SameSH(ProbeTerrainLayers.Sample(g, heights, sh, m, p), g.Sample(sh, m, p))
                             && ProbeTerrainLayers.ColumnHeight(g, heights, p.x, p.z) == 0f;
                }
            }
            Check(sb, ref pass, ref total, "B: 높이 없음(null·빈 배열) ≡ 균일 — 위치 · 샘플 600점(valid 유/무, 볼륨 밖 포함) 비트 동일 · H = 0", posOk && smpOk);
        }

        // ── C) 높이 상수 ≡ 이동한 균일 ──
        static void TestConstantHeights(StringBuilder sb, ref int pass, ref int total)
        {
            var g = TestGridDef();
            const float c = 3.7f;
            var heights = Fill(ProbeTerrainLayers.ColumnCount(g), c);
            var u = g;
            u.origin.y = c + g.origin.y;
            var sh = Field(g, new System.Random(7));
            var valid = RandomMask(g.Count, new System.Random(8));
            var rng = new System.Random(9);
            var pu = u.Positions();
            var pl = ProbeTerrainLayers.Positions(g, heights);
            bool posOk = pu.Length == pl.Length;
            for (int i = 0; posOk && i < pu.Length; i++) posOk &= pu[i].Equals(pl[i]);
            bool smpOk = true;
            for (int s = 0; s < 300; s++)
            {
                Vector3 p = AroundVolume(rng, g, u.origin.y, 1.5f);
                var m = s % 2 == 0 ? valid : null;
                smpOk &= SameSH(ProbeTerrainLayers.Sample(g, heights, sh, m, p), u.Sample(sh, m, p))
                         && ProbeTerrainLayers.ColumnHeight(g, heights, p.x, p.z) == c;
            }
            Check(sb, ref pass, ref total, $"C: 높이 전부 {c} ≡ origin.y = {c}+오프셋 균일 격자 — 위치 · 샘플 300점 비트 동일 · H = {c} 정확", posOk && smpOk);
        }

        // ── D) 열 높이 bilinear ──
        static void TestColumnHeight(StringBuilder sb, ref int pass, ref int total)
        {
            var g = TestGridDef();
            var heights = Heights(g, (x, z) => 0.3f * x - 0.45f * z + 2f);
            float dCenter = 0f, dLin = 0f;
            for (int k = 0; k < g.dims.z; k++)
                for (int i = 0; i < g.dims.x; i++)
                {
                    Vector3 c = g.Position(i, 0, k);
                    dCenter = Mathf.Max(dCenter, Mathf.Abs(ProbeTerrainLayers.ColumnHeight(g, heights, c.x, c.z) - heights[ProbeTerrainLayers.ColumnIndex(g, i, k)]));
                }
            var rng = new System.Random(11);
            for (int s = 0; s < 200; s++)
            {
                Vector3 p = AroundVolume(rng, g, 0f, 0f);
                dLin = Mathf.Max(dLin, Mathf.Abs(ProbeTerrainLayers.ColumnHeight(g, heights, p.x, p.z) - (0.3f * p.x - 0.45f * p.z + 2f)));
            }
            Vector3 hi = g.MaxCorner;
            float zMid = g.origin.z + 1.7f * g.spacing.z;
            float hOut = ProbeTerrainLayers.ColumnHeight(g, heights, hi.x + 3f, zMid);
            float hEdge = ProbeTerrainLayers.ColumnHeight(g, heights, hi.x, zMid);
            float hLow = ProbeTerrainLayers.ColumnHeight(g, heights, g.origin.x - 5f, g.origin.z - 5f);
            float h00 = heights[0];

            var g1 = new ProbeGrid(g.origin, g.spacing, new Vector3Int(1, 3, 4));
            var h1 = Heights(g1, (x, z) => 0.5f * z + 1f);
            float a1 = ProbeTerrainLayers.ColumnHeight(g1, h1, g1.origin.x - 2f, g1.origin.z + 1.5f * g1.spacing.z);
            float b1 = ProbeTerrainLayers.ColumnHeight(g1, h1, g1.origin.x + 7f, g1.origin.z + 1.5f * g1.spacing.z);
            float e1 = 0.5f * (g1.origin.z + 1.5f * g1.spacing.z) + 1f;
            Check(sb, ref pass, ref total,
                  $"D: 열 중심 = 열 높이 (Δ={dCenter:0.0e0}) · 선형 지면 재현 200점 (Δ={dLin:0.0e0}) · 밖 = 경계 클램프 · 1열 축 x 무관 ({a1:0.000}={b1:0.000}≈{e1:0.000})",
                  dCenter < 1e-5f && dLin < 1e-4f && Mathf.Abs(hOut - hEdge) < 1e-6f && Mathf.Abs(hLow - h00) < 1e-6f
                  && a1 == b1 && Mathf.Abs(a1 - e1) < 1e-5f);
        }

        // ── E) 경사 지형 샘플 ──
        static void TestSlopeSample(StringBuilder sb, ref int pass, ref int total)
        {
            var g = TestGridDef();
            var rngH = new System.Random(13);
            var heights = new float[ProbeTerrainLayers.ColumnCount(g)];
            for (int c = 0; c < heights.Length; c++) heights[c] = Lerp(rngH, -3f, 3f);   // 울퉁불퉁(비선형) 지형
            var bias = new Vector3[SH9.Count];
            var slope = new Vector3[SH9.Count * 3];
            var sh = LinearField(g, new System.Random(14), bias, slope);

            float dProbe = 0f;
            for (int idx = 0; idx < g.Count; idx++)
                dProbe = Mathf.Max(dProbe, MaxCoeffDiff(ProbeTerrainLayers.Sample(g, heights, sh, null, ProbeTerrainLayers.Position(g, heights, idx)), sh[idx]));

            // 내부 점 (x, H(x,z) + 오프셋 + t·간격 y, z) → 기대 = bias + slope·(gx, t, gz)
            var rng = new System.Random(15);
            float dLin = 0f, dLayer = 0f;
            for (int s = 0; s < 300; s++)
            {
                float gx = Lerp(rng, 0f, g.dims.x - 1), gz = Lerp(rng, 0f, g.dims.z - 1), t = Lerp(rng, 0f, g.dims.y - 1);
                float x = g.origin.x + gx * g.spacing.x, z = g.origin.z + gz * g.spacing.z;
                float H = ProbeTerrainLayers.ColumnHeight(g, heights, x, z);
                var p = new Vector3(x, H + g.origin.y + t * g.spacing.y, z);
                var got = ProbeTerrainLayers.Sample(g, heights, sh, null, p);
                dLin = Mathf.Max(dLin, MaxCoeffDiff(got, Expected(bias, slope, new Vector3(gx, t, gz))));
                // 지면 기준 층 좌표: 열 격자의 g.y = t (지형이 달라도 같은 층)
                dLayer = Mathf.Max(dLayer, Mathf.Abs(ProbeTerrainLayers.ColumnGrid(g, heights, p).GridCoord(p).y - t));
            }
            Check(sb, ref pass, ref total,
                  $"E: 울퉁불퉁 지형 — 프로브 위치 샘플 = 그 프로브 (Δc={dProbe:0.0e0}) · 지면 기준 선형장 300점 재현 (Δc={dLin:0.0e0}) · 층 좌표 = 지면 위 높이/간격 (Δ={dLayer:0.0e0})",
                  dProbe < 1e-4f && dLin < 2e-4f && dLayer < 1e-4f);
        }

        // ── F) 지면 레이 ──
        static void TestGroundRays(StringBuilder sb, ref int pass, ref int total)
        {
            var g = new ProbeGrid(new Vector3(-4f, 0.5f, -3f), new Vector3(1f, 1f, 1f), new Vector3Int(9, 3, 7));
            Func<float, float, float> plane = (x, z) => 0.2f * x + 0.1f * z - 1f;
            // 경사면 메시: x ∈ [-4.5, 2.5] 만 덮음 → x = 3, 4 열(2열 × 7)은 미스
            var tris = new List<Tri>();
            AddPatch(tris, plane, -4.5f, 2.5f, -3.5f, 3.5f, 7, 7, flip: false);
            var hs = ProbeTerrainLayers.ColumnHeights(new BruteForceOccluder(tris.ToArray()), g, 10f, -10f, out int miss);
            float dHit = 0f; bool missFinite = true;
            for (int k = 0; k < g.dims.z; k++)
                for (int i = 0; i < g.dims.x; i++)
                {
                    Vector3 c = g.Position(i, 0, k);
                    float h = hs[ProbeTerrainLayers.ColumnIndex(g, i, k)];
                    if (c.x <= 2.5f) dHit = Mathf.Max(dHit, Mathf.Abs(h - plane(c.x, c.z)));
                    else missFinite &= !float.IsNaN(h) && h >= -3f && h <= 1f;
                }
            Check(sb, ref pass, ref total, $"F: 경사면 지면 높이 (Δ={dHit:0.0e0}) · 메시 밖 미스 {miss}열(기대 14) → 이웃 보간 값(유한·범위 안)",
                  dHit < 1e-4f && miss == 14 && missFinite);

            var flipped = new List<Tri>();
            AddPatch(flipped, plane, -4.5f, 4.5f, -3.5f, 3.5f, 9, 7, flip: true);
            var hf = ProbeTerrainLayers.ColumnHeights(new BruteForceOccluder(flipped.ToArray()), g, 10f, -10f, out int missF);
            float dF = 0f;
            for (int k = 0; k < g.dims.z; k++)
                for (int i = 0; i < g.dims.x; i++)
                {
                    Vector3 c = g.Position(i, 0, k);
                    dF = Mathf.Max(dF, Mathf.Abs(hf[ProbeTerrainLayers.ColumnIndex(g, i, k)] - plane(c.x, c.z)));
                }
            var hn = ProbeTerrainLayers.ColumnHeights(null, g, 10f, -2.5f, out int missN);
            bool allBottom = true;
            foreach (float h in hn) allBottom &= h == -2.5f;
            var hAbove = ProbeTerrainLayers.ColumnHeights(new BruteForceOccluder(tris.ToArray()), g, -5f, -9f, out int missA);   // 지면이 탐색 범위 위
            bool aboveBottom = true;
            foreach (float h in hAbove) aboveBottom &= h == -9f;
            Check(sb, ref pass, ref total,
                  $"F: 감김 반대 메시도 같은 높이 (Δ={dF:0.0e0}, 미스 {missF}) · 지면 없음 → 전부 bottom (미스 {missN}) · 지면이 범위 위 → 전부 미스 → bottom",
                  dF < 1e-4f && missF == 0 && allBottom && missN == g.dims.x * g.dims.z && aboveBottom && missA == g.dims.x * g.dims.z);
        }

        // ── G) FillMissing ──
        static void TestFill(StringBuilder sb, ref int pass, ref int total)
        {
            var g3 = new ProbeGrid(Vector3.zero, Vector3.one, new Vector3Int(3, 1, 3));
            var h = new float[9];
            var known = new bool[9];
            for (int k = 0; k < 3; k++) for (int i = 0; i < 3; i++) { h[i + 3 * k] = i + k; known[i + 3 * k] = true; }
            known[4] = false; h[4] = 99f;
            int f1 = ProbeTerrainLayers.FillMissing(g3, h, known, -1f);
            bool center = f1 == 1 && h[4] == 2f && !known[4];   // (1 + 3 + 1 + 3)/4 · known 은 바꾸지 않음

            var g4 = new ProbeGrid(Vector3.zero, Vector3.one, new Vector3Int(4, 1, 1));
            var hc = new[] { 2f, 0f, 0f, 8f };
            int f2 = ProbeTerrainLayers.FillMissing(g4, hc, new[] { true, false, false, true }, -1f);
            bool ring = f2 == 2 && hc[1] == 2f && hc[2] == 8f;   // 1링: 둘 다 스냅샷 기준 한쪽 이웃만 → 순서 무관

            var g2 = new ProbeGrid(Vector3.zero, Vector3.one, new Vector3Int(2, 1, 2));
            var hn = new[] { 5f, 5f, 5f, 5f };
            int f3 = ProbeTerrainLayers.FillMissing(g2, hn, new bool[4], -7f);
            bool none = f3 == 0 && hn[0] == -7f && hn[3] == -7f;
            Check(sb, ref pass, ref total, $"G: FillMissing — 4-이웃 평균 {h[4]} · 1링 스냅샷 [{hc[1]}, {hc[2]}] · 알려진 열 없음 → 폴백 {hn[0]}", center && ring && none);
        }

        // ── H) 절감 리포트 ──
        static void TestStats(StringBuilder sb, ref int pass, ref int total)
        {
            var g = new ProbeGrid(new Vector3(0f, 0.25f, 0f), new Vector3(2f, 1f, 2f), new Vector3Int(10, 6, 8));
            var flat = ProbeTerrainLayers.Measure(g, Fill(80, 4f));
            var none = ProbeTerrainLayers.Measure(g, null);
            var slope = Heights(g, (x, z) => x * (10f / 18f));   // x 0..18 → 지면 0..10
            var s = ProbeTerrainLayers.Measure(g, slope, missColumns: 3);
            string log = ProbeTerrainLayers.Describe(g, s);
            bool okFlat = flat.probes == 480 && flat.uniformLayers == 6 && flat.Savings == 0f && none.uniformProbes == 480;
            bool okSlope = s.probes == 480 && s.uniformLayers == 16 && s.uniformProbes == 1280 && Mathf.Abs(s.Savings - 0.625f) < 1e-6f
                           && s.minHeight == 0f && Mathf.Abs(s.maxHeight - 10f) < 1e-5f;
            Check(sb, ref pass, ref total, $"H: 평지 절감 {flat.Savings:0%} · 경사(지면 0~10, 6층) 균일 환산 {s.uniformLayers}층 {s.uniformProbes} → −{100f * s.Savings:0.0}% · 로그 '{log}'",
                  okFlat && okSlope && log.Contains("−62.5%") && log.Contains("10×6×8 = 480") && log.Contains("지면 미스 3열"));
        }

        // ───────────────────────── 헬퍼 ─────────────────────────

        static float[] Fill(int n, float v) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }

        static float[] Heights(in ProbeGrid g, Func<float, float, float> f)
        {
            var h = new float[ProbeTerrainLayers.ColumnCount(g)];
            for (int k = 0; k < g.dims.z; k++)
                for (int i = 0; i < g.dims.x; i++)
                {
                    Vector3 c = g.Position(i, 0, k);
                    h[ProbeTerrainLayers.ColumnIndex(g, i, k)] = f(c.x, c.z);
                }
            return h;
        }

        // 격자 x/z 범위 ± margin(셀), y = baseY 기준 층 범위 ± margin
        static Vector3 AroundVolume(System.Random rng, in ProbeGrid g, float baseY, float marginCells)
        {
            Vector3 lo = g.origin, ext = Vector3.Scale(g.spacing, new Vector3(g.dims.x - 1, g.dims.y - 1, g.dims.z - 1));
            return new Vector3(
                Lerp(rng, lo.x - marginCells * g.spacing.x, lo.x + ext.x + marginCells * g.spacing.x),
                Lerp(rng, baseY - marginCells * g.spacing.y, baseY + ext.y + marginCells * g.spacing.y),
                Lerp(rng, lo.z - marginCells * g.spacing.z, lo.z + ext.z + marginCells * g.spacing.z));
        }

        static void AddPatch(List<Tri> tris, Func<float, float, float> f, float x0, float x1, float z0, float z1, int nx, int nz, bool flip)
        {
            for (int b = 0; b < nz; b++)
                for (int a = 0; a < nx; a++)
                {
                    float xa = x0 + (x1 - x0) * a / nx, xb = x0 + (x1 - x0) * (a + 1) / nx;
                    float za = z0 + (z1 - z0) * b / nz, zb = z0 + (z1 - z0) * (b + 1) / nz;
                    var p00 = new Vector3(xa, f(xa, za), za); var p10 = new Vector3(xb, f(xb, za), za);
                    var p01 = new Vector3(xa, f(xa, zb), zb); var p11 = new Vector3(xb, f(xb, zb), zb);
                    if (!flip) { tris.Add(new Tri { V0 = p00, V1 = p01, V2 = p11 }); tris.Add(new Tri { V0 = p00, V1 = p11, V2 = p10 }); }
                    else { tris.Add(new Tri { V0 = p00, V1 = p11, V2 = p01 }); tris.Add(new Tri { V0 = p00, V1 = p10, V2 = p11 }); }
                }
        }

        static SH9[] Field(in ProbeGrid g, System.Random rng)
        {
            var sh = new SH9[g.Count];
            for (int i = 0; i < sh.Length; i++)
                for (int k = 0; k < SH9.Count; k++) SetCoeff(ref sh[i], k, new Vector3(Lerp(rng, -1f, 1f), Lerp(rng, -1f, 1f), Lerp(rng, -1f, 1f)));
            return sh;
        }

        // 계수 k = bias[k] + (slope[3k]·g, slope[3k+1]·g, slope[3k+2]·g), g = 격자 좌표(i, j, k)
        static SH9[] LinearField(in ProbeGrid g, System.Random rng, Vector3[] bias, Vector3[] slope)
        {
            for (int i = 0; i < bias.Length; i++) bias[i] = new Vector3(Lerp(rng, -1f, 1f), Lerp(rng, -1f, 1f), Lerp(rng, -1f, 1f));
            for (int i = 0; i < slope.Length; i++) slope[i] = new Vector3(Lerp(rng, -0.3f, 0.3f), Lerp(rng, -0.3f, 0.3f), Lerp(rng, -0.3f, 0.3f));
            var sh = new SH9[g.Count];
            for (int idx = 0; idx < sh.Length; idx++)
            {
                var c = g.Coord(idx);
                sh[idx] = Expected(bias, slope, new Vector3(c.x, c.y, c.z));
            }
            return sh;
        }

        static SH9 Expected(Vector3[] bias, Vector3[] slope, Vector3 gc)
        {
            var s = new SH9();
            for (int k = 0; k < SH9.Count; k++)
                SetCoeff(ref s, k, bias[k] + new Vector3(Vector3.Dot(slope[3 * k], gc), Vector3.Dot(slope[3 * k + 1], gc), Vector3.Dot(slope[3 * k + 2], gc)));
            return s;
        }

        static bool[] RandomMask(int n, System.Random rng)
        {
            var m = new bool[n];
            for (int i = 0; i < n; i++) m[i] = rng.NextDouble() > 0.25;
            return m;
        }

        static void SetCoeff(ref SH9 s, int k, Vector3 v)
        {
            switch (k)
            {
                case 0: s.c0 = v; break; case 1: s.c1 = v; break; case 2: s.c2 = v; break;
                case 3: s.c3 = v; break; case 4: s.c4 = v; break; case 5: s.c5 = v; break;
                case 6: s.c6 = v; break; case 7: s.c7 = v; break; default: s.c8 = v; break;
            }
        }

        static Vector3 Coeff(in SH9 s, int k) => k switch
        {
            0 => s.c0, 1 => s.c1, 2 => s.c2, 3 => s.c3, 4 => s.c4, 5 => s.c5, 6 => s.c6, 7 => s.c7, _ => s.c8,
        };

        static bool SameSH(in SH9 a, in SH9 b)
        {
            for (int k = 0; k < SH9.Count; k++) if (!Coeff(a, k).Equals(Coeff(b, k))) return false;   // 성분별 정확 비교
            return true;
        }

        static float MaxCoeffDiff(in SH9 a, in SH9 b)
        {
            float m = 0f;
            for (int k = 0; k < SH9.Count; k++)
            {
                Vector3 d = Coeff(a, k) - Coeff(b, k);
                m = Mathf.Max(m, Mathf.Max(Mathf.Abs(d.x), Mathf.Max(Mathf.Abs(d.y), Mathf.Abs(d.z))));
            }
            return m;
        }

        static float Lerp(System.Random rng, float a, float b) => a + (float)rng.NextDouble() * (b - a);

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
