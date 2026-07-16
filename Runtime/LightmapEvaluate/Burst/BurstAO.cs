using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using ReadOnlyAttribute = Unity.Collections.ReadOnlyAttribute;
namespace HuskyLibs.CustomLightmapper.Bake
{

    /// <summary>
    /// G1: Burst AO — 텍셀별 IJobParallelFor. G0 의 인터페이스 없는 순회(BurstTwoLevel.Occluded)를
    /// 처음으로 Job 으로 래핑. RadianceCore.CosineHemisphere·Rng 재사용 → CPU EvaluateAO 와 정확히 일치.
    ///
    /// AO = 1 - occ/samples (π 상쇄로 가시성 평균). o = point + n*1e-3(EvaluateAO 와 동일 하드코드).
    /// 시드 규약: baseSeed + texel*2654435761u (베이크 규약과 동일).
    /// </summary>


    public static class BurstAO
    {
        //IJobParallelFor + BurstCompile
        //  RadianceCore.CosinHemisphere로 반구샘플 -> BrustTwoLevel.Occluded로 차폐 -> AO = 1- occ/samples, o = point + n *1e-3
        //  BurstScene을 잡 필드로 읽기 전용 사용.

        [BurstCompile]
        public struct AoJob : IJobParallelFor
        {
            public BurstScene scene;                   // 읽기 전용 사용(차폐 질의)
            [ReadOnly] public NativeArray<Vector3> Points;
            [ReadOnly] public NativeArray<Vector3> Normals;
            [ReadOnly] public NativeArray<bool> Valid;
            public int Samples;
            public uint BaseSeed;
            public float MaxDist;
            [WriteOnly] public NativeArray<float> Ao;


            public void Execute(int index)
            {
                if (!Valid[index]) { Ao[index] = 0f; return; }
                uint seed = BaseSeed + (uint)index * 2654435761u;
                var rng = new Rng(seed);
                Vector3 n = Normals[index];
                Vector3 o = Points[index] + n * 1e-3f;

                int occ = 0;
                for (int s = 0; s < Samples; s++)
                {
                    Vector3 d = RadianceCore.CosineHemisphere(n, ref rng);
                    if (BurstTwoLevelBVH.Occluded(scene, o, d, MaxDist))
                        occ++;
                }
                Ao[index] = 1f - ((float)occ / Samples);


            }
        }


        /// <summary>AO 병렬 베이크. 결과 NativeArray 는 호출측이 Dispose.</summary>

        public static NativeArray<float> Compute(in BurstScene s, NativeArray<Vector3> points, NativeArray<Vector3> normals, NativeArray<bool> valid,
        int samples, uint baseSeed, float maxDist, Allocator resultAlloc, int batch = 32)
        {
            int n = points.Length;
            var ao = new NativeArray<float>(n, resultAlloc);
            var job = new AoJob
            {
                scene = s,
                Points = points,
                Normals = normals,
                Valid = valid,
                BaseSeed = baseSeed,
                Samples = samples,
                MaxDist = maxDist,
                Ao = ao

            };
            job.Schedule(n, batch).Complete();
            return ao;
        }

    }

}