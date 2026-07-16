using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// G5 검증(GPU↔Burst): PathTrace.compute 의 AO/Direct/Indirect 커널이
    ///   <see cref="BurstAO"/> / <see cref="BurstDirect"/> / <see cref="BurstIndirect"/>
    ///   정답과 일치함을 확인. 씬/샘플/시드 규약은 BurstRadianceCompareTests 와 동일 패턴.
    ///
    /// 검증 기준(핵심 — GPU 초월함수(sqrt/sin/cos) 발산):
    ///   • Direct   : 무작위 없음 → per-texel |gpu-cpu| < 1e-3, hard-miss=0 요구.
    ///                (traversal 은 G4 에서 hard-miss=0 검증됨 → 그림자 boolean 도 안정)
    ///   • AO       : 방향 샘플 → 대부분 0, 경계 텍셀만 갈림. mean < 1e-2, over(2/AoSamples) 로그.
    ///   • Indirect : Monte Carlo → per-texel 비트동일 불가. mean < 1e-2, over(threshold) 로그.
    /// RNG(uint xorshift)는 비트동일하나 CosineHemisphere 의 sqrt/sin/cos 가 하드웨어별로
    /// 미세 발산 → AO/Indirect 는 통계 기준(mean/ε), Direct 만 타이트.
    ///
    /// 시드 규약: seed_i = BaseSeed + i*2654435761u (CPU/GPU 동일). Direct 는 무작위 없음.
    ///
    /// ⚠️ 렌더 컨텍스트 필요(에디터/플레이) — 헤드리스 CI 불가. compute 미지원 → SKIP.
    /// 호출: Debug.Log(GpuRadianceCompareTests.RunAll());
    /// </summary>
    public static class GpuRadianceCompareTests
    {
        const uint  BaseSeed  = 0xC0FFEEu;
        const float AoMaxDist = 5f;

        static ComputeShader LoadCompute()
        {
            return Resources.Load<ComputeShader>("PathTrace"); // Shaders/Resources 배치 → 에디터·빌드 공통 로드
        }

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== G5 GPU PathTrace ≡ Burst(AO/Direct/Indirect) ===");

            if (!SystemInfo.supportsComputeShaders)
            { sb.AppendLine("  [SKIP] compute shader 미지원 플랫폼 — GPU 경로추적 검증 불가"); return sb.ToString(); }

            var cs = LoadCompute();
            if (cs == null)
            { sb.AppendLine("  [FAIL] PathTrace.compute 로드 실패 (경로/컴파일 확인)"); return sb.ToString(); }

            int kAO = cs.FindKernel("CSAO");
            int kDir = cs.FindKernel("CSDirect");
            int kInd = cs.FindKernel("CSIndirect");
            int kRad = cs.FindKernel("CSRadiance");
            if (kAO < 0 || kDir < 0 || kInd < 0 || kRad < 0)
            { sb.AppendLine("  [FAIL] 커널(CSAO/CSDirect/CSIndirect/CSRadiance) 미발견"); return sb.ToString(); }

            int pass = 0, total = 0;

            // ── 씬/샘플 (BurstRadianceCompareTests 패턴) ──
            BuildScene(out var meshes, out var albedo, out var insts);
            using var bvh = new TwoLevelBVH(meshes, insts);
            using var scene = BurstScene.Create(bvh, albedo, Allocator.Persistent);
            using var gpu = new GpuScene(scene);

            CollectSurfaceSamples(meshes, insts, 400, out var pts, out var nrm);
            int n = pts.Length;
            var sun = Sun();
            var skyM = new GradientSky(new Vector3(0.5f, 0.6f, 0.8f), new Vector3(0.15f, 0.12f, 0.10f));
            var skyB = BurstSky.FromSky(skyM);
            var q = new BakeQualitySettings { AoSamples = 64, IndirectSamples = 64, MaxBounces = 4, RRStartDepth = 2, RayBias = 1e-4f };

            // ── CPU 정답(Burst) ──
            var naPts = new NativeArray<Vector3>(pts, Allocator.TempJob);
            var naNrm = new NativeArray<Vector3>(nrm, Allocator.TempJob);
            var naVal = new NativeArray<bool>(n, Allocator.TempJob);
            for (int i = 0; i < n; i++) naVal[i] = true;

            var cpuAo  = BurstAO.Compute(scene, naPts, naNrm, naVal, q.AoSamples, BaseSeed, AoMaxDist, Allocator.TempJob);
            var cpuDir = BurstDirect.Compute(scene, naPts, naNrm, naVal, sun, Allocator.TempJob);
            var cpuInd = BurstIndirect.Compute(scene, skyB, sun, q, naPts, naNrm, naVal, BaseSeed, Allocator.TempJob);

            // ── GPU 입력 버퍼 ──
            // 시드 규약: 커널이 _Seeds[i] 를 읽으므로 CPU(Burst) 와 동일한 seed_i = BaseSeed + i*const 를 명시 공급.
            //   이전(디스패치 인덱스 기반 _BaseSeed) 과 값이 동일 → G5 결과 비트동일 재현.
            var validU = new uint[n];
            var seedsU = new uint[n];
            for (int i = 0; i < n; i++) { validU[i] = 1u; seedsU[i] = BaseSeed + (uint)i * 2654435761u; }
            var ptsBuf = new ComputeBuffer(n, 12, ComputeBufferType.Structured);
            var nrmBuf = new ComputeBuffer(n, 12, ComputeBufferType.Structured);
            var valBuf = new ComputeBuffer(n, sizeof(uint), ComputeBufferType.Structured);
            var seedBuf = new ComputeBuffer(n, sizeof(uint), ComputeBufferType.Structured);
            ptsBuf.SetData(pts); nrmBuf.SetData(nrm); valBuf.SetData(validU); seedBuf.SetData(seedsU);

            var aoBuf  = new ComputeBuffer(n, sizeof(float), ComputeBufferType.Structured);
            var dirBuf = new ComputeBuffer(n, 12, ComputeBufferType.Structured);
            var indBuf = new ComputeBuffer(n, 12, ComputeBufferType.Structured);
            var radBuf = new ComputeBuffer(n, 12, ComputeBufferType.Structured);

            var gpuAo  = new float[n];
            var gpuDir = new Vector3[n];
            var gpuInd = new Vector3[n];
            var gpuRad = new Vector3[n];
            int groups = (n + 63) / 64;

            try
            {
                // 공통 uniform + 씬/입력 배선(커널별)
                foreach (int k in new[] { kAO, kDir, kInd, kRad })
                {
                    gpu.Bind(cs, k);            // 순회 SRV + _TlasCount
                    gpu.BindLighting(cs, k);    // _InstNormals, _MeshAlbedo
                    cs.SetBuffer(k, "_Points", ptsBuf);
                    cs.SetBuffer(k, "_Normals", nrmBuf);
                    cs.SetBuffer(k, "_Valid", valBuf);
                    cs.SetBuffer(k, "_Seeds", seedBuf);
                    cs.SetInt("_Count", n);
                    cs.SetInt("_AoSamples", q.AoSamples);
                    cs.SetInt("_IndirectSamples", q.IndirectSamples);
                    cs.SetInt("_MaxBounces", q.MaxBounces);
                    cs.SetInt("_RRStartDepth", q.RRStartDepth);
                    cs.SetFloat("_RayBias", q.RayBias);
                    cs.SetFloat("_AoMaxDist", AoMaxDist);
                    cs.SetVector("_SunDir", sun.Direction);
                    cs.SetVector("_SunColor", sun.Color);
                    cs.SetFloat("_SunIntensity", sun.Intensity);
                    cs.SetInt("_SkyType", skyB.Type);
                    cs.SetVector("_SkyTop", skyB.A);
                    cs.SetVector("_SkyBottom", skyB.B);
                }

                cs.SetBuffer(kAO, "_AoOut", aoBuf);
                cs.Dispatch(kAO, groups, 1, 1);
                aoBuf.GetData(gpuAo);

                cs.SetBuffer(kDir, "_DirectOut", dirBuf);
                cs.Dispatch(kDir, groups, 1, 1);
                dirBuf.GetData(gpuDir);

                cs.SetBuffer(kInd, "_IndirectOut", indBuf);
                cs.Dispatch(kInd, groups, 1, 1);
                indBuf.GetData(gpuInd);

                cs.SetBuffer(kRad, "_RadianceOut", radBuf);
                cs.Dispatch(kRad, groups, 1, 1);
                radBuf.GetData(gpuRad);
            }
            finally
            {
                ptsBuf.Dispose(); nrmBuf.Dispose(); valBuf.Dispose(); seedBuf.Dispose();
                aoBuf.Dispose(); dirBuf.Dispose(); indBuf.Dispose(); radBuf.Dispose();
            }

            // ── 대조 ──
            // Direct: 타이트. hard-miss(>1e-3)=0 요구.
            StatsV(cpuDir, gpuDir, n, 1e-3f, out float dMax, out float dMean, out int dOver);
            Check(sb, ref pass, ref total,
                $"G5 Direct ≡ CPU (n={n}, mean={dMean:E2}, max={dMax:E2}, over(1e-3)={dOver})",
                dOver == 0);

            // AO: 통계. mean<1e-2, over(2/AoSamples) 로그.
            float aoStep = 2f / q.AoSamples;
            StatsF(cpuAo, gpuAo, n, aoStep, out float aMax, out float aMean, out int aOver);
            Check(sb, ref pass, ref total,
                $"G5 AO ≡ CPU (n={n}, spp={q.AoSamples}, mean={aMean:E2}, max={aMax:E2}, over({aoStep:F3})={aOver})",
                aMean < 1e-2f);

            // Indirect: Monte Carlo. mean<1e-2, over(5e-2) 로그.
            StatsV(cpuInd, gpuInd, n, 5e-2f, out float iMax, out float iMean, out int iOver);
            Check(sb, ref pass, ref total,
                $"G5 Indirect ≡ CPU (n={n}, spp={q.IndirectSamples}, bnc={q.MaxBounces}, mean={iMean:E2}, max={iMax:E2}, over(5e-2)={iOver})",
                iMean < 1e-2f);

            // CSRadiance ≡ CSDirect+CSIndirect (동일 GPU·동일 _Seeds → 비트동일 기대). 합성 커널 정합 확인.
            var sumDI = new Vector3[n];
            for (int i = 0; i < n; i++) sumDI[i] = gpuDir[i] + gpuInd[i];
            StatsV2(sumDI, gpuRad, n, 1e-4f, out float rMax, out float rMean, out int rOver);
            Check(sb, ref pass, ref total,
                $"G6-2 CSRadiance ≡ CSDirect+CSIndirect (n={n}, mean={rMean:E2}, max={rMax:E2}, over(1e-4)={rOver})",
                rOver == 0);

            sb.AppendLine($"  [info] TLAS nodes={bvh.TlasNodeCount}, inst={bvh.InstanceCount}, BLAS={bvh.BlasCount}");
            sb.AppendLine("  [info] Direct=타이트(hard-miss=0), AO/Indirect=통계(mean/ε) — 초월함수 발산은 경계/MC 잡음으로 흡수.");

            naPts.Dispose(); naNrm.Dispose(); naVal.Dispose();
            cpuAo.Dispose(); cpuDir.Dispose(); cpuInd.Dispose();

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ── 통계: 텍셀별 max-성분 절대오차 → 평균/최대/over(>epsTexel) ──
        static void StatsF(NativeArray<float> a, float[] b, int n, float epsTexel,
            out float maxErr, out float meanErr, out int over)
        {
            maxErr = 0f; double sum = 0; over = 0;
            for (int i = 0; i < n; i++)
            {
                float e = Mathf.Abs(a[i] - b[i]);
                if (e > maxErr) maxErr = e;
                sum += e;
                if (e > epsTexel) over++;
            }
            meanErr = n > 0 ? (float)(sum / n) : 0f;
        }

        static void StatsV(NativeArray<Vector3> a, Vector3[] b, int n, float epsTexel,
            out float maxErr, out float meanErr, out int over)
        {
            maxErr = 0f; double sum = 0; over = 0;
            for (int i = 0; i < n; i++)
            {
                Vector3 d = a[i] - b[i];
                float e = Mathf.Max(Mathf.Abs(d.x), Mathf.Max(Mathf.Abs(d.y), Mathf.Abs(d.z)));
                if (e > maxErr) maxErr = e;
                sum += e;
                if (e > epsTexel) over++;
            }
            meanErr = n > 0 ? (float)(sum / n) : 0f;
        }

        // 두 managed Vector3[] 비교(GPU 커널 간 정합용).
        static void StatsV2(Vector3[] a, Vector3[] b, int n, float epsTexel,
            out float maxErr, out float meanErr, out int over)
        {
            maxErr = 0f; double sum = 0; over = 0;
            for (int i = 0; i < n; i++)
            {
                Vector3 d = a[i] - b[i];
                float e = Mathf.Max(Mathf.Abs(d.x), Mathf.Max(Mathf.Abs(d.y), Mathf.Abs(d.z)));
                if (e > maxErr) maxErr = e;
                sum += e;
                if (e > epsTexel) over++;
            }
            meanErr = n > 0 ? (float)(sum / n) : 0f;
        }

        // ── 씬(BurstRadianceCompareTests.BuildScene 동일): enclosing 박스 + 회전·비균등 인스턴스 ──
        static void BuildScene(out Tri[][] meshes, out Vector3[] albedo, out TwoLevelBVH.Instance[] insts)
        {
            var rng = new System.Random(20240629);
            meshes = new Tri[][]
            {
                LightmapEvaluateTests.MakeBox(Vector3.zero, 2f),
                RandomTris(rng, 30, 1.0f, 0.5f),
            };
            albedo = new Vector3[]
            {
                new Vector3(0.72f, 0.70f, 0.68f),
                new Vector3(0.55f, 0.35f, 0.30f),
            };

            var list = new List<TwoLevelBVH.Instance>
            {
                new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * 6f) },
            };
            for (int i = 0; i < 8; i++)
            {
                int mi = rng.Next(meshes.Length);
                var trs = Matrix4x4.TRS(
                    RandV(rng, 3.5f),
                    Quaternion.Euler(Rand(rng, 180f), Rand(rng, 180f), Rand(rng, 180f)),
                    new Vector3(0.6f + (float)rng.NextDouble(), 0.6f + (float)rng.NextDouble(), 0.6f + (float)rng.NextDouble()));
                list.Add(new TwoLevelBVH.Instance { MeshIndex = mi, LocalToWorld = trs });
            }
            insts = list.ToArray();
        }

        static void CollectSurfaceSamples(Tri[][] meshes, TwoLevelBVH.Instance[] insts, int maxCount,
            out Vector3[] pts, out Vector3[] nrm)
        {
            var lp = new List<Vector3>(maxCount);
            var ln = new List<Vector3>(maxCount);

            int totalTris = 0;
            foreach (var inst in insts) totalTris += meshes[inst.MeshIndex].Length;
            int stride = Mathf.Max(1, totalTris / Mathf.Max(1, maxCount));

            int counter = 0;
            foreach (var inst in insts)
            {
                var tris = meshes[inst.MeshIndex];
                Matrix4x4 l2w = inst.LocalToWorld;
                Matrix4x4 nm = l2w.inverse.transpose;
                for (int t = 0; t < tris.Length; t++)
                {
                    if ((counter++ % stride) != 0) continue;
                    Tri tri = tris[t];
                    Vector3 fnLocal = Vector3.Cross(tri.V1 - tri.V0, tri.V2 - tri.V0);
                    if (fnLocal.sqrMagnitude < 1e-12f) continue;
                    Vector3 wn = nm.MultiplyVector(fnLocal).normalized;
                    Vector3 cLocal = (tri.V0 + tri.V1 + tri.V2) / 3f;
                    Vector3 wp = l2w.MultiplyPoint3x4(cLocal) + wn * 1e-3f;
                    lp.Add(wp); ln.Add(wn);
                    if (lp.Count >= maxCount) { pts = lp.ToArray(); nrm = ln.ToArray(); return; }
                }
            }
            pts = lp.ToArray();
            nrm = ln.ToArray();
        }

        static DirectionalLight Sun() => new DirectionalLight
        {
            Direction = new Vector3(-0.4f, -1f, -0.25f).normalized,
            Color = new Vector3(1f, 0.95f, 0.8f),
            Intensity = 1.3f
        };

        static Tri[] RandomTris(System.Random rng, int count, float extent, float size)
        {
            var tris = new Tri[count];
            for (int i = 0; i < count; i++)
            {
                Vector3 c = RandV(rng, extent);
                tris[i] = new Tri { V0 = c + RandV(rng, size), V1 = c + RandV(rng, size), V2 = c + RandV(rng, size) };
            }
            return tris;
        }

        static Vector3 RandV(System.Random rng, float e) => new Vector3(Rand(rng, e), Rand(rng, e), Rand(rng, e));
        static float Rand(System.Random rng, float half) => (float)(rng.NextDouble() * 2.0 - 1.0) * half;

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
