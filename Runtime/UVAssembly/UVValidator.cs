using System;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    public struct UVReport
    {
        public bool HasFoldover;   // 부호 혼재(겹침) → 폴백 필요
        public int Triangles;
        public int Flipped;        // 소수파(뒤집힌) 삼각형 수
        public int Degenerate;     // 면적 ~0
        public float MinArea;      // UV 삼각형 |면적| (밀도 감 잡기용)
        public float MaxArea;

        public override string ToString() =>
            $"tris={Triangles}, foldover={HasFoldover}, flipped={Flipped}, degenerate={Degenerate}, area[min={MinArea:0.0000}, max={MaxArea:0.0000}]";
    }

    /// <summary>UV 유효성 검사. 삼각형 부호 면적이 섞이면 foldover(겹침)로 판정.</summary>
    public static class UVValidator
    {
        public static UVReport Validate(ChartMesh cm, float degenEps = 1e-12f)
        {
            var uv = cm.UV;
            var t = cm.Triangles;
            int pos = 0, neg = 0, degen = 0;
            float minA = float.MaxValue, maxA = 0f;

            for (int i = 0; i < t.Length; i += 3)
            {
                Vector2 a = uv[t[i]], b = uv[t[i + 1]], c = uv[t[i + 2]];
                float area = 0.5f * ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)); // 부호 면적
                float abs = Mathf.Abs(area);
                if (abs < degenEps) degen++;
                else if (area > 0f) pos++; else neg++;
                if (abs < minA) minA = abs;
                if (abs > maxA) maxA = abs;
            }

            return new UVReport
            {
                Triangles = t.Length / 3,
                HasFoldover = pos > 0 && neg > 0,
                Flipped = Mathf.Min(pos, neg),
                Degenerate = degen,
                MinArea = minA == float.MaxValue ? 0f : minA,
                MaxArea = maxA,
            };
        }

        public static bool HasFoldover(ChartMesh cm) => Validate(cm).HasFoldover;

        /// <summary>
        /// L2 stretch(Sander et al. 2001) — UV→3D 야코비안 특이값 Γ,γ 의 삼각형별 √((Γ²+γ²)/2) 를 3D 면적 가중 RMS 한 뒤
        /// √(ΣA_uv/ΣA_3D) 로 정규화: 등거리(또는 균일 스케일) = 1, 왜곡이 클수록 커짐. 3D 면적이 있는데 UV 면적이 0 인
        /// 삼각형이 있으면 +∞. 차트 평탄화 디스패처(<see cref="ChartFlattener"/>)의 Planar/LSCM 비교 척도.
        /// </summary>
        public static float Stretch(ChartMesh cm)
        {
            var uv = cm.UV;
            var t = cm.Triangles;
            var q = cm.positions;
            double acc = 0.0, sum3 = 0.0, sumUv = 0.0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                Vector3 q1 = q[t[i]], q2 = q[t[i + 1]], q3 = q[t[i + 2]];
                Vector2 a = uv[t[i]], b = uv[t[i + 1]], c = uv[t[i + 2]];
                double ex = (double)q2.x - q1.x, ey = (double)q2.y - q1.y, ez = (double)q2.z - q1.z;
                double fx = (double)q3.x - q1.x, fy = (double)q3.y - q1.y, fz = (double)q3.z - q1.z;
                double cx = ey * fz - ez * fy, cy = ez * fx - ex * fz, cz = ex * fy - ey * fx;
                double a3 = 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
                if (!(a3 > 1e-20)) continue;
                double s1 = a.x, t1 = a.y, s2 = b.x, t2 = b.y, s3 = c.x, t3 = c.y;
                double auv = 0.5 * ((s2 - s1) * (t3 - t1) - (s3 - s1) * (t2 - t1));
                if (!(Math.Abs(auv) > 1e-20)) return float.PositiveInfinity;
                double k = 1.0 / (2.0 * auv);
                // Ss = ∂q/∂s, St = ∂q/∂t
                double ssx = (q1.x * (t2 - t3) + q2.x * (t3 - t1) + q3.x * (t1 - t2)) * k;
                double ssy = (q1.y * (t2 - t3) + q2.y * (t3 - t1) + q3.y * (t1 - t2)) * k;
                double ssz = (q1.z * (t2 - t3) + q2.z * (t3 - t1) + q3.z * (t1 - t2)) * k;
                double stx = (q1.x * (s3 - s2) + q2.x * (s1 - s3) + q3.x * (s2 - s1)) * k;
                double sty = (q1.y * (s3 - s2) + q2.y * (s1 - s3) + q3.y * (s2 - s1)) * k;
                double stz = (q1.z * (s3 - s2) + q2.z * (s1 - s3) + q3.z * (s2 - s1)) * k;
                double aa = ssx * ssx + ssy * ssy + ssz * ssz, cc = stx * stx + sty * sty + stz * stz;
                acc += a3 * 0.5 * (aa + cc);
                sum3 += a3;
                sumUv += Math.Abs(auv);
            }
            if (!(sum3 > 0.0)) return 1f;
            return (float)(Math.Sqrt(acc / sum3) * Math.Sqrt(sumUv / sum3));
        }
    }
}