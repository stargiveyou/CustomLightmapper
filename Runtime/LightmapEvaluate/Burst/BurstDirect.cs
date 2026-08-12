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

            // 태양 원반 샘플링(RadianceCore.EvaluateDirectSampled 미러). Samples≤1 이면 아래 기존 경로 그대로.
            public int Samples;
            [ReadOnly] public NativeArray<uint> Seeds;   // 샘플 패턴 위상 회전용(Samples≤1 이면 미참조)

            public void Execute(int index)
            {
                if (!Valid[index]) { Radiance[index] = Vector3.zero; return; }

                Vector3 n = Normals[index];
                Vector3 L = -Sun.Direction.normalized; // 광원 향함

                if (Samples > 1 && Sun.AngularDiameterDeg > 0f)
                {
                    Radiance[index] = SampledDirect(index, n, L);
                    return;
                }

                float ndl = Vector3.Dot(L, n);

                if (ndl <= 0f) { Radiance[index] = Vector3.zero; return; } // 백페이스

                if (BurstTwoLevelBVH.Occluded(scene, Points[index] + n * 1e-3f, L, 1e30f))
                {
                    Radiance[index] = Vector3.zero;
                    return;
                }
                Radiance[index] = Sun.Color * Sun.Intensity * ndl;

            }

            // RadianceCore.EvaluateDirectSampled 와 같은 식(같은 저불일치 수열·같은 기저).
            readonly Vector3 SampledDirect(int index, Vector3 n, Vector3 L)
            {
                float half = Sun.AngularDiameterDeg * 0.5f * Mathf.Deg2Rad;
                if (Vector3.Dot(L, n) <= -Mathf.Sin(half)) return Vector3.zero;

                RadianceCore.SunBasis(L, out Vector3 t, out Vector3 b);
                float cosHalf = Mathf.Cos(half);
                float rot = RadianceCore.SunConeRotation(Seeds[index]);
                Vector3 o = Points[index] + n * 1e-3f;

                float sum = 0f;
                for (int i = 0; i < Samples; i++)
                {
                    Vector3 d = RadianceCore.SunConeDirection(L, t, b, cosHalf, i, Samples, rot);
                    float ndl = Vector3.Dot(d, n);
                    if (ndl <= 0f) continue;
                    if (BurstTwoLevelBVH.Occluded(scene, o, d, 1e30f)) continue;
                    sum += ndl;
                }
                return Sun.Color * Sun.Intensity * (sum / Samples);
            }
        }

        /// <summary>단발 그림자 레이(기존 규약). 결과는 알파/샘플링 도입 전과 비트동일.</summary>
        public static NativeArray<Vector3> Compute(in BurstScene scene,
        NativeArray<Vector3> points, NativeArray<Vector3> normals, NativeArray<bool> valid,
        DirectionalLight sun, Allocator resultAlloc, int batch = 32)
            => Compute(scene, points, normals, valid, sun, 1, default, resultAlloc, batch);

        /// <summary>
        /// 태양 원반 샘플링 버전. <paramref name="samples"/>≤1 이면 단발 경로와 동일.
        /// <paramref name="seeds"/> 는 텍셀별 샘플 패턴 위상(미할당이면 내부에서 더미 생성).
        /// </summary>
        public static NativeArray<Vector3> Compute(in BurstScene scene,
        NativeArray<Vector3> points, NativeArray<Vector3> normals, NativeArray<bool> valid,
        DirectionalLight sun, int samples, NativeArray<uint> seeds, Allocator resultAlloc, int batch = 32)
        {
            int n = points.Length;
            var rad = new NativeArray<Vector3>(n, resultAlloc);

            // 잡 안전 시스템: NativeArray 필드는 스케줄 시 항상 할당돼 있어야 한다.
            // 샘플링을 쓰는데 시드를 안 넘겼으면 텍셀별로 서로 다른 위상을 만들어 준다(밴딩 방지).
            bool ownSeeds = !seeds.IsCreated;
            NativeArray<uint> seedArr;
            if (!ownSeeds) seedArr = seeds;
            else if (samples > 1)
            {
                seedArr = new NativeArray<uint>(n, Allocator.TempJob);
                for (int i = 0; i < n; i++) seedArr[i] = (uint)(i + 1);
            }
            else seedArr = new NativeArray<uint>(1, Allocator.TempJob);

            var job = new DirectJob()
            {
                scene = scene,
                Points = points,
                Normals = normals,
                Valid = valid,
                Sun = sun,
                Samples = samples,
                Seeds = seedArr,
                Radiance = rad
            };
            job.Schedule(n, batch).Complete();
            if (ownSeeds) seedArr.Dispose();
            return rad;
        }

    }

}