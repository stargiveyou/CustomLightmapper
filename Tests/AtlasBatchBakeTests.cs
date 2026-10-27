using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// G6-3b 배치 베이크 · G6-4 분할 디스패치 검증 — AtlasApplyDebug 를 숨김 프리미티브 5개(지면·큐브·구·캡슐·실린더)로 실제 베이크.
    /// 텍셀 조도는 점별 독립(점·노멀·시드)이므로 배치 크기·디스패치 분할과 무관하게 아틀라스가 **비트 동일**해야 한다.
    ///  A) RadianceGI · Burst: bakeBatchLumels = 0(인스턴스마다 — 구 거동) ≡ 1<<20(전부 한 배치) ≡ 300(중간 분할) — SSAA Adaptive 포함.
    ///  B) Radiance(CPU AO+Direct): 배치 0 ≡ 1<<20.
    ///  C) RadianceGI · GPU(compute 필요): 배치 0·chunk 기본 ≡ 배치 1<<20·chunk 1000(64 의 배수 아님) ≡ chunk 64 — 실제 GPU 사용 확인.
    ///  D) GpuScene 순회 스택: 이 씬은 TraversalFits(요구 칸 ≤ 32) — 폴백 없이 GPU 로 돈 것과 일관.
    /// 단계마다 RestoreOriginals, 끝에 임시 객체·베이커가 만든 런타임 객체 파괴. URP 셰이더 'CustomLightmapper/LightmapDebug' 필요(에디터).
    /// </summary>
    public static class AtlasBatchBakeTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== G6-3b 배치 베이크 · G6-4 분할 디스패치 ≡ 인스턴스별 ===");
            int pass = 0, total = 0;

            if (Shader.Find("CustomLightmapper/LightmapDebug") == null)
            { sb.AppendLine("  [SKIP] 셰이더 'CustomLightmapper/LightmapDebug' 없음"); return sb.ToString(); }

            var root = new GameObject("AtlasBatchBakeTests") { hideFlags = HideFlags.HideAndDontSave };
            var filters = new List<MeshFilter>
            {
                Prim(root, PrimitiveType.Cube, new Vector3(0f, -0.05f, 0f), new Vector3(8f, 0.1f, 8f)),
                Prim(root, PrimitiveType.Cube, new Vector3(0f, 0.5f, 0f), Vector3.one),
                Prim(root, PrimitiveType.Sphere, new Vector3(2f, 0.6f, 0.3f), Vector3.one * 1.2f),
                Prim(root, PrimitiveType.Capsule, new Vector3(-2f, 1f, 1f), Vector3.one),
                Prim(root, PrimitiveType.Cylinder, new Vector3(1f, 0.5f, -2f), new Vector3(0.8f, 0.5f, 0.8f)),
            };
            var baker = root.AddComponent<AtlasApplyDebug>();
            baker.localLightSource = LocalLightSource.None;   // 열린 씬의 라이트와 무관하게(테스트 격리)
            var made = new List<Object>();
            try
            {
                baker.saveBakeResult = false;
                baker.applyOnEnable = false;
                baker.denoise = false;                 // 결과는 결정적이지만 테스트 시간 절약
                baker.atlasResolution = 128;
                baker.texelsPerWorldUnit = 6f;
                baker.indirectSamples = 4;
                baker.maxBounces = 1;
                baker.aoSamples = 4;
                baker.ssaaMode = AtlasApplyDebug.SsaaMode.Adaptive;   // 배치 SSAA 2패스도 탄다
                baker.ssaaFactor = 2;
                baker.lightIntensity = 2f;

                // ── A) RadianceGI · Burst ──
                baker.mode = AtlasApplyDebug.BakeMode.RadianceGI;
                baker.radianceBackend = AtlasApplyDebug.RadianceBackend.Burst;
                baker.bakeBatchLumels = 0;
                var a0 = Bake(baker, filters, made, out string beA0);
                baker.bakeBatchLumels = 1 << 20;
                var a1 = Bake(baker, filters, made, out _);
                baker.bakeBatchLumels = 300;
                var a2 = Bake(baker, filters, made, out _);
                Check(sb, ref pass, ref total, $"A: RadianceGI·{beA0} 배치 0 ≡ 1<<20 ≡ 300 (텍셀 {Count(a0)}, 차트 텍셀 {Lit(a0, baker)})",
                      a0 != null && Same(a0, a1) && Same(a0, a2) && Lit(a0, baker) > 100);

                // ── B) Radiance(CPU) ──
                baker.mode = AtlasApplyDebug.BakeMode.Radiance;
                baker.bakeBatchLumels = 0;
                var b0 = Bake(baker, filters, made, out _);
                baker.bakeBatchLumels = 1 << 20;
                var b1 = Bake(baker, filters, made, out _);
                Check(sb, ref pass, ref total, "B: Radiance(CPU) 배치 0 ≡ 1<<20", b0 != null && Same(b0, b1));

                // ── C) RadianceGI · GPU ──
                if (!SystemInfo.supportsComputeShaders || AtlasApplyDebug.LoadPathCompute() == null)
                {
                    sb.AppendLine("  [SKIP] C/D: compute 미지원 또는 PathTrace.compute 로드 실패");
                }
                else
                {
                    baker.mode = AtlasApplyDebug.BakeMode.RadianceGI;
                    baker.radianceBackend = AtlasApplyDebug.RadianceBackend.Gpu;
                    baker.bakeBatchLumels = 0; baker.gpuDispatchChunk = GpuBakeLimits.DefaultChunk;
                    var c0 = Bake(baker, filters, made, out string beC0);
                    baker.bakeBatchLumels = 1 << 20; baker.gpuDispatchChunk = 1000;
                    var c1 = Bake(baker, filters, made, out string beC1);
                    baker.gpuDispatchChunk = 64;
                    var c2 = Bake(baker, filters, made, out _);
                    baker.gpuDispatchChunk = GpuBakeLimits.DefaultChunk;
                    Check(sb, ref pass, ref total, $"C: RadianceGI·GPU(실사용 {beC0}/{beC1}) 배치 0·chunk 기본 ≡ 배치 1<<20·chunk 1000 ≡ chunk 64",
                          beC0 == "Gpu" && beC1 == "Gpu" && c0 != null && Same(c0, c1) && Same(c0, c2));

                    // ── D) 순회 스택 ──
                    var tris = new List<Tri[]>();
                    var insts = new List<TwoLevelBVH.Instance>();
                    foreach (var mf in filters)
                    {
                        insts.Add(new TwoLevelBVH.Instance { MeshIndex = tris.Count, LocalToWorld = mf.transform.localToWorldMatrix });
                        tris.Add(TemplateInstanceSource.MeshToLocalTris(mf.sharedMesh));
                    }
                    using var bvh = new TwoLevelBVH(tris.ToArray(), insts.ToArray(), Unity.Collections.Allocator.TempJob, BVH.BuildQuality.SAH);
                    using var bs = BurstScene.Create(bvh, Unity.Collections.Allocator.TempJob);
                    using var gs = new GpuScene(bs);
                    Check(sb, ref pass, ref total, $"D: GpuScene 순회 스택 — 요구 {gs.RequiredStack}칸(TLAS {gs.TlasStack}·BLAS {gs.MaxBlasStack}, 깊이 {gs.TlasDepth}/{gs.MaxBlasDepth}) ≤ {GpuBakeLimits.TraversalStack} → TraversalFits",
                          gs.TraversalFits && gs.RequiredStack >= 1 && gs.RequiredStack == Mathf.Max(gs.TlasStack, gs.MaxBlasStack)
                          && gs.TlasStack <= GpuBakeLimits.RequiredStack(gs.TlasDepth) && gs.MaxBlasStack <= GpuBakeLimits.RequiredStack(gs.MaxBlasDepth));
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
                foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
            }

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        internal static MeshFilter Prim(GameObject root, PrimitiveType t, Vector3 pos, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(t);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            return go.GetComponent<MeshFilter>();
        }

        // 베이크 → 전 페이지 픽셀 복사 → 원복(다음 베이크가 아틀라스를 바꿀 수 있어 픽셀은 여기서 복사)
        static Color[][] Bake(AtlasApplyDebug baker, List<MeshFilter> filters, List<Object> made, out string backend)
        {
            var originals = new Mesh[filters.Count];
            for (int i = 0; i < filters.Count; i++) originals[i] = filters[i].sharedMesh;
            baker.BakeAndApply();
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
                if (filters[i].sharedMesh != originals[i]) made.Add(filters[i].sharedMesh);   // 스왑된 uv2 메시만(내장 프리미티브 에셋은 파괴 금지)
            baker.RestoreOriginals();
            return px;
        }

        // 성분별 정확 비교(Color == 는 근사)
        internal static bool Same(Color[][] a, Color[][] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int p = 0; p < a.Length; p++)
            {
                if (a[p].Length != b[p].Length) return false;
                for (int i = 0; i < a[p].Length; i++)
                {
                    Color x = a[p][i], y = b[p][i];
                    if (x.r != y.r || x.g != y.g || x.b != y.b || x.a != y.a) return false;
                }
            }
            return true;
        }

        internal static int Count(Color[][] a) { int n = 0; if (a != null) foreach (var p in a) n += p.Length; return n; }

        // 배경색이 아닌 텍셀 수(차트 + dilation) — 비교가 빈 아틀라스끼리가 아님을 확인
        internal static int Lit(Color[][] a, AtlasApplyDebug baker)
        {
            if (a == null) return 0;
            Color bg = baker.background.linear;
            int n = 0;
            foreach (var p in a)
                foreach (var c in p)
                    if (Mathf.Abs(c.r - bg.r) + Mathf.Abs(c.g - bg.g) + Mathf.Abs(c.b - bg.b) > 1e-3f) n++;
            return n;
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
