using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using UnityEngine;
using static HuskyLibs.CustomLightmapper.Bake.ProbeGridTests;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Track B B2 검증: <see cref="ProbeVolumeBaker"/> 지형 추종 2.5D 배치(<see cref="ProbeVolumeBaker.Layout.TerrainLayers"/>) — 메모리 베이크(디스크 저장 없음).
    /// 볼륨 8×10×8(중심 y 3 → 지면 탐색 y −2..8), 간격 (1, 0.5, 1), 첫 층 0.25, 층 높이 1.5 → 9×4×9 = 324 프로브.
    ///  A) 평지(슬래브 윗면 y = 0): 열 높이 ≈ 0 · 미스 0 · 에셋 구조(columnHeights · 높이 텍스처 RFloat/RGBAFloat·Point·Clamp·텍셀 = 높이) ·
    ///     프로브 위치 ≈ 균일 격자(origin.y = 0.25) · Validate 음성(열 높이 길이·NaN).
    ///  B) 에셋 SH·valid ≡ 코어 직접 파이프라인(지형 위치로 BakeBurstAt → BackfaceRatio → Classify → Dilate, 비트 동일) · ProbePosition ≡ 위치 배열.
    ///  C) 경사(12° 슬래브): 열 높이 ≈ 해석 평면 · 첫 층 = 지면 + 0.25 · 절감 리포트(균일 환산 8층 → −50%) · layoutLog.
    ///  D) 지면 메시 규칙: 템플릿(수관)은 지면이 아님 → 높이 불변 · groundRoots 지정 → 그 메시(지붕) 높이.
    ///  E) 인스턴스 샘플 = 지형 추종 CPU 샘플(비트 동일) · 균일 해석과 다름(비자명).
    ///  F) 셰이더 바인딩: 지형 추종 params.w = 1 · origin.y = 첫 층 높이 · 높이 텍스처 없으면 비활성 · 균일 에셋 w = 0 · Uniform 베이크 = 열 높이 없음.
    ///  G) GPU(렌더 컨텍스트): HuskyProbeVolume.hlsl 지형 추종 ≡ CPU 미러 — 내부 임의점(투과 절반) · 프로브 위치 · 층 위/아래 클램프 · 페이드.
    /// 호출: Debug.Log(ProbeVolumeTerrainTests.RunAll());
    /// </summary>
    public static class ProbeVolumeTerrainTests
    {
        const float Offset = 0.25f, LayerH = 1.5f, SlopeDeg = 12f, SlopeC = 0.5f, SlabHalfThick = 0.05f;

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Track B 지형 추종 2.5D 베이커 (B2) ===");
            int pass = 0, total = 0;

            var box = InstanceSceneBakerTests.MakeBoxMesh();
            var made = new List<ProbeVolumeAsset>();
            var roots = new List<GameObject>();
            try
            {
                var go = Hidden("ProbeVolumeTerrainTests", roots);
                go.transform.position = new Vector3(0f, 3f, 0f);
                var baker = go.AddComponent<ProbeVolumeBaker>();
                baker.renderOnEnable = false;
                baker.localLights.source = LocalLightSource.None;   // 열린 씬의 라이트와 무관하게(테스트 격리)
                baker.bakeVertexAO = false;
                baker.volumeSize = new Vector3(8f, 10f, 8f);
                baker.probeSpacing = new Vector3(1f, 0.5f, 1f);
                baker.dirCount = 64;
                baker.bounces = 1;
                baker.layout = ProbeVolumeBaker.Layout.TerrainLayers;
                baker.groundOffset = Offset;
                baker.layerHeight = LayerH;
                baker.templates = new ProbeVolumeBaker.TemplateInput[0];

                var flat = Hidden("Flat", roots).transform;
                Child(flat, "Slab", box, Matrix4x4.TRS(new Vector3(0f, -SlabHalfThick, 0f), Quaternion.identity, new Vector3(12f, 2f * SlabHalfThick, 12f)));
                var slope = Hidden("Slope", roots).transform;
                Child(slope, "Ramp", box, Matrix4x4.TRS(new Vector3(0f, SlopeC, 0f), Quaternion.Euler(0f, 0f, SlopeDeg), new Vector3(14f, 2f * SlabHalfThick, 12f)));

                baker.staticRoots = new[] { flat };
                var aFlat = ProbeVolumeBakerTests.Bake(baker, made);
                TestFlat(sb, ref pass, ref total, baker, aFlat);
                if (aFlat != null) TestDirectPipeline(sb, ref pass, ref total, baker, aFlat);
                TestGroundRules(sb, ref pass, ref total, baker, box, roots, made);

                baker.staticRoots = new[] { slope };
                var aSlope = ProbeVolumeBakerTests.Bake(baker, made);
                TestSlope(sb, ref pass, ref total, baker, aSlope);
                if (aSlope != null)
                {
                    TestInstanceSampling(sb, ref pass, ref total, baker, box, aSlope);
                    TestBinding(sb, ref pass, ref total, baker, aSlope, made);
                    TestGpu(sb, ref pass, ref total, aSlope);
                }
            }
            finally
            {
                foreach (var r in roots) if (r != null) Object.DestroyImmediate(r);
                Object.DestroyImmediate(box);
                foreach (var a in made) ProbeVolumeBakerTests.DestroyAsset(a);
            }

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        static float SlopeGround(float x) => SlopeC + Mathf.Tan(SlopeDeg * Mathf.Deg2Rad) * x + SlabHalfThick / Mathf.Cos(SlopeDeg * Mathf.Deg2Rad);

        // ── A) 평지 ──
        static void TestFlat(StringBuilder sb, ref int pass, ref int total, ProbeVolumeBaker baker, ProbeVolumeAsset a)
        {
            Check(sb, ref pass, ref total, $"A: 평지 BakeToAsset · Validate (backend={a?.backend}) · layout '{a?.layoutLog}'", a != null && a.Validate(out _));
            if (a == null) return;
            var g = a.grid;
            float dH = 0f;
            foreach (float h in a.columnHeights) dH = Mathf.Max(dH, Mathf.Abs(h));
            bool dims = g.dims == new Vector3Int(9, 4, 9) && g.origin.y == Offset && a.columnHeights.Length == 81 && a.IsTerrainLayers;
            Check(sb, ref pass, ref total, $"A: 격자 {g.dims.x}×{g.dims.y}×{g.dims.z}(9×4×9) · origin.y = 첫 층 {g.origin.y} · 열 높이 81개 ≈ 0 (max|h|={dH:0.0e0}) · 미스 없음",
                  dims && dH < 1e-4f && !a.layoutLog.Contains("미스"));

            var t = a.heightTexture;
            bool texOk = t != null && t.width == 9 && t.height == 9 && (t.format == TextureFormat.RFloat || t.format == TextureFormat.RGBAFloat)
                         && t.filterMode == FilterMode.Point && t.wrapMode == TextureWrapMode.Clamp;
            if (texOk)
            {
                var px = t.GetPixels();
                for (int i = 0; i < px.Length; i++) texOk &= px[i].r == a.columnHeights[i];
            }
            var u = g;
            float dPos = 0f;
            for (int i = 0; i < g.Count; i++) dPos = Mathf.Max(dPos, (a.ProbePosition(i) - u.Position(i)).magnitude);
            Check(sb, ref pass, ref total, $"A: 높이 텍스처 9×9 ({t?.format}, Point·Clamp) 텍셀 = 열 높이 · 프로브 위치 ≈ 균일 격자(origin.y 0.25) (Δ={dPos:0.0e0})",
                  texOk && dPos < 1e-4f);

            var savedH = a.columnHeights;
            a.columnHeights = new float[80];
            bool badLen = !a.Validate(out string e1);
            a.columnHeights = (float[])savedH.Clone();
            a.columnHeights[3] = float.NaN;
            bool badNaN = !a.Validate(out string e2);
            a.columnHeights = savedH;
            Check(sb, ref pass, ref total, $"A: Validate 음성 — 열 높이 길이 '{e1}' · NaN '{e2}'", badLen && badNaN && a.Validate(out _));
        }

        // ── B) 코어 직접 파이프라인 ──
        static void TestDirectPipeline(StringBuilder sb, ref int pass, ref int total, ProbeVolumeBaker baker, ProbeVolumeAsset a)
        {
            if (!baker.BuildSceneInput(out var tris, out var albedo, out var insts, out var countBackface))
            { Check(sb, ref pass, ref total, "B: 씬 입력", false); return; }
            var g = a.grid;
            var pos = ProbeTerrainLayers.Positions(g, a.columnHeights);
            using var bvh = new TwoLevelBVH(tris, insts, Allocator.TempJob, BVH.BuildQuality.SAH);
            using var scene = BurstScene.Create(bvh, albedo, Allocator.TempJob);
            var q = new BakeQualitySettings { MaxBounces = baker.bounces, RRStartDepth = 3, RayBias = 1e-4f };
            var sh = ProbeGrid.BakeBurstAt(scene, BurstSky.Gradient(baker.skyTop, baker.skyBottom), Sun(baker), q, pos, baker.dirCount, baker.seed);
            var valid = BurstProbeValidity.Classify(BurstProbeValidity.BackfaceRatio(scene, pos, baker.validityDirCount, countBackface), baker.validityThreshold);
            BurstProbeValidity.Dilate(g, sh, valid, baker.dilationIterations);

            float d = 0f; bool sameValid = true, samePos = true;
            var fromAsset = a.UnpackProbes();
            for (int i = 0; i < g.Count; i++)
            {
                d = Mathf.Max(d, MaxCoeffDiff(sh[i], fromAsset[i]));
                sameValid &= valid[i] == a.valid[i];
                samePos &= a.ProbePosition(i).Equals(pos[i]);
            }
            Check(sb, ref pass, ref total, $"B: 에셋 SH·valid ≡ 코어 직접(지형 위치 → BakeBurstAt → BackfaceRatio → Classify → Dilate) (Δc={d}) · ProbePosition ≡ 위치 배열",
                  d == 0f && sameValid && samePos);
        }

        // ── C) 경사 ──
        static void TestSlope(StringBuilder sb, ref int pass, ref int total, ProbeVolumeBaker baker, ProbeVolumeAsset a)
        {
            Check(sb, ref pass, ref total, $"C: 경사 BakeToAsset · Validate · layout '{a?.layoutLog}'", a != null && a.Validate(out _));
            if (a == null) return;
            var g = a.grid;
            float dH = 0f, dLayer = 0f;
            for (int k = 0; k < g.dims.z; k++)
                for (int i = 0; i < g.dims.x; i++)
                {
                    Vector3 c = g.Position(i, 0, k);
                    float h = a.columnHeights[ProbeTerrainLayers.ColumnIndex(g, i, k)];
                    dH = Mathf.Max(dH, Mathf.Abs(h - SlopeGround(c.x)));
                    Vector3 p0 = ProbeTerrainLayers.Position(g, a.columnHeights, i, 0, k);
                    Vector3 p3 = ProbeTerrainLayers.Position(g, a.columnHeights, i, 3, k);
                    dLayer = Mathf.Max(dLayer, Mathf.Max(Mathf.Abs(p0.y - h - Offset), Mathf.Abs(p3.y - h - Offset - 3f * g.spacing.y)));
                }
            var st = ProbeTerrainLayers.Measure(g, a.columnHeights);
            Check(sb, ref pass, ref total,
                  $"C: 열 높이 ≈ 12° 평면 (Δ={dH:0.0e0}) · 층 = 지면 + 0.25 + j·0.5 (Δ={dLayer:0.0e0}) · 균일 환산 {st.uniformLayers}층 → −{100f * st.Savings:0.0}%",
                  dH < 1e-3f && dLayer < 1e-5f && st.uniformLayers == 8 && Mathf.Abs(st.Savings - 0.5f) < 1e-6f
                  && a.layoutLog.Contains("균일 환산") && a.layoutLog.Contains("−50.0%"));
        }

        // ── D) 지면 메시 규칙 ──
        static void TestGroundRules(StringBuilder sb, ref int pass, ref int total, ProbeVolumeBaker baker, Mesh box, List<GameObject> roots, List<ProbeVolumeAsset> made)
        {
            var g = baker.Grid;
            var hFlat = baker.ComputeColumnHeights(g, out _, out string logFlat);

            // 수관 템플릿(지면 위 y 3)을 넣어도 지면은 정적 입력만
            baker.SetInstances(new[] { box }, new[] { new[] { Matrix4x4.TRS(new Vector3(0f, 3f, 0f), Quaternion.identity, new Vector3(6f, 0.2f, 6f)) } });
            var hCanopy = baker.ComputeColumnHeights(g, out _, out _);
            baker.templates = new ProbeVolumeBaker.TemplateInput[0];
            float dCanopy = 0f;
            for (int i = 0; i < hFlat.Length; i++) dCanopy = Mathf.Max(dCanopy, Mathf.Abs(hCanopy[i] - hFlat[i]));

            // groundRoots = 지붕(정적 입력 밖) → 그 메시 높이(윗면 y 4.1), 지붕 밖 열은 미스 → 이웃 보간
            var roof = Hidden("Roof", roots).transform;
            Child(roof, "RoofSlab", box, Matrix4x4.TRS(new Vector3(0f, 4f, 0f), Quaternion.identity, new Vector3(4.5f, 0.2f, 4.5f)));
            baker.groundRoots = new[] { roof };
            var hRoof = baker.ComputeColumnHeights(g, out int missRoof, out string logRoof);
            baker.groundRoots = new Transform[0];
            float center = hRoof[ProbeTerrainLayers.ColumnIndex(g, 4, 4)];
            Check(sb, ref pass, ref total,
                  $"D: 수관 템플릿은 지면 아님 (Δh={dCanopy}) · '{logFlat}' · groundRoots 지붕 → 중앙 열 {center:0.000}(4.1) · 지붕 밖 미스 {missRoof}열 · '{logRoof}'",
                  dCanopy == 0f && Mathf.Abs(center - 4.1f) < 1e-3f && missRoof == 81 - 25 && logRoof.StartsWith("groundRoots 1개"));
        }

        // ── E) 인스턴스 샘플 ──
        static void TestInstanceSampling(StringBuilder sb, ref int pass, ref int total, ProbeVolumeBaker baker, Mesh box, ProbeVolumeAsset a)
        {
            var mats = new[]
            {
                Matrix4x4.TRS(new Vector3(-2.6f, SlopeGround(-2.6f) + 0.3f, 1.2f), Quaternion.identity, new Vector3(0.4f, 0.8f, 0.4f)),
                Matrix4x4.TRS(new Vector3(1.9f, SlopeGround(1.9f) + 0.3f, -0.7f), Quaternion.Euler(0f, 40f, 0f), new Vector3(0.5f, 1.1f, 0.5f)),
                Matrix4x4.TRS(new Vector3(3.1f, SlopeGround(3.1f) + 0.3f, 2.8f), Quaternion.identity, new Vector3(0.3f, 0.6f, 0.3f)),
            };
            baker.SetInstances(new[] { box }, new[] { mats });
            baker.templates[0].occluder = ProbeVolumeBaker.OccluderMode.Excluded;
            var savedAsset = baker.volumeAsset;
            baker.volumeAsset = a;
            try
            {
                var got = baker.SampleInstances(0);
                baker.InstanceProbePoints(baker.templates[0], out var top, out var bottom);
                var probes = a.UnpackProbes();
                bool same = got != null && got.Length == 2 * top.Length;
                bool differs = false;
                for (int i = 0; same && i < top.Length; i++)
                {
                    var eTop = ProbeTerrainLayers.Sample(a.grid, a.columnHeights, probes, a.valid, top[i]);
                    var eBot = ProbeTerrainLayers.Sample(a.grid, a.columnHeights, probes, a.valid, bottom[i]);
                    same &= MaxCoeffDiff(got[2 * i], eTop) == 0f && MaxCoeffDiff(got[2 * i + 1], eBot) == 0f;
                    differs |= MaxCoeffDiff(eTop, a.grid.Sample(probes, a.valid, top[i])) > 1e-4f;   // 균일로 해석하면 다른 층을 읽음
                }
                Check(sb, ref pass, ref total, $"E: SampleInstances(상/하 2-프로브 × {top.Length}) ≡ 지형 추종 CPU 샘플(비트 동일) · 균일 해석과 다름", same && differs);
            }
            finally
            {
                baker.volumeAsset = savedAsset;
                baker.templates = new ProbeVolumeBaker.TemplateInput[0];
            }
        }

        // ── F) 셰이더 바인딩 ──
        static void TestBinding(StringBuilder sb, ref int pass, ref int total, ProbeVolumeBaker baker, ProbeVolumeAsset a, List<ProbeVolumeAsset> made)
        {
            var v = ProbeVolumeShaderBinding.Compute(a, 0.25f, 1f);
            var savedTex = a.heightTexture;
            a.heightTexture = null;
            var vNoTex = ProbeVolumeShaderBinding.Compute(a, 0.25f, 1f);
            a.heightTexture = savedTex;

            baker.layout = ProbeVolumeBaker.Layout.Uniform;
            var u = ProbeVolumeBakerTests.Bake(baker, made);
            baker.layout = ProbeVolumeBaker.Layout.TerrainLayers;
            var vu = ProbeVolumeShaderBinding.Compute(u, 0.25f, 1f);
            bool uniformOk = u != null && !u.IsTerrainLayers && u.heightTexture == null && u.layoutLog.StartsWith("Uniform") && vu.parameters.w == 0f && vu.parameters.x == 1f;
            Check(sb, ref pass, ref total,
                  $"F: 지형 추종 params.w = {v.parameters.w} · origin.y = {v.origin.y}(첫 층) · 높이 텍스처 없음 → 활성 {vNoTex.parameters.x} · Uniform 베이크 = 열 높이 없음·w {vu.parameters.w} · '{u?.layoutLog}'",
                  v.parameters.x == 1f && v.parameters.w == 1f && v.origin.y == a.grid.origin.y && vNoTex.parameters.x == 0f && uniformOk);
        }

        // ── G) GPU ≡ CPU ──
        static void TestGpu(StringBuilder sb, ref int pass, ref int total, ProbeVolumeAsset a)
        {
            var shader = Shader.Find(ProbeVolumeGpuSampleTests.TestShader);
            if (shader == null) { Check(sb, ref pass, ref total, $"G: 셰이더 '{ProbeVolumeGpuSampleTests.TestShader}' 없음", false); return; }
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat)) { sb.AppendLine("  [SKIP] G: ARGBFloat RT 미지원"); return; }

            var probes = ProbeVolumeGpuSampleTests.ReadBackProbes(a);   // GPU 가 보는 half 값
            var g = a.grid;
            var rng = new System.Random(41);
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var pts = new List<Vector3>(); var nrm = new List<Vector4>();
                for (int s = 0; s < 40; s++)
                {
                    pts.Add(OnTerrain(rng, a, Lerp(rng, 0f, g.dims.y - 1)));
                    nrm.Add(ProbeVolumeGpuSampleTests.Dir4(ProbeVolumeGpuSampleTests.RandDir(rng), s % 2 == 0 ? 0f : 0.4f));
                }
                ProbeVolumeGpuSampleTests.RunGroup(sb, ref pass, ref total, mat, a, probes, "G1 지형 위 임의점 40(투과 절반)", pts, nrm, 0f, 0f, 1e-2f);

                pts.Clear(); nrm.Clear();
                for (int s = 0; s < 16; s++) { pts.Add(a.ProbePosition(rng.Next(g.Count))); nrm.Add(ProbeVolumeGpuSampleTests.Dir4(ProbeVolumeGpuSampleTests.RandDir(rng), 0f)); }
                ProbeVolumeGpuSampleTests.RunGroup(sb, ref pass, ref total, mat, a, probes, "G2 프로브 위치 16", pts, nrm, 0f, 0f, 2e-3f);

                pts.Clear(); nrm.Clear();
                for (int s = 0; s < 16; s++)
                {
                    float t = s % 2 == 0 ? -Lerp(rng, 0.5f, 2f) : g.dims.y - 1 + Lerp(rng, 0.5f, 2f);   // 첫 층 아래 / 마지막 층 위
                    pts.Add(OnTerrain(rng, a, t)); nrm.Add(ProbeVolumeGpuSampleTests.Dir4(ProbeVolumeGpuSampleTests.RandDir(rng), 0f));
                }
                ProbeVolumeGpuSampleTests.RunGroup(sb, ref pass, ref total, mat, a, probes, "G3 층 위/아래 클램프 16", pts, nrm, 0f, 0f, 1e-2f);
                ProbeVolumeGpuSampleTests.RunGroup(sb, ref pass, ref total, mat, a, probes, "G4 같은 점 · 페이드 1셀 + normalBias 0.3셀 16", pts, nrm, 0.3f, 1f, 1e-2f);
            }
            finally { Object.DestroyImmediate(mat); }
        }

        // 지형 위 임의 XZ, 층 좌표 t(연속)인 점 = (x, H + 첫 층 + t·간격, z)
        static Vector3 OnTerrain(System.Random rng, ProbeVolumeAsset a, float t)
        {
            var g = a.grid;
            float x = g.origin.x + Lerp(rng, 0f, g.dims.x - 1) * g.spacing.x;
            float z = g.origin.z + Lerp(rng, 0f, g.dims.z - 1) * g.spacing.z;
            float h = ProbeTerrainLayers.ColumnHeight(g, a.columnHeights, x, z);
            return new Vector3(x, h + g.origin.y + t * g.spacing.y, z);
        }

        // ───────────────────────── 헬퍼 ─────────────────────────

        static DirectionalLight Sun(ProbeVolumeBaker b)
        {
            var c = b.sunColor.linear;
            return new DirectionalLight { Direction = b.sunDirection.normalized, Color = new Vector3(c.r, c.g, c.b), Intensity = b.sunIntensity };
        }

        static GameObject Hidden(string name, List<GameObject> roots)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            roots.Add(go);
            return go;
        }

        // 월드 TRS 의 박스 MeshFilter + MeshRenderer(머티리얼 없음 → 기본 알베도)
        static MeshFilter Child(Transform parent, string name, Mesh mesh, Matrix4x4 trs)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(parent, false);
            go.transform.position = trs.GetColumn(3);
            go.transform.rotation = trs.rotation;
            go.transform.localScale = trs.lossyScale;
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            go.AddComponent<MeshRenderer>();
            return mf;
        }
    }
}
