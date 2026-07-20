using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 디노이즈 품질 파라미터. CPU 직렬판/Burst판 공유(백엔드 교차검증 가능).
    /// </summary>
    [System.Serializable]
    public struct DenoiseSettings
    {
        public int Iterations;       // à-trous 패스 수(step=1,2,4,…). 3이면 유효 반경 ≈ 17텍셀
        public float NormalPower;    // 노멀 가중 지수 pow(max(0,dot),p). 클수록 각진 면 분리 강함(하드 엣지 보존)
        public float PositionSigma;  // 월드 위치 가우시안 σ(월드 단위, step 1 기준 — 커널 내부에서 step 배율)
        public float ColorSigma;     // 색 range σ(Linear RGB L2). 그림자 경계 등 실제 라이팅 엣지 보존.
                                     // 휘도가 아닌 색 거리 — 단일 채널만 다른 엣지(색 있는 GI)도 휘도 스칼라에 묻히지 않고 보존.

        public static DenoiseSettings Default => new DenoiseSettings
        {
            Iterations = 3,
            NormalPower = 32f,
            PositionSigma = 0.125f,   // texelsPerWorldUnit=16 기준 2텍셀
            ColorSigma = 0.25f,
        };
    }

    /// <summary>
    /// 라이트맵 후처리 — À-trous Joint Bilateral 디노이즈(순수 C# 직렬 레퍼런스).
    ///
    /// 몬테카를로 베이크(AO/경로추적)는 텍셀마다 독립 난수로 적분해 고주파 그레인이 남는다.
    /// 색만 블러하면 하드 엣지·차트 경계·그림자 경계가 뭉개지므로, 베이크가 이미 갖고 있는
    /// 텍셀별 월드 노멀·월드 위치(LumelMap)를 가이드로 쓰는 에지 보존 필터로 평탄면 노이즈만 평활한다.
    ///
    /// 탭 가중치 = B3 커널 × 노멀(powᵖ) × 위치(가우시안, σ∝step) × 색(range 가우시안, RGB L2):
    ///  - 노멀: 다른 방향 면(큐브 모서리 등)은 dot<1 → 감쇠. seamMaxAngleDeg 게이팅과 같은 원리.
    ///  - 위치: 아틀라스에서 인접해도 월드에서 먼 텍셀(다른 차트)은 배제 → 차트 간 bleed 차단.
    ///          σ에 step 을 곱해 연속 표면에선 step 불변, 불연속(차트 점프)만 기각.
    ///  - 색  : 노이즈 진폭보다 큰 라이팅 엣지(직사 그림자 경계)는 보존. RGB L2 거리 —
    ///          휘도 스칼라는 단일 채널 엣지를 과소평가해(예: 순수 R 0.7 계단이 휘도론 0.15) 엣지가 번진다.
    ///
    /// valid 텍셀만 읽고 valid 텍셀에만 쓴다(무효/배경 불변, valid 마스크 불변).
    /// 순서: blit → Denoise → Seam Stitch → Dilate (경계값을 안정화한 뒤 스티칭·확장).
    /// 순수 함수(Unity 씬 비의존) → 헤드리스 단위테스트 가능.
    /// </summary>
    public static class LightmapDenoise
    {
        // B3 스플라인 이항 커널 {1,4,6,4,1}/16 — à-trous 표준 5탭.
        static readonly float[] K = { 1f / 16f, 4f / 16f, 6f / 16f, 4f / 16f, 1f / 16f };

        public static void Denoise(Color[] px, bool[] valid, Vector3[] normal, Vector3[] worldPos,
                                   int w, int h, in DenoiseSettings s)
        {
            if (px == null || valid == null || normal == null || worldPos == null || s.Iterations <= 0)
                return;
            int n = w * h;
            if (px.Length != n || valid.Length != n || normal.Length != n || worldPos.Length != n)
                return;

            var src = px;
            var dst = new Color[n];

            for (int it = 0; it < s.Iterations; it++)
            {
                int step = 1 << it;
                // σ는 step 배율 — 연속 표면에서 탭 거리(∝step)와 σ가 같이 늘어 감쇠가 step 불변.
                float sigP = Mathf.Max(1e-6f, s.PositionSigma * step);
                float invTwoSigP2 = 1f / (2f * sigP * sigP);
                float sigC = Mathf.Max(1e-6f, s.ColorSigma);
                float invTwoSigC2 = 1f / (2f * sigC * sigC);

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        if (!valid[idx]) { dst[idx] = src[idx]; continue; } // 무효/배경 불변

                        Vector3 cN = normal[idx];
                        Vector3 cP = worldPos[idx];
                        Color cC = src[idx];

                        float r = 0, g = 0, b = 0, a = 0, wsum = 0;
                        for (int dy = -2; dy <= 2; dy++)
                        {
                            int ny = y + dy * step;
                            if (ny < 0 || ny >= h) continue;
                            for (int dx = -2; dx <= 2; dx++)
                            {
                                int nx = x + dx * step;
                                if (nx < 0 || nx >= w) continue;
                                int nidx = ny * w + nx;
                                if (!valid[nidx]) continue; // valid 텍셀만 소스

                                float wt = K[dx + 2] * K[dy + 2];
                                if (nidx != idx)
                                {
                                    float ndot = Mathf.Max(0f, Vector3.Dot(cN, normal[nidx]));
                                    wt *= Mathf.Pow(ndot, s.NormalPower);

                                    Vector3 dp = worldPos[nidx] - cP;
                                    wt *= Mathf.Exp(-Vector3.Dot(dp, dp) * invTwoSigP2);

                                    Color nc = src[nidx];
                                    float dr = nc.r - cC.r, dg = nc.g - cC.g, db = nc.b - cC.b;
                                    wt *= Mathf.Exp(-(dr * dr + dg * dg + db * db) * invTwoSigC2);
                                }

                                Color c = src[nidx];
                                r += c.r * wt; g += c.g * wt; b += c.b * wt; a += c.a * wt;
                                wsum += wt;
                            }
                        }
                        // 중앙 탭(wt>0)이 항상 포함되므로 valid 텍셀에서 wsum>0.
                        dst[idx] = new Color(r / wsum, g / wsum, b / wsum, a / wsum);
                    }
                }

                // 더블버퍼 스왑 — 다음 패스는 이번 결과를 읽는다.
                (src, dst) = (dst, src);
            }

            // 홀수 패스면 최종 결과가 스크래치에 있음 → 원본으로 복사.
            if (!ReferenceEquals(src, px))
                System.Array.Copy(src, px, n);
        }
    }
}
