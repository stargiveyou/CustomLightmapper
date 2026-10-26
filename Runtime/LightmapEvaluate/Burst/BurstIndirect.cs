using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    // <summary>
    /// G3 ★: Burst Indirect(경로추적 + RR) — 텍셀별 IJobParallelFor.
    /// RadianceCore.EvaluateIndirect 의 '반복형 바운스 루프'를 그대로 미러:
    ///   코사인 중요도 샘플(π·1/π 상쇄 → 알베도만 누적, π는 하늘 미스 항에만),
    ///   바운스별 직접 조도(NEE), throughput*=ρ, RRStartDepth부터 Russian Roulette.
    /// CosineHemisphere·Rng·ClosestHit·Occluded(전부 ≡CPU) 재사용 → 정확 일치.
    /// 라이트맵=조도(점 알베도 미적용).
    /// 경로 1개 = <see cref="TracePath"/>(RadianceCore.TracePath 미러) — 일괄 잡(IndirectJob)과 이어 굽기 잡(AccumJob)의 단일 진실원.
    /// </summary>
    public static class BurstIndirect
    {
        [BurstCompile]
        public struct IndirectJob : IJobParallelFor
        {
            public BurstScene Scene;
            public BurstSky Sky;
            public DirectionalLight Sun;
            public BakeQualitySettings Q;
            [ReadOnly] public NativeArray<uint> Seeds; // per-texel 시드(베이크 규약). baseSeed 오버로드가 seed_i 를 채워 넘김
            [ReadOnly] public NativeArray<Vector3> Points;
            [ReadOnly] public NativeArray<Vector3> Normals;
            [ReadOnly] public NativeArray<bool> Valid;
            [WriteOnly] public NativeArray<Vector3> Indirect;

            public void Execute(int i)
            {
                if (!Valid[i]) { Indirect[i] = Vector3.zero; return; }

                var rng = new Rng(Seeds[i]);
                Vector3 n = Normals[i], p = Points[i];
                Vector3 sum = Vector3.zero;

                for (int sp = 0; sp < Q.IndirectSamples; sp++)
                    sum += TracePath(Scene, Sky, Sun, Q, p, n, ref rng);
                Indirect[i] = sum / (float)Q.IndirectSamples;
            }
        }

        /// <summary>
        /// 이어 굽기(진행형·적응 샘플링) — RadianceCore.AccumulateIndirect 미러. 활성 점 i = Active[j] 만
        /// 상태(State)·합(Sum)·휘도 제곱합(M2)을 읽어 경로 Samples 개를 더 누적하고 되쓴다(점마다 자기 칸만 → 병렬 안전).
        /// 시작 상태 = new Rng(seed).State · Sum = 0 이면 N 개 누적 후 Sum/N 이 IndirectJob(N spp) 와 비트 동일.
        /// </summary>
        [BurstCompile]
        public struct AccumJob : IJobParallelFor
        {
            public BurstScene Scene;
            public BurstSky Sky;
            public DirectionalLight Sun;
            public BakeQualitySettings Q;
            public int Samples;
            [ReadOnly] public NativeArray<int> Active;
            [ReadOnly] public NativeArray<Vector3> Points;
            [ReadOnly] public NativeArray<Vector3> Normals;
            [NativeDisableParallelForRestriction] public NativeArray<uint> State;
            [NativeDisableParallelForRestriction] public NativeArray<Vector3> Sum;
            [NativeDisableParallelForRestriction] public NativeArray<float> M2;

            public void Execute(int j)
            {
                int i = Active[j];
                var rng = new Rng(State[i]);
                Vector3 n = Normals[i], p = Points[i];
                Vector3 sum = Sum[i];
                float m2 = M2[i];
                for (int sp = 0; sp < Samples; sp++)
                {
                    Vector3 acc = TracePath(Scene, Sky, Sun, Q, p, n, ref rng);
                    sum += acc;
                    float l = RadianceCore.Luminance(acc);
                    m2 += l * l;
                }
                State[i] = rng.State;
                Sum[i] = sum;
                M2[i] = m2;
            }
        }

        /// <summary>경로 1개 — RadianceCore.TracePath 와 같은 식·같은 RNG 소비 순서.</summary>
        public static Vector3 TracePath(in BurstScene scene, in BurstSky sky, in DirectionalLight sun, in BakeQualitySettings q,
                                        Vector3 p, Vector3 n, ref Rng rng)
        {
            Vector3 acc = Vector3.zero;
            Vector3 tp = Vector3.one;
            Vector3 dir = RadianceCore.CosineHemisphere(n, ref rng);
            Vector3 o = p + n * q.RayBias;

            for (int b = 0; ; b++)
            {
                if (!BurstTwoLevelBVH.ClosestHit(scene, o, dir, 0f, float.MaxValue, out Vector3 hp, out Vector3 hn, out Vector3 alb))
                {
                    acc += Vector3.Scale(tp, sky.Radiance(dir)) * Mathf.PI; // 미스: 하늘(π)
                    break;
                }

                Vector3 eD = DirectAt(scene, sun, hp, hn);               // 바운스 표면 직접 조도
                acc += Vector3.Scale(tp, Vector3.Scale(alb, eD));
                tp = Vector3.Scale(tp, alb);                              // throughput *= ρ

                if (b + 1 >= q.MaxBounces) break;

                if (b + 1 >= q.RRStartDepth)
                {
                    float pSurv = Mathf.Clamp(Mathf.Max(tp.x, Mathf.Max(tp.y, tp.z)), 0.05f, 1f);
                    if (rng.Next() > pSurv) break;
                    tp /= pSurv;
                }

                dir = RadianceCore.CosineHemisphere(hn, ref rng);
                o = hp + hn * q.RayBias;
            }
            return acc;
        }

        // EvaluateDirect 미러(그림자 레이 origin 은 1e-3 하드코드)
        static Vector3 DirectAt(in BurstScene scene, in DirectionalLight sun, Vector3 hp, Vector3 hn)
        {
            Vector3 L = -sun.Direction.normalized;
            float ndl = Vector3.Dot(L, hn);
            if (ndl <= 0f) return Vector3.zero;
            if (BurstTwoLevelBVH.Occluded(scene, hp + hn * 1e-3f, L, 1e30f)) return Vector3.zero;
            return sun.Color * sun.Intensity * ndl;
        }

        /// <summary>per-texel 시드 배열 버전(베이크 시드 규약 일치용).</summary>
        public static NativeArray<Vector3> Compute(
            in BurstScene scene, BurstSky sky, DirectionalLight sun, BakeQualitySettings q,
            NativeArray<Vector3> points, NativeArray<Vector3> normals, NativeArray<bool> valid,
            NativeArray<uint> seeds, Allocator resultAlloc, int batch = 16)
        {
            int n = points.Length;
            var ind = new NativeArray<Vector3>(n, resultAlloc);
            var job = new IndirectJob
            {
                Scene = scene,
                Sky = sky,
                Sun = sun,
                Q = q,
                Seeds = seeds,
                Points = points,
                Normals = normals,
                Valid = valid,
                Indirect = ind
            };
            job.Schedule(n, batch).Complete();
            return ind;
        }

        /// <summary>baseSeed 버전: seed_i = baseSeed + i*2654435761u (BurstAO·교차검증 규약). 내부에서 per-texel 배열 생성.
        /// Job 안전 시스템상 Seeds 필드는 스케줄 시 항상 할당돼야 하므로 fallback 대신 명시 배열을 만든다.</summary>
        public static NativeArray<Vector3> Compute(
            in BurstScene scene, BurstSky sky, DirectionalLight sun, BakeQualitySettings q,
            NativeArray<Vector3> points, NativeArray<Vector3> normals, NativeArray<bool> valid,
            uint baseSeed, Allocator resultAlloc, int batch = 16)
        {
            int n = points.Length;
            var seeds = new NativeArray<uint>(n, Allocator.TempJob);
            for (int i = 0; i < n; i++) seeds[i] = baseSeed + (uint)i * 2654435761u;
            var ind = Compute(scene, sky, sun, q, points, normals, valid, seeds, resultAlloc, batch);
            seeds.Dispose();
            return ind;
        }

        /// <summary>
        /// 이어 굽기 1패스: <paramref name="active"/> 의 앞 <paramref name="activeCount"/> 개 점에 경로 <paramref name="samples"/> 개씩
        /// 더 누적(State/Sum/M2 제자리 갱신 — 길이 = 전체 점 수, 호출측 소유).
        /// </summary>
        public static void Accumulate(
            in BurstScene scene, BurstSky sky, DirectionalLight sun, BakeQualitySettings q,
            NativeArray<Vector3> points, NativeArray<Vector3> normals,
            NativeArray<uint> state, NativeArray<Vector3> sum, NativeArray<float> m2,
            NativeArray<int> active, int activeCount, int samples, int batch = 16)
        {
            if (activeCount <= 0 || samples <= 0) return;
            var job = new AccumJob
            {
                Scene = scene,
                Sky = sky,
                Sun = sun,
                Q = q,
                Samples = samples,
                Active = active,
                Points = points,
                Normals = normals,
                State = state,
                Sum = sum,
                M2 = m2,
            };
            job.Schedule(activeCount, batch).Complete();
        }
    }
}
