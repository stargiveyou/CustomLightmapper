using UnityEngine;


namespace HuskyLibs.CustomLightmapper.Bake
{


    /// <summary>결정적 per-texel 난수(xorshift32).</summary>
    /// xorshift32 기반 -> Monte Carlo 적분용
    public struct Rng
    {
        private uint _s;
        public Rng(uint seed) { _s = seed == 0 ? 1u : seed; }

        public float Next()
        {
            _s ^= _s << 13;
            _s ^= _s >> 17;
            _s ^= _s << 5;
            return (_s & 0xFFFFFF) / 16777216f;
        }

    }



    public struct DirectionalLight
    {
        public Vector3 Direction;
        public Vector3 Color;
        public float Intensity;

    }
    // === 후속 ===
    // Indirect(RR path tracing, min-bounce) · point/area light · alpha-cutout any-hit

    /// <summary>
    /// 베이크 품질 파라미터(12). CPU/GPU 공유 제약
    /// </summary>
    [System.Serializable]
    public struct BakeQualitySettings
    {
        public int AoSamples;        // 반구 AO 레이 수
        public int IndirectSamples;  // spp: 텍셀당 간접 경로 수
        public int MaxBounces;       // 표면 바운스 상한
        public int RRStartDepth;     // 이 깊이부터 Russian Roulette
        public float RayBias;        // 자기교차 방지 오프셋

        public static BakeQualitySettings Default => new BakeQualitySettings
        {
            AoSamples = 64,
            IndirectSamples = 64,
            MaxBounces = 1,      // ⑫ 검증 시작값: 1바운스
            RRStartDepth = 3,
            RayBias = 1e-4f
        };

        public static BakeQualitySettings HighQuality => new BakeQualitySettings
        {
            AoSamples = 256,
            IndirectSamples = 256,
            MaxBounces = 8,
            RRStartDepth = 3,
            RayBias = 1e-4f
        };


    }

    /// <summary>
    /// C2 공유 radiance Core. 두 트랙 공통 진입점.
    /// 현재 A0 구현. Direct(NEE) . Indirect(RR)는 같은 시그니처에 단계적으로 추가.
    /// 라이트맵은 irradiance(조도)를 저장 -> 알베도는 런타임에 적용
    /// NEE   : Next Event Estimation
    /// RR    :  Russian Roulette
    /// </summary>
    public class RadianceCore
    {
        /// 공통 진입점(현재는 A0) 라이팅이 채워지면 여기로 통합.
        public static float EvaluateAO(IOccluder occluder, Vector3 point, Vector3 normal, int samples, uint seed, float maxDist = float.MaxValue)
        {
            var rng = new Rng(seed);
            Vector3 o = point + normal * 1e-3f; // self-hit 방지 바이어스
            int occ = 0;
            for (int s = 0; s < samples; s++)
            {
                Vector3 d = CosineHemisphere(normal, ref rng);
                if (occluder.Occluded(o, d, maxDist)) occ++;
            }
            return 1f - (float)occ / samples; // 1 = 열림, 0 = 막힘
        }

        /// <summary>
        ///  코사인 가중 반구 샘플 (노멀 기준)
        ///  TBN Space
        /// </summary>
        /// <param name="n"></param>
        /// <param name="rng"></param>
        /// <returns></returns>
        public static Vector3 CosineHemisphere(Vector3 n, ref Rng rng)
        {
            float r1 = rng.Next();
            float r2 = rng.Next();
            float st = Mathf.Sqrt(r1);
            float phi = 2 * Mathf.PI * r2;

            float lx = st * Mathf.Cos(phi);
            float ly = st * Mathf.Sin(phi);
            float lz = Mathf.Sqrt(Mathf.Max(0f, 1f - r1));

            Vector3 up = Mathf.Abs(n.x) < 0.9f ? Vector3.right : Vector3.up;
            Vector3 t = Vector3.Cross(n, up).normalized;
            Vector3 b = Vector3.Cross(n, t);
            return (t * lx + b * ly + n * lz).normalized;
        }

