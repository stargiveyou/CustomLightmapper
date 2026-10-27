
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// SH-2: per-instance SH9 베이크 — 인스턴스 대표점에서 '방향별 입사 조도 L(d)'를 구면 샘플로 모아 SH9 프로젝션.
    /// IJobParallelFor(인스턴스 병렬). G0(BurstTwoLevel) 순회 재사용.
    ///
    /// 입사 radiance 정의(1-bounce, 결정적) — 반사 규약 ρ/π·E(텍스처 경로·다바운스와 통일, v15.3):
    ///   L(d) = ClosestHit(p,d) 있으면  ρ ⊙ (E_direct/π + Sky(n_hit))  = ρ/π ⊙ (E_direct + π·Sky(n_hit))
    ///          없으면            sky.Radiance(d)
    ///   (π·Sky(n) = 가림 없는 균일 하늘 아래의 간접 조도 근사 → 이웃 표면의 앰비언트.)
    ///   → RNG 없음(고정 피보나치 방향셋) → 재현·검증 용이. 다바운스는 <see cref="BakeMultiBounce"/>(SHPathJob).
    /// SH엔 간접+환경만 담김(직사광은 이웃 반사로 들어오되, 인스턴스 자신의 직사광은 셰이더 실시간 — SH-5).
    /// L2(v15.18): 로컬 광원(BurstScene.lights)은 이웃 반사(히트점 NEE, 전 광원)에 더해, 셰이더가 실시간으로 그리지 않으므로
    /// Direct 플래그 광원의 '프로브가 직접 보는 빛'도 SH 에 투영(<see cref="LightSampling.ProjectSH{TQ}"/>). 광원 0개면 기존과 비트 동일.
    ///
    /// 주의: 대표점이 솔리드 메시 내부(AABB 중심)면 자기차폐로 어두워짐 → 점 배치는 호출측 책임
    ///       (얇은 프롭은 근사 OK, 큰 메시는 텍스처 경로). 씬은 자기 포함 전체를 차폐로 트레이스.
    /// 의존: BurstScene/BurstTwoLevel/BurstSky/SH9 (전달·미등록). 실측 게이트(G0~G3) 통과 전제.
    /// </summary>
    /// 
    public static class BurstSHBaker
    {
        [BurstCompile]
        public struct SHJob : IJobParallelFor
        {
            public BurstScene Scene;
            public BurstSky Sky;
            public DirectionalLight Sun;
            [ReadOnly] public NativeArray<Vector3> Points;   // 인스턴스 대표점
            [ReadOnly] public NativeArray<Vector3> Dirs;     // 구면 균등 방향셋(공유)
            public float Weight;                             // 4π/N
            [WriteOnly] public NativeArray<SH9> Out;

            public void Execute(int index)
            {
                Vector3 p = Points[index];

                var sh = new SH9();
                for (int s = 0; s < Dirs.Length; s++)
                {
                    Vector3 d = Dirs[s];
                    sh.Accumulate(d, Incoming(p, d), Weight);
                }
                // L2: Direct 플래그 로컬 광원이 프로브에 직접 주는 빛(태양 직사는 셰이더 실시간이라 제외). 광원 0개면 그대로.
                if (Scene.lightCount > 0) BurstLights.AddDirectSH(ref sh, Scene, p, (uint)index);
                Out[index] = sh;
            }

            Vector3 Incoming(Vector3 p, Vector3 d)
            {
                if (BurstTwoLevelBVH.ClosestHit(Scene, p, d, 1e-4f, float.MaxValue, out var hp, out var hn, out var albedo))
                {
                    // 이웃 표면 반사 radiance = ρ/π·(태양 조도(그림자 포함) + π·하늘 앰비언트).
                    // 앰비언트가 없으면 그늘진 이웃 방향이 정확히 0(검정)이 되어 하부 반구가
                    // 색-or-검정 이진 패치워크로 무너지고, L2 재구성 링잉→단색(빨강) 아티팩트를 키운다.
                    // (v15.3 전엔 태양 항이 ρ·E 로 π배 컸음 — 텍스처 경로와 규약 통일.)
                    Vector3 eD = DirectAt(hp, hn);
                    if (Scene.lightCount > 0) eD = BurstLights.AddBounce(eD, Scene, hp, hn);   // L2: 이웃 표면이 받은 로컬 광원(중심 1발)
                    Vector3 irr = eD / Mathf.PI + AmbientAt(hn);
                    return new Vector3(albedo.x * irr.x, albedo.y * irr.y, albedo.z * irr.z);
                }
                return Sky.Radiance(d); // 미스 : 하늘
            }

            private Vector3 DirectAt(Vector3 hp, Vector3 hn)
            {
                Vector3 L = -Sun.Direction.normalized;
                float ndl = Vector3.Dot(hn, L); // n·l : 히트 표면 노멀 × 광원 방향(EvaluateDirect 미러)
                if (ndl <= 0) return Vector3.zero;

                if (BurstTwoLevelBVH.Occluded(Scene, hp + hn * 1e-3f, L, 1e30f))
                    return Vector3.zero;
                return Sun.Color * Sun.Intensity * ndl;
            }

            // 그늘/뒷면 이웃도 하늘빛을 받아 반사한다(1-bounce 앰비언트 근사).
            // 결정적·저비용: 히트 노멀 방향 하늘 복사휘도를 앰비언트 조도로 사용 → 하부 반구에
            // 바닥(파랑 계열) DC 항이 생겨 '완전 검정' 제거 + G/B 채널 양수 유지(단색화 방지).
            private Vector3 AmbientAt(Vector3 hn) => Sky.Radiance(hn);
        }

        /// <summary>
        /// SH-2 다바운스(경로추적). 방향 d 마다 경로 1개 — BurstIndirect.IndirectJob 바운스 루프를 '첫 방향 고정'으로 미러:
        ///   미스 → Sky(d) / 히트 → ρ/π·(E_direct + E_indirect) (이후 바운스는 코사인 샘플·바운스별 NEE·RR).
        /// 텍스처 라이트맵과 같은 물리 규약(반사 radiance = ρ/π·E)이라 SH 평가 E(n) 이 같은 점·노멀의 텍셀 간접 조도와 일치.
        /// (SHJob 1바운스 근사도 같은 규약 — 이웃의 간접광만 균일 하늘 근사로 대체.)
        /// RNG: 방향별 독립 시드 → 바운스 수만 바꿔도 같은 경로 접두(공통 난수) + 재현 가능.
        /// </summary>
        [BurstCompile]
        public struct SHPathJob : IJobParallelFor
        {
            public BurstScene Scene;
            public BurstSky Sky;
            public DirectionalLight Sun;
            public BakeQualitySettings Q;                    // MaxBounces·RRStartDepth·RayBias (IndirectSamples 미사용: 방향당 경로 1개)
            public uint Seed;
            [ReadOnly] public NativeArray<Vector3> Points;
            [ReadOnly] public NativeArray<Vector3> Dirs;
            public float Weight;                             // 4π/N
            [WriteOnly] public NativeArray<SH9> Out;

            public void Execute(int index)
            {
                Vector3 p = Points[index];
                uint probeSeed = Seed + (uint)index * 2654435761u;   // 텍셀 시드 규약과 동일

                var sh = new SH9();
                for (int s = 0; s < Dirs.Length; s++)
                {
                    var rng = new Rng(Hash(probeSeed ^ Hash((uint)s + 1u)));
                    Vector3 d = Dirs[s];
                    sh.Accumulate(d, IncomingPath(p, d, ref rng), Weight);
                }
                if (Scene.lightCount > 0) BurstLights.AddDirectSH(ref sh, Scene, p, (uint)index);   // L2 직접광(SHJob 과 같은 규약)
                Out[index] = sh;
            }

            // π·L_in 을 BurstIndirect 와 같은 식으로 누적(히트: tp⊙ρ⊙E_direct, 미스: tp⊙Sky·π) 후 1/π → L_in(radiance).
            Vector3 IncomingPath(Vector3 p, Vector3 d, ref Rng rng)
            {
                Vector3 acc = Vector3.zero;
                Vector3 tp = Vector3.one;
                Vector3 dir = d;
                Vector3 o = p;
                float tmin = 1e-4f;                          // 첫 레이: SHJob 과 동일(대표점은 표면이 아님)

                for (int b = 0; ; b++)
                {
                    if (!BurstTwoLevelBVH.ClosestHit(Scene, o, dir, tmin, float.MaxValue, out Vector3 hp, out Vector3 hn, out Vector3 alb))
                    {
                        acc += Vector3.Scale(tp, Sky.Radiance(dir)) * Mathf.PI; // 미스: 하늘(π)
                        break;
                    }

                    Vector3 eD = DirectAt(hp, hn);
                    if (Scene.lightCount > 0) eD = BurstLights.AddBounce(eD, Scene, hp, hn);   // L2: 바운스 표면의 로컬 광원
                    acc += Vector3.Scale(tp, Vector3.Scale(alb, eD));
                    tp = Vector3.Scale(tp, alb);                              // throughput *= ρ

                    if (b + 1 >= Q.MaxBounces) break;

                    if (b + 1 >= Q.RRStartDepth)
                    {
                        float pSurv = Mathf.Clamp(Mathf.Max(tp.x, Mathf.Max(tp.y, tp.z)), 0.05f, 1f);
                        if (rng.Next() > pSurv) break;
                        tp /= pSurv;
                    }

                    dir = RadianceCore.CosineHemisphere(hn, ref rng);
                    o = hp + hn * Q.RayBias;
                    tmin = 0f;                               // 이후: BurstIndirect 와 동일(바이어스 오프셋 + tmin 0)
                }
                return acc / Mathf.PI;
            }

            // EvaluateDirect 미러(그림자 레이 origin 은 1e-3 하드코드)
            readonly Vector3 DirectAt(Vector3 hp, Vector3 hn)
            {
                Vector3 L = -Sun.Direction.normalized;
                float ndl = Vector3.Dot(L, hn);
                if (ndl <= 0f) return Vector3.zero;
                if (BurstTwoLevelBVH.Occluded(Scene, hp + hn * 1e-3f, L, 1e30f)) return Vector3.zero;
                return Sun.Color * Sun.Intensity * ndl;
            }

            // lowbias32 정수 해시 — 인접 (probe, dir) 시드의 xorshift 초기 상관 제거
            static uint Hash(uint x)
            {
                x ^= x >> 16; x *= 0x7feb352du;
                x ^= x >> 15; x *= 0x846ca68bu;
                x ^= x >> 16;
                return x;
            }
        }

        /// <summary>인스턴스별 SH9 베이크. 결과·중간 방향셋은 내부 관리(결과만 반환, 호출측 Dispose).</summary>
        public static NativeArray<SH9> Bake(in BurstScene scene, BurstSky sky, DirectionalLight sun, NativeArray<Vector3> points, int dirCount, Allocator alloc, int batch = 16)
        {
            var dirs = FibonacchiSphere(dirCount, Allocator.TempJob);
            var outp = new NativeArray<SH9>(points.Length, alloc);
            var job = new SHJob
            {
                Scene = scene,
                Sky = sky,
                Sun = sun,
                Points = points,
                Dirs = dirs,
                Weight = 4f * Mathf.PI / dirCount,
                Out = outp
            };
            job.Schedule(points.Length, batch).Complete();
            dirs.Dispose();
            return outp;
        }

        /// <summary>
        /// 다바운스(경로추적) SH9 베이크 — <see cref="SHPathJob"/>. q.MaxBounces = 표면 바운스 상한
        /// (1 = 이웃 직사 반사만, 앰비언트 근사 없음 → 그늘진 이웃은 0. 하늘빛 바운스를 담으려면 ≥2),
        /// q.RRStartDepth·q.RayBias 사용. 방향당 경로 1개라 노이즈는 dirCount 로 조절. 같은 seed 면 결과 동일.
        /// Burst 전용 — GPU(CSSHBake) 미러는 아직 1바운스 <see cref="Bake"/> 만.
        /// </summary>
        public static NativeArray<SH9> BakeMultiBounce(in BurstScene scene, BurstSky sky, DirectionalLight sun, BakeQualitySettings q,
            NativeArray<Vector3> points, int dirCount, uint seed, Allocator alloc, int batch = 4)
        {
            var dirs = FibonacchiSphere(dirCount, Allocator.TempJob);
            var outp = new NativeArray<SH9>(points.Length, alloc);
            var job = new SHPathJob
            {
                Scene = scene,
                Sky = sky,
                Sun = sun,
                Q = q,
                Seed = seed,
                Points = points,
                Dirs = dirs,
                Weight = 4f * Mathf.PI / dirCount,
                Out = outp
            };
            job.Schedule(points.Length, batch).Complete();
            dirs.Dispose();
            return outp;
        }

        //구면 균등(피보나치) 방향 n개 결정적
        //피보나치 스피어(Fibonacci Sphere)는 구(Sphere)의 표면에 점들을 완전히 균등하고 겹치지 않게 배치하는 수학적 알고리즘
        /*
            황금각 활용: 구 표면의 위도와 경도를 황금비(≈ 137.5°)를 기준으로 회전시키며 점을 나선형으로 찍어 나갑니다.
            완벽한 균등 분포: 구 전체에 점들이 뭉치거나 비는 곳 없이 일정하게 분산됩니다.
            입력 개수의 자유도: 구 표면을 동일한 크기의 다각형으로 나누는 정다면체 방식과 달리, 사용자가 원하는 임의의 개수만큼 점을 배치할 수 있습니다
        */
        public static NativeArray<Vector3> FibonacchiSphere(int n, Allocator alloc)
        {
            var pts = new NativeArray<Vector3>(n, alloc);
            float golden = Mathf.PI * (3.0f - Mathf.Sqrt(5f));
            for (int i = 0; i < n; i++)
            {
                float y = 1f - (i + 0.5f) / n * 2.0f;
                float r = Mathf.Sqrt(Mathf.Max(0, 1f - y * y));
                float theta = golden * i;
                pts[i] = new Vector3(r * Mathf.Cos(theta), y, r * Mathf.Sin(theta));
            }
            return pts;
        }


    }
}