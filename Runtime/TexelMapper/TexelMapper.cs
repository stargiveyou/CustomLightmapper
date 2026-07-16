using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /*
     A3 조립 메시(uv2 + normals)를 받아 uv2 삼각형을 아틀라스에 래스터, 
     텍셀마다 바리센트릭으로 worldPos·worldNormal·valid를 복원해 LumelMap 반환.
     인스턴스 트랜스폼(localToWorldMatrix)을 받아 월드 공간으로 변환. 레이도 가속구조도 안 씁니다.
    */

    /// <summary>텍셀별 복원 결과(루멜 맵). 라이팅 베이크의 입력이자 디버그 대상.</summary>
    public struct LumelMap
    {
        public int Resolution;
        public Vector3[] WorldPos;     // R*R
        public Vector3[] WorldNormal;  // R*R (정규화)
        public bool[] Valid;           // R*R (차트 커버 여부)
        public Vector3 BoundsMin, BoundsMax; // valid 위치 인코딩용
    }

    /// <summary>
    /// uv2(아틀라스 텍셀) <-> 월드 좌표/노멀 복원.
    /// 레이트레이싱·BVH 불필요.
    /// 조립 메시 (uv2 + normals) 만으로 동작 -> C1/C2 없이 단독 테스트 가능
    /// </summary>
    public static class TexelMapper
    {
        public static LumelMap Map(Mesh mesh, int res) => Map(mesh, res, Matrix4x4.identity);

        public static LumelMap Map(Mesh mesh, int res, Matrix4x4 l2w)
        {
            var V = mesh.vertices;
            var N = mesh.normals;
            var UV = mesh.uv2;
            var T = mesh.triangles;


            if (UV == null || UV.Length != V.Length) throw new System.Exception("[TexelMapper] uv2 없음/불일치");
            if (N == null || N.Length != V.Length) { mesh.RecalculateNormals(); N = mesh.normals; }

            var wp = new Vector3[V.Length];
            var wn = new Vector3[V.Length];

            for (int i = 0; i < V.Length; i++)
            {
                wp[i] = l2w.MultiplyPoint3x4(V[i]);
                wn[i] = l2w.MultiplyVector(N[i]).normalized; // 노멀=방향 → 이동 성분 제외(MultiplyVector)
            }

            var m = new LumelMap
            {
                Resolution = res,
                WorldPos = new Vector3[res * res],
                WorldNormal = new Vector3[res * res],
                Valid = new bool[res * res]
            };

            for (int t = 0; t < T.Length; t += 3)
            {
                int i0 = T[t], i1 = T[t + 1], i2 = T[t + 2];
                Raster(m, res,  UV[i0] * res, UV[i1] * res, UV[i2] * res,
                                wp[i0]      , wp[i1]      , wp[i2]      ,
                                wn[i0]      , wn[i1]      , wn[i2]
                );
            }

            // valid 텍셀의 월드 바운드(position 인코딩용)
            Vector3 mn = new Vector3(1e30f, 1e30f, 1e30f), mx = -mn;
            bool any = false;
            for (int i = 0; i < m.Valid.Length; i++)
            {
                if (m.Valid[i])
                {
                    mn = Vector3.Min(mn, m.WorldPos[i]);
                    mx = Vector3.Max(mx, m.WorldPos[i]);
                    any = true;
                }
            }

            if (!any)
            {
                mn = Vector3.zero;
                mx = Vector3.one;
            }
            m.BoundsMin = mn; m.BoundsMax = mx;
            return m;
        }

        /// 소프트웨어 래스터라이저(Software Rasterizer)
        /// 2D 라이트맵 픽셀(Texel, Lumel)을 3D 월드 공간의 정보로 복원해 내는가.
        private static void Raster(LumelMap m, int res,
        Vector2 p0, Vector2 p1, Vector2 p2,
        Vector3 w0, Vector3 w1, Vector3 w2,
        Vector3 n0, Vector3 n1, Vector3 n2)
        {
            int minx = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x))), 0, res - 1);
            int maxx = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x))), 0, res - 1);
            int miny = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y))), 0, res - 1);
            int maxy = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y))), 0, res - 1);

            float denom = (p1.y - p2.y) * (p0.x - p2.x) + (p2.x - p1.x) * (p0.y - p2.y);
            if (Mathf.Abs(denom) < 1e-9f)
                return;

            for (int y = miny; y <= maxy; y++)
            {
                for (int x = minx; x <= maxx; x++)
                {

                    //무게 중심 좌표계(Bycentric Coordinates)계산
                    // 0.5f 를 더하는 이유 -> 픽셀의 테두리가 아닌 픽셀의 정중앙에 레이를 쏘기 위함
                    float fx = x + 0.5f;
                    float fy = y + 0.5f;


                    float b0 = ((p1.y - p2.y) * (fx - p2.x) + (p2.x - p1.x) * (fy - p2.y)) / denom;
                    float b1 = ((p2.y - p0.y) * (fx - p2.x) + (p0.x - p2.x) * (fy - p2.y)) / denom;
                    float b2 = 1f - b0 - b1;


                    //삼각형 내부 판별 (Point-in-Triangle Test)
                    if (!((b0 >= 0 && b1 >= 0 && b2 >= 0) || (b0 <= 0 && b1 <= 0 && b2 <= 0)))
                        continue;

                    int idx = y * res + x;
                    m.WorldPos[idx] = w0 * b0 + w1 * b1 + w2 * b2;
                    m.WorldNormal[idx] = (n0 * b0 + n1 * b1 + n2 * b2).normalized;
                    m.Valid[idx] = true;
                }
            }


        }


    }

}