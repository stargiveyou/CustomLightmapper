using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 적응 샘플링 · 진행형 누적 · GPU 후처리 — 실제 백엔드(Burst/GPU)와 AtlasApplyDebug 실베이크 검증(에디터).
    /// 순수 엔진·CPU 경로는 헤드리스 <see cref="ProgressiveAccumulatorTests"/>, 후처리 계획은 <see cref="LightmapPostPlanTests"/>.
    ///  A) Burst 누적(비적응, K=5) ≡ BurstRadianceBaker.Bake(같은 시드·N spp) — 같은 Burst 코드라 비트 동일(원반 4발·RR 포함).
    ///  B) Burst 적응: 2회 동일 · 점 집합 반씩 따로 = 동일(배치 무관) · count ∈ [min, N] · 경로 &lt; 상한.
    ///  C) GPU(compute 필요): CSRadianceAccum 누적(비적응) ≈ CSRadiance 일괄(커널이 달라 통계 — 평균 상대차) ·
    ///     GPU 적응 2회 동일(같은 커널 → 비트 동일) · GPU 누적 ≈ Burst 누적(평균 상대차).
    ///  D) AtlasApplyDebug · Burst: 진행형(BeginProgressive + ProgressiveStep, 비적응) 최종 ≡ BakeAndApply ·
    ///     적응 BakeAndApply ≡ 적응 진행형 · 적응이 경로를 줄임(LastAccumSamples &lt; LastAccumBudget) — SSAA Adaptive 포함, 아틀라스 성분별 정확.
    ///  E) AtlasApplyDebug · GPU: 적응 BakeAndApply ≡ 적응 진행형(같은 커널 → 비트 동일) · 실제 GPU 사용.
    ///  F) GPU 후처리: 합성 페이지(겹침 그룹·segment·무효 텍셀) LightmapPostGpu ≈ CPU 판(상대 1e-5, valid 동일) ·
    ///     실베이크 gpuPostProcess on ≈ off(RGBAHalf 반올림 범위) · LastPostBackend = GPU.
    /// URP 셰이더 'CustomLightmapper/LightmapDebug' 필요. 임시 객체·베이커 생성 객체는 끝에 파괴.
    /// </summary>
    public static class ProgressiveBakeTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 적응 샘플링 · 진행형 누적 · GPU 후처리 (백엔드 · 실베이크) ===");
            int pass = 0, total = 0;
            bool gpu = SystemInfo.supportsComputeShaders && AtlasApplyDebug.LoadPathCompute() != null;

            BackendTests(sb, ref pass, ref total, gpu);
            AtlasTests(sb, ref pass, ref total, gpu);
            PostGpuSynthetic(sb, ref pass, ref total);

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        static BakeQualitySettings Q(int spp) => new BakeQualitySettings
        {
            AoSamples = 1, IndirectSamples = spp, MaxBounces = 3, RRStartDepth = 2, RayBias = 1e-4f, DirectSamples = 4,
        };

        static bool Same(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;

        static double MeanRel(Vector3[] a, Vector3[] b)
        {
            double sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                double d = (a[i] - b[i]).magnitude, m = Mathf.Max(1e-3f, b[i].magnitude);
                sum += d / m;
            }
            return a.Length > 0 ? sum / a.Length : 0;
        }

        // ── A·B·C: 백엔드 ──
        static void BackendTests(StringBuilder sb, ref int pass, ref int total, bool gpu)
        {
            GpuRadianceCompareTests.BuildScene(out var meshes, out var albedo, out var insts);
            using var bvh = new TwoLevelBVH(meshes, insts);
            var bs = BurstScene.Create(bvh, albedo, Allocator.Persistent);
            try
            {
                GpuRadianceCompareTests.CollectSurfaceSamples(meshes, insts, 300, out var pts, out var nrm);
                int n = pts.Length;
                var seeds = new uint[n];
                for (int i = 0; i < n; i++) seeds[i] = 777u + (uint)i * 2654435761u;
                var sun = GpuRadianceCompareTests.Sun();
                sun.AngularDiameterDeg = 1.5f;
                var skyB = BurstSky.FromSky(new GradientSky(new Vector3(0.5f, 0.6f, 0.8f), new Vector3(0.15f, 0.12f, 0.10f)));
                const int N = 24;
                var q = Q(N);

                // A) Burst 누적 ≡ 일괄
                var naP = new NativeArray<Vector3>(pts, Allocator.TempJob);
                var naN = new NativeArray<Vector3>(nrm, Allocator.TempJob);
                var naV = new NativeArray<bool>(n, Allocator.TempJob);
                var naS = new NativeArray<uint>(seeds, Allocator.TempJob);
                for (int i = 0; i < n; i++) naV[i] = true;
                var one = BurstRadianceBaker.Bake(bs, skyB, sun, q, naP, naN, naV, naS, Allocator.TempJob);
                var oneShot = one.ToArray();
                one.Dispose(); naP.Dispose(); naN.Dispose(); naV.Dispose(); naS.Dispose();

                Vector3[] burstAcc;
                int exact = 0;
                using (var be = new BurstPathAccum(bs, skyB, sun, q))
                {
                    var acc = new ProgressiveAccumulator(be, new AdaptiveSampling { MaxSamples = N, SamplesPerPass = 5 }, pts, nrm, seeds, n);
                    acc.Run();
                    burstAcc = acc.Result();
                    for (int i = 0; i < n; i++) if (Same(burstAcc[i], oneShot[i])) exact++;
                }
                Check(sb, ref pass, ref total, $"A: Burst 누적(K=5) ≡ BurstRadianceBaker.Bake(N={N}, 원반 4발·RR) — 비트 동일 {exact}/{n}", n > 0 && exact == n);

                // B) Burst 적응
                var st = new AdaptiveSampling { MaxSamples = 96, SamplesPerPass = 8, Adaptive = true, MinSamples = 16, Threshold = 0.03f };
                var q96 = Q(96);
                ProgressiveAccumulator RunB(Vector3[] p, Vector3[] nn, uint[] s)
                {
                    using var be = new BurstPathAccum(bs, skyB, sun, q96);
                    var a = new ProgressiveAccumulator(be, st, p, nn, s, p.Length);
                    a.Run();
                    return a;
                }
                var b1 = RunB(pts, nrm, seeds);
                var b2 = RunB(pts, nrm, seeds);
                int h = n / 2;
                var bh1 = RunB(pts[..h], nrm[..h], seeds[..h]);
                var bh2 = RunB(pts[h..], nrm[h..], seeds[h..]);
                bool det = true, split = true, range = true;
                for (int i = 0; i < n; i++)
                {
                    det &= b1.Count[i] == b2.Count[i] && Same(b1.Estimate(i), b2.Estimate(i));
                    var part = i < h ? bh1 : bh2; int j = i < h ? i : i - h;
                    split &= part.Count[j] == b1.Count[i] && Same(part.Estimate(j), b1.Estimate(i));
                    range &= b1.Count[i] >= 16 && b1.Count[i] <= 96;
                }
                long budget = (long)n * 96;
                Check(sb, ref pass, ref total, $"B: Burst 적응 — 결정적 · 반씩 나눠도 동일 · count ∈ [16, 96] · 경로 {100.0 * b1.TotalSamples / budget:F1}%",
                      det && split && range && b1.TotalSamples < budget);

                // C) GPU
                if (!gpu) { sb.AppendLine("  [SKIP] C: compute 미지원 또는 PathTrace.compute 로드 실패"); return; }
                var cs = AtlasApplyDebug.LoadPathCompute();
                int kRad = cs.FindKernel("CSRadiance"), kAcc = cs.FindKernel("CSRadianceAccum");
                using var gs = new GpuScene(bs);
                Vector3[] gOne;
                using (var io = new AtlasApplyDebug.GpuIoBuffers())
                    gOne = AtlasApplyDebug.DispatchRadianceGpu(gs, cs, kRad, sun, skyB, q, pts, nrm, seeds, n, io);
                Vector3[] gAcc;
                ProgressiveAccumulator g1, g2;
                using (var ge = new GpuPathAccum(gs, cs, kAcc, sun, skyB, q, chunk: 128))
                {
                    var a = new ProgressiveAccumulator(ge, new AdaptiveSampling { MaxSamples = N, SamplesPerPass = 5 }, pts, nrm, seeds, n);
                    a.Run();
                    gAcc = a.Result();
                }
                using (var ge = new GpuPathAccum(gs, cs, kAcc, sun, skyB, q96))
                {
                    g1 = new ProgressiveAccumulator(ge, st, pts, nrm, seeds, n); g1.Run();
                    g2 = new ProgressiveAccumulator(ge, st, pts, nrm, seeds, n); g2.Run();   // 같은 백엔드 재Bind
                }
                bool gdet = true;
                for (int i = 0; i < n; i++) gdet &= g1.Count[i] == g2.Count[i] && Same(g1.Estimate(i), g2.Estimate(i));
                double relOne = MeanRel(gAcc, gOne), relBurst = MeanRel(gAcc, burstAcc);
                Check(sb, ref pass, ref total, $"C: GPU 누적 ≈ CSRadiance 일괄(평균 상대차 {relOne:E2}) ≈ Burst 누적({relBurst:E2}) · GPU 적응 2회 동일 · 경로 {100.0 * g1.TotalSamples / budget:F1}%",
                      relOne < 2e-3 && relBurst < 5e-3 && gdet && g1.TotalSamples < budget);
            }
            finally { bs.Dispose(); }
        }

        // ── D·E: AtlasApplyDebug 실베이크 ──
        static void AtlasTests(StringBuilder sb, ref int pass, ref int total, bool gpu)
        {
            if (Shader.Find("CustomLightmapper/LightmapDebug") == null)
            { sb.AppendLine("  [SKIP] D/E/F(실베이크): 셰이더 'CustomLightmapper/LightmapDebug' 없음"); return; }

            var root = new GameObject("ProgressiveBakeTests") { hideFlags = HideFlags.HideAndDontSave };
            var filters = new List<MeshFilter>
            {
                AtlasBatchBakeTests.Prim(root, PrimitiveType.Cube, new Vector3(0f, -0.05f, 0f), new Vector3(8f, 0.1f, 8f)),
                AtlasBatchBakeTests.Prim(root, PrimitiveType.Cube, new Vector3(0f, 0.5f, 0f), Vector3.one),
                AtlasBatchBakeTests.Prim(root, PrimitiveType.Sphere, new Vector3(2f, 0.6f, 0.3f), Vector3.one * 1.2f),
                AtlasBatchBakeTests.Prim(root, PrimitiveType.Cylinder, new Vector3(-1.5f, 0.5f, -1.5f), new Vector3(0.8f, 0.5f, 0.8f)),
            };
            var baker = root.AddComponent<AtlasApplyDebug>();
            var made = new List<Object>();
            try
            {
                baker.saveBakeResult = false;
                baker.applyOnEnable = false;
                baker.denoise = false;
                baker.atlasResolution = 128;
                baker.texelsPerWorldUnit = 6f;
                baker.mode = AtlasApplyDebug.BakeMode.RadianceGI;
                baker.indirectSamples = 32;
                baker.maxBounces = 2;
                baker.ssaaMode = AtlasApplyDebug.SsaaMode.Adaptive;
                baker.ssaaFactor = 2;
                baker.lightIntensity = 2f;
                baker.samplesPerPass = 8;
                baker.adaptiveMinSamples = 16;
                baker.adaptiveThreshold = 0.05f;
                baker.bakeBatchLumels = 1 << 20;

                // ── D) Burst ──
                baker.radianceBackend = AtlasApplyDebug.RadianceBackend.Burst;
                baker.adaptiveSampling = false;
                var d0 = Bake(baker, filters, made, false, out _);
                var d1 = Bake(baker, filters, made, true, out _);
                baker.adaptiveSampling = true;
                baker.bakeBatchLumels = 500;   // 적응 일괄을 여러 배치로 — 진행형(배치 1)과 같아야 함
                var d2 = Bake(baker, filters, made, false, out _);
                long used = baker.LastAccumSamples, cap = baker.LastAccumBudget;
                baker.bakeBatchLumels = 1 << 20;
                var d3 = Bake(baker, filters, made, true, out _);
                Check(sb, ref pass, ref total, $"D: Burst 진행형 ≡ 일괄(비적응) · 적응 일괄(배치 500) ≡ 적응 진행형 · 경로 {(cap > 0 ? 100.0 * used / cap : 0):F1}% (텍셀 {AtlasBatchBakeTests.Count(d0)}, 차트 {AtlasBatchBakeTests.Lit(d0, baker)})",
                      d0 != null && AtlasBatchBakeTests.Same(d0, d1) && AtlasBatchBakeTests.Same(d2, d3) && !AtlasBatchBakeTests.Same(d0, d2)
                      && cap > 0 && used < cap && AtlasBatchBakeTests.Lit(d0, baker) > 100);

                // ── E) GPU ──
                if (!gpu) sb.AppendLine("  [SKIP] E: compute 미지원 또는 PathTrace.compute 로드 실패");
                else
                {
                    baker.radianceBackend = AtlasApplyDebug.RadianceBackend.Gpu;
                    baker.adaptiveSampling = true;
                    var e0 = Bake(baker, filters, made, false, out string be0);
                    var e1 = Bake(baker, filters, made, true, out string be1);
                    Check(sb, ref pass, ref total, $"E: GPU 적응 일괄 ≡ 적응 진행형(실사용 {be0}/{be1})",
                          be0 == "Gpu" && be1 == "Gpu" && e0 != null && AtlasBatchBakeTests.Same(e0, e1));
                }

                // ── F) GPU 후처리 실베이크 ──
                if (!SystemInfo.supportsComputeShaders || LightmapPostGpu.Resolve() == null)
                    sb.AppendLine("  [SKIP] F(실베이크): compute 미지원 또는 LightmapPost.compute 로드 실패");
                else
                {
                    baker.radianceBackend = AtlasApplyDebug.RadianceBackend.Burst;
                    baker.adaptiveSampling = false;
                    baker.dilateIterations = 4;
                    baker.gpuPostProcess = false;
                    var f0 = Bake(baker, filters, made, false, out _);
                    string pb0 = baker.LastPostBackend;
                    baker.gpuPostProcess = true;
                    var f1 = Bake(baker, filters, made, false, out _);
                    string pb1 = baker.LastPostBackend;
                    baker.gpuPostProcess = false;
                    float maxRel = MaxRel(f0, f1);
                    Check(sb, ref pass, ref total, $"F: 실베이크 GPU 후처리 ≈ CPU(최대 상대차 {maxRel:E2}, {pb0}→{pb1})",
                          pb0 == "CPU" && pb1 == "GPU" && f0 != null && f1 != null && maxRel <= 2e-3f);
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
                foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
            }
        }

        // 일괄(BakeAndApply) 또는 진행형(BeginProgressive → ProgressiveStep 끝까지) → 전 페이지 픽셀 복사 → 원복
        static Color[][] Bake(AtlasApplyDebug baker, List<MeshFilter> filters, List<Object> made, bool progressive, out string backend)
        {
            var originals = new Mesh[filters.Count];
            for (int i = 0; i < filters.Count; i++) originals[i] = filters[i].sharedMesh;
            if (progressive)
            {
                if (baker.BeginProgressive())
                {
                    int guard = 0;
                    while (baker.ProgressiveStep() && guard++ < 10000) { }
                }
            }
            else baker.BakeAndApply();
            backend = baker.LastBakeBackend;
            var atlas = baker.Atlas;
            Color[][] px = null;
            if (atlas != null)
            {
                px = new Color[atlas.depth][];
                for (int p = 0; p < atlas.depth; p++) px[p] = atlas.GetPixels(p);
            }
            made.Add(atlas); made.Add(baker.SharedMaterial);
            for (int i = 0; i < filters.Count; i++)
                if (filters[i].sharedMesh != originals[i]) made.Add(filters[i].sharedMesh);
            baker.RestoreOriginals();
            return px;
        }

        static float MaxRel(Color[][] a, Color[][] b)
        {
            if (a == null || b == null || a.Length != b.Length) return float.MaxValue;
            float m = 0f;
            for (int p = 0; p < a.Length; p++)
                for (int i = 0; i < a[p].Length; i++)
                {
                    Color x = a[p][i], y = b[p][i];
                    float d = Mathf.Max(Mathf.Abs(x.r - y.r), Mathf.Max(Mathf.Abs(x.g - y.g), Mathf.Abs(x.b - y.b)));
                    float s = Mathf.Max(1e-2f, Mathf.Max(x.r, Mathf.Max(x.g, x.b)));
                    m = Mathf.Max(m, d / s);
                }
            return m;
        }

        // ── F) 합성 페이지: LightmapPostGpu ≈ CPU 판 ──
        static void PostGpuSynthetic(StringBuilder sb, ref int pass, ref int total)
        {
            var cs = SystemInfo.supportsComputeShaders ? LightmapPostGpu.Resolve() : null;
            if (cs == null) { sb.AppendLine("  [SKIP] F(합성): compute 미지원 또는 LightmapPost.compute 로드 실패"); return; }

            var rng = new System.Random(31337);
            const int res = 64;
            int n = res * res;
            bool okAll = true;
            float worst = 0f;
            int changed = 0;
            for (int trial = 0; trial < 4; trial++)
            {
                var px = new Color[n]; var valid = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    valid[i] = rng.NextDouble() < 0.55;
                    px[i] = valid[i] ? new Color((float)rng.NextDouble() * 2f, (float)rng.NextDouble(), (float)rng.NextDouble(), 1f)
                                     : new Color(0.04f, 0.04f, 0.06f, 1f);
                }
                var groups = new List<int[]>();
                var hot = new int[16];
                for (int i = 0; i < hot.Length; i++) hot[i] = rng.Next(n);
                for (int g = 0; g < 80; g++)
                {
                    int len = 2 + rng.Next(4);
                    var arr = new int[len];
                    for (int k = 0; k < len; k++) arr[k] = rng.NextDouble() < 0.5 ? hot[rng.Next(hot.Length)] : rng.Next(n);
                    groups.Add(arr);
                }
                var segs = new List<LightmapSeamStitch.Seg[]>();
                for (int g = 0; g < 20; g++)
                {
                    var a = new Vector2((float)rng.NextDouble() * res, (float)rng.NextDouble() * res);
                    var d = new Vector2((float)rng.NextDouble() * 20f - 10f, (float)rng.NextDouble() * 20f - 10f);
                    segs.Add(new[] { new LightmapSeamStitch.Seg(a, a + d), new LightmapSeamStitch.Seg(a + Vector2.one, a + d * 0.8f) });
                }

                var cp = (Color[])px.Clone(); var cv = (bool[])valid.Clone();
                LightmapSeamStitch.Stitch(cp, cv, groups);
                LightmapSeamStitch.StitchEdges(cp, cv, res, segs, 2);
                LightmapPostProcessBurstJob.Dilate(cp, cv, res, res, 4);

                var gp = (Color[])px.Clone(); var gv = (bool[])valid.Clone();
                var opt = new LightmapPostGpu.Options { Tier1 = true, Tier2 = true, Tier2Iterations = 2, DilateIterations = 4 };
                bool ran = LightmapPostGpu.Run(cs, gp, gv, res, groups, segs, opt, out string err);
                okAll &= ran;
                if (!ran) { sb.AppendLine($"    GPU 후처리 실패: {err}"); continue; }
                for (int i = 0; i < n; i++)
                {
                    okAll &= cv[i] == gv[i];
                    float dd = Mathf.Max(Mathf.Abs(cp[i].r - gp[i].r), Mathf.Max(Mathf.Abs(cp[i].g - gp[i].g), Mathf.Abs(cp[i].b - gp[i].b)));
                    worst = Mathf.Max(worst, dd / Mathf.Max(1e-3f, Mathf.Max(cp[i].r, Mathf.Max(cp[i].g, cp[i].b))));
                    if (cp[i].r != px[i].r) changed++;
                }
            }
            Check(sb, ref pass, ref total, $"F: 합성 페이지 GPU 후처리(T1→T2×2→dilate×4) ≈ CPU 판 — 최대 상대차 {worst:E2}, valid 동일, 바뀐 텍셀 {changed}",
                  okAll && worst <= 1e-5f && changed > 0);
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