        // -- Direct Lighting ( NEE ) -> 디렉셔널 광원 + 쉐도우 레이 -- //
        public static Vector3 EvaluateDirect(IOccluder occluder, Vector3 p, Vector3 n, DirectionalLight sun)
        {
            Vector3 L = -sun.Direction.normalized; // 광원을 향하는 방향
            float ndl = Vector3.Dot(L, n);
            if (ndl <= 0f) return Vector3.zero;
            if (occluder.Occluded(p + n * 1e-3f, L, 1e30f))
                return Vector3.zero; // 그림자

            return sun.Color * sun.Intensity * ndl;


        }

        // -- 통합 진입점 : direct(그림자 포함) + ambient*AO -> Linear RGB -- 
        public static Vector3 EvaluateRadiance(IOccluder occluder, Vector3 p, Vector3 n, DirectionalLight sun, Vector3 ambient, int aoSamples, uint seed)
        {
            Vector3 direct = EvaluateDirect(occluder, p, n, sun);
            float ao = EvaluateAO(occluder, p, n, aoSamples, seed);
            return direct + ambient * ao;
        }

        /// <summary>
        /// 간접 조도(경로추적 + RR). 코사인 중요도 샘플이라 히트에서 π·(1/π) 상쇄 → 알베도만 누적,
        /// π는 하늘(미스) 항에만 남는다. 점 p의 알베도는 적용하지 않음(런타임 적용).
        /// </summary>
        public static Vector3 EvaluateIndirect(IRadianceScene scene, Vector3 p, Vector3 n, DirectionalLight sun, ISky sky, in BakeQualitySettings q, uint seed)
        {
            var rng = new Rng(seed);
            Vector3 sum = Vector3.zero;


            for (int i = 0; i < q.IndirectSamples; i++)
            {
                Vector3 acc = Vector3.zero;
                Vector3 tp = Vector3.one;
                Vector3 dir = CosineHemisphere(n, ref rng);
                Vector3 o = p + n * q.RayBias;

                for (int b = 0; ; b++)
                {
                    if (!scene.ClosestHit(o, dir, 0f, float.MaxValue, out Vector3 hp, out Vector3 hn, out Vector3 alb))
                    {
                        // 미스: 하늘. 반구 적분의 π는 여기에만 남는다.
                        acc += Vector3.Scale(tp, sky.Radiance(dir)) * Mathf.PI;
                        break;

                    }
                    // 바운스 표면의 직접 조도(태양)를 알베도로 반사 → tp·ρ·E_direct(q)
                    Vector3 eD = EvaluateDirect(scene.Occluder, hp, hn, sun);
                    acc += Vector3.Scale(tp, Vector3.Scale(alb, eD));

                    tp = Vector3.Scale(tp, alb); // // throughput *= ρ

                    if (b + 1 >= q.MaxBounces) break;

                    if (b + 1 >= q.RRStartDepth)
                    {
                        float pSurv = Mathf.Clamp(Mathf.Max(tp.x, Mathf.Max(tp.y, tp.z)), 0.05f, 1f);
                        if (rng.Next() > pSurv) break;
                        tp /= pSurv; // 무편향 보정
                    }

                    dir = CosineHemisphere(hn, ref rng);
                    o = hp + hn * q.RayBias;
                }
                sum += acc;
            }
            return sum / (float)q.IndirectSamples;
        }


        ///<summary> 전체 경로 추적 라이트맵 값 (조도) 
        ///         Direct(sun) + InDirect(scene, key) </summary>
        /// <summary>전체 경로추적 라이트맵 값(조도) = Direct(sun) + Indirect(scene, sky).</summary>
        public static Vector3 EvaluateRadiance(IRadianceScene scene, Vector3 p, Vector3 n,
                                               DirectionalLight sun, ISky sky,
                                               in BakeQualitySettings q, uint seed)
        {
            Vector3 direct = EvaluateDirect(scene.Occluder, p, n, sun);
            Vector3 indirect = EvaluateIndirect(scene, p, n, sun, sky, q, seed);
            return direct + indirect;
        }
 



    }


}