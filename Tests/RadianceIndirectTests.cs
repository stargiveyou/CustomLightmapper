using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// C2 Indirect 검증 (RadianceCore.EvaluateIndirect + RadianceScene).
    ///  a) 빈 씬 + 균일하늘 L → 간접조도 ≈ π·L (반구 적분 해석해, 균일하늘은 분산 0)
    ///  b) 빈 씬 + 하늘0 → 0 (모든 미스가 0)
    ///  c) 닫힌 흑색 박스(알베도0) → 0 (미스 없음 + 알베도0)
    ///  d) 회색 박스 내부 + 광원 없음 → 0 (에너지 보존 sanity: 소스 없으면 다바운스라도 0)
    ///  e) RR 무편향: 얕은(RR) vs 깊은(no-RR) 평균 일치(노이즈 허용)
    ///
    /// 호출: Debug.Log(RadianceIndirectTests.RunAll());
    /// </summary>
    public static class RadianceIndirectTests
    {
        static readonly Vector3 P = Vector3.zero;   // 평가점(박스 내부 중심)
        static readonly Vector3 N = Vector3.up;     // 평가 노멀(상반구)

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== C2 Indirect Self-Tests ===");
            int pass = 0, total = 0;

            // a) 빈 씬 + 균일하늘 L → π·L  (모든 미스가 sky·π, 균일하늘이라 분산 0 → 정확)
            {
                Vector3 L = new Vector3(0.2f, 0.3f, 0.4f);
                using var scene = new RadianceScene(System.Array.Empty<Tri>(), Vector3.one * 0.5f);
                Vector3 ind = RadianceCore.EvaluateIndirect(scene, P, N, NoSun(), new UniformSky(L), Q(64, 1), 1);
                Vector3 expect = L * Mathf.PI;
                Check(sb, ref pass, ref total, $"a) 빈 씬+균일하늘 → π·L  (ind={Fmt(ind)} exp={Fmt(expect)})",
                      ApproxV(ind, expect, 1e-3f));
            }

            // b) 빈 씬 + 하늘0 → 0
            {
                using var scene = new RadianceScene(System.Array.Empty<Tri>(), Vector3.one * 0.5f);
                Vector3 ind = RadianceCore.EvaluateIndirect(scene, P, N, NoSun(), new UniformSky(Vector3.zero), Q(64, 1), 2);
                Check(sb, ref pass, ref total, $"b) 빈 씬+하늘0 → 0  (ind={Fmt(ind)})", ApproxV(ind, Vector3.zero, 1e-5f));
            }

            // c) 닫힌 흑색 박스(α0) → 0  (하늘 밝아도 박스가 막음 + 알베도0)
            {
                var box = LightmapEvaluateTests.MakeBox(Vector3.zero, 4f);   // -2..2, P=원점은 내부
                using var scene = new RadianceScene(box, Vector3.zero);      // 알베도 0
                Vector3 ind = RadianceCore.EvaluateIndirect(scene, P, N, NoSun(), new UniformSky(Vector3.one), Q(64, 4), 3);
                Check(sb, ref pass, ref total, $"c) 닫힌 흑박스(α0) → 0  (ind={Fmt(ind)})", ApproxV(ind, Vector3.zero, 1e-5f));
            }

            // d) 닫힌 회색 박스(α0.5) + 광원 없음 → 0  (에너지 보존: 소스 없으면 다바운스라도 0)
            {
                var box = LightmapEvaluateTests.MakeBox(Vector3.zero, 4f);
                using var scene = new RadianceScene(box, Vector3.one * 0.5f);
                Vector3 ind = RadianceCore.EvaluateIndirect(scene, P, N, NoSun(), new UniformSky(Vector3.one), Q(128, 8), 4);
                Check(sb, ref pass, ref total, $"d) 닫힌 회색박스+무광원 → 0  (ind={Fmt(ind)})", ApproxV(ind, Vector3.zero, 1e-5f));
            }

            // e) RR 무편향: 얕은(RR start=2) vs 깊은(RR 안 함, 16바운스) 평균 일치
            //    천장 열린 회색 박스 + 균일하늘 → 다바운스 후 하늘로 탈출. 같은 적분의 두 추정치라 평균 일치.
            {
                var box = MakeOpenBox(Vector3.zero, 4f);                     // +Y(천장) 생략
                using var scene = new RadianceScene(box, Vector3.one * 0.7f);
                var sky = new UniformSky(Vector3.one);
                Vector3 pe = new Vector3(0f, -1.9f, 0f);                     // 바닥 근처, 상반구로 탈출
                var qRR = new BakeQualitySettings { IndirectSamples = 4096, MaxBounces = 16, RRStartDepth = 2, RayBias = 1e-4f, AoSamples = 1 };
                var qNo = new BakeQualitySettings { IndirectSamples = 4096, MaxBounces = 16, RRStartDepth = 999, RayBias = 1e-4f, AoSamples = 1 };
                Vector3 a = RadianceCore.EvaluateIndirect(scene, pe, N, NoSun(), sky, qRR, 5);
                Vector3 b = RadianceCore.EvaluateIndirect(scene, pe, N, NoSun(), sky, qNo, 5);
                float rel = RelDiff(a, b);
                Check(sb, ref pass, ref total, $"e) RR 무편향: RR≈no-RR (rel={rel:P1}, a={Fmt(a)} b={Fmt(b)})", rel < 0.15f);
            }

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ── 헬퍼 ──
        static DirectionalLight NoSun() => new DirectionalLight { Direction = Vector3.down, Color = Vector3.zero, Intensity = 0f };

        static BakeQualitySettings Q(int indirect, int bounces)
        {
            var q = BakeQualitySettings.Default;
            q.IndirectSamples = indirect;
            q.MaxBounces = bounces;
            return q;
        }

        // 천장(+Y) 없는 5면 박스. 하늘이 위로 들어와 다바운스 GI 형성.
        static Tri[] MakeOpenBox(Vector3 center, float size)
        {
            float h = size * 0.5f;
            Vector3 c000 = center + new Vector3(-h, -h, -h), c100 = center + new Vector3(h, -h, -h);
            Vector3 c010 = center + new Vector3(-h, h, -h), c110 = center + new Vector3(h, h, -h);
            Vector3 c001 = center + new Vector3(-h, -h, h), c101 = center + new Vector3(h, -h, h);
            Vector3 c011 = center + new Vector3(-h, h, h), c111 = center + new Vector3(h, h, h);

            var quads = new[]
            {
                (c000, c100, c110, c010), // -Z
                (c001, c101, c111, c011), // +Z
                (c000, c100, c101, c001), // -Y (바닥)
                // +Y (천장) 생략 → 열림
                (c000, c010, c011, c001), // -X
                (c100, c110, c111, c101), // +X
            };
            var tris = new Tri[quads.Length * 2];
            for (int i = 0; i < quads.Length; i++)
            {
                var (a, b, cc, d) = quads[i];
                tris[i * 2 + 0] = new Tri { V0 = a, V1 = b, V2 = cc };
                tris[i * 2 + 1] = new Tri { V0 = a, V1 = cc, V2 = d };
            }
            return tris;
        }

        static float RelDiff(Vector3 a, Vector3 b)
        {
            float d = Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Max(Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z)));
            float m = Mathf.Max(Mathf.Max(Mathf.Abs(a.x), Mathf.Abs(a.y)), Mathf.Max(Mathf.Abs(a.z),
                      Mathf.Max(Mathf.Max(Mathf.Abs(b.x), Mathf.Abs(b.y)), Mathf.Abs(b.z))));
            return m < 1e-6f ? 0f : d / m;
        }

        static bool ApproxV(Vector3 a, Vector3 b, float eps) =>
            Mathf.Abs(a.x - b.x) < eps && Mathf.Abs(a.y - b.y) < eps && Mathf.Abs(a.z - b.z) < eps;

        static string Fmt(Vector3 v) => $"({v.x:0.000},{v.y:0.000},{v.z:0.000})";

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        {
            total++;
            if (ok) pass++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }
    }
}
