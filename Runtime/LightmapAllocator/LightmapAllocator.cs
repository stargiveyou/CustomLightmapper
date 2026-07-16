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


        public static string RunSelfTests()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== A4 per-instance ST Self-Tests ===");

            // 단위 큐브 area=6, 스케일 1/2/0.5 → 월드면적 6/24/1.5 → 변 비율 1:2:0.5
            var insts = new[]
            {
                new LightmapInstance { InstanceId = 1, WorldArea = 6f },
                new LightmapInstance { InstanceId = 2, WorldArea = 24f },
                new LightmapInstance { InstanceId = 3, WorldArea = 1.5f },
            };
            var r = Allocate(insts, AllocationSettings.Default);
            float s1 = Find(r, 1).ScaleOffset.x, s2 = Find(r, 2).ScaleOffset.x, s05 = Find(r, 3).ScaleOffset.x;
            bool ratio = Mathf.Abs(s2 / s1 - 2f) < 0.05f && Mathf.Abs(s05 / s1 - 0.5f) < 0.05f;
            bool ok = ratio && StInRange(r) && NoOverlap(r) && r.PageCount == 1;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] 3 cubes(1/2/0.5): pages={r.PageCount}, util={r.Utilization:P1}, sideRatio(2:1:0.5)={ratio}, ST∈[0,1]={StInRange(r)}, noOverlap={NoOverlap(r)}");

            // 페이지 오버플로: 단위 큐브 200개
            var many = new LightmapInstance[200];
            for (int i = 0; i < 200; i++) many[i] = new LightmapInstance { InstanceId = i, WorldArea = 6f };
            var r2 = Allocate(many, AllocationSettings.Default);
            bool ok2 = r2.PageCount > 1 && StInRange(r2) && NoOverlap(r2);
            sb.AppendLine($"[{(ok2 ? "PASS" : "FAIL")}] 200 cubes: pages={r2.PageCount}, util={r2.Utilization:P1}, ST∈[0,1]={StInRange(r2)}, noOverlap={NoOverlap(r2)}");
            return sb.ToString();
        }

        private static InstanceLM Find(AllocationResult r, int id)
        { foreach (var lm in r.Instances) if (lm.InstanceId == id) return lm; return default; }

        private static bool StInRange(AllocationResult r)
        {
            foreach (var lm in r.Instances)
            {
                var st = lm.ScaleOffset;
                if (st.z < -1e-4f || st.z + st.x > 1 + 1e-4f || st.w < -1e-4f || st.w + st.y > 1 + 1e-4f) return false;
            }
            return true;
        }

        private static bool NoOverlap(AllocationResult r)
        {
            var a = r.Instances;
            for (int i = 0; i < a.Length; i++)
                for (int j = i + 1; j < a.Length; j++)
                {
                    if (a[i].LightmapIndex != a[j].LightmapIndex) continue;
                    Vector4 A = a[i].ScaleOffset, B = a[j].ScaleOffset;
                    bool sep = A.z + A.x <= B.z || B.z + B.x <= A.z || A.w + A.y <= B.w || B.w + B.y <= A.w;
                    if (!sep) return false;
                }
            return true;
        }
    }

}