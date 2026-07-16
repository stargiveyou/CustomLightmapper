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
    }
}