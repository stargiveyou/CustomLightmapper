using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{


    /// <summary>런타임에 셰이더로 전달될 인스턴스별 라이트맵 매핑.</summary>
    public struct InstanceLM
    {
        public int InstanceId;
        public int LightmapIndex;   //아틀라스 페이지(Texture2D Array)
        public Vector4 ScaleOffset; //아틀라스 atlasUV = uv2* ST.xy + ST.zw; ([0,1]아틀라스 좌표)
    }

    /// <summary>할당 입력: 인스턴스 식별자 + 월드 표면적.</summary>
    public struct LightmapInstance
    {
        public int InstanceId;
        public float WorldArea;
    }

    public struct AllocationSettings
    {
        public int AtlasResolution;         //페이지 해상도
        public float TexelsPerWorldUnit;    // 텍셀 밀도
        public int GutterTexels;            // 인스턴스 영역 간 여백
        public int MaxPages;

        public static AllocationSettings Default => new()

        {
            AtlasResolution = 1024,
            TexelsPerWorldUnit = 64f,
            GutterTexels = 2,
            MaxPages = 8
        };
    }

    public struct AllocationResult
    {
        public InstanceLM[] Instances;
        public int PageCount;
        public int Resolution;
        public float Utilization;   // 점유율(사용 텍셀 /전체)
        public bool Overflow;       // MaxPages 초과로 클램프됨
    }

    /// <summary>
    /// per-instance ST 할당(TLAS 레벨 패킹). 인스턴스마다 월드 면적 비례로 아틀라스 영역을
    /// 배정하고 ST·페이지를 산출한다. 공유 uv2(메시 [0,1])를 인스턴스 영역으로 리맵하는 것이 ST.
    /// 텍스처 없이 데이터만으로 검증 가능(인스턴싱 배칭 데이터 경로의 토대).
    /// </summary>
    public static class LightmapAllocator
    {
        /// <summary> 인스턴스 트랜스폼을 반영한 실제 월드 표면적(삼각형 변환 후 합).</summery>
        public static float WorldArea(Mesh mesh, Matrix4x4 l2w)
        {
            var v = mesh.vertices;
            var t = mesh.triangles;
            float a = 0.0f;
            for (int i = 0; i < t.Length; i += 3)
            {
                Vector3 p0 = l2w.MultiplyPoint(v[t[i]]);
                Vector3 p1 = l2w.MultiplyPoint(v[t[i + 1]]);
                Vector3 p2 = l2w.MultiplyPoint(v[t[i + 2]]);
                // Area
                a += (0.5f * Vector3.Cross(p1 - p0, p2 - p0).magnitude);
            }
            return a;
        }

        public static AllocationResult Allocate(LightmapInstance[] insts, AllocationSettings s)
        {
            int r = s.AtlasResolution;
            int g = s.GutterTexels;
            int n = insts.Length;

            // 영역 변 길이 (텍셀) = sqrt(월드 면적) * 밀도 → 면적 ∝ 월드면적, 변 ∝ 스케일
            var side = new int[n];
            for (int i = 0; i < n; i++)
            {
                int px = Mathf.CeilToInt(Mathf.Sqrt(Mathf.Max(0f, insts[i].WorldArea)) * s.TexelsPerWorldUnit);
                side[i] = Mathf.Clamp(px, 1, r - g);
            }

            var order = new int[n];
            for (int i = 0; i < n; i++)
                order[i] = i;

            System.Array.Sort(order, (a, b) => side[b].CompareTo(side[a])); // 변 길이 내림차수

            var outLM = new InstanceLM[n];
            int maxPages = Mathf.Max(1, s.MaxPages);
            int page = 0;
            int x = 0, y = 0;
            int selfH = 0;
            long used = 0;
            bool overflow = false;

            foreach (int i in order)
            {
                int w = side[i] + g, h = side[i] + g;

                // 1) 가로가 안 들어가면 다음 선반(shelf)으로
                if (x + w > r)
                {
                    x = 0;
                    y += selfH;
                    selfH = 0;
                }
                // 2) 세로가 페이지 높이를 넘으면 다음 페이지로 (이게 빠져 있었음)
                if (y + h > r)
                {
                    page++;
                    x = 0; y = 0; selfH = 0;
                    if (page >= maxPages)
                    {
                        overflow = true;
                        page = maxPages - 1; // 한도 초과분은 마지막 페이지에 클램프(겹칠 수 있음)
                    }
                }
                int rx = x + g / 2, ry = y + g / 2, sd = side[i];
                outLM[i] = new InstanceLM
                {
                    InstanceId = insts[i].InstanceId,
                    LightmapIndex = page,
                    ScaleOffset = new Vector4((float)sd / r, (float)sd / r, (float)rx / r, (float)ry / r),
                };
                used += (long)sd * sd;
                x += w;
                selfH = Mathf.Max(selfH, h);
            }

            int pages = 0;
            foreach (var lm in outLM)
            {
                pages = Mathf.Max(pages, lm.LightmapIndex + 1);
            }
            float util = pages > 0 ? (float)((double)used / ((long)r * r * pages)) : 0;


            return new AllocationResult { Instances = outLM, PageCount = pages, Resolution = r, Utilization = util, Overflow = overflow };
        }
    }

}