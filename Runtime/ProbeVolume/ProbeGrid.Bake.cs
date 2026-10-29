using System;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    // ProbeGrid 베이크 연결(partial) — 배치·샘플은 ProbeGrid.cs. 여기는 Burst/GPU SH 베이커에 위치를 넘기는 것뿐(신규 커널 없음).
    public partial struct ProbeGrid
    {
        // ───────────────────────── 베이크 (재사용) ─────────────────────────

        /// <summary>GPU 커널 이름 — <see cref="BakeGpu"/> 에 넘길 kernel 은 이 이름으로 FindKernel.</summary>
        public static string GpuKernelName(BakeQualitySettings q) => q.MaxBounces > 1 ? "CSSHBakeMulti" : "CSSHBake";

        /// <summary>
        /// 전 프로브 Burst 베이크(선형 인덱스 순서). q.MaxBounces ≤ 1 → <see cref="BurstSHBaker.Bake"/>(1바운스+앰비언트 근사, 결정적),
        /// ≥ 2 → <see cref="BurstSHBaker.BakeMultiBounce"/>(경로추적) — <see cref="InstanceSceneBaker"/> 의 bounces 규약과 동일.
        /// </summary>
        public readonly SH9[] BakeBurst(in BurstScene scene, BurstSky sky, DirectionalLight sun, BakeQualitySettings q, int dirCount, uint seed)
            => BakeBurstAt(scene, sky, sun, q, Positions(), dirCount, seed);

        /// <summary>
        /// 임의 프로브 위치 배열의 Burst 베이크(배열 순서 = SH 순서 = 다바운스 시드 인덱스). <see cref="BakeBurst"/> 가 격자 위치로 부르는 본체 —
        /// B2 지형 추종 배치(<see cref="ProbeTerrainLayers.Positions"/>)도 같은 경로.
        /// </summary>
        public static SH9[] BakeBurstAt(in BurstScene scene, BurstSky sky, DirectionalLight sun, BakeQualitySettings q, Vector3[] positions, int dirCount, uint seed)
        {
            var pts = new NativeArray<Vector3>(positions, Allocator.TempJob);
            try
            {
                var outp = q.MaxBounces > 1
                    ? BurstSHBaker.BakeMultiBounce(scene, sky, sun, q, pts, dirCount, seed, Allocator.TempJob)
                    : BurstSHBaker.Bake(scene, sky, sun, pts, dirCount, Allocator.TempJob);
                var arr = outp.ToArray();
                outp.Dispose();
                return arr;
            }
            finally { pts.Dispose(); }
        }

        /// <summary>
        /// 전 프로브 GPU 베이크 — kernel 은 <see cref="GpuKernelName"/>(q) 로 찾은 것. 방향셋은 Burst 와 같은 피보나치를 CPU 에서 계산해
        /// 업로드(교차검증 계약). 프로브 수 ≤ <see cref="MaxGpuProbes"/>.
        /// </summary>
        public readonly SH9[] BakeGpu(GpuScene gpu, ComputeShader cs, int kernel, DirectionalLight sun, BurstSky sky,
                                      BakeQualitySettings q, int dirCount, uint seed)
            => BakeGpuAt(gpu, cs, kernel, sun, sky, q, Positions(), dirCount, seed);

        /// <summary>임의 프로브 위치 배열의 GPU 베이크(<see cref="BakeBurstAt"/> 의 GPU 판). 위치 수 ≤ <see cref="MaxGpuProbes"/>.</summary>
        public static SH9[] BakeGpuAt(GpuScene gpu, ComputeShader cs, int kernel, DirectionalLight sun, BurstSky sky,
                                      BakeQualitySettings q, Vector3[] positions, int dirCount, uint seed)
        {
            if (positions.Length > MaxGpuProbes)
                throw new ArgumentOutOfRangeException(nameof(positions), $"프로브 {positions.Length}개 > GPU 디스패치 한도 {MaxGpuProbes}");
            var dirsNa = BurstSHBaker.FibonacchiSphere(dirCount, Allocator.Temp);
            var dirs = dirsNa.ToArray();
            dirsNa.Dispose();
            float w = 4f * Mathf.PI / dirCount;
            return q.MaxBounces > 1
                ? GpuSHBaker.BakeMultiBounce(gpu, cs, kernel, sun, sky, q, seed, positions, dirs, w)
                : GpuSHBaker.Bake(gpu, cs, kernel, sun, sky, positions, dirs, w);
        }
    }
}
