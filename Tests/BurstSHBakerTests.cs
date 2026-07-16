using System.Text;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// SH-2 검증: BurstSHBaker.
    ///  A) 빈 씬 + 균일 하늘 → 미스만 → E(n) ≈ π·skyL (전 노멀 동일). 프로젝션·평가 end-to-end.
    ///  B) 빈 씬 + 그라디언트 하늘(위 밝음) → E(up) > E(down). 방향성.
    ///  C) 아래 바닥(태양 위→아래 조명) + 어두운 하늘 → hit 경로로 SH 비자명 + 아래에서 밝음.
    /// 결정적(RNG 없음). 호출: Debug.Log(BurstSHBakerTests.RunAll());
    /// 주의: BurstScene/BurstTwoLevel/BurstSky/SH9(전달) + 실측 게이트 전제.
    /// </summary>
    public static class BurstSHBakerTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== BurstSHBaker (SH-2) ===");
            int pass = 0, total = 0;
            int N = 2048;
            var sun = new DirectionalLight { Direction = new Vector3(0, -1, 0), Color = Vector3.one, Intensity = 1f };

            // ── A) 빈 씬 + 균일 하늘 ──
            {
                using var bvh = new TwoLevelBVH(new Tri[0][], new TwoLevelBVH.Instance[0], Allocator.TempJob);
                using var scene = BurstScene.Create(bvh, new Vector3[0], Allocator.TempJob);
                Vector3 skyL = new Vector3(0.8f, 0.9f, 1.0f);
                var sky = BurstSky.Uniform(skyL);

                var pts = new NativeArray<Vector3>(1, Allocator.TempJob);
                pts[0] = new Vector3(0, 5, 0);
                var sh = BurstSHBaker.Bake(scene, sky, sun, pts, N, Allocator.TempJob);

                Vector3 eUp = sh[0].Evaluate(Vector3.up);
                Vector3 eSide = sh[0].Evaluate(Vector3.right);
                Vector3 target = skyL * Mathf.PI;
                float err = (eUp - target).magnitude;
                float constMax = (eUp - eSide).magnitude;

                Check(sb, ref pass, ref total, $"A: 빈씬+균일하늘 E(n)≈π·L (err={err:0.000})", err < 0.05f);
                Check(sb, ref pass, ref total, $"A: 방향 무관 (Δ={constMax:0.000})", constMax < 0.02f);

                sh.Dispose(); pts.Dispose();
            }

            // ── B) 빈 씬 + 그라디언트 하늘 ──
            {
                using var bvh = new TwoLevelBVH(new Tri[0][], new TwoLevelBVH.Instance[0], Allocator.TempJob);
                using var scene = BurstScene.Create(bvh, new Vector3[0], Allocator.TempJob);
                var sky = BurstSky.Gradient(new Vector3(0.6f, 0.8f, 1.0f), new Vector3(0.05f, 0.05f, 0.08f));

                var pts = new NativeArray<Vector3>(1, Allocator.TempJob);
                pts[0] = new Vector3(0, 5, 0);
                var sh = BurstSHBaker.Bake(scene, sky, sun, pts, N, Allocator.TempJob);

                float eUp = sh[0].Evaluate(Vector3.up).x;
                float eDown = sh[0].Evaluate(Vector3.down).x;
                Check(sb, ref pass, ref total, $"B: 그라디언트 E(up)={eUp:0.00} > E(down)={eDown:0.00}", eUp > eDown + 0.1f);

                sh.Dispose(); pts.Dispose();
            }

            // ── C) 아래 바닥 + 어두운 하늘 (hit 경로) ──
            {
                // 큰 바닥 쿼드(2삼각형), y=0, 위를 향하는 노멀
                var floor = new[]
                {
                    new Tri { V0 = new Vector3(-20,0,-20), V1 = new Vector3(-20,0,20), V2 = new Vector3(20,0,20) },
                    new Tri { V0 = new Vector3(-20,0,-20), V1 = new Vector3(20,0,20),  V2 = new Vector3(20,0,-20) },
                };
                var meshes = new[] { floor };
                var albedo = new[] { new Vector3(0.8f, 0.8f, 0.8f) };
                var insts = new[] { new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = Matrix4x4.identity } };

                using var bvh = new TwoLevelBVH(meshes, insts, Allocator.TempJob, BVH.BuildQuality.SAH);
                using var scene = BurstScene.Create(bvh, albedo, Allocator.TempJob);
                var sky = BurstSky.Uniform(new Vector3(0.02f, 0.02f, 0.02f)); // 어두운 하늘

                var pts = new NativeArray<Vector3>(1, Allocator.TempJob);
                pts[0] = new Vector3(0, 3, 0); // 바닥 위 3m
                var sh = BurstSHBaker.Bake(scene, sky, sun, pts, N, Allocator.TempJob);

                // 바닥은 태양(위→아래)에 정면으로 조명 → 반사가 위로 → 대표점이 아래를 보면(=down normal) 바닥 기여 큼
                float eDown = sh[0].Evaluate(Vector3.down).x;   // 바닥을 향하는 노멀
                float eUp = sh[0].Evaluate(Vector3.up).x;       // 하늘(어두움) 향하는 노멀
                float coeffMag = sh[0].c0.magnitude + sh[0].c3.magnitude + sh[0].c1.magnitude;

                Check(sb, ref pass, ref total, $"C: hit 경로 SH 비자명 (Σ|c|≈{coeffMag:0.000})", coeffMag > 0.01f);
                Check(sb, ref pass, ref total, $"C: 바닥 반사 → E(down)={eDown:0.000} > E(up)={eUp:0.000}", eDown > eUp);

                sh.Dispose(); pts.Dispose();
            }

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
