using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// LightmapAllocator 확장 자체 테스트 모음. NUnit/asmdef 없이 문자열 리포트로 PASS/FAIL 집계.
    /// 엣지케이스(빈 입력·면적0·거대 단일·gutter0·overflow·결정성 등)까지 커버.
    /// LightmapAllocatorDebug 의 "Run Full Tests" 로 실행.
    /// </summary>
    public static class LightmapAllocatorTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== LightmapAllocator Extended Tests ===");
            int pass = 0, fail = 0;

            void Check(string name, bool ok, string detail = "")
            {
                if (ok) pass++; else fail++;
                sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(string.IsNullOrEmpty(detail) ? "" : " — " + detail)}");
            }

            var def = AllocationSettings.Default;

            // 1) 빈 입력 → 빈 결과, 페이지 0, overflow 없음
            {
                var r = LightmapAllocator.Allocate(new LightmapInstance[0], def);
                Check("empty input",
                    r.Instances != null && r.Instances.Length == 0 && r.PageCount == 0 && !r.Overflow,
                    $"pages={r.PageCount}, len={r.Instances?.Length}");
            }

            // 2) 단일 인스턴스 → page 0, ST 유효
            {
                var r = LightmapAllocator.Allocate(new[] { new LightmapInstance { InstanceId = 7, WorldArea = 6f } }, def);
                Check("single instance",
                    r.PageCount == 1 && StInRange(r) && r.Instances[0].LightmapIndex == 0,
                    $"pages={r.PageCount}, st={r.Instances[0].ScaleOffset}");
            }

            // 3) gutter=0 → 여백 없이도 범위/비겹침(인접 접촉은 겹침 아님)
            {
                var s = def; s.GutterTexels = 0;
                var r = LightmapAllocator.Allocate(Cubes(10, 6f), s);
                Check("gutter=0 no overlap", StInRange(r) && NoOverlap(r));
            }

            // 4) 면적 0/음수 → side=1 로 클램프, 크래시 없음, ST 유효
            {
                var insts = new[]
                {
                    new LightmapInstance { InstanceId = 1, WorldArea = 0f },
                    new LightmapInstance { InstanceId = 2, WorldArea = -5f },
                };
                var r = LightmapAllocator.Allocate(insts, def);
                float sidePx = r.Instances[0].ScaleOffset.x * def.AtlasResolution;
                Check("zero/negative area → clamp side≥1",
                    StInRange(r) && NoOverlap(r) && sidePx >= 0.5f, $"sidePx≈{sidePx:0.0}");
            }

            // 5) 거대 단일(면적 매우 큼) → side 가 r-g 로 클램프, ST≤1
            {
                var r = LightmapAllocator.Allocate(new[] { new LightmapInstance { InstanceId = 1, WorldArea = 1e9f } }, def);
                var st = r.Instances[0].ScaleOffset;
                Check("giant single clamped to atlas",
                    StInRange(r) && st.x <= 1f + 1e-4f && st.x > 0.9f, $"scale={st.x:0.000}");
            }

            // 6) overflow 강제 (MaxPages=1, 큐브 200개) → Overflow=true, 페이지 1로 클램프
            {
                var s = def; s.MaxPages = 1;
                var r = LightmapAllocator.Allocate(Cubes(200, 6f), s);
                Check("overflow clamps to MaxPages",
                    r.Overflow && r.PageCount == 1, $"overflow={r.Overflow}, pages={r.PageCount}");
            }

            // 7) 충분한 MaxPages → overflow 없음 + 멀티페이지
            {
                var r = LightmapAllocator.Allocate(Cubes(200, 6f), def);
                Check("multi-page within MaxPages",
                    !r.Overflow && r.PageCount > 1 && r.PageCount <= def.MaxPages,
                    $"overflow={r.Overflow}, pages={r.PageCount}");
            }

            // 8) 변길이 ∝ √면적 (면적 4배 → 변 2배)
            {
                var insts = new[]
                {
                    new LightmapInstance { InstanceId = 1, WorldArea = 6f },
                    new LightmapInstance { InstanceId = 2, WorldArea = 24f },
                };
                var r = LightmapAllocator.Allocate(insts, def);
                float a = Find(r, 1).ScaleOffset.x, b = Find(r, 2).ScaleOffset.x;
                Check("side ∝ sqrt(area)", a > 0f && Mathf.Abs(b / a - 2f) < 0.05f, $"ratio={(a > 0 ? b / a : 0):0.000}");
            }

            // 9) util 범위: overflow 없을 때 0 < util ≤ 1
            {
                var r = LightmapAllocator.Allocate(Cubes(50, 6f), def);
                Check("utilization in (0,1]",
                    r.Utilization > 0f && r.Utilization <= 1f + 1e-4f, $"util={r.Utilization:P1}");
            }

            // 10) 결정성: 같은 입력 → 같은 출력
            {
                var insts = Cubes(64, 6f);
                var r1 = LightmapAllocator.Allocate(insts, def);
                var r2 = LightmapAllocator.Allocate(insts, def);
                bool same = r1.PageCount == r2.PageCount && r1.Instances.Length == r2.Instances.Length;
                if (same)
                    for (int i = 0; i < r1.Instances.Length; i++)
                        if (r1.Instances[i].LightmapIndex != r2.Instances[i].LightmapIndex ||
                            r1.Instances[i].ScaleOffset != r2.Instances[i].ScaleOffset) { same = false; break; }
                Check("deterministic (same in → same out)", same);
            }

            // 11) 혼합 크기 대량 → 전 인스턴스 ST∈[0,1] & 비겹침
            {
                var insts = new LightmapInstance[120];
                for (int i = 0; i < insts.Length; i++)
                    insts[i] = new LightmapInstance { InstanceId = i, WorldArea = 1f + (i % 7) * 3f };
                var r = LightmapAllocator.Allocate(insts, def);
                Check("mixed sizes ST∈[0,1] & noOverlap",
                    StInRange(r) && NoOverlap(r), $"pages={r.PageCount}, util={r.Utilization:P1}");
            }

            sb.AppendLine($"--- {pass} passed, {fail} failed ---");
            return sb.ToString();
        }

        // ── helpers ───────────────────────────────────────────
        static LightmapInstance[] Cubes(int n, float area)
        {
            var a = new LightmapInstance[n];
            for (int i = 0; i < n; i++) a[i] = new LightmapInstance { InstanceId = i, WorldArea = area };
            return a;
        }

        static InstanceLM Find(AllocationResult r, int id)
        { foreach (var lm in r.Instances) if (lm.InstanceId == id) return lm; return default; }

        // ST 가 모두 [0,1] 아틀라스 영역 안에 있는가 (z+x≤1, w+y≤1)
        public static bool StInRange(AllocationResult r)
        {
            foreach (var lm in r.Instances)
            {
                var st = lm.ScaleOffset;
                if (st.z < -1e-4f || st.z + st.x > 1 + 1e-4f || st.w < -1e-4f || st.w + st.y > 1 + 1e-4f) return false;
            }
            return true;
        }

        // 같은 페이지 내 두 영역이 겹치지 않는가 (접촉은 허용)
        public static bool NoOverlap(AllocationResult r)
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
