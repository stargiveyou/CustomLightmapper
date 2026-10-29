using System;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Track B(B2 — 결정 D1): 지형 추종 2.5D 프로브 배치. XZ 는 <see cref="ProbeGrid"/> 그대로, 열 (i,k) 마다 지면 높이
    /// h[i + dims.x·k] 를 두고 층 j 를 y = (h + grid.origin.y) + j·spacing.y 에 놓는다.
    ///   grid.origin.y = 지면 위 첫 층 높이(오프셋), grid.dims.y = 층 수, grid.spacing.y = 층 간격.
    ///
    /// 핵심 정의 — 점 p 의 '열 격자'(<see cref="ColumnGrid"/>): origin.y 만 H(p.xz) + grid.origin.y 로 바꾼 ProbeGrid.
    ///   H = 열 높이의 bilinear(경계 클램프, 이중 lerp a + (b - a)·t — 이웃이 같으면 정확히 그 값). 프로브 (i,j,k) 의 열에서 H = h(i,k) 라
    ///   위치·샘플·셰이더가 균일 격자 규약을 그대로 쓴다: 위치 = 열 격자의 Position, 샘플 = <see cref="ColumnGrid"/>(p).Sample(p),
    ///   셰이더 = 격자 좌표 g.y 만 열 기준(HuskyProbeVolume.hlsl HuskyPV_ColumnHeight — 이 클래스와 같은 식).
    ///   → 높이가 없으면(빈 배열) 균일 격자 그 자체, 높이가 전부 c 면 origin.y = c + grid.origin.y 인 균일 격자와 비트 동일.
    /// 인덱스·SH 배열·Texture3D 텍셀·dilation 6-이웃은 인덱스 공간이라 균일과 같다(dims = nx × 층 × nz) — 런타임엔 높이맵 1장만 추가.
    /// 엔진 API 는 Vector3·Mathf·Bounds 만(헤드리스 테스트 대상 — <c>ProbeTerrainLayersTests</c>).
    /// </summary>
    public static class ProbeTerrainLayers
    {
        // ───────────────────────── 배치 ─────────────────────────

        /// <summary>층 수 = ⌈높이/간격⌉ + 1(<see cref="ProbeGrid.FromBounds"/> 의 축 규칙과 같은 식). 높이 0 → 1층.</summary>
        public static int LayerCount(float layerHeight, float spacingY)
        {
            if (!(spacingY > 0f)) throw new ArgumentException("spacingY 는 > 0 이어야 함", nameof(spacingY));
            return Mathf.Max(0, Mathf.CeilToInt(Mathf.Max(0f, layerHeight) / spacingY - 1e-4f)) + 1;
        }

        /// <summary>
        /// 지형 추종 격자: XZ = 볼륨의 균일 격자(<see cref="ProbeGrid.FromBounds"/> — 같은 볼륨·간격이면 균일 배치와 열 위치가 같다),
        /// y = (원점 <paramref name="groundOffset"/>, 층 수 <see cref="LayerCount"/>(<paramref name="layerHeight"/>)).
        /// </summary>
        public static ProbeGrid Grid(Bounds volume, Vector3 spacing, float groundOffset, float layerHeight)
        {
            var xz = ProbeGrid.FromBounds(volume, spacing);
            return new ProbeGrid(new Vector3(xz.origin.x, groundOffset, xz.origin.z), spacing,
                                 new Vector3Int(xz.dims.x, LayerCount(layerHeight, spacing.y), xz.dims.z));
        }

        public static int ColumnCount(in ProbeGrid g) => g.dims.x * g.dims.z;
        public static int ColumnIndex(in ProbeGrid g, int i, int k) => i + g.dims.x * k;
        static bool HasHeights(float[] heights) => heights != null && heights.Length > 0;

        /// <summary>열 (i,k) 의 격자 — origin.y = h(i,k) + grid.origin.y. 높이 없으면 grid 그대로.</summary>
        public static ProbeGrid ColumnGridAt(in ProbeGrid g, float[] heights, int i, int k)
        {
            if (!HasHeights(heights)) return g;
            var c = g;
            c.origin.y = heights[ColumnIndex(g, i, k)] + g.origin.y;
            return c;
        }

        /// <summary>프로브 (i,j,k) 위치 = 열 격자의 Position(i,j,k). 높이 없으면 <see cref="ProbeGrid.Position(int,int,int)"/> 와 같다.</summary>
        public static Vector3 Position(in ProbeGrid g, float[] heights, int i, int j, int k) => ColumnGridAt(g, heights, i, k).Position(i, j, k);

        public static Vector3 Position(in ProbeGrid g, float[] heights, int index)
        {
            var c = g.Coord(index);
            return Position(g, heights, c.x, c.y, c.z);
        }

        /// <summary>전 프로브 위치(선형 인덱스 순서 = 베이크 points 순서 — <see cref="ProbeGrid.Positions"/> 와 같은 루프).</summary>
        public static Vector3[] Positions(in ProbeGrid g, float[] heights)
        {
            if (!HasHeights(heights)) return g.Positions();
            var p = new Vector3[g.Count];
            int idx = 0;
            for (int k = 0; k < g.dims.z; k++)
                for (int j = 0; j < g.dims.y; j++)
                    for (int i = 0; i < g.dims.x; i++)
                        p[idx++] = Position(g, heights, i, j, k);
            return p;
        }

        // ───────────────────────── 샘플 ─────────────────────────

        /// <summary>
        /// 월드 (x, z) 의 지면 높이 H — 열 높이 bilinear. 격자 좌표는 <see cref="ProbeGrid.GridCoord"/> 와 같은 식, 축 처리(경계 클램프 ·
        /// 1열 축)는 <see cref="ProbeGrid.Sample(SH9[], bool[], Vector3)"/> 와 같은 Axis. 높이 없으면 0.
        /// </summary>
        public static float ColumnHeight(in ProbeGrid g, float[] heights, float x, float z)
        {
            if (!HasHeights(heights)) return 0f;
            ProbeGrid.Axis((x - g.origin.x) / g.spacing.x, g.dims.x, out int i0, out float fx);
            ProbeGrid.Axis((z - g.origin.z) / g.spacing.z, g.dims.z, out int k0, out float fz);
            int i1 = Mathf.Min(i0 + 1, g.dims.x - 1);
            int k1 = Mathf.Min(k0 + 1, g.dims.z - 1);
            int nx = g.dims.x;
            float h00 = heights[i0 + nx * k0], h10 = heights[i1 + nx * k0];
            float h01 = heights[i0 + nx * k1], h11 = heights[i1 + nx * k1];
            float a = h00 + (h10 - h00) * fx;   // HLSL lerp(a, b, t) = a + t·(b - a) 와 같은 형태 — 같은 값이면 정확
            float b = h01 + (h11 - h01) * fx;
            return a + (b - a) * fz;
        }

        /// <summary>점 p 의 열 격자: origin.y = H(p.xz) + grid.origin.y. 높이 없으면 grid 그대로.</summary>
        public static ProbeGrid ColumnGrid(in ProbeGrid g, float[] heights, Vector3 p)
        {
            if (!HasHeights(heights)) return g;
            var c = g;
            c.origin.y = ColumnHeight(g, heights, p.x, p.z) + g.origin.y;
            return c;
        }

        /// <summary>
        /// 8탭 trilinear(열 격자 기준) — <see cref="ProbeGrid.Sample(SH9[], bool[], Vector3)"/> 를 열 격자로 부른다(valid 가중·클램프 규칙 동일).
        /// 높이 없으면 균일 Sample 과 비트 동일.
        /// </summary>
        public static SH9 Sample(in ProbeGrid g, float[] heights, SH9[] sh, bool[] valid, Vector3 p) => ColumnGrid(g, heights, p).Sample(sh, valid, p);

        // ───────────────────────── 지면 높이 ─────────────────────────

        /// <summary>
        /// 열 지면 높이: 열마다 (x, <paramref name="top"/>, z) 에서 아래로 레이(<paramref name="ground"/> — 지면 메시만 담은 차폐)를 쏴
        /// [top − (top − bottom)] 안의 첫 히트 높이. 미스 열은 <see cref="FillMissing"/>(이웃 링 평균, 전부 미스면 bottom).
        /// ground == null 이면 전부 미스.
        /// </summary>
        public static float[] ColumnHeights(IOccluder ground, in ProbeGrid g, float top, float bottom, out int missCount)
        {
            int nx = g.dims.x, nz = g.dims.z;
            var h = new float[nx * nz];
            var known = new bool[h.Length];
            float range = Mathf.Max(0f, top - bottom);
            var down = new Vector3(0f, -1f, 0f);
            missCount = 0;
            for (int k = 0; k < nz; k++)
                for (int i = 0; i < nx; i++)
                {
                    int c = i + nx * k;
                    if (ground != null)
                    {
                        Vector3 xz = g.Position(i, 0, k);
                        var hit = ground.Intersect(new Vector3(xz.x, top, xz.z), down, 0f, range);
                        if (hit.Valid) { h[c] = top - hit.T; known[c] = true; continue; }
                    }
                    missCount++;
                }
            FillMissing(g, h, known, bottom);
            return h;
        }

        /// <summary>
        /// 미지 열 채우기: 링 단위로 알려진 4-이웃 평균(반복 시작 스냅샷 기준 — 순서 무관·결정적, <see cref="BurstProbeValidity.Dilate"/> 와 같은 방식).
        /// 닿지 않는 열(알려진 열이 하나도 없음)은 <paramref name="fallback"/>. 반환 = 이웃 평균으로 채운 열 수.
        /// </summary>
        public static int FillMissing(in ProbeGrid g, float[] h, bool[] known, float fallback)
        {
            int nx = g.dims.x, nz = g.dims.z;
            var k2 = (bool[])known.Clone();
            var next = new float[h.Length];
            var list = new System.Collections.Generic.List<int>();
            int filled = 0;
            while (true)
            {
                list.Clear();
                for (int k = 0; k < nz; k++)
                    for (int i = 0; i < nx; i++)
                    {
                        int c = i + nx * k;
                        if (k2[c]) continue;
                        float sum = 0f; int cnt = 0;
                        if (i > 0 && k2[c - 1]) { sum += h[c - 1]; cnt++; }
                        if (i < nx - 1 && k2[c + 1]) { sum += h[c + 1]; cnt++; }
                        if (k > 0 && k2[c - nx]) { sum += h[c - nx]; cnt++; }
                        if (k < nz - 1 && k2[c + nx]) { sum += h[c + nx]; cnt++; }
                        if (cnt == 0) continue;
                        next[c] = sum / cnt;
                        list.Add(c);
                    }
                if (list.Count == 0) break;
                foreach (int c in list) { h[c] = next[c]; k2[c] = true; }
                filled += list.Count;
            }
            for (int c = 0; c < h.Length; c++) if (!k2[c]) h[c] = fallback;
            return filled;
        }

        // ───────────────────────── 절감 리포트 ─────────────────────────

        /// <summary>배치 통계 — 같은 간격의 균일 격자로 모든 층을 덮으려면 필요한 프로브 수와 비교.</summary>
        public struct Stats
        {
            public int probes;           // nx × 층 × nz
            public int layers;
            public int uniformLayers;    // 같은 열 범위를 덮는 균일 격자의 y 축 프로브 수
            public int uniformProbes;    // nx × uniformLayers × nz
            public float minHeight, maxHeight;
            public int missColumns;
            /// <summary>균일 대비 절감 비율(0 = 같음, 0.75 = 1/4 로).</summary>
            public readonly float Savings => uniformProbes > 0 ? 1f - (float)probes / uniformProbes : 0f;
        }

        /// <summary>
        /// 균일 환산: 최저 지면 열의 첫 층 ~ 최고 지면 열의 마지막 층 = (maxH − minH) + (층 − 1)·간격 → <see cref="LayerCount"/>.
        /// 평지(높이 차 0)면 uniformLayers = 층 수(절감 0).
        /// </summary>
        public static Stats Measure(in ProbeGrid g, float[] heights, int missColumns = 0)
        {
            var s = new Stats { probes = g.Count, layers = g.dims.y, missColumns = missColumns };
            if (!HasHeights(heights))
            {
                s.uniformLayers = g.dims.y;
                s.uniformProbes = g.Count;
                return s;
            }
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            foreach (float h in heights) { lo = Mathf.Min(lo, h); hi = Mathf.Max(hi, h); }
            s.minHeight = lo;
            s.maxHeight = hi;
            float span = (hi - lo) + (g.dims.y - 1) * g.spacing.y;
            s.uniformLayers = LayerCount(span, g.spacing.y);
            s.uniformProbes = g.dims.x * s.uniformLayers * g.dims.z;
            return s;
        }

        /// <summary>로그 한 줄: 격자 · 지면 범위 · 균일 환산 · 절감률 · Texture3D(RGBAHalf) 메모리 · 미스 열.</summary>
        public static string Describe(in ProbeGrid g, in Stats s)
        {
            const float BytesPerProbe = 7 * 8f;   // SHPacked float4 7개 × RGBAHalf 8B(Texture3D×7)
            float mb = s.probes * BytesPerProbe / (1024f * 1024f);
            float mbU = s.uniformProbes * BytesPerProbe / (1024f * 1024f);
            return $"TerrainLayers {g.dims.x}×{s.layers}×{g.dims.z} = {s.probes} 프로브 ({mb:0.00}MB) · 지면 {s.minHeight:0.##}~{s.maxHeight:0.##} · " +
                   $"균일 환산 {g.dims.x}×{s.uniformLayers}×{g.dims.z} = {s.uniformProbes} ({mbU:0.00}MB) → −{100f * s.Savings:0.0}%" +
                   (s.missColumns > 0 ? $" · 지면 미스 {s.missColumns}열(이웃 보간)" : "");
        }
    }
}
