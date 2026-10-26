using System.Text;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// SH-2 검증: BurstSHBaker.
    ///  A) 빈 씬 + 균일 하늘 → 미스만 → E(n) ≈ π·skyL (전 노멀 동일). 프로젝션·평가 end-to-end.
    ///  B) 빈 씬 + 그라디언트 하늘(위 밝음) → E(up) > E(down). 방향성.
    ///  C) 아래 바닥(태양 위→아래 조명) + 어두운 하늘 → hit 경로로 SH 비자명 + 아래에서 밝음.
    /// 결정적(RNG 없음). 호출: Debug.Log(BurstSHBakerTests.RunAll());
    /// 주의: BurstScene/BurstTwoLevel/BurstSky/SH9(전달) + 실측 게이트 전제.
    /// </summary>
    public static class BurstSHBakerTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== BurstSHBaker (SH-2) ===");
            int pass = 0, total = 0;
            int N = 2048;
            var sun = new DirectionalLight { Direction = new Vector3(0, -1, 0), Color = Vector3.one, Intensity = 1f };

            // ── A) 빈 씬 + 균일 하늘 ──
            {
                using var bvh = new TwoLevelBVH(new Tri[0][], new TwoLevelBVH.Instance[0], Allocator.TempJob);
                using var scene = BurstScene.Create(bvh, new Vector3[0], Allocator.TempJob);
                Vector3 skyL = new Vector3(0.8f, 0.9f, 1.0f);
                var sky = BurstSky.Uniform(skyL);

                var pts = new NativeArray<Vector3>(1, Allocator.TempJob);
                pts[0] = new Vector3(0, 5, 0);
                var sh = BurstSHBaker.Bake(scene, sky, sun, pts, N, Allocator.TempJob);

                Vector3 eUp = sh[0].Evaluate(Vector3.up);
                Vector3 eSide = sh[0].Evaluate(Vector3.right);
                Vector3 target = skyL * Mathf.PI;
                float err = (eUp - target).magnitude;
                float constMax = (eUp - eSide).magnitude;

                Check(sb, ref pass, ref total, $"A: 빈씬+균일하늘 E(n)≈π·L (err={err:0.000})", err < 0.05f);
                Check(sb, ref pass, ref total, $"A: 방향 무관 (Δ={constMax:0.000})", constMax < 0.02f);

                sh.Dispose(); pts.Dispose();
            }

            // ── B) 빈 씬 + 그라디언트 하늘 ──
            {
                using var bvh = new TwoLevelBVH(new Tri[0][], new TwoLevelBVH.Instance[0], Allocator.TempJob);
                using var scene = BurstScene.Create(bvh, new Vector3[0], Allocator.TempJob);
                var sky = BurstSky.Gradient(new Vector3(0.6f, 0.8f, 1.0f), new Vector3(0.05f, 0.05f, 0.08f));

                var pts = new NativeArray<Vector3>(1, Allocator.TempJob);
                pts[0] = new Vector3(0, 5, 0);
                var sh = BurstSHBaker.Bake(scene, sky, sun, pts, N, Allocator.TempJob);

                float eUp = sh[0].Evaluate(Vector3.up).x;
                float eDown = sh[0].Evaluate(Vector3.down).x;
                Check(sb, ref pass, ref total, $"B: 그라디언트 E(up)={eUp:0.00} > E(down)={eDown:0.00}", eUp > eDown + 0.1f);

                sh.Dispose(); pts.Dispose();
            }

            // ── C) 아래 바닥 + 어두운 하늘 (hit 경로) ──
            {
                // 큰 바닥 쿼드(2삼각형), y=0, 위를 향하는 노멀
                var floor = new[]
                {
                    new Tri { V0 = new Vector3(-20,0,-20), V1 = new Vector3(-20,0,20), V2 = new Vector3(20,0,20) },
                    new Tri { V0 = new Vector3(-20,0,-20), V1 = new Vector3(20,0,20),  V2 = new Vector3(20,0,-20) },
                };
                var meshes = new[] { floor };
                var albedo = new[] { new Vector3(0.8f, 0.8f, 0.8f) };
                var insts = new[] { new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = Matrix4x4.identity } };

                using var bvh = new TwoLevelBVH(meshes, insts, Allocator.TempJob, BVH.BuildQuality.SAH);
                using var scene = BurstScene.Create(bvh, albedo, Allocator.TempJob);
                var sky = BurstSky.Uniform(new Vector3(0.02f, 0.02f, 0.02f)); // 어두운 하늘

                var pts = new NativeArray<Vector3>(1, Allocator.TempJob);
                pts[0] = new Vector3(0, 3, 0); // 바닥 위 3m
                var sh = BurstSHBaker.Bake(scene, sky, sun, pts, N, Allocator.TempJob);

                // 바닥은 태양(위→아래)에 정면으로 조명 → 반사가 위로 → 대표점이 아래를 보면(=down normal) 바닥 기여 큼
                float eDown = sh[0].Evaluate(Vector3.down).x;   // 바닥을 향하는 노멀
                float eUp = sh[0].Evaluate(Vector3.up).x;       // 하늘(어두움) 향하는 노멀
                float coeffMag = sh[0].c0.magnitude + sh[0].c3.magnitude + sh[0].c1.magnitude;

                Check(sb, ref pass, ref total, $"C: hit 경로 SH 비자명 (Σ|c|≈{coeffMag:0.000})", coeffMag > 0.01f);
                Check(sb, ref pass, ref total, $"C: 바닥 반사 → E(down)={eDown:0.000} > E(up)={eUp:0.000}", eDown > eUp);

                sh.Dispose(); pts.Dispose();
            }

            RunMultiBounce(sb, ref pass, ref total);

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ── SH-2 다바운스(BakeMultiBounce) ──
        //  해석해 근거: 큰 바닥 평면 위 프로브 + 균일 하늘 → 입사 radiance 가 '위 a / 아래 b' 계단함수.
        //  계단함수는 홀수 밴드만 가지므로 L2 SH 조도(코사인 커널은 l=3 이상 홀수 밴드 0)가 정확:
        //  E(up)=π·a, E(down)=π·b, E(수평)=π·(a+b)/2. 바닥 radiance b = ρ/π·(E_sun + π·L) (경로추적, 물리 규약).
        static void RunMultiBounce(StringBuilder sb, ref int pass, ref int total)
        {
            const int N = 2048;
            const uint seed = 12345u;
            float rho = 0.6f;
            Vector3 skyL = new Vector3(0.3f, 0.3f, 0.3f);
            var sky = BurstSky.Uniform(skyL);
            var q = new BakeQualitySettings { MaxBounces = 2, RRStartDepth = 3, RayBias = 1e-4f, IndirectSamples = 64 };
            var probe = new Vector3(0, 1, 0);

            // ── D) 빈 씬: 전 방향 미스 → L_in = Sky 정확 → 1바운스 근사(Bake)와 동일 ──
            {
                using var bvh = new TwoLevelBVH(new Tri[0][], new TwoLevelBVH.Instance[0], Allocator.TempJob);
                using var scene = BurstScene.Create(bvh, new Vector3[0], Allocator.TempJob);
                var sun = new DirectionalLight { Direction = Vector3.down, Color = Vector3.one, Intensity = 1f };
                var pts = new NativeArray<Vector3>(1, Allocator.TempJob); pts[0] = probe;
                var a = BurstSHBaker.BakeMultiBounce(scene, sky, sun, q, pts, N, seed, Allocator.TempJob);
                var b = BurstSHBaker.Bake(scene, sky, sun, pts, N, Allocator.TempJob);
                float d = MaxCoeffDiff(a[0], b[0]);
                float err = MaxAbs(a[0].Evaluate(Vector3.up) - skyL * Mathf.PI);
                Check(sb, ref pass, ref total, $"D: 다바운스 빈씬 E(n)≈π·L (err={err:0.00000}) · ≡1바운스(Δc={d:0.0e0})", err < 1e-3f && d < 1e-5f);
                a.Dispose(); b.Dispose(); pts.Dispose();
            }

            // 큰 바닥(±1000, y=0): 높이 1 의 프로브에서 지평선 근처 미스 비율 ≈0.1% → 해석해 오차 무시 수준
            var floor = Quad(1000f, 0f);
            var meshes = new[] { floor };
            var albedo = new[] { new Vector3(rho, rho, rho) };
            var insts = new[] { new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = Matrix4x4.identity } };

            // ── E) 바닥 + 하늘(태양 0): b = ρ·L → 1바운스 근사의 앰비언트 항과 같은 값 ──
            {
                using var bvh = new TwoLevelBVH(meshes, insts, Allocator.TempJob, BVH.BuildQuality.SAH);
                using var scene = BurstScene.Create(bvh, albedo, Allocator.TempJob);
                var sun = new DirectionalLight { Direction = Vector3.down, Color = Vector3.one, Intensity = 0f };
                var pts = new NativeArray<Vector3>(1, Allocator.TempJob); pts[0] = probe;
                var sh = BurstSHBaker.BakeMultiBounce(scene, sky, sun, q, pts, N, seed, Allocator.TempJob);
                var legacy = BurstSHBaker.Bake(scene, sky, sun, pts, N, Allocator.TempJob);

                float a = skyL.x, b = rho * skyL.x;
                float eUp = sh[0].Evaluate(Vector3.up).x, eDn = sh[0].Evaluate(Vector3.down).x, eSd = sh[0].Evaluate(Vector3.right).x;
                Check(sb, ref pass, ref total, $"E: 바닥+하늘 E(up)={eUp:0.0000}≈π·L={Mathf.PI * a:0.0000}", RelOk(eUp, Mathf.PI * a));
                Check(sb, ref pass, ref total, $"E: 바닥+하늘 E(down)={eDn:0.0000}≈π·ρL={Mathf.PI * b:0.0000}", RelOk(eDn, Mathf.PI * b));
                Check(sb, ref pass, ref total, $"E: 바닥+하늘 E(수평)={eSd:0.0000}≈π(1+ρ)L/2={Mathf.PI * (a + b) * 0.5f:0.0000}", RelOk(eSd, Mathf.PI * (a + b) * 0.5f));
                float d = MaxAbs(sh[0].Evaluate(Vector3.down) - legacy[0].Evaluate(Vector3.down));
                Check(sb, ref pass, ref total, $"E: 태양 없으면 다바운스 ≡ 1바운스 근사 (ΔE(down)={d:0.0e0})", d < 1e-4f);
                sh.Dispose(); legacy.Dispose(); pts.Dispose();
            }

            // ── F) 바닥 + 태양 + 하늘: 텍스처 라이트맵 경로(BurstIndirect)와 같은 점·노멀에서 일치 ──
            {
                using var bvh = new TwoLevelBVH(meshes, insts, Allocator.TempJob, BVH.BuildQuality.SAH);
                using var scene = BurstScene.Create(bvh, albedo, Allocator.TempJob);
                float I = 1.5f;
                var sun = new DirectionalLight { Direction = Vector3.down, Color = Vector3.one, Intensity = I };
                var pts = new NativeArray<Vector3>(1, Allocator.TempJob); pts[0] = probe;
                var sh = BurstSHBaker.BakeMultiBounce(scene, sky, sun, q, pts, N, seed, Allocator.TempJob);
                var legacy = BurstSHBaker.Bake(scene, sky, sun, pts, N, Allocator.TempJob);

                // 텍셀 경로: 같은 점에서 노멀 down/up 으로 BurstIndirect(경로추적 간접 조도)
                var tp = new NativeArray<Vector3>(2, Allocator.TempJob); tp[0] = probe; tp[1] = probe;
                var tn = new NativeArray<Vector3>(2, Allocator.TempJob); tn[0] = Vector3.down; tn[1] = Vector3.up;
                var tv = new NativeArray<bool>(2, Allocator.TempJob); tv[0] = true; tv[1] = true;
                var texel = BurstIndirect.Compute(scene, sky, sun, q, tp, tn, tv, seed, Allocator.TempJob);

                float expDown = rho * I + Mathf.PI * rho * skyL.x;     // π·b, b = ρ/π·(I + π·L)
                float eDn = sh[0].Evaluate(Vector3.down).x, eUp = sh[0].Evaluate(Vector3.up).x;
                Check(sb, ref pass, ref total, $"F: E(down)={eDn:0.0000} ≈ 해석해 ρI+πρL={expDown:0.0000}", RelOk(eDn, expDown));
                Check(sb, ref pass, ref total, $"F: E(down) ≡ 텍스처 간접(BurstIndirect)={texel[0].x:0.0000}", RelOk(eDn, texel[0].x));
                Check(sb, ref pass, ref total, $"F: E(up)={eUp:0.0000} ≡ 텍스처 간접={texel[1].x:0.0000}", RelOk(eUp, texel[1].x));
                // 규약 통일(v15.3): 1바운스 근사도 ρ/π·(E_sun + π·Sky) — 열린 바닥에선 앰비언트 근사가 정확해 해석해와 같아야 함
                float lg = legacy[0].Evaluate(Vector3.down).x;
                Check(sb, ref pass, ref total, $"F: 1바운스 근사(Bake) E(down)={lg:0.0000} ≈ 해석해(규약 통일)", RelOk(lg, expDown));
                sh.Dispose(); legacy.Dispose(); texel.Dispose(); tp.Dispose(); tn.Dispose(); tv.Dispose(); pts.Dispose();
            }

            // ── G) 바닥+천장 사이(측면 개방): 바운스 증가 → 평균 조도(c0) 단조 증가 + 수렴, RR off ──
            //  방향별 독립 시드라 바운스 수만 바꾸면 같은 경로 접두에 비음수 항만 더해짐 → c0 는 정확히 단조.
            {
                var slab = new[] { Quad(5f, 0f), Quad(5f, 2f) };
                var slabAlb = new[] { new Vector3(0.7f, 0.7f, 0.7f), new Vector3(0.7f, 0.7f, 0.7f) };
                var slabInst = new[]
                {
                    new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = Matrix4x4.identity },
                    new TwoLevelBVH.Instance { MeshIndex = 1, LocalToWorld = Matrix4x4.identity },
                };
                using var bvh = new TwoLevelBVH(slab, slabInst, Allocator.TempJob, BVH.BuildQuality.SAH);
                using var scene = BurstScene.Create(bvh, slabAlb, Allocator.TempJob);
                var sun = new DirectionalLight { Direction = new Vector3(-0.6f, -0.5f, 0.2f), Color = Vector3.one, Intensity = 1f };
                var pts = new NativeArray<Vector3>(1, Allocator.TempJob); pts[0] = new Vector3(0, 1, 0);

                const int K = 6;
                var c0 = new float[K + 1];
                for (int k = 1; k <= K; k++)
                {
                    var qk = new BakeQualitySettings { MaxBounces = k, RRStartDepth = 1000, RayBias = 1e-4f };
                    var sh = BurstSHBaker.BakeMultiBounce(scene, sky, sun, qk, pts, 1024, seed, Allocator.TempJob);
                    c0[k] = sh[0].c0.x;
                    sh.Dispose();
                }
                bool mono = true;
                for (int k = 2; k <= K; k++) mono &= c0[k] >= c0[k - 1];
                float d1 = c0[2] - c0[1], dLast = c0[K] - c0[K - 1];
                Check(sb, ref pass, ref total, $"G: 바운스 1→{K} c0 단조 증가 ({c0[1]:0.0000}→{c0[K]:0.0000})", mono);
                Check(sb, ref pass, ref total, $"G: 수렴 Δ(1→2)={d1:0.0000} > Δ({K - 1}→{K})={dLast:0.00000}", d1 > 0f && dLast < d1 * 0.5f);

                // ── H) 결정성: 같은 seed 두 번 → 비트 동일 ──
                var qh = new BakeQualitySettings { MaxBounces = 4, RRStartDepth = 3, RayBias = 1e-4f };
                var h1 = BurstSHBaker.BakeMultiBounce(scene, sky, sun, qh, pts, 512, seed, Allocator.TempJob);
                var h2 = BurstSHBaker.BakeMultiBounce(scene, sky, sun, qh, pts, 512, seed, Allocator.TempJob);
                Check(sb, ref pass, ref total, $"H: 같은 seed 재현 (Δc={MaxCoeffDiff(h1[0], h2[0]):0.0e0})", MaxCoeffDiff(h1[0], h2[0]) == 0f);
                h1.Dispose(); h2.Dispose(); pts.Dispose();
            }
        }

        // y=높이, ±h 정사각 2삼각형(노멀 +Y; ClosestHit 가 레이 쪽으로 뒤집으므로 양면)
        static Tri[] Quad(float h, float y) => new[]
        {
            new Tri { V0 = new Vector3(-h, y, -h), V1 = new Vector3(-h, y, h), V2 = new Vector3(h, y, h) },
            new Tri { V0 = new Vector3(-h, y, -h), V1 = new Vector3(h, y, h),  V2 = new Vector3(h, y, -h) },
        };

        static bool RelOk(float got, float exp, float rel = 0.01f) => Mathf.Abs(got - exp) <= rel * Mathf.Abs(exp) + 1e-4f;

        static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));

        static float MaxCoeffDiff(SH9 a, SH9 b)
        {
            float m = MaxAbs(a.c0 - b.c0);
            m = Mathf.Max(m, MaxAbs(a.c1 - b.c1)); m = Mathf.Max(m, MaxAbs(a.c2 - b.c2)); m = Mathf.Max(m, MaxAbs(a.c3 - b.c3));
            m = Mathf.Max(m, MaxAbs(a.c4 - b.c4)); m = Mathf.Max(m, MaxAbs(a.c5 - b.c5)); m = Mathf.Max(m, MaxAbs(a.c6 - b.c6));
            m = Mathf.Max(m, MaxAbs(a.c7 - b.c7)); m = Mathf.Max(m, MaxAbs(a.c8 - b.c8));
            return m;
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
