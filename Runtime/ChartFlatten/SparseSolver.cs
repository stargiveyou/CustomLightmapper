using System;
using System.Collections.Generic;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 차트 평탄화(LSCM·MVC)용 CSR 희소 행렬. double · 결정적.
    /// <see cref="Builder"/> 는 행마다 삽입 순서대로 같은 (행, 열) 값을 합친다 → 정렬 알고리즘과 무관하게 합산 순서가 고정.
    /// </summary>
    public sealed class SparseMatrix
    {
        public readonly int N;
        public readonly int[] RowStart;   // N + 1
        public readonly int[] Col;
        public readonly double[] Val;
        public readonly double[] Diag;    // 대각 성분(없으면 0) — Jacobi 전처리·Gauss–Seidel

        SparseMatrix(int n, int[] rowStart, int[] col, double[] val)
        {
            N = n; RowStart = rowStart; Col = col; Val = val;
            Diag = new double[n];
            for (int r = 0; r < n; r++)
                for (int k = rowStart[r]; k < rowStart[r + 1]; k++)
                    if (col[k] == r) Diag[r] = val[k];
        }

        public int NonZeros => Val.Length;

        /// <summary>y = A·x</summary>
        public void Multiply(double[] x, double[] y)
        {
            for (int r = 0; r < N; r++)
            {
                double s = 0.0;
                for (int k = RowStart[r]; k < RowStart[r + 1]; k++) s += Val[k] * x[Col[k]];
                y[r] = s;
            }
        }

        /// <summary>트리플릿 누적기. 행별 (열, 값) 목록 — 같은 열은 선형 탐색으로 합침(평탄화 행렬은 행당 ~10~20 개).</summary>
        public sealed class Builder
        {
            readonly int _n;
            readonly List<int>[] _cols;
            readonly List<double>[] _vals;

            public Builder(int n)
            {
                _n = n;
                _cols = new List<int>[n];
                _vals = new List<double>[n];
            }

            public void Add(int row, int col, double v)
            {
                var c = _cols[row];
                if (c == null) { _cols[row] = c = new List<int>(16); _vals[row] = new List<double>(16); }
                var vs = _vals[row];
                for (int i = 0; i < c.Count; i++)
                    if (c[i] == col) { vs[i] += v; return; }
                c.Add(col);
                vs.Add(v);
            }

            public SparseMatrix Build()
            {
                var rowStart = new int[_n + 1];
                for (int r = 0; r < _n; r++) rowStart[r + 1] = rowStart[r] + (_cols[r]?.Count ?? 0);
                var col = new int[rowStart[_n]];
                var val = new double[rowStart[_n]];
                for (int r = 0; r < _n; r++)
                {
                    var c = _cols[r];
                    if (c == null) continue;
                    int o = rowStart[r];
                    var keys = c.ToArray();
                    var vals = _vals[r].ToArray();
                    Array.Sort(keys, vals);   // 열이 서로 다르므로 결과가 정렬 알고리즘과 무관
                    Array.Copy(keys, 0, col, o, keys.Length);
                    Array.Copy(vals, 0, val, o, vals.Length);
                }
                return new SparseMatrix(_n, rowStart, col, val);
            }
        }
    }

    /// <summary>
    /// 희소 반복 솔버(double, 결정적·직렬). 수렴 판정 = ‖b − A·x‖ ≤ tol·‖b‖.
    ///  · <see cref="ConjugateGradient"/>: 대칭 양정치(LSCM 정규방정식) — Jacobi 전처리.
    ///  · <see cref="BiCgStab"/>: 비대칭(MVC — 가중치 w_ij ≠ w_ji) — Jacobi 전처리.
    ///  · <see cref="GaussSeidel"/>: 대각우세 M-행렬(MVC)이면 수렴 보장 — BiCGSTAB 붕괴 시 폴백.
    /// x 는 초기값으로 받고 해로 덮어쓴다.
    /// </summary>
    public static class SparseSolver
    {
        public struct Result
        {
            public bool Converged;
            public int Iterations;
            public double RelativeResidual;
        }

        const double Tiny = 1e-300;

        public static Result ConjugateGradient(SparseMatrix a, double[] b, double[] x, int maxIter, double tol)
        {
            int n = a.N;
            var r = new double[n]; var z = new double[n]; var p = new double[n]; var ap = new double[n];
            double bn = Norm(b);
            if (bn < Tiny) { Array.Clear(x, 0, n); return new Result { Converged = true }; }
            a.Multiply(x, ap);
            for (int i = 0; i < n; i++) r[i] = b[i] - ap[i];
            Precondition(a, r, z);
            Array.Copy(z, p, n);
            double rz = Dot(r, z);
            double rel = Norm(r) / bn;
            if (rel <= tol) return new Result { Converged = true, RelativeResidual = rel };
            for (int it = 1; it <= maxIter; it++)
            {
                a.Multiply(p, ap);
                double pap = Dot(p, ap);
                if (!(pap > Tiny)) return new Result { Iterations = it, RelativeResidual = rel };   // SPD 아님/붕괴
                double alpha = rz / pap;
                for (int i = 0; i < n; i++) { x[i] += alpha * p[i]; r[i] -= alpha * ap[i]; }
                rel = Norm(r) / bn;
                if (rel <= tol) return new Result { Converged = true, Iterations = it, RelativeResidual = rel };
                Precondition(a, r, z);
                double rzNew = Dot(r, z);
                double beta = rzNew / rz;
                rz = rzNew;
                for (int i = 0; i < n; i++) p[i] = z[i] + beta * p[i];
            }
            return new Result { Iterations = maxIter, RelativeResidual = rel };
        }

        public static Result BiCgStab(SparseMatrix a, double[] b, double[] x, int maxIter, double tol)
        {
            int n = a.N;
            var r = new double[n]; var rh = new double[n]; var p = new double[n]; var v = new double[n];
            var y = new double[n]; var s = new double[n]; var z = new double[n]; var t = new double[n];
            double bn = Norm(b);
            if (bn < Tiny) { Array.Clear(x, 0, n); return new Result { Converged = true }; }
            a.Multiply(x, t);
            for (int i = 0; i < n; i++) { r[i] = b[i] - t[i]; rh[i] = r[i]; }
            double rel = Norm(r) / bn;
            if (rel <= tol) return new Result { Converged = true, RelativeResidual = rel };
            double rho = 1.0, alpha = 1.0, omega = 1.0;
            for (int it = 1; it <= maxIter; it++)
            {
                double rhoNew = Dot(rh, r);
                if (Math.Abs(rhoNew) < Tiny) return new Result { Iterations = it, RelativeResidual = rel };
                double beta = (rhoNew / rho) * (alpha / omega);
                for (int i = 0; i < n; i++) p[i] = r[i] + beta * (p[i] - omega * v[i]);
                Precondition(a, p, y);
                a.Multiply(y, v);
                double rv = Dot(rh, v);
                if (Math.Abs(rv) < Tiny) return new Result { Iterations = it, RelativeResidual = rel };
                alpha = rhoNew / rv;
                for (int i = 0; i < n; i++) s[i] = r[i] - alpha * v[i];
                double sRel = Norm(s) / bn;
                if (sRel <= tol)
                {
                    for (int i = 0; i < n; i++) x[i] += alpha * y[i];
                    return new Result { Converged = true, Iterations = it, RelativeResidual = sRel };
                }
                Precondition(a, s, z);
                a.Multiply(z, t);
                double tt = Dot(t, t);
                if (!(tt > Tiny)) return new Result { Iterations = it, RelativeResidual = rel };
                omega = Dot(t, s) / tt;
                for (int i = 0; i < n; i++) { x[i] += alpha * y[i] + omega * z[i]; r[i] = s[i] - omega * t[i]; }
                rel = Norm(r) / bn;
                if (rel <= tol) return new Result { Converged = true, Iterations = it, RelativeResidual = rel };
                if (Math.Abs(omega) < Tiny) return new Result { Iterations = it, RelativeResidual = rel };
                rho = rhoNew;
            }
            return new Result { Iterations = maxIter, RelativeResidual = rel };
        }

        /// <summary>전진 Gauss–Seidel. 잔차는 checkEvery 회마다 확인(비용 절감).</summary>
        public static Result GaussSeidel(SparseMatrix a, double[] b, double[] x, int maxIter, double tol, int checkEvery = 10)
        {
            int n = a.N;
            double bn = Norm(b);
            if (bn < Tiny) { Array.Clear(x, 0, n); return new Result { Converged = true }; }
            var ax = new double[n];
            double rel = double.PositiveInfinity;
            for (int it = 1; it <= maxIter; it++)
            {
                for (int i = 0; i < n; i++)
                {
                    double d = a.Diag[i];
                    if (Math.Abs(d) < Tiny) continue;
                    double sum = b[i];
                    for (int k = a.RowStart[i]; k < a.RowStart[i + 1]; k++)
                        if (a.Col[k] != i) sum -= a.Val[k] * x[a.Col[k]];
                    x[i] = sum / d;
                }
                if (it % checkEvery != 0 && it != maxIter) continue;
                a.Multiply(x, ax);
                double rs = 0.0;
                for (int i = 0; i < n; i++) { double e = b[i] - ax[i]; rs += e * e; }
                rel = Math.Sqrt(rs) / bn;
                if (rel <= tol) return new Result { Converged = true, Iterations = it, RelativeResidual = rel };
            }
            return new Result { Iterations = maxIter, RelativeResidual = rel };
        }

        // Jacobi: z = r / diag (대각 0 이면 그대로)
        static void Precondition(SparseMatrix a, double[] r, double[] z)
        {
            for (int i = 0; i < r.Length; i++)
            {
                double d = a.Diag[i];
                z[i] = Math.Abs(d) > Tiny ? r[i] / d : r[i];
            }
        }

        internal static double Dot(double[] a, double[] b)
        {
            double s = 0.0;
            for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }

        internal static double Norm(double[] a) => Math.Sqrt(Dot(a, a));
    }
}
