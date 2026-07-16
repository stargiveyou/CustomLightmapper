using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// C2 라이팅 코어 해석적(known-answer) 자체테스트.
    /// BVH 없이 BruteForceOccluder + RadianceCore 만으로 검증한다(레퍼런스 정확성).
    ///  - RayGeometry.RayTri : 히트/미스/tmin·tmax 컬링/평행/양면
    ///  - BruteForceOccluder : 최근접 히트·TriIndex·차폐
    ///  - RadianceCore       : AO(0/1/결정성), 코사인반구 분포(E[cosθ]=2/3), Direct(그림자/백페이스), Radiance 합성
    /// BVH 구현이 들어오면 "동일 씬 brute ≡ BVH" 교차검증을 여기에 추가.
    /// </summary>
    public static class LightmapEvaluateTests
    {
        const float Eps = 1e-4f;

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== C2 LightmapEvaluate Self-Tests ===");
            int pass = 0, total = 0;

            // 기준 삼각형: y=0 평면, V0(0,0,0) V1(1,0,0) V2(0,0,1). 내부점 (0.25,*,0.25)
            var triFloor = new Tri { V0 = new Vector3(0, 0, 0), V1 = new Vector3(1, 0, 0), V2 = new Vector3(0, 0, 1) };

            // --- RayTri ---
            {
                bool h = RayGeometry.RayTri(new Vector3(0.25f, 1, 0.25f), Vector3.down, triFloor, 0f, 10f, out float t);
                Check(sb, ref pass, ref total, "RayTri 히트(t=1)", h && Approx(t, 1f));

                bool miss = RayGeometry.RayTri(new Vector3(2f, 1, 2f), Vector3.down, triFloor, 0f, 10f, out _);
                Check(sb, ref pass, ref total, "RayTri 삼각형 밖 미스", !miss);

                bool culled = RayGeometry.RayTri(new Vector3(0.25f, 1, 0.25f), Vector3.down, triFloor, 0f, 0.5f, out _);
                Check(sb, ref pass, ref total, "RayTri tmax 컬링(t=1>0.5)", !culled);

                bool parallel = RayGeometry.RayTri(new Vector3(0.25f, 1, 0.25f), Vector3.right, triFloor, 0f, 10f, out _);
                Check(sb, ref pass, ref total, "RayTri 평행 레이 미스", !parallel);

                // 양면: 아래에서 위로 쏴도 히트해야 함(occlusion용 double-sided)
                bool back = RayGeometry.RayTri(new Vector3(0.25f, -1, 0.25f), Vector3.up, triFloor, 0f, 10f, out float tb);
                Check(sb, ref pass, ref total, "RayTri 양면(백페이스 히트)", back && Approx(tb, 1f));
            }

            // --- BruteForceOccluder ---
            {
                // 같은 (x,z)를 덮는 두 평면: y=0, y=2. 위에서 쏘면 y=2(상단)가 최근접
                var lower = triFloor;
                var upper = new Tri { V0 = new Vector3(0, 2, 0), V1 = new Vector3(1, 2, 0), V2 = new Vector3(0, 2, 1) };
                var occ = new BruteForceOccluder(new[] { lower, upper });

                var hit = occ.Intersect(new Vector3(0.25f, 3, 0.25f), Vector3.down, 0f, 100f);
                Check(sb, ref pass, ref total, "Intersect 최근접 히트(t=1, upper)", hit.Valid && Approx(hit.T, 1f) && hit.TriIndex == 1);

                // 차폐: 사이에 막는 면 있음
                var blocker = new BruteForceOccluder(new[] {
                    new Tri { V0 = new Vector3(-1,1,-1), V1 = new Vector3(3,1,-1), V2 = new Vector3(-1,1,3) } });
                bool blocked = blocker.Occluded(new Vector3(0.25f, 0, 0.25f), Vector3.up, 2f);
                Check(sb, ref pass, ref total, "Occluded 차폐됨(t=1<2)", blocked);

                bool notBlocked = blocker.Occluded(new Vector3(0.25f, 0, 0.25f), Vector3.up, 0.5f); // maxDist<t
                Check(sb, ref pass, ref total, "Occluded maxDist 밖 → 비차폐", !notBlocked);
            }

            // --- CosineHemisphere 분포 ---
            {
                var rng = new Rng(12345);
                Vector3 n = Vector3.up;
                int N = 20000;
                float sumDot = 0f; bool allUpper = true;
                for (int i = 0; i < N; i++)
                {
                    Vector3 d = RadianceCore.CosineHemisphere(n, ref rng);
                    float c = Vector3.Dot(d, n);
                    if (c < -Eps) allUpper = false;
                    sumDot += c;
                }
                float avg = sumDot / N;
                Check(sb, ref pass, ref total, "CosineHemisphere 상반구 only", allUpper);
                Check(sb, ref pass, ref total, $"CosineHemisphere E[cosθ]≈2/3 (={avg:0.000})", Mathf.Abs(avg - 2f / 3f) < 0.01f);
            }

            // --- AO ---
            {
                var empty = new BruteForceOccluder(new Tri[0]);
                float aoOpen = RadianceCore.EvaluateAO(empty, Vector3.zero, Vector3.up, 64, 7);
                Check(sb, ref pass, ref total, "AO 개방 씬 = 1", Approx(aoOpen, 1f));

                var box = new BruteForceOccluder(MakeBox(Vector3.zero, 2f)); // 원점을 감싼 닫힌 박스
                float aoClosed = RadianceCore.EvaluateAO(box, Vector3.zero, Vector3.up, 256, 7);
                Check(sb, ref pass, ref total, $"AO 폐쇄 박스 ≈ 0 (={aoClosed:0.000})", aoClosed < 0.02f);

                float a1 = RadianceCore.EvaluateAO(box, Vector3.zero, Vector3.up, 128, 99);
                float a2 = RadianceCore.EvaluateAO(box, Vector3.zero, Vector3.up, 128, 99);
                Check(sb, ref pass, ref total, "AO 결정성(같은 seed)", a1 == a2);
            }

            // --- Direct (NEE) ---
            {
                var empty = new BruteForceOccluder(new Tri[0]);
                var sun = new DirectionalLight { Direction = Vector3.down, Color = new Vector3(1, 1, 1), Intensity = 1f };

                Vector3 lit = RadianceCore.EvaluateDirect(empty, Vector3.zero, Vector3.up, sun);
                Check(sb, ref pass, ref total, "Direct 정면(ndl=1) = Color*Intensity", ApproxV(lit, new Vector3(1, 1, 1)));

                Vector3 backface = RadianceCore.EvaluateDirect(empty, Vector3.zero, Vector3.down, sun);
                Check(sb, ref pass, ref total, "Direct 백페이스(ndl≤0) = 0", ApproxV(backface, Vector3.zero));

                // 머리 위에 막는 면 → 그림자
                var roof = new BruteForceOccluder(new[] {
                    new Tri { V0 = new Vector3(-2,1,-2), V1 = new Vector3(2,1,-2), V2 = new Vector3(0,1,3) } });
                Vector3 shadow = RadianceCore.EvaluateDirect(roof, Vector3.zero, Vector3.up, sun);
                Check(sb, ref pass, ref total, "Direct 그림자 = 0", ApproxV(shadow, Vector3.zero));
            }

            // --- Radiance 합성 ---
            {
                var empty = new BruteForceOccluder(new Tri[0]);
                var sun = new DirectionalLight { Direction = Vector3.down, Color = new Vector3(1, 1, 1), Intensity = 1f };
                Vector3 amb = new Vector3(0.2f, 0.2f, 0.2f);
                // 개방 씬: AO=1 → direct(1,1,1) + ambient*1 = (1.2,...)
                Vector3 r = RadianceCore.EvaluateRadiance(empty, Vector3.zero, Vector3.up, sun, amb, 64, 7);
                Check(sb, ref pass, ref total, "Radiance = direct + ambient*AO", ApproxV(r, new Vector3(1.2f, 1.2f, 1.2f)));
            }

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // 원점을 감싼 닫힌 박스(12 tris). 내부 점은 모든 방향이 차폐 → AO=0.
        public static Tri[] MakeBox(Vector3 center, float size)
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
                (c000, c100, c101, c001), // -Y
                (c010, c110, c111, c011), // +Y
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

        static bool Approx(float a, float b) => Mathf.Abs(a - b) < Eps;
        static bool ApproxV(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < Eps * Eps;

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        {
            total++;
            if (ok) pass++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }
    }
}
