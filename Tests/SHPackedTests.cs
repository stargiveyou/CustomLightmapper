using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper
{
    /// <summary>
    /// SH-3 검증: SHPacked(9×float3 → 7×float4, std430/HLSL 정렬 패킹) 왕복.
    /// 손으로 짠 packed 레이아웃이 어긋나면 SH-5 셰이더 디코드가 조용히 틀어지므로,
    /// 27개 실수 계수(9계수×RGB)가 Pack→Unpacked 라운드트립에서 비트 동일인지 단언.
    ///
    ///  T1 라운드트립 : 27개 고유값 SH9 → Pack → Unpacked ≡ 원본(전 계수).
    ///  T2 정렬 상수  : Stride=112, Float4Count=7 (SH9.Stride=108 과 함께).
    ///  T3 패딩       : p6.w(마지막 float4 패딩)=0.
    ///  T4 셰이더 순서: packed 필드가 문서화된 (c0.rgb,c1.r) … (c8.rgb,pad) 순서와 일치.
    /// 호출: Debug.Log(SHPackedTests.RunAll());
    /// </summary>
    public static class SHPackedTests
    {
        const float Eps = 1e-6f;

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== SHPacked (SH-3) 정렬 패킹 왕복 ===");
            int pass = 0, total = 0;

            // 27개 고유값(0.5,1.5,…) → 어긋난 필드가 값으로 드러나게.
            var src = new SH9();
            src.c0 = V(0); src.c1 = V(1); src.c2 = V(2); src.c3 = V(3);
            src.c4 = V(4); src.c5 = V(5); src.c6 = V(6); src.c7 = V(7); src.c8 = V(8);

            var packed = SHPacked.Pack(src);
            var rt = packed.Unpacked();

            bool ok =
                VApprox(rt.c0, src.c0) && VApprox(rt.c1, src.c1) && VApprox(rt.c2, src.c2) &&
                VApprox(rt.c3, src.c3) && VApprox(rt.c4, src.c4) && VApprox(rt.c5, src.c5) &&
                VApprox(rt.c6, src.c6) && VApprox(rt.c7, src.c7) && VApprox(rt.c8, src.c8);
            Check(sb, ref pass, ref total, "T1 Pack→Unpacked 27계수 왕복 일치", ok);

            Check(sb, ref pass, ref total, $"T2 Stride=112·Float4Count=7·SH9.Stride=108",
                  SHPacked.Stride == 112 && SHPacked.Float4Count == 7 && SH9.Stride == 108);

            Check(sb, ref pass, ref total, $"T3 마지막 패딩 p6.w=0 (={packed.p6.w})", Mathf.Abs(packed.p6.w) < Eps);

            // T4: 문서화된 셰이더 디코드 순서 그대로인지 직접 확인(셰이더가 이 배치를 가정).
            bool order =
                A(packed.p0, src.c0.x, src.c0.y, src.c0.z, src.c1.x) &&
                A(packed.p1, src.c1.y, src.c1.z, src.c2.x, src.c2.y) &&
                A(packed.p2, src.c2.z, src.c3.x, src.c3.y, src.c3.z) &&
                A(packed.p3, src.c4.x, src.c4.y, src.c4.z, src.c5.x) &&
                A(packed.p4, src.c5.y, src.c5.z, src.c6.x, src.c6.y) &&
                A(packed.p5, src.c6.z, src.c7.x, src.c7.y, src.c7.z) &&
                A(packed.p6, src.c8.x, src.c8.y, src.c8.z, 0f);
            Check(sb, ref pass, ref total, "T4 packed 필드 순서 ≡ 문서/셰이더 디코드", order);

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        static Vector3 V(int i) => new Vector3(0.5f + i * 3, 1.5f + i * 3, 2.5f + i * 3);
        static bool VApprox(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < Eps * Eps;
        static bool A(Vector4 v, float x, float y, float z, float w) =>
            Mathf.Abs(v.x - x) < Eps && Mathf.Abs(v.y - y) < Eps &&
            Mathf.Abs(v.z - z) < Eps && Mathf.Abs(v.w - w) < Eps;

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
