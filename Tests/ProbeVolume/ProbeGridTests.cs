using System.Text;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Track B B1-1 검증: <see cref="ProbeGrid"/>.
    ///  A) 인덱스 ↔ 격자좌표 ↔ 위치 왕복 · Positions 순서 = Index · GridCoord(프로브) = (i,j,k) · TextureUVW = 텍셀 중심.
    ///  B) FromBounds: 덮음 · 중심 정렬 · 정수배 축은 코너 일치 · 크기 0 → 1 프로브.
    ///  C) 선형장 trilinear 정확: c_k(p) = A_k·p + B_k → 임의 내부점 오차 ε · 프로브 위치 = 프로브 값 ·
    ///     볼륨 밖 = 경계 클램프 · dims 1 축 · 전부 유효 마스크 ≡ 무마스크.
    ///  D) 격자 베이크 ≡ 같은 점 직접 BurstSHBaker 호출(1바운스·다바운스 비트 동일 — 인덱스 ↔ 점 ↔ 시드 계약).
    ///  E) 열린 바닥 + 균일 하늘 + 수직 태양: 전 프로브·임의 내부점에서 E(up)=πL, E(down)=ρI+πρL, E(수평)=평균
    ///     (BurstSHBakerTests F 해석해 — 무한 바닥이면 높이 무관).
    ///  F) GPU BakeGpu ≡ BakeBurst(1바운스, mean 게이트 — GpuSHBakeCompareTests 기준). compute 미지원 → SKIP.
    /// 결정적. 호출: Debug.Log(ProbeGridTests.RunAll());
    /// </summary>
    public static class ProbeGridTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Track B ProbeGrid (B1-1) ===");
            int pass = 0, total = 0;
            TestIndexing(sb, ref pass, ref total);
            TestFromBounds(sb, ref pass, ref total);
            TestTrilinear(sb, ref pass, ref total);
            TestBakeContract(sb, ref pass, ref total);
            TestOpenFloor(sb, ref pass, ref total);
            TestGpu(sb, ref pass, ref total);
            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ── A) 인덱싱 ──
        static void TestIndexing(StringBuilder sb, ref int pass, ref int total)
        {
            var sp = new Vector3(0.5f, 2f, 1.25f);
            var g = new ProbeGrid(new Vector3(-1.5f, 0.25f, 3f), sp, new Vector3Int(5, 3, 4));
            var pos = g.Positions();
            bool okRound = true, okPos = true, okGrid = true, okUvw = true;
            int idx = 0;
            for (int k = 0; k < g.dims.z; k++)
                for (int j = 0; j < g.dims.y; j++)
                    for (int i = 0; i < g.dims.x; i++, idx++)
                    {
                        okRound &= g.Index(i, j, k) == idx && g.Coord(idx) == new Vector3Int(i, j, k);
                        Vector3 expect = g.origin + new Vector3(i * sp.x, j * sp.y, k * sp.z);
                        okPos &= (pos[idx] - expect).sqrMagnitude < 1e-10f && (g.Position(idx) - expect).sqrMagnitude < 1e-10f;
                        okGrid &= (g.GridCoord(pos[idx]) - new Vector3(i, j, k)).sqrMagnitude < 1e-8f;
                        Vector3 uvw = g.TextureUVW(pos[idx]);
                        okUvw &= Mathf.Abs(uvw.x - (i + 0.5f) / g.dims.x) < 1e-5f
                              && Mathf.Abs(uvw.y - (j + 0.5f) / g.dims.y) < 1e-5f
                              && Mathf.Abs(uvw.z - (k + 0.5f) / g.dims.z) < 1e-5f;
                    }
            bool okCount = pos.Length == 60 && g.Count == 60 && g.IsValid;
            bool okMax = (g.MaxCorner - g.Position(4, 2, 3)).sqrMagnitude < 1e-10f;
            Check(sb, ref pass, ref total, $"A: Count={g.Count}=5·3·4 · MaxCorner = Position(4,2,3)", okCount && okMax);
            Check(sb, ref pass, ref total, "A: Index ↔ Coord 왕복 · Positions 순서 = 선형 인덱스", okRound && okPos);
            Check(sb, ref pass, ref total, "A: GridCoord(프로브) = (i,j,k) · TextureUVW = (ijk+0.5)/dims(텍셀 중심)", okGrid && okUvw);
        }

        // ── B) FromBounds ──
        static void TestFromBounds(StringBuilder sb, ref int pass, ref int total)
        {
            var b = new Bounds(new Vector3(1f, 2f, 3f), new Vector3(10f, 3f, 0.7f));
            var g = ProbeGrid.FromBounds(b, new Vector3(1f, 1f, 0.25f));
            bool okDims = g.dims == new Vector3Int(11, 4, 4);   // 10/1+1, 3/1+1, ⌈2.8⌉+1
            Vector3 lo = g.origin, hi = g.MaxCorner;
            bool okCover = lo.x <= b.min.x + 1e-4f && lo.y <= b.min.y + 1e-4f && lo.z <= b.min.z + 1e-4f
                        && hi.x >= b.max.x - 1e-4f && hi.y >= b.max.y - 1e-4f && hi.z >= b.max.z - 1e-4f;
            bool okCenter = ((lo + hi) * 0.5f - b.center).sqrMagnitude < 1e-8f;
            bool okExact = Mathf.Abs(lo.x - b.min.x) < 1e-4f && Mathf.Abs(hi.x - b.max.x) < 1e-4f;
            Check(sb, ref pass, ref total, $"B: FromBounds dims={g.dims} (11,4,4) · 덮음 · 중심 정렬 · 정수배 축 코너 일치",
                okDims && okCover && okCenter && okExact);

            var g0 = ProbeGrid.FromBounds(new Bounds(Vector3.one, Vector3.zero), Vector3.one);
            Check(sb, ref pass, ref total, $"B: 크기 0 → dims={g0.dims} (1,1,1), 프로브 = 중심",
                g0.dims == Vector3Int.one && (g0.origin - Vector3.one).sqrMagnitude < 1e-10f);
        }

        // ── C) 선형장 trilinear ──
        static void TestTrilinear(StringBuilder sb, ref int pass, ref int total)
        {
            var rng = new System.Random(7);
            var g = new ProbeGrid(new Vector3(-2f, -1f, 0.5f), new Vector3(0.75f, 0.5f, 1.5f), new Vector3Int(6, 4, 5));
            var field = new LinearField(rng);
            var sh = Fill(g, field);

            // 임의 내부점
            float maxErr = 0f;
            Vector3 lo = g.origin, hi = g.MaxCorner;
            for (int s = 0; s < 200; s++)
            {
                var p = new Vector3(Lerp(rng, lo.x, hi.x), Lerp(rng, lo.y, hi.y), Lerp(rng, lo.z, hi.z));
                maxErr = Mathf.Max(maxErr, MaxCoeffDiff(g.Sample(sh, p), field.At(p)));
            }
            Check(sb, ref pass, ref total, $"C: 선형장 trilinear 정확 — 내부점 200개 max|Δc|={maxErr:0.0e0}", maxErr < 1e-4f);

            // 프로브 위치 = 프로브 값
            float atProbe = 0f;
            for (int i = 0; i < g.Count; i++) atProbe = Mathf.Max(atProbe, MaxCoeffDiff(g.Sample(sh, g.Position(i)), sh[i]));
            Check(sb, ref pass, ref total, $"C: 프로브 위치 샘플 = 프로브 값 (max|Δc|={atProbe:0.0e0})", atProbe < 1e-5f);

            // 볼륨 밖 = 경계 클램프(Texture3D Clamp 와 동일)
            float outErr = 0f;
            var outside = new[] { hi + new Vector3(1f, 2f, 3f), lo - new Vector3(5f, 0.1f, 2f), new Vector3(lo.x - 1f, (lo.y + hi.y) * 0.5f, hi.z + 4f) };
            foreach (var p in outside)
            {
                var pc = new Vector3(Mathf.Clamp(p.x, lo.x, hi.x), Mathf.Clamp(p.y, lo.y, hi.y), Mathf.Clamp(p.z, lo.z, hi.z));
                outErr = Mathf.Max(outErr, MaxCoeffDiff(g.Sample(sh, p), field.At(pc)));
            }
            Check(sb, ref pass, ref total, $"C: 볼륨 밖 = 경계 클램프 값 (max|Δc|={outErr:0.0e0})", outErr < 1e-4f);

            // dims.y = 1 축: y 무시
            var g2 = new ProbeGrid(new Vector3(0f, 3f, 0f), new Vector3(1f, 1f, 2f), new Vector3Int(4, 1, 3));
            var sh2 = Fill(g2, field);
            float flatErr = 0f;
            for (int s = 0; s < 50; s++)
            {
                var p = new Vector3(Lerp(rng, 0f, 3f), Lerp(rng, -10f, 10f), Lerp(rng, 0f, 4f));
                flatErr = Mathf.Max(flatErr, MaxCoeffDiff(g2.Sample(sh2, p), field.At(new Vector3(p.x, 3f, p.z))));
            }
            Check(sb, ref pass, ref total, $"C: dims 1 축(4×1×3) — y 무관 bilinear (max|Δc|={flatErr:0.0e0})", flatErr < 1e-4f);

            // 전부 유효 마스크 ≡ 무마스크(재정규화 Σw≈1)
            var allValid = new bool[g.Count];
            for (int i = 0; i < allValid.Length; i++) allValid[i] = true;
            float maskErr = 0f;
            for (int s = 0; s < 50; s++)
            {
                var p = new Vector3(Lerp(rng, lo.x, hi.x), Lerp(rng, lo.y, hi.y), Lerp(rng, lo.z, hi.z));
                maskErr = Mathf.Max(maskErr, MaxCoeffDiff(g.Sample(sh, allValid, p), g.Sample(sh, p)));
            }
            Check(sb, ref pass, ref total, $"C: 전부 유효 마스크 ≡ 무마스크 (max|Δc|={maskErr:0.0e0})", maskErr < 1e-5f);
        }

        // ── D) 격자 베이크 ≡ 직접 BurstSHBaker 호출 ──
        static void TestBakeContract(StringBuilder sb, ref int pass, ref int total)
        {
            var meshes = new[] { Quad(20f, 0f), LightmapEvaluateTests.MakeBox(Vector3.zero, 1f) };
            var albedo = new[] { new Vector3(0.6f, 0.6f, 0.6f), new Vector3(0.7f, 0.4f, 0.3f) };
            var insts = new[]
            {
                new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = Matrix4x4.identity },
                new TwoLevelBVH.Instance { MeshIndex = 1, LocalToWorld = Matrix4x4.TRS(new Vector3(0.4f, 0.6f, -0.3f), Quaternion.Euler(10f, 30f, 0f), new Vector3(1.2f, 1.2f, 0.8f)) },
            };
            using var bvh = new TwoLevelBVH(meshes, insts, Allocator.TempJob, BVH.BuildQuality.SAH);
            using var scene = BurstScene.Create(bvh, albedo, Allocator.TempJob);
            var sun = new DirectionalLight { Direction = new Vector3(-0.4f, -1f, -0.25f).normalized, Color = Vector3.one, Intensity = 1.3f };
            var sky = BurstSky.Gradient(new Vector3(0.5f, 0.6f, 0.8f), new Vector3(0.1f, 0.1f, 0.12f));
            var g = new ProbeGrid(new Vector3(-3f, 0.5f, -3f), new Vector3(1.5f, 1f, 1.5f), new Vector3Int(5, 3, 5));
            const int dirs = 128;
            const uint seed = 4242u;

            var pts = new NativeArray<Vector3>(g.Positions(), Allocator.TempJob);
            try
            {
                var q1 = new BakeQualitySettings { MaxBounces = 1, RRStartDepth = 3, RayBias = 1e-4f };
                var grid1 = g.BakeBurst(scene, sky, sun, q1, dirs, seed);
                var direct1 = BurstSHBaker.Bake(scene, sky, sun, pts, dirs, Allocator.TempJob);
                float d1 = 0f, c0Sum = 0f;
                for (int i = 0; i < g.Count; i++) { d1 = Mathf.Max(d1, MaxCoeffDiff(grid1[i], direct1[i])); c0Sum += grid1[i].c0.x; }
                direct1.Dispose();
                Check(sb, ref pass, ref total, $"D: 격자 베이크(1바운스) ≡ BurstSHBaker.Bake 직접 (n={g.Count}, Δc={d1}, Σc0={c0Sum:0.00})",
                    d1 == 0f && c0Sum > 0.01f);

                var q3 = new BakeQualitySettings { MaxBounces = 3, RRStartDepth = 2, RayBias = 1e-4f };
                var grid3 = g.BakeBurst(scene, sky, sun, q3, dirs, seed);
                var direct3 = BurstSHBaker.BakeMultiBounce(scene, sky, sun, q3, pts, dirs, seed, Allocator.TempJob);
                float d3 = 0f;
                for (int i = 0; i < g.Count; i++) d3 = Mathf.Max(d3, MaxCoeffDiff(grid3[i], direct3[i]));
                direct3.Dispose();
                Check(sb, ref pass, ref total, $"D: 격자 베이크(다바운스 3) ≡ BakeMultiBounce 직접 — 인덱스↔시드 계약 (Δc={d3})", d3 == 0f);
            }
            finally { pts.Dispose(); }
        }

        // ── E) 열린 바닥 해석해 ──
        static void TestOpenFloor(StringBuilder sb, ref int pass, ref int total)
        {
            const float rho = 0.6f, I = 1.5f;
            float L = 0.3f;
            var meshes = new[] { Quad(1000f, 0f) };
            var albedo = new[] { new Vector3(rho, rho, rho) };
            var insts = new[] { new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = Matrix4x4.identity } };
            using var bvh = new TwoLevelBVH(meshes, insts, Allocator.TempJob, BVH.BuildQuality.SAH);
            using var scene = BurstScene.Create(bvh, albedo, Allocator.TempJob);
            var sun = new DirectionalLight { Direction = Vector3.down, Color = Vector3.one, Intensity = I };
            var sky = BurstSky.Uniform(new Vector3(L, L, L));
            var g = new ProbeGrid(new Vector3(-2f, 0.5f, -2f), new Vector3(2f, 0.75f, 2f), new Vector3Int(3, 3, 3));
            var q = new BakeQualitySettings { MaxBounces = 1, RRStartDepth = 3, RayBias = 1e-4f };
            var sh = g.BakeBurst(scene, sky, sun, q, 2048, 1u);

            float expUp = Mathf.PI * L, expDown = rho * I + Mathf.PI * rho * L, expSide = 0.5f * (expUp + expDown);
            bool okProbes = true; float worst = 0f;
            for (int i = 0; i < g.Count; i++)
            {
                float up = sh[i].Evaluate(Vector3.up).x, dn = sh[i].Evaluate(Vector3.down).x, sd = sh[i].Evaluate(Vector3.right).x;
                okProbes &= RelOk(up, expUp) && RelOk(dn, expDown) && RelOk(sd, expSide);
                worst = Mathf.Max(worst, Mathf.Max(Rel(up, expUp), Mathf.Max(Rel(dn, expDown), Rel(sd, expSide))));
            }
            Check(sb, ref pass, ref total, $"E: 전 프로브 E(up)≈πL={expUp:0.000} · E(down)≈ρI+πρL={expDown:0.000} · E(수평)≈평균 (worst rel={worst:0.0000})", okProbes);

            var rng = new System.Random(3);
            Vector3 lo = g.origin, hi = g.MaxCorner;
            bool okSample = true; float worstS = 0f;
            for (int s = 0; s < 32; s++)
            {
                var p = new Vector3(Lerp(rng, lo.x, hi.x), Lerp(rng, lo.y, hi.y), Lerp(rng, lo.z, hi.z));
                var e = g.Sample(sh, p);
                float up = e.Evaluate(Vector3.up).x, dn = e.Evaluate(Vector3.down).x;
                okSample &= RelOk(up, expUp) && RelOk(dn, expDown);
                worstS = Mathf.Max(worstS, Mathf.Max(Rel(up, expUp), Rel(dn, expDown)));
            }
            Check(sb, ref pass, ref total, $"E: 임의 내부점 32개 샘플도 해석해 (worst rel={worstS:0.0000})", okSample);
        }

        // ── F) GPU ≡ Burst ──
        static void TestGpu(StringBuilder sb, ref int pass, ref int total)
        {
            if (!SystemInfo.supportsComputeShaders) { sb.AppendLine("  [SKIP] F: compute shader 미지원 — GPU 격자 베이크 검증 불가"); return; }
            var cs = PathTraceShader.Resolve();
            var q = new BakeQualitySettings { MaxBounces = 1, RRStartDepth = 3, RayBias = 1e-4f };
            string kn = ProbeGrid.GpuKernelName(q);
            if (cs == null || !cs.HasKernel(kn)) { Check(sb, ref pass, ref total, $"F: PathTrace.compute 로드/커널({kn}) 실패", false); return; }

            var meshes = new[] { Quad(20f, 0f), LightmapEvaluateTests.MakeBox(Vector3.zero, 1f) };
            var albedo = new[] { new Vector3(0.6f, 0.6f, 0.6f), new Vector3(0.7f, 0.4f, 0.3f) };
            var insts = new[]
            {
                new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = Matrix4x4.identity },
                new TwoLevelBVH.Instance { MeshIndex = 1, LocalToWorld = Matrix4x4.TRS(new Vector3(0.4f, 0.6f, -0.3f), Quaternion.Euler(10f, 30f, 0f), Vector3.one) },
            };
            using var bvh = new TwoLevelBVH(meshes, insts, Allocator.TempJob, BVH.BuildQuality.SAH);
            using var scene = BurstScene.Create(bvh, albedo, Allocator.TempJob);
            using var gpu = new GpuScene(scene);
            var sun = new DirectionalLight { Direction = new Vector3(-0.4f, -1f, -0.25f).normalized, Color = Vector3.one, Intensity = 1.3f };
            var sky = BurstSky.Gradient(new Vector3(0.5f, 0.6f, 0.8f), new Vector3(0.1f, 0.1f, 0.12f));
            var g = new ProbeGrid(new Vector3(-3f, 0.5f, -3f), new Vector3(1.5f, 1f, 1.5f), new Vector3Int(5, 3, 5));

            var gpuSH = g.BakeGpu(gpu, cs, cs.FindKernel(kn), sun, sky, q, 256, 1u);
            var cpuSH = g.BakeBurst(scene, sky, sun, q, 256, 1u);
            double sum = 0; float max = 0f;
            for (int i = 0; i < g.Count; i++)
                for (int k = 0; k < SH9.Count; k++)
                {
                    Vector3 d = Coeff(gpuSH[i], k) - Coeff(cpuSH[i], k);
                    sum += Mathf.Abs(d.x) + Mathf.Abs(d.y) + Mathf.Abs(d.z);
                    max = Mathf.Max(max, MaxAbs(d));
                }
            float mean = (float)(sum / (g.Count * SH9.Count * 3));
            Check(sb, ref pass, ref total, $"F: GPU BakeGpu ≡ BakeBurst (n={g.Count}, mean={mean:E2}, max={max:E2})", mean < 1e-3f);
        }

        // ───────────────────────── 공용 헬퍼(ProbeValidityTests 도 사용) ─────────────────────────

        /// <summary>계수별 선형장: c_k(p) = A_k·p + B_k (채널별 다른 기울기).</summary>
        internal sealed class LinearField
        {
            readonly Vector3[] _a = new Vector3[SH9.Count * 3];
            readonly Vector3[] _b = new Vector3[SH9.Count];

            public LinearField(System.Random rng)
            {
                for (int i = 0; i < _a.Length; i++) _a[i] = new Vector3(Lerp(rng, -1f, 1f), Lerp(rng, -1f, 1f), Lerp(rng, -1f, 1f));
                for (int i = 0; i < _b.Length; i++) _b[i] = new Vector3(Lerp(rng, -1f, 1f), Lerp(rng, -1f, 1f), Lerp(rng, -1f, 1f));
            }

            public SH9 At(Vector3 p)
            {
                var sh = new SH9();
                for (int k = 0; k < SH9.Count; k++)
                    SetCoeff(ref sh, k, new Vector3(Vector3.Dot(_a[k * 3], p), Vector3.Dot(_a[k * 3 + 1], p), Vector3.Dot(_a[k * 3 + 2], p)) + _b[k]);
                return sh;
            }
        }

        internal static SH9[] Fill(in ProbeGrid g, LinearField f)
        {
            var sh = new SH9[g.Count];
            for (int i = 0; i < sh.Length; i++) sh[i] = f.At(g.Position(i));
            return sh;
        }

        // y=높이, ±h 정사각 2삼각형. cross(v1-v0, v2-v0) = +Y(외향 = 위) — 단면 지면으로도 사용.
        internal static Tri[] Quad(float h, float y) => new[]
        {
            new Tri { V0 = new Vector3(-h, y, -h), V1 = new Vector3(-h, y, h), V2 = new Vector3(h, y, h) },
            new Tri { V0 = new Vector3(-h, y, -h), V1 = new Vector3(h, y, h),  V2 = new Vector3(h, y, -h) },
        };

        internal static Vector3 Coeff(in SH9 sh, int k)
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

        internal static void SetCoeff(ref SH9 sh, int k, Vector3 v)
        {
            switch (k)
            {
                case 0: sh.c0 = v; break;
                case 1: sh.c1 = v; break;
                case 2: sh.c2 = v; break;
                case 3: sh.c3 = v; break;
                case 4: sh.c4 = v; break;
                case 5: sh.c5 = v; break;
                case 6: sh.c6 = v; break;
                case 7: sh.c7 = v; break;
                default: sh.c8 = v; break;
            }
        }

        internal static float MaxCoeffDiff(in SH9 a, in SH9 b)
        {
            float m = 0f;
            for (int k = 0; k < SH9.Count; k++) m = Mathf.Max(m, MaxAbs(Coeff(a, k) - Coeff(b, k)));
            return m;
        }

        internal static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));

        internal static float Lerp(System.Random rng, float a, float b) => a + (b - a) * (float)rng.NextDouble();

        static float Rel(float got, float exp) => Mathf.Abs(got - exp) / Mathf.Max(1e-6f, Mathf.Abs(exp));
        static bool RelOk(float got, float exp, float rel = 0.01f) => Mathf.Abs(got - exp) <= rel * Mathf.Abs(exp) + 1e-4f;

        internal static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
