using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// SH-G 검증(GPU↔Burst): PathTrace.compute 의 CSSHBake 커널이 <see cref="BurstSHBaker"/>(SH-2)
    ///   per-instance SH9 프로젝션과 일치함을 확인.
    ///
    /// 검증 기준(타이트 — 전이함수 없음):
    ///   • 방향셋: BurstSHBaker.FibonacchiSphere 를 CPU 에서 계산해 GPU 로 업로드 → 동일 방향(sqrt/sin/cos 발산 제거).
    ///   • 기저(SH9.Basis): 순수 다항식(cos/sin 없음) → GPU/CPU 비트 근접.
    ///   • 순회(ClosestHit tmin=1e-4)·DirectNEE·SkyRadiance: G4/G5 에서 검증된 로직 재사용.
    ///   ⇒ near-bit-identical. 프로브별 9계수 |gpu-cpu| mean/max 로그, over(1e-4) 카운트. mean 기준 PASS.
    ///
    /// 잔여 발산원: 순회/닷곱의 fp 반올림, near-tie 히트 선택 경계(드묾) → max 는 가끔 스파이크, mean 은 타이트.
    ///
    /// ⚠️ 렌더 컨텍스트 필요(에디터/플레이) — 헤드리스 CI 불가. compute 미지원 → SKIP.
    /// 호출: Debug.Log(GpuSHBakeCompareTests.RunAll());
    /// </summary>
    public static class GpuSHBakeCompareTests
    {
        const int   DirCount   = 256;   // 피보나치 방향 수(Burst/GPU 공유)
        const float MeanGate   = 1e-3f; // PASS 게이트(기대 실측 ~1e-4)
        const float EpsCoeff   = 1e-4f; // over 카운트 임계

        static ComputeShader LoadCompute()
        {
            return Resources.Load<ComputeShader>("PathTrace"); // Shaders/Resources 배치 → 에디터·빌드 공통 로드
        }

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== SH-G GPU CSSHBake ≡ BurstSHBaker(SH-2) ===");

            if (!SystemInfo.supportsComputeShaders)
            { sb.AppendLine("  [SKIP] compute shader 미지원 플랫폼 — GPU SH 베이크 검증 불가"); return sb.ToString(); }

            var cs = LoadCompute();
            if (cs == null)
            { sb.AppendLine("  [FAIL] PathTrace.compute 로드 실패 (경로/컴파일 확인)"); return sb.ToString(); }

            int kSH = cs.FindKernel("CSSHBake");
            if (kSH < 0)
            { sb.AppendLine("  [FAIL] 커널(CSSHBake) 미발견"); return sb.ToString(); }

            int pass = 0, total = 0;

            // ── 씬: enclosing 박스(모든 방향 히트) + 내부 인스턴스(비자명 SH) ──
            BuildScene(out var meshes, out var albedo, out var insts);
            using var bvh = new TwoLevelBVH(meshes, insts, Allocator.TempJob, BVH.BuildQuality.SAH);
            using var scene = BurstScene.Create(bvh, albedo, Allocator.TempJob);
            using var gpu = new GpuScene(scene);

            // ── 프로브: 박스 내부 여러 점 ──
            var points = ProbePoints();
            int n = points.Length;

            var sun = Sun();
            var skyM = BurstSky.Gradient(new Vector3(0.5f, 0.6f, 0.8f), new Vector3(0.10f, 0.10f, 0.12f));
            float weight = 4f * Mathf.PI / DirCount;

            // ── CPU 정답(Burst) — 내부에서 동일 FibonacchiSphere(DirCount) 사용 ──
            var naPts = new NativeArray<Vector3>(points, Allocator.TempJob);
            var cpu = BurstSHBaker.Bake(scene, skyM, sun, naPts, DirCount, Allocator.TempJob);

            // ── GPU: Burst 와 동일 방향셋을 CPU 에서 계산해 업로드 ──
            var naDirs = BurstSHBaker.FibonacchiSphere(DirCount, Allocator.TempJob);
            var dirs = new Vector3[DirCount];
            for (int i = 0; i < DirCount; i++) dirs[i] = naDirs[i];

            SH9[] gpuSH = GpuSHBaker.Bake(gpu, cs, kSH, sun, skyM, points, dirs, weight);

            // ── 대조: 프로브별 9계수(각 RGB 성분) |gpu-cpu| ──
            double sum = 0; float maxErr = 0f; int over = 0; long samples = 0;
            for (int i = 0; i < n; i++)
            {
                for (int k = 0; k < SH9.Count; k++)
                {
                    Vector3 dvec = Coeff(cpu[i], k) - Coeff(gpuSH[i], k);
                    float ex = Mathf.Abs(dvec.x), ey = Mathf.Abs(dvec.y), ez = Mathf.Abs(dvec.z);
                    float e = Mathf.Max(ex, Mathf.Max(ey, ez));
                    if (e > maxErr) maxErr = e;
                    sum += ex; sum += ey; sum += ez;
                    samples += 3;
                    if (e > EpsCoeff) over++;
                }
            }
            float mean = samples > 0 ? (float)(sum / samples) : 0f;

            Check(sb, ref pass, ref total,
                $"SH-G CSSHBake ≡ Burst (n={n}, dirs={DirCount}, mean={mean:E2}, max={maxErr:E2}, over({EpsCoeff:E0})={over}/{n * SH9.Count})",
                mean < MeanGate);

            sb.AppendLine($"  [info] TLAS nodes={bvh.TlasNodeCount}, inst={bvh.InstanceCount}, BLAS={bvh.BlasCount}");
            sb.AppendLine("  [info] 동일 방향셋+다항식 기저+G4 순회 ⇒ near-bit-identical. max 스파이크=near-tie 히트 경계(드묾).");

            naPts.Dispose(); naDirs.Dispose(); cpu.Dispose();

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // SH9 의 k 번째 계수(0..8) → Vector3. 필드가 개별 멤버라 스위치로 접근.
        static Vector3 Coeff(in SH9 sh, int k)
        {
            switch (k)
            {
                case 0: return sh.c0;
                case 1: return sh.c1;
                case 2: return sh.c2;
                case 3: return sh.c3;
                case 4: return sh.c4;
                case 5: return sh.c5;
                case 6: return sh.c6;
                case 7: return sh.c7;
                default: return sh.c8;
            }
        }

        // enclosing 박스 + 회전·비균등 내부 인스턴스(GpuRadianceCompareTests 패턴 축약).
        static void BuildScene(out Tri[][] meshes, out Vector3[] albedo, out TwoLevelBVH.Instance[] insts)
        {
            var rng = new System.Random(20240629);
            meshes = new Tri[][]
            {
                LightmapEvaluateTests.MakeBox(Vector3.zero, 2f),
                LightmapEvaluateTests.MakeBox(Vector3.zero, 1f),
            };
            albedo = new Vector3[]
            {
                new Vector3(0.72f, 0.70f, 0.68f),
                new Vector3(0.55f, 0.35f, 0.30f),
            };

            var list = new List<TwoLevelBVH.Instance>
            {
                new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * 6f) },
            };
            for (int i = 0; i < 5; i++)
            {
                var trs = Matrix4x4.TRS(
                    RandV(rng, 2.0f),
                    Quaternion.Euler(Rand(rng, 180f), Rand(rng, 180f), Rand(rng, 180f)),
                    new Vector3(0.6f + (float)rng.NextDouble(), 0.6f + (float)rng.NextDouble(), 0.6f + (float)rng.NextDouble()));
                list.Add(new TwoLevelBVH.Instance { MeshIndex = 1, LocalToWorld = trs });
            }
            insts = list.ToArray();
        }

        // 박스 내부(±6 박스, 반지름 3 이내) 결정적 프로브점.
        static Vector3[] ProbePoints()
        {
            var rng = new System.Random(1337);
            var pts = new Vector3[48];
            for (int i = 0; i < pts.Length; i++) pts[i] = RandV(rng, 2.6f);
            return pts;
        }

        static DirectionalLight Sun() => new DirectionalLight
        {
            Direction = new Vector3(-0.4f, -1f, -0.25f).normalized,
            Color = new Vector3(1f, 0.95f, 0.8f),
            Intensity = 1.3f
        };

        static Vector3 RandV(System.Random rng, float e) => new Vector3(Rand(rng, e), Rand(rng, e), Rand(rng, e));
        static float Rand(System.Random rng, float half) => (float)(rng.NextDouble() * 2.0 - 1.0) * half;

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
