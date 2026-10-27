using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// A3 차트 평탄화 검증: <see cref="LSCMSolver"/> · <see cref="MVCFallback"/> · <see cref="ChartFlattener"/> 디스패치 · <see cref="UVValidator.Stretch"/>.
    ///  A) 희소 솔버: CG(대칭 양정치) · BiCGSTAB · Gauss–Seidel(비대칭 대각우세) ≡ 해석해.
    ///  B) Stretch 척도: 등거리 = 1 · 균일 스케일 무관 · u 2배 = √1.25.
    ///  C) 평면 차트 회귀: 기울어진 평면 격자 → Planar, UV ≡ PlanarProjector(비트 동일).
    ///  D) LSCM: 평면 격자 L2 ≈ 1 · 원통 120°(전개 가능) L2 ≈ 1 &lt; 평면투영 → 디스패처 LSCM · 구면 패치(이중 곡률) LSCM &lt; 평면투영.
    ///  E) 겹침: 원통 270° 말림 → 평면투영 겹침 → LSCM 이 펴서 겹침 0.
    ///  F) MVC: 원통 120°·구멍 뚫린 평판·구멍 뚫린 원통 → 겹침 0 · 외곽 = 원 · 같은 부호. LSCM 끄면 270° 말림이 MVC 로.
    ///  G) 결정성 · 실패 경로(닫힌 사면체 → false, UV 불변) · 퇴화 삼각형(유한, 나머지 양수) · FoldoverOnly(구 디스패치) · 방향(전체 부호 면적 양수).
    /// 메시 없이 위치·삼각형 배열로 ChartMesh 를 직접 만든다(경계 루프 = ChartMeshBuilder 와 같은 규약) → 헤드리스.
    /// 호출: Debug.Log(ChartFlattenTests.RunAll());
    /// </summary>
    public static class ChartFlattenTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== A3 차트 평탄화 (LSCM / MVC / 디스패치) ===");
            int pass = 0, total = 0;
            TestSolvers(sb, ref pass, ref total);
            TestStretchMetric(sb, ref pass, ref total);
            TestPlanarRegression(sb, ref pass, ref total);
            TestLscm(sb, ref pass, ref total);
            TestFoldover(sb, ref pass, ref total);
            TestMvc(sb, ref pass, ref total);
            TestMisc(sb, ref pass, ref total);
            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ── A) 희소 솔버 ──
        static void TestSolvers(StringBuilder sb, ref int pass, ref int total)
        {
            // 1D 라플라시안(SPD): 2x_i − x_{i−1} − x_{i+1} = h², 경계 0 → 해석해 x_i = i(n+1−i)/2 · h²
            const int n = 50;
            var bl = new SparseMatrix.Builder(n);
            var b = new double[n];
            for (int i = 0; i < n; i++) { bl.Add(i, i, 2.0); if (i > 0) bl.Add(i, i - 1, -1.0); if (i < n - 1) bl.Add(i, i + 1, -1.0); b[i] = 1.0; }
            var a = bl.Build();
            var x = new double[n];
            var r = SparseSolver.ConjugateGradient(a, b, x, 1000, 1e-12);
            double err = 0.0;
            for (int i = 0; i < n; i++) err = Math.Max(err, Math.Abs(x[i] - (i + 1) * (double)(n - i) / 2.0));
            Check(sb, ref pass, ref total, $"A: CG 1D 라플라시안 ≡ 해석해 (it={r.Iterations}, max|Δ|={err:E1})", r.Converged && err < 1e-8);

            // 비대칭 대각우세: 각 행 4x_i − x_{i−1} − 2x_{i+1} = rhs (해 x = 1..n 로 rhs 구성)
            var bn = new SparseMatrix.Builder(n);
            var xs = new double[n];
            for (int i = 0; i < n; i++) xs[i] = i + 1;
            var rhs = new double[n];
            for (int i = 0; i < n; i++)
            {
                bn.Add(i, i, 4.0); rhs[i] += 4.0 * xs[i];
                if (i > 0) { bn.Add(i, i - 1, -1.0); rhs[i] -= xs[i - 1]; }
                if (i < n - 1) { bn.Add(i, i + 1, -2.0); rhs[i] -= 2.0 * xs[i + 1]; }
            }
            var an = bn.Build();
            var x1 = new double[n]; var x2 = new double[n];
            var r1 = SparseSolver.BiCgStab(an, rhs, x1, 1000, 1e-12);
            var r2 = SparseSolver.GaussSeidel(an, rhs, x2, 5000, 1e-12);
            double e1 = 0.0, e2 = 0.0;
            for (int i = 0; i < n; i++) { e1 = Math.Max(e1, Math.Abs(x1[i] - xs[i])); e2 = Math.Max(e2, Math.Abs(x2[i] - xs[i])); }
            Check(sb, ref pass, ref total, $"A: BiCGSTAB (it={r1.Iterations}, {e1:E1}) · Gauss–Seidel (it={r2.Iterations}, {e2:E1}) ≡ 해 — 비대칭",
                  r1.Converged && r2.Converged && e1 < 1e-8 && e2 < 1e-8);

            // Builder 중복 합산
            var bd = new SparseMatrix.Builder(2);
            bd.Add(0, 1, 1.5); bd.Add(0, 1, 2.5); bd.Add(1, 1, 3.0); bd.Add(0, 0, 1.0);
            var ad = bd.Build();
            Check(sb, ref pass, ref total, "A: Builder 같은 (행,열) 합산 · 행 내 열 정렬 · 대각 추출",
                  ad.NonZeros == 3 && ad.Col[0] == 0 && ad.Col[1] == 1 && ad.Val[1] == 4.0 && ad.Diag[0] == 1.0 && ad.Diag[1] == 3.0);
        }

        // ── B) Stretch 척도 ──
        static void TestStretchMetric(StringBuilder sb, ref int pass, ref int total)
        {
            var cm = Grid(6, 4, (u, v) => new Vector3(u * 2f, v * 1.5f, 0f));
            cm.UV = new Vector2[cm.VertexCount];
            for (int i = 0; i < cm.VertexCount; i++) cm.UV[i] = new Vector2(cm.positions[i].x, cm.positions[i].y);
            float iso = UVValidator.Stretch(cm);
            for (int i = 0; i < cm.VertexCount; i++) cm.UV[i] *= 7.3f;
            float scaled = UVValidator.Stretch(cm);
            for (int i = 0; i < cm.VertexCount; i++) cm.UV[i] = new Vector2(cm.positions[i].x * 2f, cm.positions[i].y);
            float aniso = UVValidator.Stretch(cm);
            Check(sb, ref pass, ref total, $"B: L2 등거리 {iso:F6} = 1 · 7.3배 스케일 {scaled:F6} = 1 · u 2배 {aniso:F5} = √1.25",
                  Mathf.Abs(iso - 1f) < 1e-5f && Mathf.Abs(scaled - 1f) < 1e-5f && Mathf.Abs(aniso - Mathf.Sqrt(1.25f)) < 1e-5f);
        }

        // ── C) 평면 차트 회귀 ──
        static void TestPlanarRegression(StringBuilder sb, ref int pass, ref int total)
        {
            var axis = new Vector3(0.3f, 0.8f, -0.5f);
            var cm = Grid(9, 7, (u, v) => Rotate(new Vector3(u * 3f - 1.2f, v * 2f + 0.4f, 0f), axis, 37f) + new Vector3(5f, -2f, 1f));
            var refCm = Clone(cm);
            PlanarProjector.Projector(ref refCm);
            var m = ChartFlattener.Flatten(ref cm, FlattenSettings.Default, out var st);
            bool same = true;
            for (int i = 0; i < cm.VertexCount; i++) same &= Same(cm.UV[i], refCm.UV[i]);
            Check(sb, ref pass, ref total, $"C: 기울어진 평면 격자 → {m} (L2 {st.PlanarStretch:F6}) · UV ≡ PlanarProjector 비트 동일 · LSCM 미실행",
                  m == FlattenMethod.Planar && same && Mathf.Abs(st.PlanarStretch - 1f) < 1e-4f && !st.LscmSolved);
        }

        // ── D) LSCM ──
        static void TestLscm(StringBuilder sb, ref int pass, ref int total)
        {
            var flat = Grid(11, 11, (u, v) => new Vector3(u * 4f, 0.2f * v, v * 3f));
            PlanarProjector.Projector(ref flat);
            bool ok = LSCMSolver.Solve(ref flat, FlattenSettings.Default, out int itFlat);
            float l2 = ok ? UVValidator.Stretch(flat) : float.NaN;
            Check(sb, ref pass, ref total, $"D: LSCM 평면 격자 → L2 {l2:F6} ≈ 1 (it={itFlat}) · 겹침 없음 · 방향 양수",
                  ok && Mathf.Abs(l2 - 1f) < 1e-4f && !UVValidator.HasFoldover(flat) && LSCMSolver.SignedArea(flat.UV, flat.Triangles) > 0.0);

            var cyl = Cylinder(120f, 1.5f, 2f, 24, 8);
            var mc = ChartFlattener.Flatten(ref cyl, FlattenSettings.Default, out var sc);
            Check(sb, ref pass, ref total, $"D: 원통 120°(전개 가능) — 평면투영 L2 {sc.PlanarStretch:F4} → LSCM {sc.LscmStretch:F6} ≈ 1 → 디스패처 {mc}",
                  mc == FlattenMethod.LSCM && sc.PlanarStretch > 1.04f && Mathf.Abs(sc.LscmStretch - 1f) < 1e-3f && !UVValidator.HasFoldover(cyl));

            var sph = SpherePatch(80f, 80f, 16, 16);
            var ms = ChartFlattener.Flatten(ref sph, FlattenSettings.Default, out var ss);
            Check(sb, ref pass, ref total, $"D: 구면 패치 80°×80°(이중 곡률) — 평면투영 L2 {ss.PlanarStretch:F4} > LSCM {ss.LscmStretch:F4} → {ms} · 겹침 없음",
                  ms == FlattenMethod.LSCM && ss.LscmStretch < ss.PlanarStretch && !UVValidator.HasFoldover(sph));
        }

        // ── E) 겹침 ──
        static void TestFoldover(StringBuilder sb, ref int pass, ref int total)
        {
            var curl = Cylinder(270f, 1f, 1.5f, 36, 6);
            var m = ChartFlattener.Flatten(ref curl, FlattenSettings.Default, out var st);
            Check(sb, ref pass, ref total, $"E: 원통 270° 말림 — 평면투영 겹침={st.PlanarFoldover} → {m} (L2 {st.LscmStretch:F5}) · 최종 겹침 없음",
                  st.PlanarFoldover && m == FlattenMethod.LSCM && !UVValidator.HasFoldover(curl) && Mathf.Abs(st.LscmStretch - 1f) < 1e-3f);
        }

        // ── F) MVC ──
        static void TestMvc(StringBuilder sb, ref int pass, ref int total)
        {
            var cyl = Cylinder(120f, 1.5f, 2f, 24, 8);
            PlanarProjector.Projector(ref cyl);
            bool ok = MVCFallback.Solve(ref cyl, FlattenSettings.Default, out int it);
            Check(sb, ref pass, ref total, $"F: MVC 원통 120° → 겹침 없음 · 외곽 루프가 원 위 · 전 삼각형 양수 (it={it})",
                  ok && AllPositive(cyl) && OnCircle(cyl));

            var plate = HoleGrid(12, 12, (u, v) => new Vector3(u * 3f, 0f, v * 3f));
            PlanarProjector.Projector(ref plate);
            bool okH = MVCFallback.Solve(ref plate, FlattenSettings.Default, out int itH);
            float hr = HoleRatio(plate);
            Check(sb, ref pass, ref total, $"F: MVC 구멍 뚫린 평판(루프 {plate.Loops.Count}) → 겹침 없음 · 전 삼각형 양수 · 구멍 면적비 UV/3D {hr:F3} ≈ 1 (가상 부채꼴, it={itH})",
                  plate.Loops.Count == 2 && okH && AllPositive(plate) && OnCircle(plate) && Mathf.Abs(hr - 1f) < 0.25f);

            var holeCyl = HoleGrid(16, 10, (u, v) => CylPoint(120f, 1.5f, 2f, u, v));
            PlanarProjector.Projector(ref holeCyl);
            bool okHC = MVCFallback.Solve(ref holeCyl, FlattenSettings.Default, out _);
            float hrc = okHC ? HoleRatio(holeCyl) : float.NaN;
            Check(sb, ref pass, ref total, $"F: MVC 구멍 뚫린 원통 120° → 겹침 없음 · 전 삼각형 양수 · 구멍 면적비 UV/3D {hrc:F3} ≈ 1",
                  okHC && AllPositive(holeCyl) && Mathf.Abs(hrc - 1f) < 0.25f);

            var curl = Cylinder(270f, 1f, 1.5f, 36, 6);
            var noLscm = FlattenSettings.Default; noLscm.EnableLSCM = false;
            var m = ChartFlattener.Flatten(ref curl, noLscm, out var st);
            Check(sb, ref pass, ref total, $"F: LSCM 끄면 270° 말림(평면투영 겹침) → {m} · 겹침 없음",
                  st.PlanarFoldover && m == FlattenMethod.MVC && st.MvcSolved && !UVValidator.HasFoldover(curl));
        }

        // ── G) 기타 ──
        static void TestMisc(StringBuilder sb, ref int pass, ref int total)
        {
            var a = SpherePatch(70f, 50f, 14, 10);
            var b = Clone(a);
            ChartFlattener.Flatten(ref a, FlattenSettings.Default, out _);
            ChartFlattener.Flatten(ref b, FlattenSettings.Default, out _);
            bool same = true;
            for (int i = 0; i < a.VertexCount; i++) same &= Same(a.UV[i], b.UV[i]);
            Check(sb, ref pass, ref total, "G: 결정성 — 같은 차트 2회 평탄화 UV 비트 동일", same);

            // 닫힌 사면체(경계 없음) → LSCM/MVC false · UV 불변 · 디스패처 예외 없음
            var tet = Build(new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1) },
                            new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 });
            PlanarProjector.Projector(ref tet);
            var before = (Vector2[])tet.UV.Clone();
            bool l = LSCMSolver.Solve(ref tet, FlattenSettings.Default, out _);
            bool mv = MVCFallback.Solve(ref tet, FlattenSettings.Default, out _);
            bool unchanged = true;
            for (int i = 0; i < before.Length; i++) unchanged &= Same(tet.UV[i], before[i]);
            var mt = ChartFlattener.Flatten(ref tet, FlattenSettings.Default, out _);
            Check(sb, ref pass, ref total, $"G: 닫힌 사면체(루프 {tet.Loops.Count}) → LSCM/MVC false · UV 불변 · 디스패처 {mt}", !l && !mv && unchanged && tet.Loops.Count == 0);

            // 퇴화 삼각형: 원통 패치 경계 변에 일직선 꼭짓점 하나 끼운 면적 0 삼각형
            var dg = DegenerateCylinder();
            PlanarProjector.Projector(ref dg);
            bool okD = LSCMSolver.Solve(ref dg, FlattenSettings.Default, out _);
            bool finite = okD;
            if (okD) foreach (var p in dg.UV) finite &= !float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsInfinity(p.x) && !float.IsInfinity(p.y);
            Check(sb, ref pass, ref total, "G: 퇴화(면적 0) 삼각형 포함 → LSCM 유한 · 나머지 삼각형 전부 양수", finite && AllPositive(dg, skipLast: 1));

            var cyl = Cylinder(120f, 1.5f, 2f, 24, 8);
            var mOld = ChartFlattener.Flatten(ref cyl, FlattenSettings.FoldoverOnly, out var so);
            Check(sb, ref pass, ref total, $"G: FoldoverOnly(구 디스패치) — 겹침 없는 원통 120° 는 {mOld} 유지(LSCM 미실행)", mOld == FlattenMethod.Planar && !so.LscmSolved);
        }

        // ───────────────────────── 차트 생성 ─────────────────────────

        /// <summary>위치·삼각형 → ChartMesh. 경계 루프 = 짝 없는 방향 변을 면 감김 순서로 이은 것(목적 정점 나열 — ChartMeshBuilder 규약), 둘레 내림차순.</summary>
        internal static ChartMesh Build(Vector3[] pos, int[] tris)
        {
            var dir = new HashSet<long>();
            int n = pos.Length;
            for (int t = 0; t < tris.Length; t += 3)
                for (int k = 0; k < 3; k++) dir.Add((long)tris[t + k] * n + tris[t + (k + 1) % 3]);
            var next = new Dictionary<int, int>();
            for (int t = 0; t < tris.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = tris[t + k], b = tris[t + (k + 1) % 3];
                    if (!dir.Contains((long)b * n + a)) next[a] = b;   // 경계 방향 변 a→b
                }
            var loops = new List<int[]>();
            var used = new HashSet<int>();
            var starts = new List<int>(next.Keys);
            starts.Sort();
            foreach (int s0 in starts)
            {
                if (used.Contains(s0)) continue;
                var loop = new List<int>();
                int cur = s0, guard = 0;
                do { used.Add(cur); cur = next[cur]; loop.Add(cur); } while (cur != s0 && next.ContainsKey(cur) && ++guard < n + 1);
                if (loop.Count >= 3) loops.Add(loop.ToArray());
            }
            float Per(int[] lp) { float s = 0f; for (int i = 0; i < lp.Length; i++) s += Vector3.Distance(pos[lp[i]], pos[lp[(i + 1) % lp.Length]]); return s; }
            loops.Sort((x, y) => Per(y).CompareTo(Per(x)));

            Vector3 nrm = Vector3.zero;
            for (int t = 0; t < tris.Length; t += 3) nrm += Vector3.Cross(pos[tris[t + 1]] - pos[tris[t]], pos[tris[t + 2]] - pos[tris[t]]);
            var mv = new int[n];
            for (int i = 0; i < n; i++) mv[i] = i;
            return new ChartMesh { chartID = 0, positions = pos, MeshVertex = mv, Triangles = tris, Loops = loops, PlaneNormal = nrm.normalized };
        }

        /// <summary>(nu+1)×(nv+1) 격자 — 삼각형 (p00,p10,p11)·(p00,p11,p01), 노멀 ∝ ∂S/∂u × ∂S/∂v.</summary>
        static ChartMesh Grid(int nu, int nv, Func<float, float, Vector3> s) => Build(GridPositions(nu, nv, s), GridTris(nu, nv, null));

        // 가운데 1/3 사각 구멍
        static ChartMesh HoleGrid(int nu, int nv, Func<float, float, Vector3> s)
            => Build(GridPositions(nu, nv, s), GridTris(nu, nv, (i, j) => i >= nu / 3 && i < nu - nu / 3 && j >= nv / 3 && j < nv - nv / 3));

        static Vector3[] GridPositions(int nu, int nv, Func<float, float, Vector3> s)
        {
            var p = new Vector3[(nu + 1) * (nv + 1)];
            for (int j = 0; j <= nv; j++)
                for (int i = 0; i <= nu; i++) p[j * (nu + 1) + i] = s((float)i / nu, (float)j / nv);
            return p;
        }

        static int[] GridTris(int nu, int nv, Func<int, int, bool> skipCell)
        {
            var t = new List<int>();
            for (int j = 0; j < nv; j++)
                for (int i = 0; i < nu; i++)
                {
                    if (skipCell != null && skipCell(i, j)) continue;
                    int p00 = j * (nu + 1) + i, p10 = p00 + 1, p01 = p00 + nu + 1, p11 = p01 + 1;
                    t.AddRange(new[] { p00, p10, p11, p00, p11, p01 });
                }
            return t.ToArray();
        }

        static Vector3 CylPoint(float arcDeg, float r, float h, float u, float v)
        {
            float th = (u - 0.5f) * arcDeg * Mathf.Deg2Rad;
            return new Vector3(r * Mathf.Sin(th), v * h, -r * Mathf.Cos(th));
        }

        static ChartMesh Cylinder(float arcDeg, float r, float h, int nu, int nv) => Grid(nu, nv, (u, v) => CylPoint(arcDeg, r, h, u, v));

        static ChartMesh SpherePatch(float lonDeg, float latDeg, int nu, int nv) => Grid(nu, nv, (u, v) =>
        {
            float lon = (u - 0.5f) * lonDeg * Mathf.Deg2Rad, lat = (v - 0.5f) * latDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(lat) * Mathf.Sin(lon), Mathf.Sin(lat), -Mathf.Cos(lat) * Mathf.Cos(lon));
        });

        // 원통 120° 격자 + 아랫변 첫 칸에 일직선 중점 M 을 끼운 면적 0 삼각형(마지막 삼각형) — 경계가 A→M→B 로 돈다
        static ChartMesh DegenerateCylinder()
        {
            const int nu = 12, nv = 4;
            var pos = new List<Vector3>(GridPositions(nu, nv, (u, v) => CylPoint(120f, 1.5f, 2f, u, v)));
            var tris = new List<int>(GridTris(nu, nv, null));
            int a = 0, b = 1;                               // 아랫변 첫 칸 (p00 → p10 은 경계 방향 변: 셀 삼각형 (p00,p10,p11))
            pos.Add((pos[a] + pos[b]) * 0.5f);
            int m = pos.Count - 1;
            tris.AddRange(new[] { b, a, m });               // 짝 변 b→a 로 경계를 닫고 a→m→b 가 새 경계
            return Build(pos.ToArray(), tris.ToArray());
        }

        static ChartMesh Clone(ChartMesh c) => new ChartMesh
        {
            chartID = c.chartID,
            positions = (Vector3[])c.positions.Clone(),
            MeshVertex = (int[])c.MeshVertex.Clone(),
            Triangles = (int[])c.Triangles.Clone(),
            Loops = new List<int[]>(c.Loops),
            PlaneNormal = c.PlaneNormal,
            UV = c.UV != null ? (Vector2[])c.UV.Clone() : null,
        };

        // Rodrigues 회전(Quaternion.Euler 는 네이티브 호출이라 헤드리스에서 피함)
        static Vector3 Rotate(Vector3 v, Vector3 axis, float deg)
        {
            var k = axis.normalized;
            float th = deg * Mathf.Deg2Rad, c = Mathf.Cos(th), s = Mathf.Sin(th);
            return v * c + Vector3.Cross(k, v) * s + k * (Vector3.Dot(k, v) * (1f - c));
        }

        // ───────────────────────── 판정 ─────────────────────────

        static bool AllPositive(ChartMesh cm, int skipLast = 0)
        {
            var t = cm.Triangles;
            int count = t.Length / 3 - skipLast;
            for (int i = 0; i < count; i++)
            {
                Vector2 a = cm.UV[t[3 * i]], b = cm.UV[t[3 * i + 1]], c = cm.UV[t[3 * i + 2]];
                double area = ((double)b.x - a.x) * ((double)c.y - a.y) - ((double)b.y - a.y) * ((double)c.x - a.x);
                if (!(area > 0.0)) return false;
            }
            return true;
        }

        // (구멍 루프 면적 / 외곽 루프 면적) 의 UV 대 3D 비 — 구멍을 자유 경계로 두면 구멍이 부풀어(≈3.4배) 1 에서 멀어짐
        static float HoleRatio(ChartMesh cm)
        {
            double Uv(int[] lp)
            {
                double s = 0.0;
                for (int i = 0; i < lp.Length; i++) { Vector2 a = cm.UV[lp[i]], b = cm.UV[lp[(i + 1) % lp.Length]]; s += (double)a.x * b.y - (double)a.y * b.x; }
                return Math.Abs(s) * 0.5;
            }
            double Area3(int[] lp)
            {
                Vector3 c = Vector3.zero;
                foreach (int v in lp) c += cm.positions[v];
                c /= lp.Length;
                double s = 0.0;
                for (int i = 0; i < lp.Length; i++) s += 0.5 * Vector3.Cross(cm.positions[lp[i]] - c, cm.positions[lp[(i + 1) % lp.Length]] - c).magnitude;
                return s;
            }
            double uvRatio = Uv(cm.Loops[1]) / Uv(cm.Loops[0]), r3 = Area3(cm.Loops[1]) / Area3(cm.Loops[0]);
            return (float)(uvRatio / r3);
        }

        // 외곽 루프 정점이 원점 중심 같은 반지름 위(MVC 경계)
        static bool OnCircle(ChartMesh cm)
        {
            var lp = cm.Loops[0];
            float r0 = cm.UV[lp[0]].magnitude;
            foreach (int v in lp) if (Mathf.Abs(cm.UV[v].magnitude - r0) > 1e-4f * Mathf.Max(1f, r0)) return false;
            return r0 > 0f;
        }

        // 정확 비교(Unity 의 Vector2 == 는 ε = 1e-5 근사라 비트 동일 판정에 못 씀)
        static bool Same(Vector2 a, Vector2 b) => a.x == b.x && a.y == b.y;

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
