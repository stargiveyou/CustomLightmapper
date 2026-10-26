using System;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// LSCM(Least-Squares Conformal Map, Lévy et al. 2002). 곡률 차트용 등각 자유경계 평탄화.
    ///
    /// 삼각형마다 자기 평면의 등거리 로컬 좌표 x_j(꼭짓점 0 = 원점, 0→1 = x 축, 꼭짓점 2 는 y &gt; 0)를 잡고
    /// Cauchy–Riemann 잔차 R_T = Σ_j W_j·U_j (W_j = x_{j+2} − x_{j+1} 복소수, U_j = u_j + i·v_j)의
    /// 면적 정규화 제곱합 E = Σ_T |R_T|² / (2·A_T) 를 최소화한다(∂U/∂z̄ = 0 의 최소제곱 — 방향 보존).
    /// 실수화: W·U = (a·u − b·v) + i(b·u + a·v) → 삼각형당 2행. 외곽 루프의 근사 지름 두 점을 (0,0)·(|p_a − p_b|, 0) 에 고정하면
    /// 정규방정식 AᵀA·x = Aᵀb 가 대칭 양정치 → Jacobi 전처리 CG(double). 초기값 = 평면투영 UV 를 두 고정점에 맞춘 상사변환.
    /// 퇴화 삼각형(로컬 높이 ≈ 0)은 건너뛰고, 그래서 에너지가 없는 정점이 생겨도 풀리도록 초기값 쪽으로 아주 약한 정칙화
    /// (λ = 1e-9 × 평균 대각)를 더한다. 스케일은 임의 — 후단 DensityNormalizer 가 맞춘다.
    /// 결과의 전체 부호 면적이 음수면 u 를 뒤집어 Planar 와 같은 방향(양수)으로 맞춘다.
    /// </summary>
    public static class LSCMSolver
    {
        public static bool Solve(ref ChartMesh cm) => Solve(ref cm, FlattenSettings.Default, out _);

        /// <summary>성공 시 cm.UV 를 LSCM 해로 교체하고 true. 경계 없음·고정점 불가·수렴 실패·NaN 이면 false(UV 그대로).</summary>
        public static bool Solve(ref ChartMesh cm, in FlattenSettings s, out int iterations)
        {
            iterations = 0;
            if (cm?.positions == null || cm.Triangles == null) return false;
            int n = cm.VertexCount;
            var tri = cm.Triangles;
            var pos = cm.positions;
            if (n < 3 || tri.Length < 3 || cm.Loops == null || cm.Loops.Count == 0 || cm.Loops[0] == null || cm.Loops[0].Length < 2) return false;

            // 1) 고정점: 외곽 루프의 근사 지름(2-pass 최원점) → (0,0), (d,0)
            var loop = cm.Loops[0];
            int pa = Farthest(pos, loop, loop[0]);
            int pb = Farthest(pos, loop, pa);
            double dist = Math.Sqrt(Dist2(pos[pa], pos[pb]));
            if (pa == pb || !(dist > 1e-12)) return false;

            // 2) 미지수 번호: u_i → i, v_i → n + i. 고정 4개 제외 → m = 2n − 4
            var free = new int[2 * n];
            var pinVal = new double[2 * n];
            int m = 0;
            for (int k = 0; k < 2 * n; k++)
            {
                int vi = k < n ? k : k - n;
                free[k] = (vi == pa || vi == pb) ? -1 : m++;
            }
            pinVal[pb] = dist;   // u_pb = d, 나머지 고정값 0

            // 3) 초기값: 평면투영 UV(있으면)를 pa→0, pb→d 로 보내는 복소 상사변환
            var x = new double[m];
            var uv0 = cm.UV;
            if (uv0 != null && uv0.Length == n)
            {
                double zax = uv0[pa].x, zay = uv0[pa].y;
                double dzx = uv0[pb].x - zax, dzy = uv0[pb].y - zay;
                double dz2 = dzx * dzx + dzy * dzy;
                if (dz2 > 1e-24)
                {
                    double fx = dist * dzx / dz2, fy = -dist * dzy / dz2;   // f = d / dz
                    for (int i = 0; i < n; i++)
                    {
                        double px = uv0[i].x - zax, py = uv0[i].y - zay;
                        double u = px * fx - py * fy, v = px * fy + py * fx;
                        if (free[i] >= 0) x[free[i]] = u;
                        if (free[n + i] >= 0) x[free[n + i]] = v;
                    }
                }
            }

            // 4) 조립: 삼각형당 실수부·허수부 2행 → AᵀA(6×6 블록) · 고정 열은 우변으로
            var builder = new SparseMatrix.Builder(m);
            var rhs = new double[m];
            var diag = new double[m];
            var vars = new int[6];
            var a = new double[3];
            var b = new double[3];
            var coef = new double[6];
            int used = 0;
            for (int t = 0; t + 2 < tri.Length; t += 3)
            {
                int i0 = tri[t], i1 = tri[t + 1], i2 = tri[t + 2];
                Vector3 p0 = pos[i0], p1 = pos[i1], p2 = pos[i2];
                double e1x = (double)p1.x - p0.x, e1y = (double)p1.y - p0.y, e1z = (double)p1.z - p0.z;
                double e2x = (double)p2.x - p0.x, e2y = (double)p2.y - p0.y, e2z = (double)p2.z - p0.z;
                double l1 = Math.Sqrt(e1x * e1x + e1y * e1y + e1z * e1z);
                if (!(l1 > 1e-20)) continue;
                double cx = (e2x * e1x + e2y * e1y + e2z * e1z) / l1;
                double crx = e1y * e2z - e1z * e2y, cry = e1z * e2x - e1x * e2z, crz = e1x * e2y - e1y * e2x;
                double cr = Math.Sqrt(crx * crx + cry * cry + crz * crz);   // = 2·면적
                double cy = cr / l1;
                if (!(cy > 1e-12 * l1)) continue;                            // 퇴화
                used++;

                // 로컬 x0 = (0,0), x1 = (l1,0), x2 = (cx,cy) — W_j = x_{j+2} − x_{j+1}
                a[0] = cx - l1; b[0] = cy;
                a[1] = -cx;     b[1] = -cy;
                a[2] = l1;      b[2] = 0.0;
                double inv = 1.0 / cr;
                vars[0] = i0; vars[1] = i1; vars[2] = i2;
                vars[3] = n + i0; vars[4] = n + i1; vars[5] = n + i2;

                for (int j = 0; j < 3; j++) { coef[j] = a[j]; coef[3 + j] = -b[j]; }   // 실수부: a·u − b·v
                Accumulate(builder, rhs, diag, free, pinVal, vars, coef, inv);
                for (int j = 0; j < 3; j++) { coef[j] = b[j]; coef[3 + j] = a[j]; }    // 허수부: b·u + a·v
                Accumulate(builder, rhs, diag, free, pinVal, vars, coef, inv);
            }
            if (used == 0) return false;

            // 5) 정칙화(초기값 쪽, 아주 약하게) — 에너지 없는 정점의 특이성 제거
            double dsum = 0.0; int dcnt = 0;
            for (int k = 0; k < m; k++) if (diag[k] > 0.0) { dsum += diag[k]; dcnt++; }
            double lambda = dcnt > 0 ? 1e-9 * dsum / dcnt : 1e-9;
            for (int k = 0; k < m; k++) { builder.Add(k, k, lambda); rhs[k] += lambda * x[k]; }

            var res = SparseSolver.ConjugateGradient(builder.Build(), rhs, x, s.Iterations(m), s.Tol);
            iterations = res.Iterations;
            if (!res.Converged && !(res.RelativeResidual < 1e-6)) return false;

            // 6) 해 → UV (방향: 전체 부호 면적 양수)
            var uv = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                double u = free[i] >= 0 ? x[free[i]] : pinVal[i];
                double v = free[n + i] >= 0 ? x[free[n + i]] : pinVal[n + i];
                if (double.IsNaN(u) || double.IsNaN(v) || double.IsInfinity(u) || double.IsInfinity(v)) return false;
                uv[i] = new Vector2((float)u, (float)v);
            }
            if (SignedArea(uv, tri) < 0.0)
                for (int i = 0; i < n; i++) uv[i].x = -uv[i].x;
            cm.UV = uv;
            return true;
        }

        static void Accumulate(SparseMatrix.Builder builder, double[] rhs, double[] diag, int[] free, double[] pinVal,
                               int[] vars, double[] coef, double inv)
        {
            for (int k = 0; k < 6; k++)
            {
                int fk = free[vars[k]];
                double ck = coef[k];
                if (fk < 0 || ck == 0.0) continue;
                for (int l = 0; l < 6; l++)
                {
                    double cl = coef[l];
                    if (cl == 0.0) continue;
                    int fl = free[vars[l]];
                    double w = ck * cl * inv;
                    if (fl >= 0)
                    {
                        builder.Add(fk, fl, w);
                        if (fl == fk) diag[fk] += w;
                    }
                    else rhs[fk] -= w * pinVal[vars[l]];
                }
            }
        }

        static int Farthest(Vector3[] pos, int[] loop, int from)
        {
            int best = from;
            double bestD = -1.0;
            foreach (int v in loop)
            {
                double d = Dist2(pos[from], pos[v]);
                if (d > bestD) { bestD = d; best = v; }
            }
            return best;
        }

        static double Dist2(Vector3 p, Vector3 q)
        {
            double dx = (double)p.x - q.x, dy = (double)p.y - q.y, dz = (double)p.z - q.z;
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>UV 전체 부호 면적(×2) — 삼각형 (i0,i1,i2) 순서 그대로.</summary>
        internal static double SignedArea(Vector2[] uv, int[] tri)
        {
            double s = 0.0;
            for (int t = 0; t + 2 < tri.Length; t += 3)
            {
                Vector2 p = uv[tri[t]], q = uv[tri[t + 1]], r = uv[tri[t + 2]];
                s += ((double)q.x - p.x) * ((double)r.y - p.y) - ((double)q.y - p.y) * ((double)r.x - p.x);
            }
            return s;
        }
    }
}
