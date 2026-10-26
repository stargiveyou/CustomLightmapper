using System.Collections.Generic;
using System.Text;
using UnityEngine;
using static HuskyLibs.CustomLightmapper.Bake.ProbeGridTests;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Track B B1-4 검증: 픽셀 볼륨 샘플(HuskyProbeVolume.hlsl) GPU ≡ CPU 미러(<see cref="ProbeVolumeShaderBinding.EvaluateIrradiance"/>).
    ///  M) CPU 미러 자체(GPU 불필요): 내부 = 무가중 Sample 평가 · 페이드 가중치(안 1 / 1셀 밖 0.5 / 2셀 밖 0, fade 0 → 1) ·
    ///     멀리 밖 = 폴백 · 투과 = E(n)+t·E(-n) · Compute(바이어스 = 셀 × 최소 간격, 폴백 없으면 페이드 0, 에셋 없음 → 비활성).
    ///  G) GPU(실 Texture3D×7 + StructuredBuffer 왕복 — Hidden/HuskyLibs/ProbeVolumeSampleTest 를 N×1 float RT 로 Blit → ReadPixels):
    ///     G1 내부 임의점(투과 일부) · G2 텍셀 중심(= 프로브) · G3 볼륨 밖 클램프 · G4 가장자리 페이드 · G5 normalBias · G6 비활성.
    ///     CPU 기준 프로브는 텍스처에서 되읽은 값(half 반올림 반영) → 남는 차이는 HW 필터 가중치 양자화뿐(선형장·작은 기울기로 억제).
    ///  V) 식생 셰이더(HuskyLibs/InstancedVegetation_URP) 로드·지원(URP 활성 시).
    /// ⚠️ G 는 렌더 컨텍스트 필요(에디터/플레이). 호출: Debug.Log(ProbeVolumeGpuSampleTests.RunAll());
    /// </summary>
    public static class ProbeVolumeGpuSampleTests
    {
        const string TestShader = "Hidden/HuskyLibs/ProbeVolumeSampleTest";

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Track B 픽셀 볼륨 샘플 GPU ≡ CPU (B1-4) ===");
            int pass = 0, total = 0;

            var asset = MakeAsset();
            try
            {
                var probesHalf = ReadBackProbes(asset);   // 텍스처 값(half) — GPU 가 실제로 보는 프로브
                TestMirror(sb, ref pass, ref total, asset, probesHalf);
                TestGpu(sb, ref pass, ref total, asset, probesHalf);
                TestVegetationShader(sb, ref pass, ref total);
            }
            finally
            {
                foreach (var t in asset.textures) if (t != null) Object.DestroyImmediate(t);
                Object.DestroyImmediate(asset);
            }

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // 격자 6×4×5(축별 다른 간격) · 계수 선형장(격자 좌표 기준 셀당 기울기 ≤ 0.12, c0 우세 → 조도 양수) · 폴백 = 그라디언트 하늘 SH
        static ProbeVolumeAsset MakeAsset()
        {
            var g = new ProbeGrid(new Vector3(-1.3f, 0.2f, 2.1f), new Vector3(0.75f, 0.5f, 1.25f), new Vector3Int(6, 4, 5));
            var rng = new System.Random(17);
            var slope = new Vector3[SH9.Count * 3];
            var bias = new Vector3[SH9.Count];
            for (int i = 0; i < slope.Length; i++) slope[i] = new Vector3(Lerp(rng, -0.12f, 0.12f), Lerp(rng, -0.12f, 0.12f), Lerp(rng, -0.12f, 0.12f));
            for (int k = 0; k < SH9.Count; k++)
                bias[k] = k == 0 ? new Vector3(Lerp(rng, 0.8f, 1.4f), Lerp(rng, 0.8f, 1.4f), Lerp(rng, 0.8f, 1.4f))
                                 : new Vector3(Lerp(rng, -0.2f, 0.2f), Lerp(rng, -0.2f, 0.2f), Lerp(rng, -0.2f, 0.2f));
            var sh = new SH9[g.Count];
            for (int idx = 0; idx < sh.Length; idx++)
            {
                Vector3 c = g.Coord(idx);
                for (int k = 0; k < SH9.Count; k++)
                    SetCoeff(ref sh[idx], k, bias[k] + new Vector3(Vector3.Dot(slope[k * 3], c), Vector3.Dot(slope[k * 3 + 1], c), Vector3.Dot(slope[k * 3 + 2], c)));
            }
            var a = ScriptableObject.CreateInstance<ProbeVolumeAsset>();
            a.hideFlags = HideFlags.HideAndDontSave;
            a.grid = g;
            a.shPacked = SHInstancedBuffer.Flatten(sh);
            a.valid = new bool[g.Count];
            for (int i = 0; i < a.valid.Length; i++) a.valid[i] = true;
            a.textures = ProbeVolumeAsset.CreateTextures(g, a.shPacked, "ProbeVolumeGpuSampleTests_SH");
            a.fallbackSH = SHInstancedBuffer.Flatten(new[] { ProbeVolumeBaker.SkySH(BurstSky.Gradient(new Vector3(0.5f, 0.7f, 1f), new Vector3(0.1f, 0.1f, 0.12f)), 256) });
            return a;
        }

        static SH9[] ReadBackProbes(ProbeVolumeAsset a)
        {
            int n = a.grid.Count;
            var packed = new Vector4[n * SHPacked.Float4Count];
            for (int t = 0; t < ProbeVolumeAsset.TextureCount; t++)
            {
                var px = a.textures[t].GetPixels();
                for (int i = 0; i < n; i++) packed[i * SHPacked.Float4Count + t] = new Vector4(px[i].r, px[i].g, px[i].b, px[i].a);
            }
            var sh = new SH9[n];
            for (int i = 0; i < n; i++) sh[i] = SHInstancedBuffer.UnflattenAt(packed, i);
            return sh;
        }

        // ── M) CPU 미러 ──
        static void TestMirror(StringBuilder sb, ref int pass, ref int total, ProbeVolumeAsset a, SH9[] probes)
        {
            var g = a.grid;
            var v = ProbeVolumeShaderBinding.Compute(a, 0f, 2f);
            var fb = ProbeVolumeShaderBinding.UnpackFallback(v);
            var rng = new System.Random(5);

            float dIn = 0f, dT = 0f;
            for (int s = 0; s < 32; s++)
            {
                var p = Interior(rng, g);
                var n = RandDir(rng);
                var smp = g.Sample(probes, p);
                dIn = Mathf.Max(dIn, MaxAbs(ProbeVolumeShaderBinding.EvaluateIrradiance(g, probes, fb, v, p, n) - smp.Evaluate(n)));
                dT = Mathf.Max(dT, MaxAbs(ProbeVolumeShaderBinding.EvaluateIrradiance(g, probes, fb, v, p, n, 0.4f) - (smp.Evaluate(n) + 0.4f * smp.Evaluate(-n))));
            }
            Check(sb, ref pass, ref total, $"M: 내부 = 무가중 Sample 평가 (Δ={dIn:0.0e0}) · 투과 E(n)+t·E(-n) (Δ={dT:0.0e0})", dIn < 1e-6f && dT < 1e-6f);

            // 페이드 가중치: x 축으로 경계 밖 d 셀
            Vector3 edge = g.Position(g.dims.x - 1, 1, 2);
            float w0 = ProbeVolumeShaderBinding.VolumeWeight(g, edge - new Vector3(0.3f * g.spacing.x, 0f, 0f), 2f);
            float w1 = ProbeVolumeShaderBinding.VolumeWeight(g, edge + new Vector3(1f * g.spacing.x, 0f, 0f), 2f);
            float w2 = ProbeVolumeShaderBinding.VolumeWeight(g, edge + new Vector3(2.5f * g.spacing.x, 0f, 0f), 2f);
            float wNoFade = ProbeVolumeShaderBinding.VolumeWeight(g, edge + new Vector3(5f * g.spacing.x, 0f, 0f), 0f);
            Check(sb, ref pass, ref total, $"M: 페이드 가중치 안 {w0} / 1셀 밖 {w1:0.000} / 2.5셀 밖 {w2} / fade 0 → {wNoFade}",
                w0 == 1f && Mathf.Abs(w1 - 0.5f) < 1e-5f && w2 == 0f && wNoFade == 1f);

            var far = edge + new Vector3(4f * g.spacing.x, 0f, 0f);
            float dFar = MaxAbs(ProbeVolumeShaderBinding.EvaluateIrradiance(g, probes, fb, v, far, Vector3.up) - fb.Evaluate(Vector3.up));
            Check(sb, ref pass, ref total, $"M: 볼륨 멀리 밖(페이드 완료) = 폴백 하늘 SH (Δ={dFar}) · 폴백 E(up) > E(down)",
                dFar < 1e-6f && fb.Evaluate(Vector3.up).x > fb.Evaluate(Vector3.down).x);

            var vb = ProbeVolumeShaderBinding.Compute(a, 0.4f, 1f);
            float minSp = Mathf.Min(g.spacing.x, Mathf.Min(g.spacing.y, g.spacing.z));
            var saved = a.fallbackSH;
            a.fallbackSH = new Vector4[0];
            var vNoFb = ProbeVolumeShaderBinding.Compute(a, 0.4f, 1f);
            a.fallbackSH = saved;
            var vNull = ProbeVolumeShaderBinding.Compute(null, 0.4f, 1f);
            Check(sb, ref pass, ref total, $"M: Compute — 바이어스 {vb.parameters.y:0.000} = 0.4×{minSp} · 폴백 없으면 페이드 {vNoFb.parameters.z} · 에셋 없음 → 활성 {vNull.parameters.x}",
                Mathf.Abs(vb.parameters.y - 0.4f * minSp) < 1e-6f && vb.parameters.x == 1f && vb.parameters.z == 1f
                && vNoFb.parameters.z == 0f && vNull.parameters.x == 0f);
        }

        // ── G) GPU ≡ CPU ──
        static void TestGpu(StringBuilder sb, ref int pass, ref int total, ProbeVolumeAsset a, SH9[] probes)
        {
            var shader = Shader.Find(TestShader);
            if (shader == null) { Check(sb, ref pass, ref total, $"G: 셰이더 '{TestShader}' 없음 (등록/컴파일 확인)", false); return; }
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
            { sb.AppendLine("  [SKIP] G: ARGBFloat RT 미지원 — GPU 검증 불가"); return; }
            if (SystemInfo.maxComputeBufferInputsFragment <= 0)
                sb.AppendLine("  [WARN] 프래그먼트 StructuredBuffer 제한 가능(결과 0 이면 이 원인)");

            var g = a.grid;
            var rng = new System.Random(29);
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                // G1 내부(절반은 투과 t=0.4)
                var pts = new List<Vector3>(); var nrm = new List<Vector4>();
                for (int s = 0; s < 48; s++) { pts.Add(Interior(rng, g)); nrm.Add(Dir4(RandDir(rng), s % 2 == 0 ? 0f : 0.4f)); }
                RunGroup(sb, ref pass, ref total, mat, a, probes, "G1 내부 임의점 48(투과 절반)", pts, nrm, 0f, 0f, 1e-2f);

                // G2 텍셀 중심 = 프로브
                pts.Clear(); nrm.Clear();
                for (int s = 0; s < 24; s++) { pts.Add(g.Position(rng.Next(g.Count))); nrm.Add(Dir4(RandDir(rng), 0f)); }
                RunGroup(sb, ref pass, ref total, mat, a, probes, "G2 텍셀 중심(프로브 위치) 24", pts, nrm, 0f, 0f, 2e-3f);

                // G3 볼륨 밖 — 경계 클램프(fade 0)
                pts.Clear(); nrm.Clear();
                Vector3 lo = g.origin, hi = g.MaxCorner;
                for (int s = 0; s < 16; s++)
                {
                    var p = Interior(rng, g);
                    int axis = s % 3; float sign = (s / 3) % 2 == 0 ? 1f : -1f;
                    float outBy = Lerp(rng, 0.5f, 3f) * g.spacing[axis];
                    p[axis] = sign > 0 ? hi[axis] + outBy : lo[axis] - outBy;
                    pts.Add(p); nrm.Add(Dir4(RandDir(rng), 0f));
                }
                RunGroup(sb, ref pass, ref total, mat, a, probes, "G3 볼륨 밖 경계 클램프 16", pts, nrm, 0f, 0f, 1e-2f);

                // G4 가장자리 페이드(2셀): 경계 밖 0~3셀
                pts.Clear(); nrm.Clear();
                var outs = new[] { 0f, 0.5f, 1f, 1.5f, 2f, 3f };
                for (int s = 0; s < outs.Length * 2; s++)
                {
                    var p = Interior(rng, g);
                    int axis = s % 3;
                    p[axis] = hi[axis] + outs[s % outs.Length] * g.spacing[axis];
                    pts.Add(p); nrm.Add(Dir4(RandDir(rng), s % 4 == 3 ? 0.4f : 0f));
                }
                RunGroup(sb, ref pass, ref total, mat, a, probes, "G4 가장자리 페이드 2셀(0~3셀 밖) 12", pts, nrm, 0f, 2f, 1e-2f);

                // G5 normalBias 0.4셀
                pts.Clear(); nrm.Clear();
                for (int s = 0; s < 24; s++) { pts.Add(Interior(rng, g)); nrm.Add(Dir4(RandDir(rng), 0f)); }
                RunGroup(sb, ref pass, ref total, mat, a, probes, "G5 normalBias 0.4셀 24", pts, nrm, 0.4f, 1f, 1e-2f);

                // G6 비활성(에셋 없음) → 폴백 0 → 조도 0
                pts.Clear(); nrm.Clear();
                for (int s = 0; s < 4; s++) { pts.Add(Interior(rng, g)); nrm.Add(Dir4(RandDir(rng), 0f)); }
                RunGroup(sb, ref pass, ref total, mat, null, probes, "G6 비활성(바인딩 없음) 4", pts, nrm, 0f, 0f, 1e-5f);
            }
            finally { Object.DestroyImmediate(mat); }
        }

        // 한 그룹: 바인딩(Material) → N×1 Blit → ReadPixels → CPU 미러와 |Δ| ≤ tol·(1 + |E|)
        static void RunGroup(StringBuilder sb, ref int pass, ref int total, Material mat, ProbeVolumeAsset a, SH9[] probes,
                             string name, List<Vector3> pts, List<Vector4> nrm, float biasCells, float fadeCells, float tol)
        {
            int n = pts.Count;
            ProbeVolumeShaderBinding.Bind(mat, a, biasCells, fadeCells);
            var v = ProbeVolumeShaderBinding.Compute(a, biasCells, fadeCells);
            var fb = ProbeVolumeShaderBinding.UnpackFallback(v);
            var grid = a != null ? a.grid : new ProbeGrid(Vector3.zero, Vector3.one, Vector3Int.one);

            var pData = new Vector4[n];
            for (int i = 0; i < n; i++) pData[i] = new Vector4(pts[i].x, pts[i].y, pts[i].z, 1f);
            var pBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, 16);
            var nBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, 16);
            pBuf.SetData(pData);
            nBuf.SetData(nrm.ToArray());
            mat.SetBuffer("_TestPoints", pBuf);
            mat.SetBuffer("_TestNormals", nBuf);

            var rt = new RenderTexture(n, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { useMipMap = false };
            rt.Create();
            var tex = new Texture2D(n, 1, TextureFormat.RGBAFloat, false, true);
            var prev = RenderTexture.active;
            float maxErr = 0f; int over = 0;
            try
            {
                Graphics.Blit(Texture2D.whiteTexture, rt, mat);
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, n, 1), 0, 0, false);
                tex.Apply(false);
                for (int i = 0; i < n; i++)
                {
                    Color c = tex.GetPixel(i, 0);
                    Vector3 nn = new Vector3(nrm[i].x, nrm[i].y, nrm[i].z);
                    Vector3 cpu = a != null
                        ? ProbeVolumeShaderBinding.EvaluateIrradiance(grid, probes, fb, v, pts[i], nn, nrm[i].w)
                        : Vector3.zero;
                    float err = MaxAbs(new Vector3(c.r, c.g, c.b) - cpu);
                    maxErr = Mathf.Max(maxErr, err);
                    if (err > tol * (1f + MaxAbs(cpu))) over++;
                }
            }
            finally
            {
                RenderTexture.active = prev;
                pBuf.Dispose(); nBuf.Dispose();
                rt.Release(); Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
            }
            Check(sb, ref pass, ref total, $"{name}: GPU ≡ CPU 미러 (max|Δ|={maxErr:0.0e0}, 허용 {tol}·(1+|E|) 초과 {over})", over == 0);
        }

        // ── V) 식생 셰이더 ──
        static void TestVegetationShader(StringBuilder sb, ref int pass, ref int total)
        {
            var s = Shader.Find(ProbeVolumeShaderBinding.VegetationShaderName);
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline == null)
            { sb.AppendLine($"  [SKIP] V: URP 비활성 — '{ProbeVolumeShaderBinding.VegetationShaderName}' 지원 확인 생략(로드 {(s != null ? "됨" : "안 됨")})"); return; }
            Check(sb, ref pass, ref total, $"V: 식생 셰이더 로드·지원 ({ProbeVolumeShaderBinding.VegetationShaderName})", s != null && s.isSupported);
        }

        // ───────────────────────── 헬퍼 ─────────────────────────

        static Vector3 Interior(System.Random rng, in ProbeGrid g)
        {
            Vector3 lo = g.origin, hi = g.MaxCorner;
            return new Vector3(Lerp(rng, lo.x, hi.x), Lerp(rng, lo.y, hi.y), Lerp(rng, lo.z, hi.z));
        }

        static Vector3 RandDir(System.Random rng)
        {
            Vector3 d;
            do d = new Vector3(Lerp(rng, -1f, 1f), Lerp(rng, -1f, 1f), Lerp(rng, -1f, 1f)); while (d.sqrMagnitude < 1e-2f || d.sqrMagnitude > 1f);
            return d.normalized;
        }

        static Vector4 Dir4(Vector3 d, float t) => new Vector4(d.x, d.y, d.z, t);
    }
}
