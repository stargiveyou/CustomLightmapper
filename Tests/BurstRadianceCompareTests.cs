using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// G2/G3 교차검증: Burst 경로 ≡ RadianceCore(managed) ground truth (ε).
    ///   G2  BurstDirect.DirectJob     ≡ RadianceCore.EvaluateDirect(TwoLevelBVH)
    ///   G3  BurstIndirect.IndirectJob ≡ RadianceCore.EvaluateIndirect(InstancedRadianceScene 모드 A)
    ///
    /// 두 경로는 동일 RNG·CosineHemisphere·BVH(RayAABB/RayTri)를 재사용 → 사실상 정확일치.
    /// Burst(FMA)와 Mono(strict)의 부동소수 차이로 '경계 텍셀'(ndl≈0 백페이스 경계,
    /// 그림자/히트 경계에서 ±1 ULP로 분기 반전)만 드물게 발산 → '불일치 텍셀 수'(비율) +
    /// 평균/최대 오차(ε)로 판정. fast-math off 가정.
    ///
    /// 시드 규약(= BurstAO):
    ///   managed 는 텍셀별 seed_i = BaseSeed + i*2654435761u 로 호출,
    ///   Burst Job 은 BaseSeed 만 받아 Execute 내부에서 동일 해시 적용 → 동일 RNG 시퀀스.
    ///
    /// 공유 BVH:
    ///   managed InstancedRadianceScene 와 BurstScene 이 '동일' TwoLevelBVH 를 공유해야
    ///   순회/면노멀/알베도 인덱싱이 비트 일치(모드 A: 메시 균일 알베도).
    ///
    /// 호출: Debug.Log(BurstRadianceCompareTests.RunAll());
    /// </summary>
    public static class BurstRadianceCompareTests
    {
        const uint BaseSeed = 0xC0FFEEu;

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== G2/G3 Burst ≡ RadianceCore (managed) ===");
            int pass = 0, total = 0;

            DirectEquiv(sb, ref pass, ref total);   // G2
            IndirectEquiv(sb, ref pass, ref total); // G3

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ── G2: Burst Direct ≡ EvaluateDirect ─────────────────────────────
        static void DirectEquiv(StringBuilder sb, ref int pass, ref int total)
        {
            BuildScene(out var meshes, out _, out var insts);
            using var bvh = new TwoLevelBVH(meshes, insts);
            var scene = BurstScene.Create(bvh, Allocator.TempJob);   // 알베도 불필요(차폐만)

            CollectSurfaceSamples(meshes, insts, 400, out var pts, out var nrm);
            int n = pts.Length;
            var sun = Sun();

            // managed 정답
            var refR = new Vector3[n];
            for (int i = 0; i < n; i++)
                refR[i] = RadianceCore.EvaluateDirect(bvh, pts[i], nrm[i], sun);

            // Burst Job
            var naPts = new NativeArray<Vector3>(pts, Allocator.TempJob);
            var naNrm = new NativeArray<Vector3>(nrm, Allocator.TempJob);
            var naVal = new NativeArray<bool>(n, Allocator.TempJob);
            for (int i = 0; i < n; i++) naVal[i] = true;
            var naRad = new NativeArray<Vector3>(n, Allocator.TempJob);

            var job = new BurstDirect.DirectJob
            {
                scene = scene,
                Points = naPts,
                Normals = naNrm,
                Valid = naVal,
                Sun = sun,
                Radiance = naRad
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            job.Schedule(n, 32).Complete();
            sw.Stop();

            Stats(refR, naRad, n, 1e-3f, out float maxE, out float meanE, out int mism);
            int budget = Mathf.Max(1, Mathf.RoundToInt(n * 0.01f)); // 그림자 경계 ±1ULP 반전 허용(≤1%)
            Check(sb, ref pass, ref total,
                $"G2 Direct ≡ CPU (n={n}, mean={meanE:E2}, max={maxE:E2}, mism={mism}/≤{budget}, {sw.Elapsed.TotalMilliseconds:F1}ms)",
                mism <= budget && meanE < 1e-2f);

            naPts.Dispose(); naNrm.Dispose(); naVal.Dispose(); naRad.Dispose();
            scene.Dispose();
        }

        // ── G3: Burst Indirect ≡ EvaluateIndirect ─────────────────────────
        static void IndirectEquiv(StringBuilder sb, ref int pass, ref int total)
        {
            BuildScene(out var meshes, out var albedo, out var insts);
            using var bvh = new TwoLevelBVH(meshes, insts);
            using var managed = new InstancedRadianceScene(meshes, albedo, insts, bvh);  // 공유 BVH(모드 A)
            var scene = BurstScene.Create(bvh, albedo, Allocator.TempJob);

            CollectSurfaceSamples(meshes, insts, 200, out var pts, out var nrm);
            int n = pts.Length;
            var sun = Sun();
            var skyM = new GradientSky(new Vector3(0.5f, 0.6f, 0.8f), new Vector3(0.15f, 0.12f, 0.10f));
            var skyB = BurstSky.FromSky(skyM);
            var q = new BakeQualitySettings { IndirectSamples = 24, MaxBounces = 4, RRStartDepth = 2, RayBias = 1e-4f, AoSamples = 1 };

            // managed 정답 (텍셀별 동일 해시 시드)
            var refR = new Vector3[n];
            for (int i = 0; i < n; i++)
                refR[i] = RadianceCore.EvaluateIndirect(managed, pts[i], nrm[i], sun, skyM, q, BaseSeed + (uint)i * 2654435761u);

            // Burst Job
            var naPts = new NativeArray<Vector3>(pts, Allocator.TempJob);
            var naNrm = new NativeArray<Vector3>(nrm, Allocator.TempJob);
            var naVal = new NativeArray<bool>(n, Allocator.TempJob);
            for (int i = 0; i < n; i++) naVal[i] = true;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var naRad = BurstIndirect.Compute(scene, skyB, sun, q, naPts, naNrm, naVal, BaseSeed, Allocator.TempJob);
            sw.Stop();

            Stats(refR, naRad, n, 5e-2f, out float maxE, out float meanE, out int mism);
            int budget = Mathf.Max(2, Mathf.RoundToInt(n * 0.03f)); // RR/히트 경계 반전 허용(≤3%)
            Check(sb, ref pass, ref total,
                $"G3 Indirect ≡ CPU (n={n}, spp={q.IndirectSamples}, bnc={q.MaxBounces}, mean={meanE:E2}, max={maxE:E2}, mism={mism}/≤{budget}, {sw.Elapsed.TotalMilliseconds:F1}ms)",
                mism <= budget && meanE < 5e-3f);

            naPts.Dispose(); naNrm.Dispose(); naVal.Dispose(); naRad.Dispose();
            scene.Dispose();
        }

        // ── 씬: enclosing 박스(바운스 수신) + 내부 회전·비균등 스케일 인스턴스(차폐/바운스 타겟) ──
        static void BuildScene(out Tri[][] meshes, out Vector3[] albedo, out TwoLevelBVH.Instance[] insts)
        {
            var rng = new System.Random(20240629);
            meshes = new Tri[][]
            {
                LightmapEvaluateTests.MakeBox(Vector3.zero, 2f),   // 메시0: 박스(flat 면)
                RandomTris(rng, 30, 1.0f, 0.5f),                   // 메시1: 랜덤 삼각형
            };
            albedo = new Vector3[]
            {
                new Vector3(0.72f, 0.70f, 0.68f),
                new Vector3(0.55f, 0.35f, 0.30f),
            };

            var list = new List<TwoLevelBVH.Instance>
            {
                // 큰 enclosing 박스: 내부 텍셀이 다바운스 후 면을 맞도록
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

        // 인스턴스 삼각형 무게중심 → 표면 샘플(점=면 위 살짝 띄움, 노멀=월드 면노멀). 균등 stride 로 maxCount 근사.
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
                Matrix4x4 nm = l2w.inverse.transpose;          // 역전치 = 노멀 행렬(비균등 스케일 정확)
                for (int t = 0; t < tris.Length; t++)
                {
                    if ((counter++ % stride) != 0) continue;
                    Tri tri = tris[t];
                    Vector3 fnLocal = Vector3.Cross(tri.V1 - tri.V0, tri.V2 - tri.V0);
                    if (fnLocal.sqrMagnitude < 1e-12f) continue; // degenerate 삼각형 제외
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

        // 텍셀별 max-성분 절대오차 → 평균/최대/불일치(>epsTexel) 카운트.
        static void Stats(Vector3[] a, NativeArray<Vector3> b, int n, float epsTexel,
            out float maxErr, out float meanErr, out int mism)
        {
            maxErr = 0f; double sum = 0; mism = 0;
            for (int i = 0; i < n; i++)
            {
                Vector3 d = a[i] - b[i];
                float e = Mathf.Max(Mathf.Abs(d.x), Mathf.Max(Mathf.Abs(d.y), Mathf.Abs(d.z)));
                if (e > maxErr) maxErr = e;
                sum += e;
                if (e > epsTexel) mism++;
            }
            meanErr = n > 0 ? (float)(sum / n) : 0f;
        }

        // ── 유틸(결정적) ──
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
        {
            total++; if (ok) pass++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }
    }
}
