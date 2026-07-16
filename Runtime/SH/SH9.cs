using UnityEngine;

namespace HuskyLibs.CustomLightmapper
{

    //SH9.cs(베이크) ↔ 셰이더 SH 함수(런타임)는 인코더↔디코더 관계. SH9.Evaluate는 ShadeSH9의 C# 버전.


    /// <summary>
    /// SH-1: 3밴드(L0..L2) 9계수 구면조화 조도. 채널당 RGB(Vector3).
    /// Unity ShadeSH9 규약과 정합:
    ///   기저(정규화 실수 SH) Y0=0.282095, Y1=0.488603·(y,z,x),
    ///   Y2=1.092548·(xy,yz,zx), 1.092548/2·(x²-y²)? → 아래 상수 참조.
    ///   복사 조도 재구성 시 코사인 로브 컨볼루션 계수 A0=π, A1=2π/3, A2=π/4 를 적용.
    /// 프로젝션: coeff_l += L(d)·Y_lm(d)·(4π/N)  (몬테카를로 구면 균등 N샘플)
    /// 평가(조도): E(n) = Σ A_l · coeff · Y_lm(n)   → Lambert 표면 조도(albedo 미포함)
    /// 계수 순서(Unity 호환): [0]=Y00, [1..3]=Y1(-1,0,1)=(y,z,x), [4..8]=Y2(-2,-1,0,1,2)
    /// </summary>
    public struct SH9
    {
        public const int Count = 9;
        public const int Stride = 9 * 3 * 4; // 108 ( float3 * 9 )


        public Vector3 c0;                    // Y00
        public Vector3 c1, c2, c3;            // Y1: y, z, x
        public Vector3 c4, c5, c6, c7, c8;    // Y2: xy, yz, (3z²-1), xz, (x²-y²)


        // ── 실수 SH 기저 상수 ──
        const float k0 = 0.2820948f;          // 1/(2√π)
        const float k1 = 0.4886025f;          // √(3/4π)
        const float k2a = 1.0925484f;         // √(15/4π)      (xy, yz, xz)
        const float k2b = 0.3153916f;         // √(5/16π)      (3z²-1)
        const float k2c = 0.5462742f;         // √(15/16π)     (x²-y²)

        // ── 코사인 로브 컨볼루션(조도 재구성) 밴드 계수 ──
        const float A0 = Mathf.PI;            // 3.1415927
        const float A1 = 2.0943952f;          // 2π/3
        const float A2 = 0.7853982f;          // π/4

        // ── 디링잉 밴드 윈도우 (EvaluateSH9.hlsl W1/W2 와 반드시 동일) ──
        // L2 truncation ringing 억제 → 고대비 입사에서 채널 음수 overshoot→클램프로
        // 단색(빨강) 무너짐 방지. 셰이더(런타임)와 기즈모(C#)가 같은 색을 내도록 1:1 미러.
        const float W1 = 1.0f;                // L1 유지
        const float W2 = 0.5f;                // L2 절반 감쇠(주 링잉원)



        ///   <summary>
        /// 구면조화 프로젝터(SH9)
        /// 방향 d(정규화)에서의 9개 실수 SH 기저값
        /// </summary>
        /// 
        public static void Basis(Vector3 d, out float y0,
             out float y1, out float y2, out float y3,
             out float y4, out float y5, out float y6, out float y7, out float y8)
        {
            float x = d.x, y = d.y, z = d.z;
            y0 = k0;
            y1 = k1 * y;
            y2 = k1 * z;
            y3 = k1 * x;
            float xy = x * y, yz = y * z, zx = z * x;
            float x2y2 = x * x - y * y;
            float z2m1 = 3.0f * z * z - 1.0f;
            y4 = k2a * xy;
            y5 = k2a * yz;
            y6 = k2b * z2m1;
            y7 = k2a * zx;
            y8 = k2c * x2y2;

        }

        ///<summary>
        /// 단일 샘플 기여 누적
        /// coeff += L·Y(d)·weight (weight=4π/N)
        /// </summary>
        /// <param name="d">
        ///   샘플 방향(정규화 필수). 구(球) 위 균등 몬테카를로 샘플의 방향.
        /// </param>
        /// <param name="L">
        ///   샘플 방향 d에서 들어온 복사휘도(radiance) RGB. 트레이서가 반환한 라이팅 값.
        /// </param>
        /// <param name="weighten">
        ///   몬테카를로 가중치 = 4π/N.
        ///   4π = 구 전체 입체각(sr), N = 총 샘플 수. 균등 샘플이라 pdf=1/4π 의 역수.
        /// </param>
        public void Accumulate(Vector3 d, Vector3 L, float weighten)
        {
            // Y(d): 방향 d에서의 9개 실수 SH 기저값 Y_lm(d)
            Basis(d, out float y0, out float y1, out float y2, out float y3, out float y4, out float y5, out float y6, out float y7, out float y8);

            // 각 계수에 coeff += L · Y_lm(d) · weight 누적 (채널별 RGB 벡터 연산)
            //   coeff(c0..c8) : 누적 중인 SH 계수 / L : 이 방향의 복사휘도 샘플
            //   y0..y8        : 밴드·차수별 기저값 Y_lm(d) / weighten : 4π/N 가중치
            c0 += L * y0 * weighten;
            c1 += L * y1 * weighten;
            c2 += L * y2 * weighten;
            c3 += L * y3 * weighten;
            c4 += L * y4 * weighten;
            c5 += L * y5 * weighten;
            c6 += L * y6 * weighten;
            c7 += L * y7 * weighten;
            c8 += L * y8 * weighten;
        }

        /// <summary>노멀 n 방향 Lambert 조도 재구성 E(n) (albedo 미포함, 채널별). 음수는 0 클램프.</summary>
        public Vector3 Evaluate(Vector3 n)
        {
            Basis(n, out float y0, out float y1, out float y2, out float y3,
                      out float y4, out float y5, out float y6, out float y7, out float y8);
            Vector3 e = c0 * (A0 * y0)
                      + (c1 * (A1 * y1) + c2 * (A1 * y2) + c3 * (A1 * y3)) * W1
                      + (c4 * (A2 * y4) + c5 * (A2 * y5) + c6 * (A2 * y6) + c7 * (A2 * y7) + c8 * (A2 * y8)) * W2;
            return new Vector3(Mathf.Max(0f, e.x), Mathf.Max(0f, e.y), Mathf.Max(0f, e.z));
        }

    }
}
