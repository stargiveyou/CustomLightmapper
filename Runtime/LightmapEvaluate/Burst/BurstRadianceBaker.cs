using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Burst 베이크 백엔드: 라이트맵 텍셀값(조도) = Direct(G2) + Indirect(G3). AtlasApplyDebug 등이 호출.
    /// EvaluateRadiance(scene) = Direct + Indirect 와 동일 합성. per-texel 시드로 CPU 와 비트 동일.
    /// </summary>
    public static class BurstRadianceBaker
    {
        /// <summary>Burst 경로. seeds 는 per-texel(베이크 시드 규약과 동일하게 채워 넘김). 결과는 호출측 Dispose.</summary>
        //Direct(G2) + Indirect(G3) = 라이트맵 조도.
        public static NativeArray<Vector3> Bake(in BurstScene scene, BurstSky sky, DirectionalLight sun, BakeQualitySettings q,
        NativeArray<Vector3> points, NativeArray<Vector3> normals, NativeArray<bool> valid,
        NativeArray<uint> seeds, Allocator allocator)
        {
            // 1차 직사광만 태양 원반 샘플링(q.DirectSamples). 바운스 NEE 는 BurstIndirect 안에서 1발 유지.
            var direct = BurstDirect.Compute(scene, points, normals, valid, sun, q.DirectSamples, seeds, allocator);
            var ind = BurstIndirect.Compute(scene, sky, sun, q, points, normals, valid, seeds, allocator);

            int n = points.Length;
            var rad = new NativeArray<Vector3>(n, allocator);
            for (int i = 0; i < n; i++) rad[i] = direct[i] + ind[i];

            direct.Dispose();
            ind.Dispose();
            return rad;
        }

        public static Vector3[] BakeCPU(IRadianceScene scene, DirectionalLight sun, ISky sky, BakeQualitySettings q, Vector3[] points, Vector3[] normals, bool[] valid, uint[] seeds)
        {
            int n = points.Length;
            var r = new Vector3[n];
            for (int i = 0; i < n; i++)
                r[i] = valid[i] ? RadianceCore.EvaluateRadiance(scene, points[i], normals[i], sun, sky, q, seeds[i]) : Vector3.zero;
            return r;
        }



    }
}