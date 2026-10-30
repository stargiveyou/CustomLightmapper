using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// G6-7 GPU 디노이즈(LightmapPost.compute CSDenoise) 검증 — compute 필요(없으면 [SKIP]).
    ///  A) 라이트맵 유사 장면(차트 2개·그림자 계단·노이즈·거터, 72×40 비정사각) 반복 1~4(핑퐁 홀/짝): GPU ≈ Burst(상대 1e-4) · 무효 텍셀 비트 동일 · valid 불변
    ///  B) 무작위 가이드(수직·반대 노멀) · HDR 색 · NormalPower 32 / 0(pow(0,0) = 1 경로) / 1: GPU ≈ Burst · 전부 유한
    ///  C) 계약: Iterations 0 → 불변·true · 버퍼 크기 불일치 → false(px 불변) · Run 에 가이드 없이 Denoise → false
    ///  D) 통합 Run(denoise → T1 → T2 → dilate, 업로드 1회) ≈ CPU 파이프라인(Burst denoise → Stitch → StitchEdges → Burst dilate) · valid 동일
    ///  E) 실베이크(URP + compute): gpuPostProcess off(Burst) ≈ on(GPU denoise) · gpuDenoise off 면 denoise 만 Burst · 로그 백엔드
    ///  [time] 512² 3패스 Burst vs GPU(업로드·readback 포함, 정보)
    /// pow·exp 가 GPU 근사라 CPU 와 비트 동일은 아니다 — 같은 HLSL 을 llvmpipe(SPIR-V)로 돌린 헤드리스 대조에서 상대 ≤ 4.2e-7.
    /// 호출: Debug.Log(GpuDenoiseCompareTests.RunAll());
    /// </summary>
    public static class GpuDenoiseCompareTests
    {
        const float Tol = 1e-4f;

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== G6-7 GPU Denoise Compare (CSDenoise vs Burst) ===");
            int pass = 0, total = 0;

            var cs = SystemInfo.supportsComputeShaders ? LightmapPostGpu.Resolve() : null;
            if (cs == null || !cs.HasKernel("CSDenoise"))
            {
                sb.AppendLine("  [SKIP] A~E: compute 미지원 또는 LightmapPost.compute(CSDenoise) 로드 실패");
                sb.AppendLine($"--- {pass}/{total} PASS ---");
                return sb.ToString();
            }

            TestScene(sb, ref pass, ref total, cs);
            TestRandomGuides(sb, ref pass, ref total, cs);
            TestContract(sb, ref pass, ref total, cs);
            TestRunPipeline(sb, ref pass, ref total, cs);
            TestRealBake(sb, ref pass, ref total);
            Timing(sb, cs);

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ── A) 장면 입력 · 반복 1~4 ──
        static void TestScene(StringBuilder sb, ref int pass, ref int total, ComputeShader cs)
        {
            const int w = 72, h = 40;
            LightmapDenoiseCompare.MakeSceneInput(w, h, out var px, out var valid, out var nrm, out var pos, out _);
            float worst = 0f;
            bool ok = true;
            for (int it = 1; it <= 4; it++)
            {
                var s = DenoiseSettings.Default; s.Iterations = it;
                var cpu = (Color[])px.Clone();
                LightmapDenoiseBurstJob.Denoise(cpu, valid, nrm, pos, w, h, s);
                var gpu = (Color[])px.Clone();
                var gv = (bool[])valid.Clone();
                bool ran = LightmapPostGpu.Denoise(cs, gpu, gv, nrm, pos, w, h, s, out string err);
                if (!ran) { sb.AppendLine($"    GPU 디노이즈 실패(it={it}): {err}"); ok = false; continue; }
                ok &= Compare(px, cpu, gpu, valid, out float rel) && SameMask(valid, gv);
                worst = Mathf.Max(worst, rel);
            }
            Check(sb, ref pass, ref total, $"A: 장면 72×40 반복 1~4 GPU ≈ Burst(최대 상대차 {worst:E2} ≤ {Tol:E0}) · 무효 텍셀 비트 동일 · valid 불변", ok && worst <= Tol);
        }

        // ── B) 무작위 가이드 · HDR · NormalPower 32/0/1 ──
        static void TestRandomGuides(StringBuilder sb, ref int pass, ref int total, ComputeShader cs)
        {
            const int w = 37, h = 29;
            int n = w * h;
            var rng = new System.Random(7);
            var px = new Color[n]; var valid = new bool[n]; var nrm = new Vector3[n]; var pos = new Vector3[n];
            Vector3[] dirs = { Vector3.up, Vector3.right, Vector3.down, new Vector3(0.6f, 0.8f, 0f), new Vector3(0f, 0.8f, -0.6f) };
            for (int i = 0; i < n; i++)
            {
                valid[i] = rng.NextDouble() < 0.7;
                nrm[i] = dirs[rng.Next(dirs.Length)];
                pos[i] = new Vector3((float)rng.NextDouble() * 0.6f, (float)rng.NextDouble() * 0.3f, (float)rng.NextDouble() * 0.6f);
                px[i] = new Color((float)rng.NextDouble() * 4f, (float)rng.NextDouble(), (float)rng.NextDouble() * 2f, (float)rng.NextDouble());
            }
            var settings = new[]
            {
                new DenoiseSettings { Iterations = 3, NormalPower = 32f, PositionSigma = 0.125f, ColorSigma = 0.25f },
                new DenoiseSettings { Iterations = 3, NormalPower = 0f, PositionSigma = 0.125f, ColorSigma = 0.25f },
                new DenoiseSettings { Iterations = 3, NormalPower = 1f, PositionSigma = 0.5f, ColorSigma = 2f },
            };
            float worst = 0f;
            bool ok = true;
            foreach (var s in settings)
            {
                var cpu = (Color[])px.Clone();
                LightmapDenoiseBurstJob.Denoise(cpu, valid, nrm, pos, w, h, s);
                var gpu = (Color[])px.Clone();
                bool ran = LightmapPostGpu.Denoise(cs, gpu, valid, nrm, pos, w, h, s, out string err);
                if (!ran) { sb.AppendLine($"    GPU 디노이즈 실패(p={s.NormalPower}): {err}"); ok = false; continue; }
                ok &= Compare(px, cpu, gpu, valid, out float rel) && Finite(gpu);
                worst = Mathf.Max(worst, rel);
            }
            Check(sb, ref pass, ref total, $"B: 무작위 가이드(수직·반대 노멀)·HDR · NormalPower 32/0/1 GPU ≈ Burst(최대 상대차 {worst:E2}) · 전부 유한", ok && worst <= Tol);
        }

        // ── C) 계약 ──
        static void TestContract(StringBuilder sb, ref int pass, ref int total, ComputeShader cs)
        {
            const int w = 16, h = 12;
            LightmapDenoiseCompare.MakeSceneInput(w, h, out var px, out var valid, out var nrm, out var pos, out _);
            var s0 = DenoiseSettings.Default; s0.Iterations = 0;
            var a = (Color[])px.Clone();
            bool r0 = LightmapPostGpu.Denoise(cs, a, valid, nrm, pos, w, h, s0, out _);
            bool same0 = SameColors(a, px);

            var b = (Color[])px.Clone();
            bool r1 = LightmapPostGpu.Denoise(cs, b, valid, new Vector3[3], pos, w, h, DenoiseSettings.Default, out string e1);
            bool same1 = SameColors(b, px);

            const int res = 16;
            var page = new Color[res * res]; var pv = new bool[res * res];
            for (int i = 0; i < pv.Length; i++) { pv[i] = (i % 3) != 0; page[i] = new Color(0.5f, 0.4f, 0.3f, 1f); }
            var opt = new LightmapPostGpu.Options { Denoise = true, DenoiseSettings = DenoiseSettings.Default, DilateIterations = 1 };
            bool r2 = LightmapPostGpu.Run(cs, page, pv, res, null, null, opt, out string e2);

            Check(sb, ref pass, ref total,
                $"C: Iterations 0 → 불변·true({r0}) · 가이드 크기 불일치 → false '{e1}'(px 불변) · Run 가이드 없음 → false '{e2}'",
                r0 && same0 && !r1 && same1 && !string.IsNullOrEmpty(e1) && !r2 && !string.IsNullOrEmpty(e2));
        }

        // ── D) 통합 Run: denoise → T1 → T2 → dilate ≈ CPU 파이프라인 ──
        static void TestRunPipeline(StringBuilder sb, ref int pass, ref int total, ComputeShader cs)
        {
            const int res = 64;
            int n = res * res;
            LightmapDenoiseCompare.MakeSceneInput(res, res, out var px, out var valid, out var nrm, out var pos, out _);
            var rng = new System.Random(4242);
            for (int i = 0; i < n; i++) if (valid[i] && rng.NextDouble() < 0.1) valid[i] = false;   // 차트 안 구멍 → dilation 대상
            var groups = new List<int[]>();
            for (int g = 0; g < 40; g++)
            {
                int len = 2 + rng.Next(3);
                var arr = new int[len];
                for (int k = 0; k < len; k++) arr[k] = rng.Next(n);
                groups.Add(arr);
            }
            var segs = new List<LightmapSeamStitch.Seg[]>();
            for (int g = 0; g < 12; g++)
            {
                var a = new Vector2((float)rng.NextDouble() * res, (float)rng.NextDouble() * res);
                var d = new Vector2((float)rng.NextDouble() * 16f - 8f, (float)rng.NextDouble() * 16f - 8f);
                segs.Add(new[] { new LightmapSeamStitch.Seg(a, a + d), new LightmapSeamStitch.Seg(a + Vector2.one, a + d * 0.9f) });
            }
            var s = DenoiseSettings.Default;

            var cp = (Color[])px.Clone(); var cv = (bool[])valid.Clone();
            LightmapDenoiseBurstJob.Denoise(cp, cv, nrm, pos, res, res, s);
            LightmapSeamStitch.Stitch(cp, cv, groups);
            LightmapSeamStitch.StitchEdges(cp, cv, res, segs, 2);
            LightmapPostProcessBurstJob.Dilate(cp, cv, res, res, 4);

            var gp = (Color[])px.Clone(); var gv = (bool[])valid.Clone();
            var opt = new LightmapPostGpu.Options
            {
                Tier1 = true, Tier2 = true, Tier2Iterations = 2, DilateIterations = 4,
                Denoise = true, DenoiseSettings = s, GuideNormal = nrm, GuidePos = pos,
            };
            bool ran = LightmapPostGpu.Run(cs, gp, gv, res, groups, segs, opt, out string err);
            if (!ran) sb.AppendLine($"    GPU 후처리 실패: {err}");

            bool maskSame = ran && SameMask(cv, gv);
            float worst = 0f;
            int filled = 0;
            for (int i = 0; ran && i < n; i++)
            {
                if (!cv[i]) continue;
                if (!valid[i]) filled++;
                worst = Mathf.Max(worst, Rel(cp[i], gp[i]));
            }
            Check(sb, ref pass, ref total,
                $"D: 통합 Run(denoise×3 → T1 → T2×2 → dilate×4) ≈ CPU 파이프라인(최대 상대차 {worst:E2}) · valid 동일 · dilation 채움 {filled}",
                ran && maskSame && worst <= Tol && filled > 0);
        }

        // ── E) 실베이크: AtlasApplyDebug denoise CPU ↔ GPU ──
        static void TestRealBake(StringBuilder sb, ref int pass, ref int total)
        {
            if (Shader.Find("CustomLightmapper/LightmapDebug") == null)
            { sb.AppendLine("  [SKIP] E: 셰이더 'CustomLightmapper/LightmapDebug' 없음"); return; }

            var root = new GameObject("GpuDenoiseCompareTests") { hideFlags = HideFlags.HideAndDontSave };
            var filters = new List<MeshFilter>
            {
                AtlasBatchBakeTests.Prim(root, PrimitiveType.Cube, new Vector3(0f, -0.05f, 0f), new Vector3(8f, 0.1f, 8f)),
                AtlasBatchBakeTests.Prim(root, PrimitiveType.Cube, new Vector3(0f, 0.5f, 0f), Vector3.one),
                AtlasBatchBakeTests.Prim(root, PrimitiveType.Sphere, new Vector3(2f, 0.6f, 0.3f), Vector3.one * 1.2f),
            };
            var baker = root.AddComponent<AtlasApplyDebug>();
            baker.localLightSource = LocalLightSource.None;   // 열린 씬의 라이트와 무관하게(테스트 격리)
            var made = new List<Object>();
            try
            {
                baker.saveBakeResult = false;
                baker.applyOnEnable = false;
                baker.atlasResolution = 128;
                baker.texelsPerWorldUnit = 6f;
                baker.mode = AtlasApplyDebug.BakeMode.RadianceGI;
                baker.radianceBackend = AtlasApplyDebug.RadianceBackend.Burst;
                baker.indirectSamples = 16;
                baker.maxBounces = 1;
                baker.lightIntensity = 2f;
                baker.denoise = true;
                baker.denoiseBurst = true;
                baker.dilateIterations = 4;

                baker.gpuPostProcess = false;
                var e0 = ProgressiveBakeTests.Bake(baker, filters, made, false, out _);
                string d0 = baker.LastDenoiseBackend, p0 = baker.LastPostBackend;

                baker.gpuPostProcess = true;
                baker.gpuDenoise = true;
                var e1 = ProgressiveBakeTests.Bake(baker, filters, made, false, out _);
                string d1 = baker.LastDenoiseBackend, p1 = baker.LastPostBackend;

                baker.gpuDenoise = false;
                var e2 = ProgressiveBakeTests.Bake(baker, filters, made, false, out _);
                string d2 = baker.LastDenoiseBackend, p2 = baker.LastPostBackend;
                baker.gpuPostProcess = false;
                baker.gpuDenoise = true;

                float rel1 = ProgressiveBakeTests.MaxRel(e0, e1), rel2 = ProgressiveBakeTests.MaxRel(e0, e2);
                Check(sb, ref pass, ref total,
                    $"E: 실베이크 denoise {d0}/{p0} → {d1}/{p1}(상대 {rel1:E2}) · gpuDenoise off → {d2}/{p2}(상대 {rel2:E2}) — RGBAHalf 반올림 범위 ≤ 2e-3",
                    e0 != null && e1 != null && e2 != null && d0 == "Burst" && p0 == "CPU" && d1 == "GPU" && p1 == "GPU" && d2 == "Burst" && p2 == "GPU"
                    && rel1 <= 2e-3f && rel2 <= 2e-3f);
            }
            finally
            {
                Object.DestroyImmediate(root);
                foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
            }
        }

        // ── [time] 512² 3패스: Burst vs GPU(업로드·readback 포함) ──
        static void Timing(StringBuilder sb, ComputeShader cs)
        {
            const int w = 512, h = 512, reps = 3;
            LightmapDenoiseCompare.MakeSceneInput(w, h, out var px, out var valid, out var nrm, out var pos, out _);
            var s = DenoiseSettings.Default;
            { var p = (Color[])px.Clone(); LightmapDenoiseBurstJob.Denoise(p, valid, nrm, pos, w, h, s); }   // 워밍업
            { var p = (Color[])px.Clone(); LightmapPostGpu.Denoise(cs, p, valid, nrm, pos, w, h, s, out _); }
            double burst = double.MaxValue, gpu = double.MaxValue;
            var sw = new Stopwatch();
            for (int r = 0; r < reps; r++)
            {
                var a = (Color[])px.Clone();
                sw.Restart(); LightmapDenoiseBurstJob.Denoise(a, valid, nrm, pos, w, h, s); sw.Stop();
                burst = System.Math.Min(burst, sw.Elapsed.TotalMilliseconds);
                var b = (Color[])px.Clone();
                sw.Restart(); LightmapPostGpu.Denoise(cs, b, valid, nrm, pos, w, h, s, out _); sw.Stop();
                gpu = System.Math.Min(gpu, sw.Elapsed.TotalMilliseconds);
            }
            sb.AppendLine($"  [time] {w}×{h}, iters={s.Iterations} (best of {reps}) Burst = {burst:0.00} ms · GPU(업로드·readback 포함) = {gpu:0.00} ms (≈{burst / System.Math.Max(1e-4, gpu):0.0}×)");
        }

        // 무효 텍셀은 비트 동일, valid 텍셀은 상대차 ≤ Tol 판정용 최대값.
        static bool Compare(Color[] input, Color[] cpu, Color[] gpu, bool[] valid, out float worst)
        {
            worst = 0f;
            bool invalidSame = true;
            for (int i = 0; i < input.Length; i++)
            {
                if (!valid[i]) { invalidSame &= Same(gpu[i], input[i]); continue; }
                worst = Mathf.Max(worst, Rel(cpu[i], gpu[i]));
            }
            return invalidSame;
        }

        static float Rel(Color c, Color g)
        {
            float d = Mathf.Max(Mathf.Max(Mathf.Abs(c.r - g.r), Mathf.Abs(c.g - g.g)), Mathf.Max(Mathf.Abs(c.b - g.b), Mathf.Abs(c.a - g.a)));
            float s = Mathf.Max(1f, Mathf.Max(Mathf.Max(Mathf.Abs(c.r), Mathf.Abs(c.g)), Mathf.Max(Mathf.Abs(c.b), Mathf.Abs(c.a))));
            return float.IsNaN(d) ? float.MaxValue : d / s;
        }

        static bool Same(Color a, Color b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        static bool SameColors(Color[] a, Color[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!Same(a[i], b[i])) return false;
            return true;
        }

        static bool SameMask(bool[] a, bool[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static bool Finite(Color[] px)
        {
            foreach (var c in px)
                if (float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsNaN(c.b) || float.IsNaN(c.a) ||
                    float.IsInfinity(c.r) || float.IsInfinity(c.g) || float.IsInfinity(c.b) || float.IsInfinity(c.a)) return false;
            return true;
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
