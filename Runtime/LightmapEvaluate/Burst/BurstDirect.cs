using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using ReadOnlyAttribute = Unity.Collections.ReadOnlyAttribute;
namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// G2: Burst Direct(NEE + 그림자) — 텍셀별 IJobParallelFor.
    /// RadianceCore.EvaluateDirect 와 '동일 식', 차폐는 BurstTwoLevel.Occluded(≡CPU) → 정확 일치.
    ///
    ///  L = -sun.Direction.normalized ; ndl = dot(L,n) ; ndl<=0 → 0 ;
    ///  Occluded(p + n*1e-3, L, 1e30) → 0(그림자) ; else sun.Color*sun.Intensity*ndl.
    /// 라이트맵은 조도 저장 → 알베도 미적용(EvaluateDirect 와 동일).
    /// </summary>




    public static class BurstDirect
    {

        [BurstCompile]
        public struct DirectJob : IJobParallelFor
        {
            public BurstScene scene;
            [ReadOnly] public NativeArray<Vector3> Points;
            [ReadOnly] public NativeArray<Vector3> Normals;
            [ReadOnly] public NativeArray<bool> Valid;

            public DirectionalLight Sun;
            [WriteOnly] public NativeArray<Vector3> Radiance;


            public void Execute(int index)
            {
                if (!Valid[index]) { Radiance[index] = Vector3.zero; return; }

                Vector3 n = Normals[index];
                Vector3 L = -Sun.Direction.normalized; // 광원 향함
                float ndl = Vector3.Dot(L, n);

                if (ndl <= 0f) { Radiance[index] = Vector3.zero; return; } // 백페이스

                if (BurstTwoLevelBVH.Occluded(scene, Points[index] + n * 1e-3f, L, 1e30f))
                {
                    Radiance[index] = Vector3.zero;
                    return;
                }
                Radiance[index] = Sun.Color * Sun.Intensity * ndl;

            }
        }

        public static NativeArray<Vector3> Compute(in BurstScene scene,
        NativeArray<Vector3> points, NativeArray<Vector3> normals, NativeArray<bool> valid,
        DirectionalLight sun, Allocator resultAlloc, int batch = 32)
        {
            int n = points.Length;
            var rad = new NativeArray<Vector3>(n, resultAlloc);
            var job = new DirectJob()
            {
                scene = scene,
                Points = points,
                Normals = normals,
                Valid = valid,
                Sun = sun,
                Radiance = rad
            };
            job.Schedule(n, batch).Complete();
            return rad;
        }

    }

}