using System;
using System.Collections.Generic;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 라이트맵 후처리 GPU 판(LightmapPost.compute): 페이지 1장을 업로드 1회 → Tier1 웨이브 → Tier2(샘플 → 수집, Jacobi 반복)
    /// → dilation 핑퐁 → readback 1회. 계획은 <see cref="LightmapPostPlan"/>(CPU 판과 같은 텍셀·같은 덧셈 순서) →
    /// 결과 = CPU 판(<see cref="LightmapSeamStitch"/> + <see cref="LightmapPostProcessBurstJob"/>)과 나눗셈 ULP 차이 이내.
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

        /// <summary>페이지 1장(res×res) 후처리. 순서·의미는 CPU 판과 동일: Tier1 → Tier2 → dilation(채운 텍셀은 valid 로).</summary>
        public static bool Run(ComputeShader cs, Color[] px, bool[] valid, int res,
                               List<int[]> tier1Groups, List<LightmapSeamStitch.Seg[]> tier2Groups, Options opt, out string error)
        {
            error = null;
            int n = res * res;
            if (cs == null) { error = "셰이더 없음"; return false; }
            if (px == null || valid == null || px.Length != n || valid.Length != n) { error = "버퍼 크기 불일치"; return false; }

            var p1 = opt.Tier1 && tier1Groups != null && tier1Groups.Count > 0 ? LightmapPostPlan.BuildTier1(tier1Groups, valid) : null;
            var p2 = opt.Tier2 && tier2Groups != null && tier2Groups.Count > 0 ? LightmapPostPlan.BuildTier2(tier2Groups, valid, res) : null;
            bool doT1 = p1 != null && p1.GroupStart.Length > 0;
            bool doT2 = p2 != null && p2.SampleStart.Length > 0;
            bool doDil = opt.DilateIterations > 0;
            if (!doT1 && !doT2 && !doDil) return true;

            string[] kernels = { "CSStitchT1", "CSStitchT2Sample", "CSStitchT2Gather", "CSDilate" };
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
                    var vu = new uint[n];
                    for (int i = 0; i < n; i++) vu[i] = valid[i] ? 1u : 0u;
                    var vA = Buf(vu, 4);
                    var pxB = new ComputeBuffer(n, 16, ComputeBufferType.Structured); owned.Add(pxB);
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
