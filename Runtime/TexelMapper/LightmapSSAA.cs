using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /*
     SSAA(텍셀 내부 슈퍼샘플링) 지원 유틸.

     왜 필요한가:
       TexelMapper 는 텍셀당 정중앙 1점만 복원한다. 그래서 그림자 경계가 텍셀 격자에 양자화되고,
       런타임 확대(magnification) 후에는 텍셀 크기의 계단으로 보인다. 태양 원반 샘플링
       (DirectSamples + AngularDiameterDeg) 은 반그림자를 만들지만, 반그림자 폭
       (= 차폐물까지 거리 × tan(각지름)) 이 텍셀보다 작으면 다시 한 텍셀 안에서 뭉개진다.
       계단을 없애려면 "텍셀 안 어디를 샘플했는가" 자체를 늘려야 한다 = 서브텍셀 커버리지 정보.

     왜 후처리 필터(FXAA 류)가 아닌가:
       FXAA 는 이미 양자화된 값을 이웃과 섞을 뿐 정보를 만들지 않는다. 게다가 아틀라스에서
       "이웃 텍셀"은 월드에서 남남일 수 있고(차트 경계), valid 마스크·노멀 가이드가 없는
       필터는 디레이션 링(가짜 값)까지 빨아들여 차트 경계에 rim 을 만든다.

     비용 전략(적응형):
       1패스로 기존대로 텍셀당 1샘플 → 그 결과에서 라이팅이 급변하는 텍셀만 검출 →
       그 텍셀만 S×S 재샘플. 그림자 경계는 아틀라스 면적의 몇 % 라 총 레이는 1.1~1.3배 수준.
    */

    /// <summary>SSAA 시드 규약 + 엣지 텍셀 검출. 실제 래스터는 <see cref="TexelMapper.MapSubsamples"/>.</summary>
    public static class LightmapSSAA
    {
        /// <summary>텍셀 시드 승수. CPU/Burst/GPU 세 백엔드가 공유하는 기존 규약(seed + li*이 값).</summary>
        public const uint TexelSeedMul = 2654435761u;
        /// <summary>서브샘플 시드 승수(황금비 상수). 서브샘플마다 MC 노이즈가 달라져 평균이 AA+디노이즈를 겸한다.</summary>
        public const uint SubSeedMul = 0x9E3779B9u;

        /// <summary>텍셀 li 의 베이크 시드(기존 규약 그대로).</summary>
        public static uint TexelSeed(uint seed, int li) => seed + (uint)li * TexelSeedMul;

        /// <summary>텍셀 li 의 sub 번째 서브샘플 시드. sub 는 0-베이스.</summary>
        public static uint SubSeed(uint seed, int li, int sub)
            => seed + (uint)li * TexelSeedMul + (uint)(sub + 1) * SubSeedMul;

        /// <summary>Rec.709 상대 휘도(Linear RGB). 엣지 판정용.</summary>
        public static float Luma(Vector3 lin) => 0.2126f * lin.x + 0.7152f * lin.y + 0.0722f * lin.z;

        /// <summary>
        /// 1패스 결과에서 "라이팅이 급변하는" 텍셀을 찾는다 — 여기만 S×S 재샘플하면 된다.
        ///
        /// 게이팅이 핵심이다. 인스턴스의 루멜 격자에서 인접 텍셀은 다른 차트일 수 있고, 그 경계의
        /// 값 차이는 에일리어싱이 아니라 정상적인 불연속이다(스티칭이 다룰 몫). 그래서 이웃을
        /// <paramref name="normalCosThresh"/>(노멀 일치) 와 <paramref name="maxWorldDist"/>(월드 인접)
        /// 로 걸러 실제 표면 이웃일 때만 비교한다 — 디노이즈가 쓰는 가이드와 같은 발상.
        ///
        /// 차이는 절대값이 아니라 상대값으로 본다. 라이트맵은 linear HDR 조도라 1을 넘고,
        /// 절대 임계값은 밝은 영역에서 전부 엣지가 되어버린다.
        /// </summary>
        /// <param name="lm">1패스 루멜맵(월드 위치·노멀·valid 제공).</param>
        /// <param name="radiance">1패스 조도. lm 과 같은 li 인덱스, 길이 res*res.</param>
        /// <param name="res">루멜 격자 한 변(= 인스턴스의 아틀라스 영역 sidePx).</param>
        /// <param name="relThreshold">상대 휘도차 임계. 0.1 = 이웃과 10% 차이.</param>
        /// <param name="normalCosThresh">이웃으로 인정할 노멀 내적 하한(cos).</param>
        /// <param name="maxWorldDist">이웃으로 인정할 월드 거리 상한(텍셀 몇 개분).</param>
        /// <param name="dilateRings">검출 후 마스크를 몇 링 확장할지. 1 = 경계 양쪽을 모두 포함(권장).</param>
        public static bool[] DetectEdges(in LumelMap lm, Vector3[] radiance, int res,
                                         float relThreshold, float normalCosThresh, float maxWorldDist,
                                         int dilateRings, out int edgeCount)
        {
            int n = res * res;
            var mask = new bool[n];
            edgeCount = 0;
            if (radiance == null || lm.Valid == null || radiance.Length < n || lm.Valid.Length < n)
                return mask;

            float maxDistSq = maxWorldDist * maxWorldDist;
            var valid = lm.Valid; var wpos = lm.WorldPos; var wnrm = lm.WorldNormal;

            // 오른쪽·위 이웃만 본다(각 쌍을 한 번씩만 비교, 걸리면 양쪽 다 마킹).
            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    int a = y * res + x;
                    if (!valid[a]) continue;

                    float la = Luma(radiance[a]);
                    Vector3 pa = wpos[a], na = wnrm[a];

                    for (int dir = 0; dir < 2; dir++)
                    {
                        int b;
                        if (dir == 0) { if (x + 1 >= res) continue; b = a + 1; }
                        else { if (y + 1 >= res) continue; b = a + res; }

                        if (!valid[b]) continue;
                        if (Vector3.Dot(na, wnrm[b]) < normalCosThresh) continue;   // 각진 면 = 정상 불연속
                        if ((pa - wpos[b]).sqrMagnitude > maxDistSq) continue;      // 다른 차트 = 이웃 아님

                        float lb = Luma(radiance[b]);
                        if (Mathf.Abs(la - lb) <= relThreshold * (Mathf.Max(la, lb) + 1e-4f)) continue;

                        mask[a] = true;
                        mask[b] = true;
                    }
                }
            }

            // 확장: 경계 텍셀만 고치면 계단의 한쪽 면만 매끈해져 오히려 눈에 띈다.
            for (int r = 0; r < dilateRings; r++) mask = DilateMask(mask, lm.Valid, res);

            for (int i = 0; i < n; i++) if (mask[i]) edgeCount++;
            return mask;
        }

        /// 4-이웃 1링 확장. valid 텍셀 안에서만 퍼진다(배경/거터는 베이크 대상이 아니므로).
        static bool[] DilateMask(bool[] src, bool[] valid, int res)
        {
            var dst = (bool[])src.Clone();
            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    int i = y * res + x;
                    if (!src[i]) continue;
                    if (x > 0) Set(i - 1);
                    if (x + 1 < res) Set(i + 1);
                    if (y > 0) Set(i - res);
                    if (y + 1 < res) Set(i + res);
                }
            }
            return dst;

            void Set(int j) { if (valid[j]) dst[j] = true; }
        }
    }
}
