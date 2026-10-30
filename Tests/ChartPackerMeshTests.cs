using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// A4 차트 패킹 — Unity 메시로 확인(에디터). 합성 세트 검증은 <see cref="ChartPackerTests"/>(헤드리스).
    ///  A) 기본 메시 6종(Cube·Sphere·Capsule·Cylinder·Quad·Plane)을 실제 파이프라인(분할 → 평탄화 → 밀도 정규화)에 통과 →
    ///     Shelf / Best 패킹: 계약(ChartPackerTests.Contract — [0,1]²·비겹침·gutter·균일 스케일·감기) · Best 점유 ≥ Shelf · 점유율 표
    ///  B) 실베이크(AtlasApplyDebug PerInstanceColor, dilation off): chartPacking Shelf ↔ Best — 둘 다 성공 · 차트가 덮은 아틀라스 텍셀 Best ≥ Shelf × 0.98
    /// 호출: Debug.Log(ChartPackerMeshTests.RunAll());
    /// </summary>
    public static class ChartPackerMeshTests
    {
        const float Gutter = 0.01f;

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== A4 Chart Packer — Unity 메시 (실파이프라인 · 실베이크) ===");
            int pass = 0, total = 0;
            TestPrimitives(sb, ref pass, ref total);
            TestRealBake(sb, ref pass, ref total);
            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        static void TestPrimitives(StringBuilder sb, ref int pass, ref int total)
        {
            PrimitiveType[] types = { PrimitiveType.Cube, PrimitiveType.Sphere, PrimitiveType.Capsule, PrimitiveType.Cylinder, PrimitiveType.Quad, PrimitiveType.Plane };
            var table = new StringBuilder();
            string fail = null;
            foreach (var t in types)
            {
                var go = GameObject.CreatePrimitive(t);
                go.hideFlags = HideFlags.HideAndDontSave;
                try
                {
                    var mesh = go.GetComponent<MeshFilter>().sharedMesh;
                    var pr = ParameterizationPipeline.Run(mesh, SegmentationSettings.Default, FlattenSettings.Default);
                    if (pr.Charts == null || pr.Charts.Length == 0) { fail ??= $"{t}: 차트 0개"; continue; }
                    DensityNormalizer.Normalize(pr.Charts);

                    var shelf = ChartPackerTests.Clone(pr.Charts);
                    ChartPacker.Pack(shelf, Gutter, ChartPacker.Method.Shelf, out float s0);
                    var best = ChartPackerTests.Clone(pr.Charts);
                    var used = ChartPacker.Pack(best, Gutter, ChartPacker.Method.Best, out float s1);

                    string e0 = ChartPackerTests.Contract(pr.Charts, shelf, Gutter, s0);
                    string e1 = ChartPackerTests.Contract(pr.Charts, best, Gutter, s1);
                    if (e0 != null) fail ??= $"{t}/Shelf: {e0}";
                    if (e1 != null) fail ??= $"{t}/Best: {e1}";
                    float o0 = ChartPackerTests.Occupancy(shelf), o1 = ChartPackerTests.Occupancy(best);
                    if (o1 < o0 * (1f - 1e-5f)) fail ??= $"{t}: Best {o1:P1} < Shelf {o0:P1}";
                    table.AppendLine($"    {t,-9} 차트 {pr.Charts.Length,3}: {o0 * 100f,5:0.0}% → {o1 * 100f,5:0.0}% ({used})");
                }
                catch (System.Exception e) { fail ??= $"{t}: 예외 {e.Message}"; }
                finally { Object.DestroyImmediate(go); }
            }
            Check(sb, ref pass, ref total, $"A: 기본 메시 6종 실파이프라인 — Shelf·Best 계약 · Best 점유 ≥ Shelf{(fail != null ? " — " + fail : "")}", fail == null);
            sb.AppendLine("  [표] 점유율 Shelf → Best(사용)");
            sb.Append(table);
        }

        static void TestRealBake(StringBuilder sb, ref int pass, ref int total)
        {
            if (Shader.Find("CustomLightmapper/LightmapDebug") == null)
            { sb.AppendLine("  [SKIP] B: 셰이더 'CustomLightmapper/LightmapDebug' 없음"); return; }

            var root = new GameObject("ChartPackerMeshTests") { hideFlags = HideFlags.HideAndDontSave };
            var filters = new List<MeshFilter>
            {
                AtlasBatchBakeTests.Prim(root, PrimitiveType.Sphere, new Vector3(0f, 0.6f, 0f), Vector3.one * 1.2f),
                AtlasBatchBakeTests.Prim(root, PrimitiveType.Capsule, new Vector3(2f, 1f, 0f), Vector3.one),
                AtlasBatchBakeTests.Prim(root, PrimitiveType.Cylinder, new Vector3(-2f, 1f, 0f), Vector3.one),
            };
            var baker = root.AddComponent<AtlasApplyDebug>();
            var made = new List<Object>();
            try
            {
                baker.saveBakeResult = false;
                baker.applyOnEnable = false;
                baker.mode = AtlasApplyDebug.BakeMode.PerInstanceColor;
                baker.dilate = false;
                baker.atlasResolution = 256;
                baker.texelsPerWorldUnit = 16f;

                baker.chartPacking = ChartPacker.Method.Shelf;
                var a0 = ProgressiveBakeTests.Bake(baker, filters, made, false, out _);
                baker.chartPacking = ChartPacker.Method.Best;
                var a1 = ProgressiveBakeTests.Bake(baker, filters, made, false, out _);

                int c0 = Covered(a0, baker.background), c1 = Covered(a1, baker.background);
                Check(sb, ref pass, ref total, $"B: 실베이크 PerInstanceColor — 차트 텍셀 Shelf {c0} → Best {c1}(×{(c0 > 0 ? (float)c1 / c0 : 0f):0.000}) ≥ ×0.98",
                      a0 != null && a1 != null && c0 > 0 && c1 >= c0 * 0.98f);
            }
            finally
            {
                Object.DestroyImmediate(root);
                foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
            }
        }

        // 배경색이 아닌 텍셀 수(dilation off → 차트가 실제로 덮은 텍셀)
        static int Covered(Color[][] pages, Color bg)
        {
            if (pages == null) return 0;
            int n = 0;
            foreach (var p in pages)
                foreach (var c in p)
                    if (Mathf.Abs(c.r - bg.r) + Mathf.Abs(c.g - bg.g) + Mathf.Abs(c.b - bg.b) > 2e-3f) n++;
            return n;
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
