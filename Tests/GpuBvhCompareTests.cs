using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// G4 검증(GPU↔CPU/Burst): BvhTraverse.compute 의 2단 BVH 순회가
    ///   <see cref="BurstTwoLevelBVH.IntersectInstanced"/> / <see cref="BurstTwoLevelBVH.Occluded"/>
    ///   와 같은 레이에 대해 ε 오차 내 일치함을 확인.
    ///
    /// 씬: BurstSceneTests.EquivFuzz 와 동일(3메시·20인스턴스·seed 424242, 회전+비균등 스케일).
    ///     비균등 스케일 인스턴스가 있어 행렬 major/mul 순서가 틀리면 즉시 드러난다.
    ///
    /// 대조:
    ///   ① IntersectInstanced : Valid/inst/mesh/tri 정확 일치 기대. 인덱스 불일치는
    ///      |Tcpu-Tgpu|<ε 인 near-tie(그레이징) 만 허용(카운트 로그). T 는 ε 비교.
    ///   ② Occluded : bool 일치.
    ///
    /// ⚠️ 렌더 컨텍스트 필요(에디터/플레이) — 헤드리스 CI 불가. compute 미지원 → SKIP.
    /// 호출: Debug.Log(GpuBvhCompareTests.RunAll());
    /// </summary>
    public static class GpuBvhCompareTests
    {
        const float Eps = 1e-3f;             // GPU fp32/fast-math 발산 흡수
        const int Rays = 5000;

        static ComputeShader LoadCompute()
        {
            return Resources.Load<ComputeShader>("BvhTraverse"); // Shaders/Resources 배치 → 에디터·빌드 공통 로드
        }

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== G4 GPU BvhTraverse ≡ BurstTwoLevelBVH (2단 순회) ===");

            if (!SystemInfo.supportsComputeShaders)
            { sb.AppendLine("  [SKIP] compute shader 미지원 플랫폼 — GPU 순회 검증 불가"); return sb.ToString(); }

            var cs = LoadCompute();
            if (cs == null)
            { sb.AppendLine("  [FAIL] BvhTraverse.compute 로드 실패 (경로/컴파일 확인)"); return sb.ToString(); }

            int kClosest = cs.FindKernel("CSClosestHit");
            int kOcc = cs.FindKernel("CSOccluded");
            if (kClosest < 0 || kOcc < 0)
            { sb.AppendLine("  [FAIL] 커널(CSClosestHit/CSOccluded) 미발견"); return sb.ToString(); }

            int pass = 0, total = 0;

            // ── 씬 구성 (BurstSceneTests.EquivFuzz 와 동일) ──
            var rng = new System.Random(424242);
            var meshes = new Tri[][]
            {
                MakeRandomTris(rng, 40, 1.0f, 0.5f),
                MakeBoxTris(0.8f),
                MakeRandomTris(rng, 25, 0.6f, 0.4f),
            };
            int instN = 20;
            var insts = new TwoLevelBVH.Instance[instN];
            var worldTris = new List<Tri>();
            for (int i = 0; i < instN; i++)
            {
                int mi = rng.Next(meshes.Length);
                Matrix4x4 l2w = Matrix4x4.TRS(
                    RandPoint(rng, 8f),
                    Quaternion.Euler(Rand(rng, 180f), Rand(rng, 180f), Rand(rng, 180f)),
                    new Vector3(0.5f + (float)rng.NextDouble() * 1.5f,
                                0.5f + (float)rng.NextDouble() * 1.5f,
                                0.5f + (float)rng.NextDouble() * 1.5f));
                insts[i] = new TwoLevelBVH.Instance { MeshIndex = mi, LocalToWorld = l2w };
                foreach (var t in meshes[mi])
                    worldTris.Add(new Tri
                    {
                        V0 = l2w.MultiplyPoint3x4(t.V0),
                        V1 = l2w.MultiplyPoint3x4(t.V1),
                        V2 = l2w.MultiplyPoint3x4(t.V2),
                    });
            }

            using var tlas = new TwoLevelBVH(meshes, insts);
            using var scene = BurstScene.Create(tlas, Allocator.Persistent);
            using var gpu = new GpuScene(scene);

            ComputeBounds(worldTris.ToArray(), out Vector3 mn, out Vector3 mx);
            Vector3 c = (mn + mx) * 0.5f;
            Vector3 ext = Vector3.Max(mx - mn, Vector3.one) * 1.5f;
            float md0 = Mathf.Max(ext.magnitude, 1f);

            // ── 레이 생성(CPU/GPU 공통) ──
            var rr = new System.Random(7);
            var closestRays = new GpuScene.GpuRay[Rays];
            var occRays = new GpuScene.GpuRay[Rays];
            var maxDist = new float[Rays];
            for (int k = 0; k < Rays; k++)
            {
                Vector3 o = c + new Vector3(Rand01(rr) * ext.x, Rand01(rr) * ext.y, Rand01(rr) * ext.z);
                Vector3 d = RandDir(rr);
                float md = (0.2f + (float)rr.NextDouble()) * md0;
                maxDist[k] = md;
                closestRays[k] = new GpuScene.GpuRay { origin = o, tmin = 0f, dir = d, tmax = 1000f };
                occRays[k] = new GpuScene.GpuRay { origin = o, tmin = 0f, dir = d, tmax = md };
            }

            // ── GPU: closest ──
            var hitsGpu = new GpuScene.GpuHit[Rays];
            var occGpu = new uint[Rays];
            var rayBuf = new ComputeBuffer(Rays, GpuScene.GpuRay.Stride, ComputeBufferType.Structured);
            var hitBuf = new ComputeBuffer(Rays, GpuScene.GpuHit.Stride, ComputeBufferType.Structured);
            var occBuf = new ComputeBuffer(Rays, sizeof(uint), ComputeBufferType.Structured);
            int groups = (Rays + 63) / 64;
            try
            {
                // closest
                rayBuf.SetData(closestRays);
                gpu.Bind(cs, kClosest);
                cs.SetInt("_RayCount", Rays);
                cs.SetBuffer(kClosest, "_Rays", rayBuf);
                cs.SetBuffer(kClosest, "_HitsOut", hitBuf);
                cs.Dispatch(kClosest, groups, 1, 1);
                hitBuf.GetData(hitsGpu);

                // occluded
                rayBuf.SetData(occRays);
                gpu.Bind(cs, kOcc);
                cs.SetInt("_RayCount", Rays);
                cs.SetBuffer(kOcc, "_Rays", rayBuf);
                cs.SetBuffer(kOcc, "_OccOut", occBuf);
                cs.Dispatch(kOcc, groups, 1, 1);
                occBuf.GetData(occGpu);
            }
            finally
            {
                rayBuf.Dispose(); hitBuf.Dispose(); occBuf.Dispose();
            }

            // ── 대조 ──
            int validMiss = 0, tMiss = 0, idxHardMiss = 0, idxNearTie = 0, occMiss = 0, hits = 0;
            float maxTErr = 0f;
            for (int k = 0; k < Rays; k++)
            {
                var o = closestRays[k].origin; var d = closestRays[k].dir;
                var cpu = BurstTwoLevelBVH.IntersectInstanced(scene, o, d, 0f, 1000f);
                var g = hitsGpu[k];
                bool gValid = g.valid != 0;

                if (cpu.Valid != gValid) { validMiss++; }
                else if (cpu.Valid)
                {
                    hits++;
                    float tErr = Mathf.Abs(cpu.T - g.t);
                    maxTErr = Mathf.Max(maxTErr, tErr);
                    if (tErr >= Eps) tMiss++;

                    bool idxSame = cpu.InstanceIndex == g.inst &&
                                   cpu.MeshIndex == g.mesh &&
                                   cpu.MeshTriIndex == g.tri;
                    if (!idxSame)
                    {
                        if (tErr < Eps) idxNearTie++;   // 그레이징 near-tie → 허용
                        else idxHardMiss++;             // 진짜 불일치
                    }
                }

                // occluded
                bool cpuOcc = BurstTwoLevelBVH.Occluded(scene, o, d, maxDist[k]);
                if (cpuOcc != (occGpu[k] != 0)) occMiss++;
            }

            Check(sb, ref pass, ref total, $"IntersectInstanced Valid 일치 (miss={validMiss}/{Rays}, hits={hits})", validMiss == 0);
            Check(sb, ref pass, ref total, $"IntersectInstanced T 일치 (miss={tMiss}, maxErr={maxTErr:0.000000}, ε={Eps})", tMiss == 0);
            Check(sb, ref pass, ref total, $"IntersectInstanced 인덱스 hard-miss=0 (nearTie 허용={idxNearTie})", idxHardMiss == 0 && hits > 0);
            Check(sb, ref pass, ref total, $"Occluded 일치 (miss={occMiss}/{Rays})", occMiss == 0);
            sb.AppendLine($"  [info] TLAS nodes={tlas.TlasNodeCount}, inst={tlas.InstanceCount}, BLAS={tlas.BlasCount}, worldTris={worldTris.Count}");
            if (idxNearTie > 0)
                sb.AppendLine($"  [info] near-tie 인덱스 갈림 {idxNearTie}건 — |Tcpu-Tgpu|<ε 라 허용(그레이징/fast-math 발산).");

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ── 지오메트리/난수 (BurstSceneTests 와 동일) ──
        static Tri[] MakeBoxTris(float half) => LightmapEvaluateTests.MakeBox(Vector3.zero, half * 2f);

        static Tri[] MakeRandomTris(System.Random rng, int n, float extent, float size)
        {
            var tris = new Tri[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 c = RandPoint(rng, extent);
                tris[i] = new Tri { V0 = c + RandOffset(rng, size), V1 = c + RandOffset(rng, size), V2 = c + RandOffset(rng, size) };
            }
            return tris;
        }

        static void ComputeBounds(Tri[] tris, out Vector3 mn, out Vector3 mx)
        {
            mn = new Vector3(1e30f, 1e30f, 1e30f); mx = -mn;
            foreach (var t in tris)
            {
                mn = Vector3.Min(mn, Vector3.Min(t.V0, Vector3.Min(t.V1, t.V2)));
                mx = Vector3.Max(mx, Vector3.Max(t.V0, Vector3.Max(t.V1, t.V2)));
            }
            if (tris.Length == 0) { mn = Vector3.zero; mx = Vector3.one; }
        }

        static Vector3 RandPoint(System.Random rng, float e) => new Vector3(Rand(rng, e), Rand(rng, e), Rand(rng, e));
        static Vector3 RandOffset(System.Random rng, float s) => new Vector3(Rand(rng, s), Rand(rng, s), Rand(rng, s));
        static float Rand(System.Random rng, float half) => (float)(rng.NextDouble() * 2.0 - 1.0) * half;
        static float Rand01(System.Random rng) => (float)(rng.NextDouble() - 0.5);
        static Vector3 RandDir(System.Random rng)
        {
            float z = (float)(rng.NextDouble() * 2.0 - 1.0);
            float a = (float)(rng.NextDouble() * 2.0 * System.Math.PI);
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
            return new Vector3(r * Mathf.Cos(a), r * Mathf.Sin(a), z);
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
