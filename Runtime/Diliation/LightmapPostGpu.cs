using System;
using System.Collections.Generic;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 라이트맵 후처리 GPU 판(LightmapPost.compute): 페이지 1장을 업로드 1회 → (선택) 디노이즈 핑퐁 → Tier1 웨이브
    /// → Tier2(샘플 → 수집, Jacobi 반복) → dilation 핑퐁 → readback 1회. 스티칭 계획은 <see cref="LightmapPostPlan"/>(CPU 판과 같은
    /// 텍셀·같은 덧셈 순서) → 스티칭·dilation 결과 = CPU 판(<see cref="LightmapSeamStitch"/> + <see cref="LightmapPostProcessBurstJob"/>)과
    /// 나눗셈 ULP 차이 이내. 디노이즈는 <see cref="LightmapDenoiseBurstJob"/> 와 같은 식·탭 순서지만 pow/exp 가 GPU 근사라 ε(상대 ~1e-6) 일치.
    /// 실패(셰이더·커널 없음, 예외)는 false + 사유 — 호출측이 CPU 판으로 폴백. px/valid 는 성공했을 때만 의미 있음.
    /// </summary>
    public static class LightmapPostGpu
    {
        public const string EditorAssetPath = "Packages/com.huskylibs.customlightmapper/Shaders/Resources/LightmapPost.compute";

        public struct Options
        {
            public bool Tier1, Tier2;
            public int Tier2Iterations;
            public int DilateIterations;
            /// <summary>스티칭 전에 디노이즈(<see cref="DenoiseSettings"/>, Iterations ≤ 0 이면 생략). 가이드 = 텍셀 월드 노멀·위치(res², valid 텍셀만 의미).</summary>
            public bool Denoise;
            public DenoiseSettings DenoiseSettings;
            public Vector3[] GuideNormal, GuidePos;
        }

        /// <summary>우선순위: 지정 → Resources("LightmapPost") → (에디터) 패키지 경로.</summary>
        public static ComputeShader Resolve(ComputeShader assigned = null)
        {
            if (assigned != null) return assigned;
            var cs = Resources.Load<ComputeShader>("LightmapPost");
#if UNITY_EDITOR
            if (cs == null) cs = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(EditorAssetPath);
#endif
            return cs;
        }

        /// <summary>
        /// 디노이즈만 GPU 로(w×h, 정사각 아니어도 됨). <see cref="LightmapDenoise.Denoise"/> 와 같은 계약: valid 텍셀만 읽고 쓰고,
        /// 무효 텍셀·valid 마스크 불변, Iterations ≤ 0 이면 아무것도 안 함. 실패 시 false + 사유(px 불변).
        /// </summary>
        public static bool Denoise(ComputeShader cs, Color[] px, bool[] valid, Vector3[] normal, Vector3[] worldPos,
                                   int w, int h, in DenoiseSettings s, out string error)
        {
            error = null;
            int n = w * h;
            if (cs == null) { error = "셰이더 없음"; return false; }
            if (px == null || valid == null || normal == null || worldPos == null ||
                px.Length != n || valid.Length != n || normal.Length != n || worldPos.Length != n)
            { error = "버퍼 크기 불일치"; return false; }
            if (s.Iterations <= 0) return true;
            if (!cs.HasKernel("CSDenoise")) { error = "커널 CSDenoise 없음"; return false; }

            var owned = new List<ComputeBuffer>();
            try
            {
                var cur = Upload(px, 16, owned);
                var other = Alloc(n, 16, owned);
                var vb = Upload(ValidBits(valid), 4, owned);
                var gn = Upload(Pad(normal), 16, owned);
                var gp = Upload(Pad(worldPos), 16, owned);
                DispatchDenoise(cs, ref cur, ref other, vb, gn, gp, w, h, s);
                cur.GetData(px);
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
            finally { foreach (var b in owned) b.Dispose(); }
        }

        /// <summary>페이지 1장(res×res) 후처리. 순서·의미는 CPU 판과 동일: (디노이즈) → Tier1 → Tier2 → dilation(채운 텍셀은 valid 로).</summary>
        public static bool Run(ComputeShader cs, Color[] px, bool[] valid, int res,
                               List<int[]> tier1Groups, List<LightmapSeamStitch.Seg[]> tier2Groups, Options opt, out string error)
        {
            error = null;
            int n = res * res;
            if (cs == null) { error = "셰이더 없음"; return false; }
            if (px == null || valid == null || px.Length != n || valid.Length != n) { error = "버퍼 크기 불일치"; return false; }
            bool doDn = opt.Denoise && opt.DenoiseSettings.Iterations > 0;
            if (doDn && (opt.GuideNormal == null || opt.GuidePos == null || opt.GuideNormal.Length != n || opt.GuidePos.Length != n))
            { error = "디노이즈 가이드 크기 불일치"; return false; }

            var p1 = opt.Tier1 && tier1Groups != null && tier1Groups.Count > 0 ? LightmapPostPlan.BuildTier1(tier1Groups, valid) : null;
            var p2 = opt.Tier2 && tier2Groups != null && tier2Groups.Count > 0 ? LightmapPostPlan.BuildTier2(tier2Groups, valid, res) : null;
            bool doT1 = p1 != null && p1.GroupStart.Length > 0;
            bool doT2 = p2 != null && p2.SampleStart.Length > 0;
            bool doDil = opt.DilateIterations > 0;
            if (!doDn && !doT1 && !doT2 && !doDil) return true;

            var kernels = new List<string> { "CSStitchT1", "CSStitchT2Sample", "CSStitchT2Gather", "CSDilate" };
            if (doDn) kernels.Add("CSDenoise");
            foreach (var k in kernels)
                if (!cs.HasKernel(k)) { error = $"커널 {k} 없음"; return false; }
            int kT1 = cs.FindKernel("CSStitchT1"), kS = cs.FindKernel("CSStitchT2Sample");
            int kG = cs.FindKernel("CSStitchT2Gather"), kD = cs.FindKernel("CSDilate");

            var owned = new List<ComputeBuffer>();
            ComputeBuffer Buf<T>(T[] data, int stride) where T : struct
            {
                var b = new ComputeBuffer(Math.Max(1, data.Length), stride, ComputeBufferType.Structured);
                owned.Add(b);
                if (data.Length > 0) b.SetData(data);
                return b;
            }

            try
            {
                var pxA = Buf(px, 16);
                ComputeBuffer final = pxA;
                ComputeBuffer finalValid = null;
                ComputeBuffer pxB = null, vA = null;

                // 디노이즈: valid 만 읽고 쓰는 핑퐁. 스티칭은 디노이즈 결과 위에서(CPU 판 순서 denoise → stitch → dilate).
                // 계획(p1·p2)은 valid 만 보므로 디노이즈 전에 만들어도 같다. 가이드 버퍼는 끝나면 바로 해제(최대 메모리 ↓).
                if (doDn)
                {
                    pxB = new ComputeBuffer(n, 16, ComputeBufferType.Structured); owned.Add(pxB);
                    vA = Buf(ValidBits(valid), 4);
                    using (var gn = new ComputeBuffer(n, 16, ComputeBufferType.Structured))
                    using (var gp = new ComputeBuffer(n, 16, ComputeBufferType.Structured))
                    {
                        gn.SetData(Pad(opt.GuideNormal));
                        gp.SetData(Pad(opt.GuidePos));
                        DispatchDenoise(cs, ref pxA, ref pxB, vA, gn, gp, res, res, opt.DenoiseSettings);
                    }
                    final = pxA;   // 핑퐁 후 결과가 든 쪽
                }

                if (doT1)
                {
                    var gs = Buf(p1.GroupStart, 4); var gc = Buf(p1.GroupCount, 4); var tx = Buf(p1.Texels, 4);
                    cs.SetBuffer(kT1, "_Px", pxA);
                    cs.SetBuffer(kT1, "_GroupStart", gs);
                    cs.SetBuffer(kT1, "_GroupCount", gc);
                    cs.SetBuffer(kT1, "_Texels", tx);
                    for (int w = 0; w < p1.WaveStart.Length; w++)
                    {
                        if (p1.WaveCount[w] == 0) continue;
                        cs.SetInt("_WaveStart", p1.WaveStart[w]);
                        Dispatch1D(cs, kT1, p1.WaveCount[w]);   // 웨이브 = 디스패치 → 앞 웨이브 쓰기가 다음 웨이브에 보인다
                    }
                }

                if (doT2)
                {
                    var ss = Buf(p2.SampleStart, 4); var sc = Buf(p2.SampleCount, 4); var st = Buf(p2.SampleTexels, 4);
                    var avg = new ComputeBuffer(Math.Max(1, p2.SampleStart.Length), 16, ComputeBufferType.Structured); owned.Add(avg);
                    var tt = Buf(p2.Texels, 4); var cst = Buf(p2.ContribStart, 4); var cc = Buf(p2.ContribCount, 4); var csm = Buf(p2.ContribSamples, 4);
                    cs.SetBuffer(kS, "_Px", pxA);
                    cs.SetBuffer(kS, "_SampleStart", ss);
                    cs.SetBuffer(kS, "_SampleCount", sc);
                    cs.SetBuffer(kS, "_SampleTexels", st);
                    cs.SetBuffer(kS, "_SampleAvg", avg);
                    cs.SetBuffer(kG, "_Px", pxA);
                    cs.SetBuffer(kG, "_SampleAvgIn", avg);
                    cs.SetBuffer(kG, "_TouchTexels", tt);
                    cs.SetBuffer(kG, "_ContribStart", cst);
                    cs.SetBuffer(kG, "_ContribCount", cc);
                    cs.SetBuffer(kG, "_ContribSamples", csm);
                    for (int it = 0; it < Mathf.Max(1, opt.Tier2Iterations); it++)
                    {
                        Dispatch1D(cs, kS, p2.SampleStart.Length);
                        Dispatch1D(cs, kG, p2.Texels.Length);
                    }
                }

                if (doDil)
                {
                    // 디노이즈·스티칭은 valid 를 바꾸지 않으므로 디노이즈 때 올린 마스크를 그대로 쓴다.
                    if (vA == null) vA = Buf(ValidBits(valid), 4);
                    if (pxB == null) { pxB = new ComputeBuffer(n, 16, ComputeBufferType.Structured); owned.Add(pxB); }
                    var vB = new ComputeBuffer(n, 4, ComputeBufferType.Structured); owned.Add(vB);
                    ComputeBuffer srcP = pxA, srcV = vA, dstP = pxB, dstV = vB;
                    cs.SetInt("_W", res);
                    cs.SetInt("_H", res);
                    int groups = (res + 7) / 8;
                    for (int it = 0; it < opt.DilateIterations; it++)
                    {
                        cs.SetBuffer(kD, "_SrcPx", srcP);
                        cs.SetBuffer(kD, "_SrcValid", srcV);
                        cs.SetBuffer(kD, "_DstPx", dstP);
                        cs.SetBuffer(kD, "_DstValid", dstV);
                        cs.Dispatch(kD, groups, groups, 1);
                        (srcP, dstP) = (dstP, srcP);
                        (srcV, dstV) = (dstV, srcV);
                    }
                    final = srcP;
                    finalValid = srcV;
                }

                final.GetData(px);
                if (finalValid != null)
                {
                    var vu = new uint[n];
                    finalValid.GetData(vu);
                    for (int i = 0; i < n; i++) valid[i] = vu[i] != 0u;
                }
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
            finally
            {
                cs.SetInt("_Base", 0);
                foreach (var b in owned) b.Dispose();
            }
        }

        // À-trous 패스 Iterations 회 핑퐁(cur → other). 끝나면 cur = 결과. σ 상수는 CPU 판(LightmapDenoise)과 같은 식·같은 float 연산.
        static void DispatchDenoise(ComputeShader cs, ref ComputeBuffer cur, ref ComputeBuffer other,
                                    ComputeBuffer valid, ComputeBuffer guideN, ComputeBuffer guideP, int w, int h, in DenoiseSettings s)
        {
            int k = cs.FindKernel("CSDenoise");
            float sigC = Mathf.Max(1e-6f, s.ColorSigma);
            cs.SetInt("_W", w);
            cs.SetInt("_H", h);
            cs.SetFloat("_NormalPower", s.NormalPower);
            cs.SetFloat("_InvTwoSigC2", 1f / (2f * sigC * sigC));
            cs.SetBuffer(k, "_SrcValid", valid);
            cs.SetBuffer(k, "_GuideN", guideN);
            cs.SetBuffer(k, "_GuideP", guideP);
            for (int it = 0; it < s.Iterations; it++)
            {
                int step = 1 << it;
                float sigP = Mathf.Max(1e-6f, s.PositionSigma * step);
                cs.SetInt("_Step", step);
                cs.SetFloat("_InvTwoSigP2", 1f / (2f * sigP * sigP));
                cs.SetBuffer(k, "_SrcPx", cur);
                cs.SetBuffer(k, "_DstPx", other);
                cs.Dispatch(k, (w + 7) / 8, (h + 7) / 8, 1);
                (cur, other) = (other, cur);
            }
        }

        static uint[] ValidBits(bool[] valid)
        {
            var vu = new uint[valid.Length];
            for (int i = 0; i < valid.Length; i++) vu[i] = valid[i] ? 1u : 0u;
            return vu;
        }

        // float3 구조화 버퍼는 플랫폼마다 stride 정렬이 달라 float4 로 올린다(w 미사용).
        static Vector4[] Pad(Vector3[] v)
        {
            var o = new Vector4[v.Length];
            for (int i = 0; i < v.Length; i++) o[i] = new Vector4(v[i].x, v[i].y, v[i].z, 0f);
            return o;
        }

        static ComputeBuffer Upload<T>(T[] data, int stride, List<ComputeBuffer> owned) where T : struct
        {
            var b = Alloc(data.Length, stride, owned);
            if (data.Length > 0) b.SetData(data);
            return b;
        }

        static ComputeBuffer Alloc(int count, int stride, List<ComputeBuffer> owned)
        {
            var b = new ComputeBuffer(Math.Max(1, count), stride, ComputeBufferType.Structured);
            owned.Add(b);
            return b;
        }

        static void Dispatch1D(ComputeShader cs, int kernel, int count)
        {
            cs.SetInt("_Count", count);
            foreach (var (start, c) in GpuBakeLimits.Tiles(count, GpuBakeLimits.MaxPointsPerDispatch))
            {
                cs.SetInt("_Base", start);
                cs.Dispatch(kernel, GpuBakeLimits.Groups(c), 1, 1);
            }
        }
    }
}
