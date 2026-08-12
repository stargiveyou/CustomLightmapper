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

        /// <summary>
        /// 태양 원반의 각지름(도). 0 = 점광원(하드 그림자, 기존 거동).
        /// 실제 태양은 약 0.53°. 값이 커질수록 반그림자(penumbra)가 넓어진다.
        /// <see cref="BakeQualitySettings.DirectSamples"/> 가 2 이상일 때만 의미가 있다.
        /// </summary>
        public float AngularDiameterDeg;
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

        /// <summary>
        /// 직사광 그림자 레이 수. 0/1 = 텍셀당 1발(이진 판정, 기존 경로와 비트동일).
        /// 2 이상이면 태양 원반(<see cref="DirectionalLight.AngularDiameterDeg"/>)을 샘플링해
        /// 가시도를 0~1 연속값으로 만든다 → 잎 경계의 점묘(이진 에일리어싱) 제거 + 반그림자.
        /// </summary>
        public int DirectSamples;

        public static BakeQualitySettings Default => new BakeQualitySettings
        {
            AoSamples = 64,
            IndirectSamples = 64,
            MaxBounces = 1,      // ⑫ 검증 시작값: 1바운스
            RRStartDepth = 3,
            RayBias = 1e-4f,
            DirectSamples = 1,
        };

        public static BakeQualitySettings HighQuality => new BakeQualitySettings
        {
            AoSamples = 256,
            IndirectSamples = 256,
            MaxBounces = 8,
            RRStartDepth = 3,
            RayBias = 1e-4f,
            DirectSamples = 16,
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

        // ── 태양 원반 샘플링 (직사광 소프트 그림자) ───────────────────────────────
        //
        // 왜 RNG 가 아니라 결정적 저불일치 수열인가:
        //   RNG 를 쓰면 CPU/Burst/GPU 가 같은 시드로도 초월함수 발산 때문에 갈릴 수 있고,
        //   시드 배선도 늘어난다. 여기서는 SH-G 가 피보나치 방향셋을 쓴 것과 같은 이유로
        //   **인덱스만으로 정해지는 수열**을 쓴다(황금비 회전). 세 백엔드가 같은 식을 그대로 미러.
        //   텍셀마다 위상만 시드로 돌려 밴딩을 노이즈로 흩는다.

        /// <summary>태양 방향 L 기준 정규직교 기저. CosineHemisphere 와 같은 규약(|x|<0.9 분기).</summary>
        public static void SunBasis(Vector3 L, out Vector3 t, out Vector3 b)
        {
            Vector3 up = Mathf.Abs(L.x) < 0.9f ? Vector3.right : Vector3.up;
            t = Vector3.Cross(L, up).normalized;
            b = Vector3.Cross(L, t);
        }

        /// <summary>시드 → [0,1) 위상 회전. 텍셀별로 샘플 패턴을 돌려 밴딩을 방지한다.</summary>
        public static float SunConeRotation(uint seed)
            => seed == 0u ? 0f : ((seed * 2654435761u) >> 8 & 0xFFFFu) / 65536f;

        /// <summary>
        /// 원뿔(반각 acos(cosHalf)) 내부의 i번째 균등 방향. i∈[0,n), rot∈[0,1).
        /// u1 은 층화(stratified), u2 는 황금비 회전 → n 이 작아도 고르게 퍼진다.
        /// </summary>
        public static Vector3 SunConeDirection(Vector3 L, Vector3 t, Vector3 b, float cosHalf, int i, int n, float rot)
        {
            float u1 = (i + 0.5f) / n;
            float u2 = i * 0.6180339887498949f + rot;
            u2 -= Mathf.Floor(u2);

            float cosT = 1f - u1 * (1f - cosHalf);            // 입체각 균등
            float sinT = Mathf.Sqrt(Mathf.Max(0f, 1f - cosT * cosT));
            float phi = 2f * Mathf.PI * u2;

            return (t * (sinT * Mathf.Cos(phi)) + b * (sinT * Mathf.Sin(phi)) + L * cosT).normalized;
        }

        /// <summary>
        /// 직사광 — 태양 원반을 <paramref name="samples"/> 발로 샘플링해 가시도를 연속값으로 만든다.
        /// samples≤1 또는 각지름 0 이면 <see cref="EvaluateDirect"/> 를 그대로 호출한다
        /// (기존 경로 비트동일 → 기존 검증 수치 보존).
        /// </summary>
        public static Vector3 EvaluateDirectSampled(IOccluder occluder, Vector3 p, Vector3 n,
                                                    DirectionalLight sun, int samples, uint seed)
        {
            if (samples <= 1 || sun.AngularDiameterDeg <= 0f)
                return EvaluateDirect(occluder, p, n, sun);

            Vector3 L = -sun.Direction.normalized;
            // 원뿔 전체가 지면 아래면 어차피 0 — 반각만큼 여유를 두고 조기 종료.
            float half = sun.AngularDiameterDeg * 0.5f * Mathf.Deg2Rad;
            if (Vector3.Dot(L, n) <= -Mathf.Sin(half)) return Vector3.zero;

            SunBasis(L, out Vector3 t, out Vector3 b);
            float cosHalf = Mathf.Cos(half);
            float rot = SunConeRotation(seed);
            Vector3 o = p + n * 1e-3f;                       // EvaluateDirect 와 동일 바이어스

            float sum = 0f;
            for (int i = 0; i < samples; i++)
            {
                Vector3 d = SunConeDirection(L, t, b, cosHalf, i, samples, rot);
                float ndl = Vector3.Dot(d, n);
                if (ndl <= 0f) continue;
                if (occluder.Occluded(o, d, 1e30f)) continue;
                sum += ndl;
            }
            return sun.Color * sun.Intensity * (sum / samples);
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
            // 1차 직사광만 태양 원반 샘플링. 바운스 NEE(EvaluateIndirect 내부)는 1발 유지 —
            // 간접광에 하드 그림자는 보이지 않고, G5 Indirect 검증 수치도 그대로 보존된다.
            Vector3 direct = EvaluateDirectSampled(scene.Occluder, p, n, sun, q.DirectSamples, seed);
            Vector3 indirect = EvaluateIndirect(scene, p, n, sun, sky, q, seed);
            return direct + indirect;
        }
 



    }


}