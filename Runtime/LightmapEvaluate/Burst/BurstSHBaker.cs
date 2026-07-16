
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
    /// 입사 조도 정의(1-bounce, 결정적):
    ///   L(d) = ClosestHit(p,d) 있으면  이웃표면 알베도 ⊙ 직사광반사(EvaluateDirect 미러)
    ///          없으면            sky.Radiance(d)
    ///   → RNG 없음(고정 피보나치 방향셋) → 재현·검증 용이. 다바운스는 추후 확장(EvaluateIndirect 접목).
    /// SH엔 간접+환경만 담김(직사광은 이웃 반사로 들어오되, 인스턴스 자신의 직사광은 셰이더 실시간 — SH-5).
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
                Out[index] = sh;
            }

            Vector3 Incoming(Vector3 p, Vector3 d)
            {
                if (BurstTwoLevelBVH.ClosestHit(Scene, p, d, 1e-4f, float.MaxValue, out var hp, out var hn, out var albedo))
                {
                    // 이웃 표면이 받는 조도 = 태양(그림자 포함) + 하늘 앰비언트.
                    // 앰비언트가 없으면 그늘진 이웃 방향이 정확히 0(검정)이 되어 하부 반구가
                    // 색-or-검정 이진 패치워크로 무너지고, L2 재구성 링잉→단색(빨강) 아티팩트를 키운다.
                    Vector3 irr = DirectAt(hp, hn) + AmbientAt(hn);
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