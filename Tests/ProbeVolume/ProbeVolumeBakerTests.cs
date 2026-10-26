using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using UnityEngine;
using static HuskyLibs.CustomLightmapper.Bake.ProbeGridTests;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Track B B1-3 검증: <see cref="ProbeVolumeBaker"/>(메모리 베이크 — 디스크 저장 없음) + <see cref="ProbeVolumeAsset"/>.
    ///  A) 에셋 구조: Validate · 격자 = 컴포넌트 Grid · 패킹/valid 길이 · Texture3D×7(포맷·크기·Clamp·Bilinear) · 무효 → dilation 수 · 값 유한.
    ///  B) 씬 입력(Excluded 제외·twoSided → countBackface) + 에셋 SH·valid ≡ 코어 직접 파이프라인
    ///     (BakeBurst → ComputeGrid → Dilate, 비트 동일).
    ///  C) 결정 D5 토글: Excluded 템플릿은 베이크에 무영향(목록에서 뺀 것과 비트 동일), Opaque 로 바꾸면 값이 바뀜.
    ///  D) Texture3D 텍셀 ≡ 패킹 계수(half 정밀도) — 텍셀 순서 = 격자 선형 인덱스.
    ///  E) 인스턴스 적용: 템플릿 단위 대표점 ≡ 씬 버전 ComputeProbePoints(비트 동일) · SampleInstances ≡ CPU Sample(valid 가중) ·
    ///     인스턴스를 옮기면 재베이크 없이 새 위치 샘플(에셋 불변).
    ///  F) 수렴: 간격 2 → 1 → 0.5 마다 '같은 점 직접 베이크(SH 트랙)' 대비 조도 오차 단조 감소.
    ///  G) EXR 사본: 레이아웃(타일·줄바꿈) · 픽셀 ≡ 패킹 · 매직.
    ///  H) 표시: 에셋 → render 템플릿 수만큼 렌더러(URP 셰이더 있을 때) · Validate 음성.
    ///  I) B1-4: 폴백 SH = 빈 씬(하늘만) 베이크 · PerPixelVolume 템플릿 → SH 버퍼 없는 렌더러 + MPB 볼륨 바인딩.
    /// 임시 GameObject·메시·에셋(텍스처 포함)은 끝에 파괴. 호출: Debug.Log(ProbeVolumeBakerTests.RunAll());
    /// </summary>
    public static class ProbeVolumeBakerTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Track B ProbeVolumeBaker (B1-3) ===");
            int pass = 0, total = 0;

            var go = new GameObject("ProbeVolumeBakerTests") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.position = new Vector3(0f, 2.1f, 0f);   // 볼륨 y 0.1..4.1 → 지면(y=0)·간격 0.5/1/2 층이 면과 겹치지 않음
            var box = InstanceSceneBakerTests.MakeBoxMesh();
            var made = new List<ProbeVolumeAsset>();
            try
            {
                var baker = go.AddComponent<ProbeVolumeBaker>();
                baker.renderOnEnable = false;
                baker.volumeSize = new Vector3(8f, 4f, 8f);
                baker.probeSpacing = Vector3.one;
                baker.dirCount = 64;
                baker.bounces = 1;
                baker.sunDirection = new Vector3(-0.3f, -1f, -0.2f);
                baker.sunIntensity = 1.3f;
                SetupTemplates(baker, box);

                var a = Bake(baker, made);
                TestStructure(sb, ref pass, ref total, baker, a);
                if (a != null)
                {
                    TestDirectPipeline(sb, ref pass, ref total, baker, a);
                    TestExcludedToggle(sb, ref pass, ref total, baker, a, made);
                    TestTextures(sb, ref pass, ref total, a);
                    TestInstanceSampling(sb, ref pass, ref total, baker, a, box);
                    TestExr(sb, ref pass, ref total, a);
                    TestRender(sb, ref pass, ref total, baker, a);
                    TestPerPixel(sb, ref pass, ref total, baker, a);
                }
                TestConvergence(sb, ref pass, ref total, baker, box, made);
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(box);
                foreach (var a in made) DestroyAsset(a);
            }

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // 템플릿 0: 줄기 박스 3개(하나는 프로브 (0,0.1,0)·(0,1.1,0) 을 품음 → 무효) / 1: 지면 슬래브(윗면 y=0) / 2: 잎 카드(Excluded, 표시만)
        static void SetupTemplates(ProbeVolumeBaker baker, Mesh box)
        {
            var mats = new[]
            {
                new[]
                {
                    Matrix4x4.TRS(new Vector3(0f, 0.5f, 0f), Quaternion.identity, new Vector3(1.2f, 1.4f, 1.2f)),
                    Matrix4x4.TRS(new Vector3(-2.3f, 0.8f, 1.7f), Quaternion.Euler(0f, 30f, 0f), new Vector3(0.5f, 1.6f, 0.5f)),
                    Matrix4x4.TRS(new Vector3(2.2f, 0.6f, -1.6f), Quaternion.Euler(0f, 70f, 0f), new Vector3(0.6f, 1.2f, 0.6f)),
                },
                new[] { Matrix4x4.TRS(new Vector3(0f, -0.05f, 0f), Quaternion.identity, new Vector3(12f, 0.1f, 12f)) },
                new[]
                {
                    Matrix4x4.TRS(new Vector3(-2.3f, 1.9f, 1.7f), Quaternion.identity, new Vector3(1.5f, 0.05f, 1.5f)),
                    Matrix4x4.TRS(new Vector3(2.2f, 1.5f, -1.6f), Quaternion.identity, new Vector3(1.5f, 0.05f, 1.5f)),
                },
            };
            baker.SetInstances(new[] { box, box, box }, mats,
                new[] { new Color(0.45f, 0.3f, 0.2f), new Color(0.5f, 0.5f, 0.45f), new Color(0.3f, 0.6f, 0.25f) });
            baker.templates[2].occluder = ProbeVolumeBaker.OccluderMode.Excluded;
            baker.templates[2].twoSided = true;
        }

        // ── A) 에셋 구조 ──
        static void TestStructure(StringBuilder sb, ref int pass, ref int total, ProbeVolumeBaker baker, ProbeVolumeAsset a)
        {
            Check(sb, ref pass, ref total, $"A: BakeToAsset · Validate (backend={a?.backend})", a != null && a.Validate(out _));
            if (a == null) return;
            var g = baker.Grid;
            Check(sb, ref pass, ref total, $"A: 격자 {a.grid.dims} = 컴포넌트 Grid {g.dims} (9,5,9) · 패킹 {a.shPacked.Length}=프로브×7 · valid {a.valid.Length}",
                a.grid.dims == g.dims && g.dims == new Vector3Int(9, 5, 9) && (a.grid.origin - g.origin).sqrMagnitude < 1e-10f
                && a.shPacked.Length == g.Count * 7 && a.valid.Length == g.Count);

            bool texOk = a.textures != null && a.textures.Length == ProbeVolumeAsset.TextureCount;
            if (texOk)
                foreach (var t in a.textures)
                    texOk &= t != null && t.width == g.dims.x && t.height == g.dims.y && t.depth == g.dims.z
                          && (t.format == TextureFormat.RGBAHalf || t.format == TextureFormat.RGBAFloat)
                          && t.wrapMode == TextureWrapMode.Clamp && t.filterMode == FilterMode.Bilinear;
            Check(sb, ref pass, ref total, $"A: Texture3D×7 ({(a.textures != null && a.textures.Length > 0 && a.textures[0] != null ? a.textures[0].format.ToString() : "-")}, {g.dims.x}×{g.dims.y}×{g.dims.z}, Clamp·Bilinear)", texOk);

            bool finite = true;
            foreach (var v in a.shPacked) finite &= !float.IsNaN(v.x + v.y + v.z + v.w) && !float.IsInfinity(v.x + v.y + v.z + v.w);
            Check(sb, ref pass, ref total, $"A: 줄기 내부 프로브 무효 {a.invalidCount}(≥2) → dilation {a.dilatedCount} · 값 유한",
                a.validityComputed && a.invalidCount >= 2 && a.dilatedCount == a.invalidCount && finite);
        }

        // ── B) 씬 입력 + 코어 직접 파이프라인 ──
        static void TestDirectPipeline(StringBuilder sb, ref int pass, ref int total, ProbeVolumeBaker baker, ProbeVolumeAsset a)
        {
            bool okIn = baker.BuildSceneInput(out var tris, out var albedo, out var insts, out var countBackface);
            Check(sb, ref pass, ref total, $"B: 씬 입력 — Excluded 제외: 메시 {tris?.Length}(2) · 인스턴스 {insts?.Length}(3+1) · countBackface 전부 true",
                okIn && tris.Length == 2 && insts.Length == 4 && albedo.Length == 2 && countBackface.Length == 2 && countBackface[0] && countBackface[1]);
            if (!okIn) return;

            var g = baker.Grid;
            using var bvh = new TwoLevelBVH(tris, insts, Allocator.TempJob, BVH.BuildQuality.SAH);
            using var scene = BurstScene.Create(bvh, albedo, Allocator.TempJob);
            var q = new BakeQualitySettings { MaxBounces = baker.bounces, RRStartDepth = 3, RayBias = 1e-4f };
            var sh = g.BakeBurst(scene, BurstSky.Gradient(baker.skyTop, baker.skyBottom), Sun(baker), q, baker.dirCount, baker.seed);
            var valid = BurstProbeValidity.ComputeGrid(scene, g, out _, baker.validityDirCount, baker.validityThreshold, countBackface);
            BurstProbeValidity.Dilate(g, sh, valid, baker.dilationIterations);

            float d = 0f; bool sameValid = true;
            var fromAsset = a.UnpackProbes();
            for (int i = 0; i < g.Count; i++) { d = Mathf.Max(d, MaxCoeffDiff(sh[i], fromAsset[i])); sameValid &= valid[i] == a.valid[i]; }
            Check(sb, ref pass, ref total, $"B: 에셋 SH·valid ≡ 코어 직접(BakeBurst→ComputeGrid→Dilate) (Δc={d})", d == 0f && sameValid);
        }

        // ── C) Excluded 토글 ──
        static void TestExcludedToggle(StringBuilder sb, ref int pass, ref int total, ProbeVolumeBaker baker, ProbeVolumeAsset a, List<ProbeVolumeAsset> made)
        {
            var all = baker.templates;
            baker.templates = new[] { all[0], all[1] };
            var without = Bake(baker, made);
            baker.templates = all;
            Check(sb, ref pass, ref total, "C: Excluded 템플릿 = 목록에서 뺀 것과 비트 동일(베이크 무영향)", without != null && SamePacked(a, without));

            all[2].occluder = ProbeVolumeBaker.OccluderMode.Opaque;
            bool okIn = baker.BuildSceneInput(out _, out _, out _, out var cb);
            var opaque = Bake(baker, made);
            all[2].occluder = ProbeVolumeBaker.OccluderMode.Excluded;
            Check(sb, ref pass, ref total, "C: Opaque 로 바꾸면 값 변화 · twoSided → countBackface[2]=false",
                opaque != null && !SamePacked(a, opaque) && okIn && cb.Length == 3 && !cb[2]);
        }

        // ── D) Texture3D ≡ 패킹 ──
        static void TestTextures(StringBuilder sb, ref int pass, ref int total, ProbeVolumeAsset a)
        {
            int n = a.grid.Count;
            float worst = 0f; bool okLen = true;
            for (int t = 0; t < ProbeVolumeAsset.TextureCount; t++)
            {
                var px = a.textures[t].GetPixels();
                okLen &= px.Length == n;
                if (px.Length != n) continue;
                for (int i = 0; i < n; i++)
                {
                    Vector4 v = a.shPacked[i * SHPacked.Float4Count + t];
                    Color c = px[i];
                    worst = Mathf.Max(worst, Rel(c.r, v.x)); worst = Mathf.Max(worst, Rel(c.g, v.y));
                    worst = Mathf.Max(worst, Rel(c.b, v.z)); worst = Mathf.Max(worst, Rel(c.a, v.w));
                }
            }
            Check(sb, ref pass, ref total, $"D: Texture3D 텍셀(i,j,k) ≡ 패킹[idx·7+t] (worst rel={worst:0.0e0}, half 정밀도 ≤1e-3)", okLen && worst <= 1e-3f);
        }

        // ── E) 인스턴스 적용 ──
        static void TestInstanceSampling(StringBuilder sb, ref int pass, ref int total, ProbeVolumeBaker baker, ProbeVolumeAsset a, Mesh box)
        {
            baker.volumeAsset = a;
            var tp = baker.templates[0];
            var mats = tp.matrices.ToArray();

            // 템플릿 단위 대표점 ≡ 씬 버전(BuildScene → ComputeProbePoints)
            var input = new MatrixInstanceInput { templates = new[] { box }, instanceMatrices = new[] { mats } };
            using (var s = TemplateInstanceSource.BuildScene(input, Allocator.TempJob))
            {
                TemplateInstanceSource.ComputeProbePoints(s, new[] { box }, baker.anchorMode, baker.surfaceLift, baker.liftRelativeToBounds, out var topS, out var botS);
                baker.InstanceProbePoints(tp, out var top, out var bot);
                bool same = top.Length == topS.Length;
                for (int i = 0; same && i < top.Length; i++) same &= top[i].Equals(topS[i]) && bot[i].Equals(botS[i]);
                Check(sb, ref pass, ref total, "E: 템플릿 단위 대표점 ≡ 씬 버전 ComputeProbePoints(비트 동일)", same);

                var sh = baker.SampleInstances(0);
                var probes = a.UnpackProbes();
                float d = 0f;
                for (int i = 0; i < top.Length; i++)
                {
                    d = Mathf.Max(d, MaxCoeffDiff(sh[2 * i], a.grid.Sample(probes, a.valid, top[i])));
                    d = Mathf.Max(d, MaxCoeffDiff(sh[2 * i + 1], a.grid.Sample(probes, a.valid, bot[i])));
                }
                Check(sb, ref pass, ref total, $"E: SampleInstances(2-프로브 상/하) ≡ CPU Sample(valid 가중) (n={sh.Length}, Δc={d})", sh.Length == 2 * mats.Length && d == 0f);
            }

            // 배치 이동 → 재베이크 없이 새 위치 샘플
            var packedBefore = (Vector4[])a.shPacked.Clone();
            var before = baker.SampleInstances(0);
            for (int i = 0; i < tp.matrices.Count; i++) tp.matrices[i] = Matrix4x4.Translate(new Vector3(0.7f, 0f, -0.4f)) * tp.matrices[i];
            var after = baker.SampleInstances(0);
            baker.InstanceProbePoints(tp, out var top2, out _);
            var probes2 = a.UnpackProbes();
            float d2 = MaxCoeffDiff(after[0], a.grid.Sample(probes2, a.valid, top2[0]));
            bool changed = MaxCoeffDiff(before[0], after[0]) > 1e-6f;
            bool assetSame = true;
            for (int i = 0; i < packedBefore.Length; i++) assetSame &= packedBefore[i] == a.shPacked[i];
            for (int i = 0; i < tp.matrices.Count; i++) tp.matrices[i] = Matrix4x4.Translate(new Vector3(-0.7f, 0f, 0.4f)) * tp.matrices[i];
            Check(sb, ref pass, ref total, $"E: 인스턴스 이동 → 새 위치 샘플(Δc={d2}) · 값 변화 · 에셋 불변(재베이크 없음)", d2 == 0f && changed && assetSame);
        }

        // ── F) 수렴 ──
        static void TestConvergence(StringBuilder sb, ref int pass, ref int total, ProbeVolumeBaker baker, Mesh box, List<ProbeVolumeAsset> made)
        {
            var saved = baker.templates;
            int savedDirs = baker.dirCount;
            Vector3 savedSpacing = baker.probeSpacing;
            try
            {
                // 지면 + 축에 비정렬인 벽 1개(x 1.25..1.95, y 0..2, z -1.42..1.68 — 격자 면과 겹치지 않음)
                var wall = Matrix4x4.TRS(new Vector3(1.6f, 1.0f, 0.13f), Quaternion.identity, new Vector3(0.7f, 2.0f, 3.1f));
                var ground = Matrix4x4.TRS(new Vector3(0f, -0.05f, 0f), Quaternion.identity, new Vector3(12f, 0.1f, 12f));
                baker.SetInstances(new[] { box, box }, new[] { new[] { wall }, new[] { ground } },
                    new[] { new Color(0.7f, 0.5f, 0.4f), new Color(0.5f, 0.5f, 0.45f) });
                baker.dirCount = 128;

                // 측정점: 볼륨 내부·벽에서 0.3 이상 떨어진 곳
                var rng = new System.Random(21);
                var pts = new List<Vector3>();
                while (pts.Count < 48)
                {
                    var p = new Vector3(Lerp(rng, -3.5f, 3.5f), Lerp(rng, 0.3f, 3.8f), Lerp(rng, -3.5f, 3.5f));
                    bool nearWall = p.x > 0.95f && p.x < 2.25f && p.y < 2.3f && p.z > -1.72f && p.z < 1.98f;
                    if (!nearWall) pts.Add(p);
                }
                var P = pts.ToArray();

                // 기준: 같은 점 직접 베이크(SH 트랙 방식, 1바운스 결정적)
                baker.BuildSceneInput(out var tris, out var albedo, out var insts, out _);
                SH9[] reference;
                using (var bvh = new TwoLevelBVH(tris, insts, Allocator.TempJob, BVH.BuildQuality.SAH))
                using (var scene = BurstScene.Create(bvh, albedo, Allocator.TempJob))
                {
                    var na = new NativeArray<Vector3>(P, Allocator.TempJob);
                    var r = BurstSHBaker.Bake(scene, BurstSky.Gradient(baker.skyTop, baker.skyBottom), Sun(baker), na, baker.dirCount, Allocator.TempJob);
                    reference = r.ToArray();
                    r.Dispose(); na.Dispose();
                }

                var spacings = new[] { 2f, 1f, 0.5f };
                var err = new float[spacings.Length];
                var counts = new int[spacings.Length];
                for (int s = 0; s < spacings.Length; s++)
                {
                    baker.probeSpacing = Vector3.one * spacings[s];
                    var v = Bake(baker, made);
                    if (v == null) { err[s] = float.NaN; continue; }
                    counts[s] = v.grid.Count;
                    var sampled = v.Sample(P, true);
                    err[s] = MeanIrradianceError(sampled, reference);
                }
                bool mono = err[1] < err[0] && err[2] < err[1];
                Check(sb, ref pass, ref total,
                    $"F: 수렴 — 간격 2/1/0.5 (프로브 {counts[0]}/{counts[1]}/{counts[2]}) 직접 베이크 대비 평균 |ΔE| {err[0]:0.0000} > {err[1]:0.0000} > {err[2]:0.0000}", mono);
            }
            finally
            {
                baker.templates = saved;
                baker.dirCount = savedDirs;
                baker.probeSpacing = savedSpacing;
            }
        }

        // ── G) EXR ──
        static void TestExr(StringBuilder sb, ref int pass, ref int total, ProbeVolumeAsset a)
        {
            var g = a.grid;
            ProbeVolumeAsset.ExrLayout(g, out int tx, out int ty, out int w, out int h);
            var wrap = new ProbeGrid(Vector3.zero, Vector3.one, new Vector3Int(2000, 2, 20));
            ProbeVolumeAsset.ExrLayout(wrap, out int wtx, out int wty, out int ww, out int wh);
            Check(sb, ref pass, ref total, $"G: 레이아웃 {w}×{h} (타일 {tx}×{ty}) = 81×35 · 줄바꿈 (2000,2,20) → 타일 {wtx}×{wty}, {ww}×{wh} (8×3, 16000×42)",
                tx == 9 && ty == 1 && w == 81 && h == 35 && wtx == 8 && wty == 3 && ww == 16000 && wh == 42);

            var px = ProbeVolumeAsset.BuildExrPixels(g, a.shPacked);
            bool okPx = px.Length == w * h;
            for (int k = 0; okPx && k < g.dims.z; k++)
                for (int j = 0; j < g.dims.y; j++)
                    for (int i = 0; i < g.dims.x; i++)
                        for (int t = 0; t < ProbeVolumeAsset.TextureCount; t++)
                        {
                            Vector4 v = a.shPacked[g.Index(i, j, k) * 7 + t];
                            Color c = px[((t * ty + k / tx) * g.dims.y + j) * w + (k % tx) * g.dims.x + i];
                            okPx &= c.r == v.x && c.g == v.y && c.b == v.z && c.a == v.w;
                        }
            var exr = ProbeVolumeAsset.EncodeExr(g, a.shPacked);
            bool magic = exr != null && exr.Length > 4 && exr[0] == 0x76 && exr[1] == 0x2F && exr[2] == 0x31 && exr[3] == 0x01;
            Check(sb, ref pass, ref total, $"G: EXR 픽셀 ≡ 패킹(프로브·계수 위치) · 인코딩 {exr?.Length ?? 0} bytes 매직 76 2F 31 01", okPx && magic);
        }

        // ── H) 표시 · Validate 음성 ──
        static void TestRender(StringBuilder sb, ref int pass, ref int total, ProbeVolumeBaker baker, ProbeVolumeAsset a)
        {
            if (Shader.Find("HuskyLibs/InstancedSH_URP") == null) sb.AppendLine("  [SKIP] H: 셰이더 'HuskyLibs/InstancedSH_URP' 없음");
            else
            {
                baker.volumeAsset = a;
                baker.templates[1].render = false;   // 지면은 표시 안 함 → 줄기 + 잎(Excluded 도 표시)
                baker.RebuildRender();
                int count = baker.RendererCount;
                baker.ClearRender();
                baker.templates[1].render = true;
                Check(sb, ref pass, ref total, $"H: 에셋 → 렌더러 {count}개(render 템플릿 2 — Excluded 포함)", count == 2);
            }

            var bad = ScriptableObject.CreateInstance<ProbeVolumeAsset>();
            bad.grid = a.grid;
            bad.shPacked = new Vector4[a.shPacked.Length - 7];
            bool rejected = !bad.Validate(out string err);
            Object.DestroyImmediate(bad);
            Check(sb, ref pass, ref total, $"H: Validate 음성(패킹 길이 불일치) → '{err}'", rejected);
        }

        // ── I) B1-4 폴백 SH · 픽셀 볼륨 렌더러 ──
        static void TestPerPixel(StringBuilder sb, ref int pass, ref int total, ProbeVolumeBaker baker, ProbeVolumeAsset a)
        {
            var sky = ProbeVolumeBaker.SkySH(BurstSky.Gradient(baker.skyTop, baker.skyBottom), baker.dirCount);
            bool okFb = a.fallbackSH != null && a.fallbackSH.Length == SHPacked.Float4Count
                        && MaxCoeffDiff(SHInstancedBuffer.UnflattenAt(a.fallbackSH, 0), sky) == 0f
                        && sky.Evaluate(Vector3.up).x > sky.Evaluate(Vector3.down).x;
            Check(sb, ref pass, ref total, "I: 폴백 SH = 빈 씬(하늘만) 베이크(비트 동일) · E(up) > E(down)", okFb);

            if (Shader.Find(ProbeVolumeShaderBinding.VegetationShaderName) == null || Shader.Find("HuskyLibs/InstancedSH_URP") == null)
            { sb.AppendLine("  [SKIP] I: 식생/InstancedSH 셰이더 없음 — 픽셀 볼륨 렌더러 검사 생략"); return; }

            baker.volumeAsset = a;
            var saved = baker.templates[2].shading;
            baker.templates[2].shading = ProbeVolumeBaker.ShadingMode.PerPixelVolume;   // 잎 카드 → 픽셀 볼륨
            baker.templates[1].render = false;
            bool ok;
            try
            {
                baker.RebuildRender();
                // 렌더러 순서 = render 템플릿 순서 → [0] 줄기(InstanceProbes), [1] 잎(PerPixelVolume)
                ok = baker.RendererCount == 2;
                if (ok)
                {
                    var mpb = baker.Renderers[1].Properties;
                    var v = ProbeVolumeShaderBinding.Compute(a, baker.normalBiasCells, baker.edgeFadeCells);
                    ok &= mpb.GetTexture("_HuskyProbeSH0") == a.textures[0] && mpb.GetTexture("_HuskyProbeSH6") == a.textures[6]
                          && mpb.GetVector("_HuskyPV_Dims") == v.dims && mpb.GetVector("_HuskyPV_Origin") == v.origin
                          && mpb.GetVector("_HuskyPV_Params") == v.parameters && v.parameters.x == 1f;
                    ok &= baker.Renderers[0].Properties.GetTexture("_HuskyProbeSH0") == null;   // InstanceProbes 렌더러엔 볼륨 바인딩 없음
                }
            }
            finally
            {
                baker.ClearRender();
                baker.templates[2].shading = saved;
                baker.templates[1].render = true;
            }
            Check(sb, ref pass, ref total, "I: PerPixelVolume → SH 버퍼 없는 렌더러 + MPB 에 Texture3D×7·격자·파라미터 바인딩(InstanceProbes 렌더러는 미바인딩)", ok);
        }

        // ───────────────────────── 헬퍼 ─────────────────────────

        static ProbeVolumeAsset Bake(ProbeVolumeBaker baker, List<ProbeVolumeAsset> made)
        {
            var a = baker.BakeToAsset(out _);
            if (a != null) made.Add(a);
            return a;
        }

        static void DestroyAsset(ProbeVolumeAsset a)
        {
            if (a == null) return;
            if (a.textures != null) foreach (var t in a.textures) if (t != null) Object.DestroyImmediate(t);
            if (a.vertexAO != null) foreach (var e in a.vertexAO) if (e?.aoMesh != null) Object.DestroyImmediate(e.aoMesh);   // B1-5 AO 메시 사본
            Object.DestroyImmediate(a);
        }

        // 컴포넌트 SunLight 미러(sun 미지정 → 대체값, 색 Linear)
        static DirectionalLight Sun(ProbeVolumeBaker b)
        {
            var c = b.sunColor.linear;
            return new DirectionalLight { Direction = b.sunDirection.normalized, Color = new Vector3(c.r, c.g, c.b), Intensity = b.sunIntensity };
        }

        static bool SamePacked(ProbeVolumeAsset x, ProbeVolumeAsset y)
        {
            if (x.shPacked.Length != y.shPacked.Length) return false;
            for (int i = 0; i < x.shPacked.Length; i++)   // 성분별 정확 비교(Vector4 != 는 ε 근사 — 비트 동일 판정에 못 씀)
            {
                Vector4 a = x.shPacked[i], b = y.shPacked[i];
                if (a.x != b.x || a.y != b.y || a.z != b.z || a.w != b.w) return false;
            }
            return true;
        }

        // 축 6방향 노멀 조도의 평균 절대 오차(RGB 합)
        static float MeanIrradianceError(SH9[] a, SH9[] b)
        {
            var ns = new[] { Vector3.up, Vector3.down, Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
            double sum = 0;
            for (int i = 0; i < a.Length; i++)
                foreach (var n in ns)
                {
                    Vector3 d = a[i].Evaluate(n) - b[i].Evaluate(n);
                    sum += Mathf.Abs(d.x) + Mathf.Abs(d.y) + Mathf.Abs(d.z);
                }
            return (float)(sum / (a.Length * ns.Length));
        }

        static float Rel(float got, float exp) => Mathf.Abs(got - exp) / (Mathf.Abs(exp) + 1e-2f);
    }
}
