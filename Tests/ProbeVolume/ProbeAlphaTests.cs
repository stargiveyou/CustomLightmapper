using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using UnityEngine;
using static HuskyLibs.CustomLightmapper.Bake.ProbeGridTests;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Track B B1-6 검증: 알파 잎 차폐 — 패키지 α any-hit 가 프로브 SH·유효성·정점 AO 베이크에 연결됐는지.
    /// 씬 = 지면(메시 0, 위 노멀) + 캐노피 카드(메시 1, 아래 노멀, UV = 카드 평면 [0,1]²) — 알파 데이터는 손으로 조립(마스크 3종).
    ///  A) ᾱ=1 회귀: 전부 불투명 마스크(알파 켜짐 — any-hit 순회 경로) ≡ 알파 없음 — SH(1바운스·다바운스)·뒷면 비율 비트 동일.
    ///  B) ᾱ=0 ≡ 메시 제거: 전부 투명 마스크 = 카드를 뺀 씬과 SH(1·다바운스)·뒷면 비율 비트 동일(카드는 마지막 BLAS·인스턴스).
    ///  C) 체커(ᾱ≈0.5): 검은 카드 아래 E(up) — 불투명 &lt; 체커 &lt; 제거, 비율 (Ec−Eo)/(Ee−Eo) ∈ [0.3, 0.7].
    ///  D) 정점 AO(<see cref="VertexAOBake.Compute"/> alpha): 지면+캐노피 한 메시(서브메시 2) — 불투명 마스크 ≡ 알파 없음(비트 동일) ·
    ///     투명 → 지면 AO = 1 · 체커 → 지면 AO 비율 ∈ [0.3, 0.7].
    ///  E) 베이커(머티리얼 경로 — <see cref="AlphaMaskBuilder"/> 가 텍스처 알파를 읽음, 그래픽 디바이스 필요): alphaCutout on/off ·
    ///     머티리얼 없는 템플릿은 off 와 비트 동일 · 캐노피 아래 E(up) 증가 · 정점 AO 엔트리 alphaCutout + 지면 AO 증가.
    ///  F) GPU: 체커 씬 BakeGpu ≡ BakeBurst(1바운스, mean 게이트 — ProbeGridTests F 기준). compute 미지원 → SKIP.
    /// 호출: Debug.Log(ProbeAlphaTests.RunAll());
    /// </summary>
    public static class ProbeAlphaTests
    {
        enum Mask { Opaque, Transparent, Checker }

        const float CardHalf = 3f, CardHeight = 1.6f, GroundHalf = 6f;

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Track B 알파 잎 차폐 (B1-6) ===");
            int pass = 0, total = 0;
            TestOpaqueRegression(sb, ref pass, ref total);
            TestTransparentEqualsRemoved(sb, ref pass, ref total);
            TestCheckerHalf(sb, ref pass, ref total);
            TestVertexAO(sb, ref pass, ref total);
            TestBakerMaterialPath(sb, ref pass, ref total);
            TestGpu(sb, ref pass, ref total);
            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // y 0.4..2.15 — 아래 4층은 지면·카드 사이, 위 2층(1.8·2.15)은 카드 위(카드 뒷면을 봄 → 뒷면 비율이 마스크에 따라 달라짐)
        static ProbeGrid UnderCanopyGrid() => new ProbeGrid(new Vector3(-1.37f, 0.4f, -1.21f), new Vector3(0.9f, 0.35f, 0.83f), new Vector3Int(4, 6, 4));
        static DirectionalLight Sun() => new DirectionalLight { Direction = new Vector3(-0.2f, -1f, -0.1f).normalized, Color = Vector3.one, Intensity = 1.3f };
        static BurstSky Sky() => BurstSky.Gradient(new Vector3(0.5f, 0.7f, 1.0f), new Vector3(0.1f, 0.1f, 0.12f));
        static BakeQualitySettings Q(int bounces) => new BakeQualitySettings { MaxBounces = bounces, RRStartDepth = 3, RayBias = 1e-4f };

        // ── A) ᾱ=1 회귀 ──
        static void TestOpaqueRegression(StringBuilder sb, ref int pass, ref int total)
        {
            var meshes = new[] { Quad(GroundHalf, 0f), CardDown(CardHalf, CardHeight) };
            var insts = Identity(2);
            var albedo = new[] { new Vector3(0.5f, 0.5f, 0.45f), new Vector3(0.3f, 0.6f, 0.25f) };
            var alpha = CardAlpha(meshes, insts, 1, Mask.Opaque, 16);
            var g = UnderCanopyGrid();

            Bake(meshes, insts, albedo, null, g, out var sh1, out var shM, out var ratio);
            Bake(meshes, insts, albedo, alpha, g, out var sh1a, out var shMa, out var ratioA);
            Bake(new[] { meshes[0] }, Identity(1), new[] { albedo[0] }, null, g, out var sh1r, out _, out _);

            Check(sb, ref pass, ref total, "A: 불투명 마스크(알파 순회) ≡ 알파 없음 — SH 1바운스·다바운스(3)·뒷면 비율 비트 동일",
                  SameSH(sh1, sh1a) && SameSH(shM, shMa) && SameFloats(ratio, ratioA));
            bool backSeen = false;
            foreach (float r in ratio) backSeen |= r > 0.2f;
            Check(sb, ref pass, ref total, "A: (비자명성) 카드가 SH 를 바꾸고 · 카드 위 프로브는 뒷면 비율 > 0.2", !SameSH(sh1, sh1r) && backSeen);
        }

        // ── B) ᾱ=0 ≡ 메시 제거 ──
        static void TestTransparentEqualsRemoved(StringBuilder sb, ref int pass, ref int total)
        {
            var meshes = new[] { Quad(GroundHalf, 0f), CardDown(CardHalf, CardHeight) };
            var albedo = new[] { new Vector3(0.5f, 0.5f, 0.45f), new Vector3(0.3f, 0.6f, 0.25f) };
            var alpha = CardAlpha(meshes, Identity(2), 1, Mask.Transparent, 16);
            var g = UnderCanopyGrid();

            Bake(meshes, Identity(2), albedo, alpha, g, out var sh1a, out var shMa, out var ratioA);
            Bake(new[] { meshes[0] }, Identity(1), new[] { albedo[0] }, null, g, out var sh1r, out var shMr, out var ratioR);
            Check(sb, ref pass, ref total, "B: 전부 투명 마스크 ≡ 카드 제거 — SH 1바운스·다바운스(3)·뒷면 비율 비트 동일",
                  SameSH(sh1a, sh1r) && SameSH(shMa, shMr) && SameFloats(ratioA, ratioR));
        }

        // ── C) 체커 ᾱ≈0.5 ──
        static void TestCheckerHalf(StringBuilder sb, ref int pass, ref int total)
        {
            // 검은(알베도 0) 큰 카드 하나 + 균일 하늘, 태양 없음 → 위쪽 반구 = (투명 텍셀 비율) × 하늘
            var meshes = new[] { CardDown(20f, 1f) };
            var albedo = new[] { Vector3.zero };
            var g = new ProbeGrid(new Vector3(-0.77f, -0.3f, -0.61f), new Vector3(0.51f, 0.3f, 0.43f), new Vector3Int(4, 3, 4));
            var sky = BurstSky.Uniform(Vector3.one);
            float eo = MeanUp(meshes, albedo, CardAlpha(meshes, Identity(1), 0, Mask.Opaque, 256), g, sky);
            float ec = MeanUp(meshes, albedo, CardAlpha(meshes, Identity(1), 0, Mask.Checker, 256), g, sky);
            float ee = MeanUp(meshes, albedo, CardAlpha(meshes, Identity(1), 0, Mask.Transparent, 256), g, sky);
            float r = (ec - eo) / Mathf.Max(1e-6f, ee - eo);
            Check(sb, ref pass, ref total, $"C: 체커 카드 아래 E(up) 불투명 {eo:F3} < 체커 {ec:F3} < 투명 {ee:F3}(≈π) · 비율 {r:F3} ∈ [0.3, 0.7]",
                  eo < ec && ec < ee && r >= 0.3f && r <= 0.7f && Mathf.Abs(ee - Mathf.PI) < 0.05f);
        }

        // ── D) 정점 AO ──
        static void TestVertexAO(StringBuilder sb, ref int pass, ref int total)
        {
            var mesh = FloorCanopyMesh();
            try
            {
                var s = new VertexAOBake.Settings { samples = 256, maxDistanceRelative = 0f, surfaceBiasRelative = 1e-4f, seed = 777u };
                VertexAOBake.Compute(mesh, s, false, out var f0, out _);
                VertexAOBake.Compute(mesh, s, false, out var fo, out _, FloorCanopyAlpha(Mask.Opaque));
                VertexAOBake.Compute(mesh, s, false, out var ft, out _, FloorCanopyAlpha(Mask.Transparent));
                VertexAOBake.Compute(mesh, s, false, out var fc, out _, FloorCanopyAlpha(Mask.Checker));

                bool transOne = true;
                for (int i = 0; i < 4; i++) transOne &= ft[i] == 1f;
                float ao = Mean(fo, 4), ac = Mean(fc, 4);
                float r = (ac - ao) / Mathf.Max(1e-6f, 1f - ao);
                Check(sb, ref pass, ref total, "D: 정점 AO 불투명 마스크 ≡ 알파 없음(비트 동일, 8정점)", SameFloats(f0, fo));
                Check(sb, ref pass, ref total, $"D: 캐노피 투명 → 지면 AO = 1 · 불투명 지면 AO {ao:F3} < 0.2", transOne && ao < 0.2f);
                Check(sb, ref pass, ref total, $"D: 체커 캐노피 → 지면 AO {ac:F3}, 비율 {r:F3} ∈ [0.3, 0.7]", r >= 0.3f && r <= 0.7f);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        // ── E) 베이커 머티리얼 경로 ──
        static void TestBakerMaterialPath(StringBuilder sb, ref int pass, ref int total)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            { sb.AppendLine("  [SKIP] E: 그래픽 디바이스 없음 — AlphaMaskBuilder 가 텍스처 알파를 읽을 수 없음"); return; }
            // 내장 Unlit/Transparent Cutout 우선(파이프라인 무관하게 컴파일됨) → 없으면 식생 셰이더
            var shader = Shader.Find("Unlit/Transparent Cutout") ?? Shader.Find(ProbeVolumeShaderBinding.VegetationShaderName);
            if (shader == null) { sb.AppendLine("  [SKIP] E: 컷아웃 셰이더(Unlit/Transparent Cutout / InstancedVegetation_URP) 없음"); return; }

            var go = new GameObject("ProbeAlphaTests") { hideFlags = HideFlags.HideAndDontSave };
            var mesh = FloorCanopyMesh();
            var tex = CheckerTexture(64);
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            foreach (var prop in new[] { "_BaseMap", "_MainTex" }) if (mat.HasProperty(prop)) mat.SetTexture(prop, tex);
            if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", 0.5f);
            if (!mat.HasProperty("_BaseMap") && !mat.HasProperty("_MainTex"))
            {
                sb.AppendLine($"  [SKIP] E: 셰이더 '{shader.name}' 에 _BaseMap/_MainTex 없음");
                Object.DestroyImmediate(go); Object.DestroyImmediate(mesh); Object.DestroyImmediate(mat); Object.DestroyImmediate(tex);
                return;
            }
            var made = new List<ProbeVolumeAsset>();
            try
            {
                var baker = go.AddComponent<ProbeVolumeBaker>();
                baker.renderOnEnable = false;
                go.transform.position = new Vector3(0f, 0.5f, 0f);
                baker.volumeSize = new Vector3(2f, 0.6f, 2f);       // y 0.2..0.8 — 지면(0)과 캐노피(1, ×2 스케일) 사이
                baker.probeSpacing = new Vector3(0.5f, 0.3f, 0.5f);
                baker.dirCount = 128;
                baker.bounces = 1;
                baker.sunDirection = new Vector3(-0.2f, -1f, -0.1f);
                baker.alphaForceCutout = new[] { mat };             // 셰이더 판별과 무관하게 컷아웃
                baker.vertexAOSettings = new VertexAOBake.Settings { samples = 128, maxDistanceRelative = 0f, surfaceBiasRelative = 1e-4f, seed = 3u };
                baker.SetInstances(new[] { mesh }, new[] { new[] { Matrix4x4.Scale(new Vector3(2f, 2f, 2f)) } }, new[] { new Color(0.5f, 0.5f, 0.45f) });
                baker.templates[0].twoSided = true;
                baker.templates[0].alphaMaterials = new Material[] { null, mat };   // 서브메시 0 지면 불투명 · 1 캐노피 컷아웃

                baker.alphaCutout = false;
                var off = BakeAsset(baker, made);
                baker.alphaCutout = true;
                var on = BakeAsset(baker, made);
                var mats = baker.templates[0].alphaMaterials;
                baker.templates[0].alphaMaterials = null;              // 머티리얼 없음 → 알파 Disabled
                var noMat = BakeAsset(baker, made);
                baker.templates[0].alphaMaterials = mats;

                if (off == null || on == null || noMat == null) { Check(sb, ref pass, ref total, "E: BakeToAsset 3회", false); return; }
                Check(sb, ref pass, ref total, $"E: alphaCutout on → 에셋 alphaCutout={on.alphaCutout} · off={off.alphaCutout} (로그: {on.alphaLog?.Split('\n')[0]})",
                      on.alphaCutout && !off.alphaCutout);
                Check(sb, ref pass, ref total, "E: 머티리얼 없는 템플릿(alphaCutout on) ≡ off — 비트 동일 · alphaCutout=false",
                      !noMat.alphaCutout && SamePacked(off, noMat));
                float eOff = MeanUp(off), eOn = MeanUp(on);
                Check(sb, ref pass, ref total, $"E: 캐노피 아래 E(up) off {eOff:F3} < on {eOn:F3}(잎 구멍으로 하늘·태양)", eOn > eOff);

                var aoOff = FloorAO(off, mesh); var aoOn = FloorAO(on, mesh);
                bool entry = on.vertexAO != null && on.vertexAO.Length == 1 && on.vertexAO[0].alphaCutout
                          && off.vertexAO != null && off.vertexAO.Length == 1 && !off.vertexAO[0].alphaCutout;
                Check(sb, ref pass, ref total, $"E: 정점 AO 엔트리 alphaCutout · 지면 AO off {aoOff:F3} → on {aoOn:F3}(+0.2 이상)", entry && aoOn > aoOff + 0.2f);
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(mat);
                Object.DestroyImmediate(tex);
                foreach (var a in made) DestroyAsset(a);
            }
        }

        // ── F) GPU ──
        static void TestGpu(StringBuilder sb, ref int pass, ref int total)
        {
            if (!SystemInfo.supportsComputeShaders) { sb.AppendLine("  [SKIP] F: compute shader 미지원 — GPU 알파 SH 베이크 검증 불가"); return; }
            var cs = PathTraceShader.Resolve();
            var q = Q(1);
            string kn = ProbeGrid.GpuKernelName(q);
            if (cs == null || !cs.HasKernel(kn)) { Check(sb, ref pass, ref total, $"F: PathTrace.compute 로드/커널({kn}) 실패", false); return; }

            var meshes = new[] { Quad(GroundHalf, 0f), CardDown(CardHalf, CardHeight) };
            var insts = Identity(2);
            var albedo = new[] { new Vector3(0.5f, 0.5f, 0.45f), new Vector3(0.3f, 0.6f, 0.25f) };
            var alpha = CardAlpha(meshes, insts, 1, Mask.Checker, 64);
            var g = UnderCanopyGrid();
            using var bvh = new TwoLevelBVH(meshes, insts, Allocator.TempJob, BVH.BuildQuality.SAH);
            using var scene = BurstScene.Create(bvh, albedo, alpha, Allocator.TempJob);
            using var gpu = new GpuScene(scene);
            var gpuSH = g.BakeGpu(gpu, cs, cs.FindKernel(kn), Sun(), Sky(), q, 256, 1u);
            var cpuSH = g.BakeBurst(scene, Sky(), Sun(), q, 256, 1u);
            using var bvhOff = new TwoLevelBVH(meshes, insts, Allocator.TempJob, BVH.BuildQuality.SAH);
            using var sceneOff = BurstScene.Create(bvhOff, albedo, Allocator.TempJob);
            var offSH = g.BakeBurst(sceneOff, Sky(), Sun(), q, 256, 1u);

            float mean = MeanAbsCoeff(gpuSH, cpuSH), effect = MeanAbsCoeff(cpuSH, offSH);
            Check(sb, ref pass, ref total, $"F: GPU 알파 SH ≡ Burst (n={g.Count}, mean={mean:E2}) · 알파 효과 mean {effect:E2} ≫ 오차",
                  mean < 1e-3f && effect > 10f * mean);
        }

        // ───────────────────────── 헬퍼 ─────────────────────────

        static void Bake(Tri[][] meshes, TwoLevelBVH.Instance[] insts, Vector3[] albedo, AlphaSceneData alpha, in ProbeGrid g,
                         out SH9[] sh1, out SH9[] shMulti, out float[] ratio)
        {
            using var bvh = new TwoLevelBVH(meshes, insts, Allocator.TempJob, BVH.BuildQuality.SAH);
            using var scene = BurstScene.Create(bvh, albedo, alpha, Allocator.TempJob);
            sh1 = g.BakeBurst(scene, Sky(), Sun(), Q(1), 64, 7u);
            shMulti = g.BakeBurst(scene, Sky(), Sun(), Q(3), 64, 7u);
            ratio = BurstProbeValidity.BackfaceRatio(scene, g.Positions(), 64);
        }

        static float MeanUp(Tri[][] meshes, Vector3[] albedo, AlphaSceneData alpha, in ProbeGrid g, BurstSky sky)
        {
            using var bvh = new TwoLevelBVH(meshes, Identity(meshes.Length), Allocator.TempJob, BVH.BuildQuality.SAH);
            using var scene = BurstScene.Create(bvh, albedo, alpha, Allocator.TempJob);
            var sh = g.BakeBurst(scene, sky, default, Q(1), 512, 1u);
            double s = 0;
            foreach (var p in sh) s += p.Evaluate(Vector3.up).x;
            return (float)(s / sh.Length);
        }

        static float MeanUp(ProbeVolumeAsset a)
        {
            var sh = a.UnpackProbes();
            double s = 0;
            foreach (var p in sh) s += p.Evaluate(Vector3.up).x;
            return (float)(s / sh.Length);
        }

        // 에셋 AO 메시 uv3.x 의 지면 정점(0..3) 평균
        static float FloorAO(ProbeVolumeAsset a, Mesh source)
        {
            var ao = a.FindAOMesh(source);
            if (ao == null) return -1f;
            var uv = new List<Vector2>();
            ao.GetUVs(VertexAOBake.UVChannel, uv);
            if (uv.Count < 4) return -1f;
            return (uv[0].x + uv[1].x + uv[2].x + uv[3].x) * 0.25f;
        }

        static ProbeVolumeAsset BakeAsset(ProbeVolumeBaker baker, List<ProbeVolumeAsset> made)
        {
            var a = baker.BakeToAsset(out _);
            if (a != null) made.Add(a);
            return a;
        }

        static void DestroyAsset(ProbeVolumeAsset a)
        {
            if (a == null) return;
            if (a.textures != null) foreach (var t in a.textures) if (t != null) Object.DestroyImmediate(t);
            if (a.vertexAO != null) foreach (var e in a.vertexAO) if (e?.aoMesh != null) Object.DestroyImmediate(e.aoMesh);
            Object.DestroyImmediate(a);
        }

        static TwoLevelBVH.Instance[] Identity(int n)
        {
            var r = new TwoLevelBVH.Instance[n];
            for (int i = 0; i < n; i++) r[i] = new TwoLevelBVH.Instance { MeshIndex = i, LocalToWorld = Matrix4x4.identity };
            return r;
        }

        // y=h, ±a 정사각 2삼각형, cross(v1-v0, v2-v0) = -Y(아래). UV = ((x+a)/2a, (z+a)/2a) — CardAlpha 의 TriUV 와 같은 정점 순서.
        static Tri[] CardDown(float a, float h) => new[]
        {
            new Tri { V0 = new Vector3(-a, h, -a), V1 = new Vector3(a, h, -a), V2 = new Vector3(a, h, a) },
            new Tri { V0 = new Vector3(-a, h, -a), V1 = new Vector3(a, h, a),  V2 = new Vector3(-a, h, a) },
        };

        static readonly TriUV[] CardUV =
        {
            new TriUV { UV0 = new Vector2(0f, 0f), UV1 = new Vector2(1f, 0f), UV2 = new Vector2(1f, 1f) },
            new TriUV { UV0 = new Vector2(0f, 0f), UV1 = new Vector2(1f, 1f), UV2 = new Vector2(0f, 1f) },
        };

        /// <summary>
        /// 메시 cardMesh(2삼각형 = CardDown)만 컷아웃(matId 0), 나머지 불투명. 인스턴스당 슬롯 1개(서브메시 0).
        /// Opaque = 비트 전부 1(any-hit 경로를 타지만 결과는 불투명) · Transparent = 전부 0 · Checker = (x+y) 짝수 칸 불투명.
        /// </summary>
        static AlphaSceneData CardAlpha(Tri[][] meshes, TwoLevelBVH.Instance[] insts, int cardMesh, Mask mask, int m)
        {
            var start = new int[meshes.Length];
            int totalTri = 0;
            for (int i = 0; i < meshes.Length; i++) { start[i] = totalTri; totalTri += meshes[i].Length; }
            var triUV = new TriUV[totalTri];
            triUV[start[cardMesh]] = CardUV[0];
            triUV[start[cardMesh] + 1] = CardUV[1];
            var hasCut = new byte[meshes.Length];
            hasCut[cardMesh] = 1;
            var matBase = new int[insts.Length];
            var slot = new int[insts.Length];
            for (int i = 0; i < insts.Length; i++) { matBase[i] = i; slot[i] = insts[i].MeshIndex == cardMesh ? 0 : -1; }
            return new AlphaSceneData
            {
                Enabled = true,
                TriUV = triUV,
                TriSubmesh = new byte[totalTri],
                MeshHasCutout = hasCut,
                MeshTriStart = start,
                InstMatBase = matBase,
                MatSlot = slot,
                MaskBits = MaskBits(mask, m),
                MaskWord = new[] { 0 },
                MaskW = new[] { m },
                MaskH = new[] { m },
                MaskST = new[] { new Vector4(1f, 1f, 0f, 0f) },
            };
        }

        static uint[] MaskBits(Mask mask, int m)
        {
            var bits = new uint[(m * m + 31) / 32];
            for (int y = 0; y < m; y++)
                for (int x = 0; x < m; x++)
                {
                    bool opaque = mask == Mask.Opaque || (mask == Mask.Checker && ((x + y) & 1) == 0);
                    if (!opaque) continue;
                    int bit = y * m + x;
                    bits[bit >> 5] |= 1u << (bit & 31);
                }
            return bits;
        }

        // 서브메시 0 = 지면(정점 0..3, y=0, ±1, 위 노멀) · 서브메시 1 = 캐노피(정점 4..7, y=0.5, ±3, 아래 노멀, UV = 카드 평면)
        static Mesh FloorCanopyMesh()
        {
            var m = new Mesh { name = "FloorCanopy", hideFlags = HideFlags.HideAndDontSave };
            m.vertices = new[]
            {
                new Vector3(-1f, 0f, -1f), new Vector3(-1f, 0f, 1f), new Vector3(1f, 0f, 1f), new Vector3(1f, 0f, -1f),
                new Vector3(-3f, 0.5f, -3f), new Vector3(3f, 0.5f, -3f), new Vector3(3f, 0.5f, 3f), new Vector3(-3f, 0.5f, 3f),
            };
            m.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.down, Vector3.down, Vector3.down, Vector3.down };
            m.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f),
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            };
            m.subMeshCount = 2;
            m.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            m.SetTriangles(new[] { 4, 5, 6, 4, 6, 7 }, 1);
            return m;
        }

        // FloorCanopyMesh 단독 씬(메시 0 · 인스턴스 0) — 삼각형 2·3 이 캐노피(서브메시 1 → 슬롯 1 = matId 0)
        static AlphaSceneData FloorCanopyAlpha(Mask mask)
        {
            const int M = 64;
            return new AlphaSceneData
            {
                Enabled = true,
                TriUV = new[] { default, default, CardUV[0], CardUV[1] },
                TriSubmesh = new byte[] { 0, 0, 1, 1 },
                MeshHasCutout = new byte[] { 1 },
                MeshTriStart = new[] { 0 },
                InstMatBase = new[] { 0 },
                MatSlot = new[] { -1, 0 },
                MaskBits = MaskBits(mask, M),
                MaskWord = new[] { 0 },
                MaskW = new[] { M },
                MaskH = new[] { M },
                MaskST = new[] { new Vector4(1f, 1f, 0f, 0f) },
            };
        }

        // 1텍셀 체커 알파(짝수 칸 불투명) — Point 필터라 AlphaMaskBuilder 의 1:1 읽기와 같은 비트
        static Texture2D CheckerTexture(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false, true)
            { name = "AlphaChecker", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(((x + y) & 1) == 0 ? 255 : 0));
            t.SetPixels32(px);
            t.Apply(false, false);
            return t;
        }

        static bool SameSH(SH9[] a, SH9[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (MaxCoeffDiff(a[i], b[i]) != 0f) return false;
            return true;
        }

        static bool SameFloats(float[] a, float[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static bool SamePacked(ProbeVolumeAsset x, ProbeVolumeAsset y)
        {
            if (x.shPacked.Length != y.shPacked.Length) return false;
            for (int i = 0; i < x.shPacked.Length; i++)   // 성분별 정확 비교(Vector4 != 는 ε 근사)
            {
                Vector4 a = x.shPacked[i], b = y.shPacked[i];
                if (a.x != b.x || a.y != b.y || a.z != b.z || a.w != b.w) return false;
            }
            return true;
        }

        static float Mean(float[] a, int n)
        {
            float s = 0f;
            for (int i = 0; i < n; i++) s += a[i];
            return s / n;
        }

        static float MeanAbsCoeff(SH9[] a, SH9[] b)
        {
            double sum = 0;
            for (int i = 0; i < a.Length; i++)
                for (int k = 0; k < SH9.Count; k++)
                {
                    Vector3 d = Coeff(a[i], k) - Coeff(b[i], k);
                    sum += Mathf.Abs(d.x) + Mathf.Abs(d.y) + Mathf.Abs(d.z);
                }
            return (float)(sum / (a.Length * SH9.Count * 3));
        }
    }
}
