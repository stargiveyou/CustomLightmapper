using System;
using System.Collections.Generic;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// MVC(Mean Value Coordinates, Floater 2003) 폴백. foldover(겹침)가 끝까지 남았을 때의 최후 안전망.
    /// 외곽 루프(cm.Loops[0])를 원 위에 3D 호길이 비례로 고정하고, 내부 정점은 항상 양수인 MVC 가중
    ///   w_ij = (tan(α_ij/2) + tan(β_ij/2)) / |v_i − v_j|    (α, β = 변 ij 양옆 삼각형의 v_i 각)
    /// 로 이웃의 볼록결합 u_i = Σ_j (w_ij/Σw)·u_j 를 선형 solve → 원판 위상 + 볼록 경계면 전단사(겹침 없음, Tutte/Floater).
    /// 가중이 비대칭(w_ij ≠ w_ji)이라 Jacobi 전처리 BiCGSTAB(double), 붕괴 시 Gauss–Seidel(대각우세 M-행렬 → 수렴).
    /// 구멍(cm.Loops[1..]): 루프 중심에 가상 정점 + 부채꼴 삼각형으로 메워 원판으로 만든 뒤 풀고 가상 정점은 버린다
    ///   (구멍 경계를 자유 정점으로 두면 Tutte 정리가 성립하지 않아 접힐 수 있음).
    /// 실패(false, UV 그대로): 경계 없음 · 루프 정점 중복/루프끼리 공유(핀치) · 수렴 실패 · NaN.
    /// 결과의 전체 부호 면적이 음수면 u 를 뒤집어 Planar 와 같은 방향(양수)으로 맞춘다.
    /// </summary>
    public static class MVCFallback
    {
        const double MaxTanHalf = 1e8;   // 각 → π (바늘 삼각형)에서 tan(θ/2) 발산 클램프

        public static bool Solve(ref ChartMesh cm) => Solve(ref cm, FlattenSettings.Default, out _);

        public static bool Solve(ref ChartMesh cm, in FlattenSettings s, out int iterations)
        {
            iterations = 0;
            if (cm?.positions == null || cm.Triangles == null || cm.Loops == null || cm.Loops.Count == 0) return false;
            int n = cm.VertexCount;
            var tri = cm.Triangles;
            var outer = cm.Loops[0];
            if (n < 3 || tri.Length < 3 || outer == null || outer.Length < 3) return false;

            // 1) 루프 검증: 정점 중복·루프 간 공유 없음
            var loopOf = new int[n];
            for (int i = 0; i < n; i++) loopOf[i] = -1;
            for (int li = 0; li < cm.Loops.Count; li++)
            {
                var lp = cm.Loops[li];
                if (lp == null) continue;
                foreach (int v in lp)
                {
                    if ((uint)v >= (uint)n || loopOf[v] != -1) return false;
                    loopOf[v] = li;
                }
            }

            // 2) 확장 메시: 원본 + 구멍마다 가상 중심 정점·부채꼴
            var holes = new List<int[]>();
            for (int li = 1; li < cm.Loops.Count; li++)
                if (cm.Loops[li] != null && cm.Loops[li].Length >= 3) holes.Add(cm.Loops[li]);
            int na = n + holes.Count;
            var px = new double[na]; var py = new double[na]; var pz = new double[na];
            for (int i = 0; i < n; i++) { px[i] = cm.positions[i].x; py[i] = cm.positions[i].y; pz[i] = cm.positions[i].z; }
            var triAug = new List<int>(tri.Length + 3 * 64);
            triAug.AddRange(tri);
            for (int h = 0; h < holes.Count; h++)
            {
                var lp = holes[h];
                int c = n + h;
                foreach (int v in lp) { px[c] += px[v]; py[c] += py[v]; pz[c] += pz[v]; }
                px[c] /= lp.Length; py[c] /= lp.Length; pz[c] /= lp.Length;
                for (int i = 0; i < lp.Length; i++) { triAug.Add(lp[i]); triAug.Add(lp[(i + 1) % lp.Length]); triAug.Add(c); }
            }

            // 3) 외곽 경계 → 원(3D 호길이 비례, 반지름 = 둘레/2π)
            var bu = new double[na]; var bv = new double[na];
            double total = 0.0;
            var cum = new double[outer.Length];
            for (int k = 0; k < outer.Length; k++)
            {
                cum[k] = total;
                total += Len(px, py, pz, outer[k], outer[(k + 1) % outer.Length]);
            }
            if (!(total > 1e-20)) return false;
            double radius = total / (2.0 * Math.PI);
            for (int k = 0; k < outer.Length; k++)
            {
                double th = 2.0 * Math.PI * cum[k] / total;
                bu[outer[k]] = radius * Math.Cos(th);
                bv[outer[k]] = radius * Math.Sin(th);
            }

            // 4) 내부 정점 번호 + MVC 가중 조립(삼각형 꼭짓점 각마다 두 변에 tan(θ/2)/|e|)
            var idx = new int[na];
            int m = 0;
            for (int i = 0; i < na; i++) idx[i] = (i < n && loopOf[i] == 0) ? -1 : m++;
            var uOut = new double[na]; var vOut = new double[na];
            if (m > 0)
            {
                var builder = new SparseMatrix.Builder(m);
                var rhsU = new double[m]; var rhsV = new double[m];
                var diag = new double[m];
                for (int t = 0; t + 2 < triAug.Count; t += 3)
                {
                    int a = triAug[t], b = triAug[t + 1], c = triAug[t + 2];
                    Corner(a, b, c); Corner(b, c, a); Corner(c, a, b);
                }

                void Corner(int i, int j, int k)
                {
                    int row = idx[i];
                    if (row < 0) return;
                    double xx = px[j] - px[i], xy = py[j] - py[i], xz = pz[j] - pz[i];
                    double yx = px[k] - px[i], yy = py[k] - py[i], yz = pz[k] - pz[i];
                    double lx = Math.Sqrt(xx * xx + xy * xy + xz * xz), ly = Math.Sqrt(yx * yx + yy * yy + yz * yz);
                    if (!(lx > 1e-20) || !(ly > 1e-20)) return;
                    double cxx = xy * yz - xz * yy, cxy = xz * yx - xx * yz, cxz = xx * yy - xy * yx;
                    double sinPart = Math.Sqrt(cxx * cxx + cxy * cxy + cxz * cxz);
                    double den = lx * ly + (xx * yx + xy * yy + xz * yz);          // tan(θ/2) = |x×y| / (|x||y| + x·y)
                    double tanHalf = den > sinPart / MaxTanHalf ? sinPart / den : MaxTanHalf;
                    Edge(row, j, tanHalf / lx);
                    Edge(row, k, tanHalf / ly);
                }

                void Edge(int row, int j, double w)
                {
                    if (!(w > 0.0)) return;
                    builder.Add(row, row, w);
                    diag[row] += w;
                    if (idx[j] >= 0) builder.Add(row, idx[j], -w);
                    else { rhsU[row] += w * bu[j]; rhsV[row] += w * bv[j]; }
                }

                // 가중이 0 인 고립 정점(전부 퇴화 삼각형) 대비 — 원 중심 쪽 아주 약한 정칙화
                double dsum = 0.0; int dcnt = 0;
                for (int r = 0; r < m; r++) if (diag[r] > 0.0) { dsum += diag[r]; dcnt++; }
                double lambda = dcnt > 0 ? 1e-12 * dsum / dcnt : 1e-12;
                for (int r = 0; r < m; r++) builder.Add(r, r, lambda);

                var mat = builder.Build();
                var xu = new double[m]; var xv = new double[m];
                if (!SolveOne(mat, rhsU, xu, s, ref iterations) || !SolveOne(mat, rhsV, xv, s, ref iterations)) return false;
                for (int i = 0; i < na; i++)
                    if (idx[i] >= 0) { uOut[i] = xu[idx[i]]; vOut[i] = xv[idx[i]]; }
            }
            for (int k = 0; k < outer.Length; k++) { uOut[outer[k]] = bu[outer[k]]; vOut[outer[k]] = bv[outer[k]]; }

            // 5) 원본 정점만 UV 로(가상 중심 버림), 방향 양수
            var uv = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                if (double.IsNaN(uOut[i]) || double.IsNaN(vOut[i]) || double.IsInfinity(uOut[i]) || double.IsInfinity(vOut[i])) return false;
                uv[i] = new Vector2((float)uOut[i], (float)vOut[i]);
            }
            if (LSCMSolver.SignedArea(uv, tri) < 0.0)
                for (int i = 0; i < n; i++) uv[i].x = -uv[i].x;
            cm.UV = uv;
            return true;
        }

        // BiCGSTAB → (붕괴·미수렴 시) 그 자리에서 Gauss–Seidel 이어서
        static bool SolveOne(SparseMatrix a, double[] b, double[] x, in FlattenSettings s, ref int iterations)
        {
            int maxIter = s.Iterations(a.N);
            var r = SparseSolver.BiCgStab(a, b, x, maxIter, s.Tol);
            iterations += r.Iterations;
            if (r.Converged) return true;
            for (int i = 0; i < x.Length; i++) if (double.IsNaN(x[i]) || double.IsInfinity(x[i])) x[i] = 0.0;
            r = SparseSolver.GaussSeidel(a, b, x, 10 * maxIter, s.Tol);
            iterations += r.Iterations;
            return r.Converged || r.RelativeResidual < 1e-6;
        }

        static double Len(double[] px, double[] py, double[] pz, int i, int j)
        {
            double dx = px[j] - px[i], dy = py[j] - py[i], dz = pz[j] - pz[i];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
