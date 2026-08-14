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
    /// SSAA(텍셀 내부 슈퍼샘플링) 복원 결과. 요청한 텍셀(엣지 마스크)에 대해서만 슬롯을 잡는 압축 저장 —
    /// 전 텍셀을 S*S 로 들고 있으면 메모리가 S² 배로 터지므로 필요한 텍셀만 담는다.
    /// 배열 인덱스 규약: <c>slot * SamplesPerTexel + sub</c> (sub ∈ [0, SamplesPerTexel), 위치는 <see cref="TexelMapper.SubOffset"/>).
    /// </summary>
    public struct LumelSubsamples
    {
        public int Factor;            // S (한 축 서브샘플 수)
        public int SamplesPerTexel;   // S*S
        public int SlotCount;         // 마스크된 텍셀 수
        public int[] SlotOfTexel;     // [li] -> slot, 마스크 밖이면 -1 (길이 R*R)
        public int[] TexelOfSlot;     // [slot] -> li
        public Vector3[] WorldPos;    // SlotCount * SamplesPerTexel
        public Vector3[] WorldNormal; // SlotCount * SamplesPerTexel (정규화)
        public bool[] Valid;          // SlotCount * SamplesPerTexel (서브샘플이 삼각형 내부인가)
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
            Prepare(mesh, l2w, out var UV, out var T, out var wp, out var wn);

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

        /// <summary>
        /// SSAA용 서브텍셀 래스터. <paramref name="texelMask"/>[li]==true 인 텍셀에 대해서만
        /// S×S 서브샘플의 worldPos/worldNormal/valid 를 복원한다.
        /// <see cref="Map"/> 과 래스터 규약(바리센트릭·내부판정·마지막 삼각형 우선)은 동일하고,
        /// 다른 것은 샘플 위치뿐이다 — <see cref="SubOffset"/> 참조.
        /// 레이는 여기서 안 쏜다. 반환된 점들을 기존 베이크 백엔드에 그대로 흘리면 된다.
        /// </summary>
        public static LumelSubsamples MapSubsamples(Mesh mesh, int res, Matrix4x4 l2w, int factor, bool[] texelMask)
        {
            int S = Mathf.Max(1, factor);
            int spt = S * S;

            // 슬롯 테이블 — 마스크된 텍셀만 0..SlotCount-1 로 압축
            var slotOf = new int[res * res];
            int slots = 0;
            for (int i = 0; i < slotOf.Length; i++)
                slotOf[i] = (texelMask != null && i < texelMask.Length && texelMask[i]) ? slots++ : -1;

            var ss = new LumelSubsamples
            {
                Factor = S,
                SamplesPerTexel = spt,
                SlotCount = slots,
                SlotOfTexel = slotOf,
                TexelOfSlot = new int[slots],
                WorldPos = new Vector3[slots * spt],
                WorldNormal = new Vector3[slots * spt],
                Valid = new bool[slots * spt],
            };
            for (int i = 0; i < slotOf.Length; i++)
                if (slotOf[i] >= 0) ss.TexelOfSlot[slotOf[i]] = i;
            if (slots == 0) return ss;

            Prepare(mesh, l2w, out var UV, out var T, out var wp, out var wn);

            for (int t = 0; t < T.Length; t += 3)
            {
                int i0 = T[t], i1 = T[t + 1], i2 = T[t + 2];
                RasterSub(ref ss, res, S, spt, UV[i0] * res, UV[i1] * res, UV[i2] * res,
                          wp[i0], wp[i1], wp[i2], wn[i0], wn[i1], wn[i2]);
            }
            return ss;
        }

        /// <summary>
        /// 텍셀 내 서브샘플 오프셋(0..1). sub ∈ [0, S*S).
        ///
        /// 정규격자(sx+0.5)/S 를 쓰면 축에 나란한 그림자 경계에서 유효 계조가 S 단계밖에 안 나온다
        /// (S=2면 0/50/100% 셋뿐 — 계단이 그대로 남는다). 그래서 두 축 모두 N=S² 단계가 나오도록
        /// **u=층화, v=황금비 저불일치**로 흩는다. 어느 방향의 경계든 투영 샘플이 고르게 퍼진다.
        ///
        /// RNG 가 아니라 인덱스만으로 정해지는 수열인 이유는 SunConeDirection 과 같다 —
        /// 결정적이고, 재현 가능하고, 백엔드가 달라도 같은 위치가 나온다.
        /// </summary>
        public static void SubOffset(int S, int sub, out float u, out float v)
        {
            int n = S * S;
            u = (sub + 0.5f) / n;                                    // 층화(stratified)
            float g = sub * 0.6180339887498949f + 0.5f / n;          // 황금비 회전
            v = g - Mathf.Floor(g);
        }

        // 메시 → uv2 / 삼각형 / 월드 정점·노멀. Map 과 MapSubsamples 가 같은 입력을 보도록 한 곳에 둔다.
        private static void Prepare(Mesh mesh, Matrix4x4 l2w,
            out Vector2[] UV, out int[] T, out Vector3[] wp, out Vector3[] wn)
        {
            var V = mesh.vertices;
            var N = mesh.normals;
            UV = mesh.uv2;
            T = mesh.triangles;

            if (UV == null || UV.Length != V.Length) throw new System.Exception("[TexelMapper] uv2 없음/불일치");
            if (N == null || N.Length != V.Length) { mesh.RecalculateNormals(); N = mesh.normals; }

            wp = new Vector3[V.Length];
            wn = new Vector3[V.Length];
            for (int i = 0; i < V.Length; i++)
            {
                wp[i] = l2w.MultiplyPoint3x4(V[i]);
                wn[i] = l2w.MultiplyVector(N[i]).normalized; // 노멀=방향 → 이동 성분 제외(MultiplyVector)
            }
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

        /// Raster 의 서브텍셀판. 바깥 루프(텍셀 bbox)는 동일하고, 마스크된 텍셀에서만
        /// 안쪽으로 S×S 서브샘플을 돌며 각각 내부판정 + 바리센트릭 복원한다.
        private static void RasterSub(ref LumelSubsamples ss, int res, int S, int spt,
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
                    int slot = ss.SlotOfTexel[y * res + x];
                    if (slot < 0) continue;                 // 마스크 밖 텍셀 → 서브샘플 안 만듦
                    int baseIdx = slot * spt;

                    for (int s = 0; s < spt; s++)
                    {
                        SubOffset(S, s, out float u, out float v);
                        float fx = x + u;
                        float fy = y + v;

                        float b0 = ((p1.y - p2.y) * (fx - p2.x) + (p2.x - p1.x) * (fy - p2.y)) / denom;
                        float b1 = ((p2.y - p0.y) * (fx - p2.x) + (p0.x - p2.x) * (fy - p2.y)) / denom;
                        float b2 = 1f - b0 - b1;

                        if (!((b0 >= 0 && b1 >= 0 && b2 >= 0) || (b0 <= 0 && b1 <= 0 && b2 <= 0)))
                            continue;

                        int o = baseIdx + s;
                        ss.WorldPos[o] = w0 * b0 + w1 * b1 + w2 * b2;
                        ss.WorldNormal[o] = (n0 * b0 + n1 * b1 + n2 * b2).normalized;
                        ss.Valid[o] = true;
                    }
                }
            }
        }


    }

}