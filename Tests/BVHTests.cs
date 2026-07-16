using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// C1 BVH 통합 교차검증 (BVHCrossTests 흡수).
    /// 단일레벨(median/SAH · 무작위 + flat) + 2단(TLAS/BLAS) + Mesh 입력을
    /// BruteForceOccluder(ground truth)와 대조한다.
    /// 모든 구현이 RayGeometry.RayTri 를 공유하므로, 불일치 = '순회/컬링/변환' 버그.
    ///
    /// 호출:
    ///   Debug.Log(BVHTests.RunAll());
    ///   Debug.Log(BVHTests.RunMeshTest(mesh, transform.localToWorldMatrix));
    /// </summary>
    public static class BVHTests
    {
        const float Eps = 1e-4f;       // 단일레벨: 동일 좌표계·동일 RayTri → 사실상 0
        const float EpsTLAS = 1e-3f;   // 2단: 월드↔로컬 변환 FP 여유

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== C1 BVH Cross-Validation (single median/SAH + flat + two-level) ===");
            int pass = 0, total = 0;

            KnownTwoPlane(sb, ref pass, ref total);
            SingleLevelFuzz(sb, ref pass, ref total);
            TwoLevelFuzz(sb, ref pass, ref total);
            Edges(sb, ref pass, ref total);

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        /// <summary>실제 Mesh 를 월드 Tri[] 로 변환해 단일레벨(median/SAH) 교차검증. 큰 메시는 rays 조절.</summary>
        public static string RunMeshTest(Mesh mesh, Matrix4x4 l2w, int rays = 2000)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== BVH Mesh Cross-Validation ===");
            if (mesh == null) { sb.AppendLine("  mesh == null"); return sb.ToString(); }
            if (!mesh.isReadable) { sb.AppendLine($"  '{mesh.name}' Read/Write 비활성 → 정점 접근 불가(임포트 설정)."); return sb.ToString(); }

            int pass = 0, total = 0;
            CrossCheckSingle(sb, ref pass, ref total, $"{mesh.name} ({mesh.triangles.Length / 3} tris)", MeshToWorldTris(mesh, l2w), rays);
            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        /// <summary>
        /// 여러 Mesh(씬) 교차검증. 모든 MeshFilter 의 월드 삼각형을 모아 brute(정답)와,
        ///  ① 병합 단일레벨 BVH(Median/SAH)  ② 유니크 메시 BLAS + 인스턴스 2단(TwoLevelBVH)
        /// 를 대조한다. 실제 씬 분포(다양한 크기·축정렬 면·인스턴스 공유)를 그대로 검증.
        /// </summary>
        public static string RunSceneTest(MeshFilter[] filters, int rays = 3000)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== BVH Scene Cross-Validation (multi-mesh) ===");
            if (filters == null || filters.Length == 0) { sb.AppendLine("  MeshFilter 없음"); return sb.ToString(); }

            // 유니크 메시(로컬 Tri[]) + 인스턴스 + 월드 tris 동시 수집(메시 중복은 BLAS 공유)
            var meshToIdx = new Dictionary<Mesh, int>();
            var uniqueLocal = new List<Tri[]>();
            var insts = new List<TwoLevelBVH.Instance>();
            var worldTris = new List<Tri>();
            int skipped = 0;
            foreach (var mf in filters)
            {
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null || !mesh.isReadable) { skipped++; continue; }
                if (!meshToIdx.TryGetValue(mesh, out int mi))
                {
                    mi = uniqueLocal.Count;
                    meshToIdx[mesh] = mi;
                    uniqueLocal.Add(LocalTris(mesh));
                }
                Matrix4x4 l2w = mf.transform.localToWorldMatrix;
                insts.Add(new TwoLevelBVH.Instance { MeshIndex = mi, LocalToWorld = l2w });
                foreach (var t in uniqueLocal[mi])
                    worldTris.Add(new Tri
                    {
                        V0 = l2w.MultiplyPoint3x4(t.V0),
                        V1 = l2w.MultiplyPoint3x4(t.V1),
                        V2 = l2w.MultiplyPoint3x4(t.V2),
                    });
            }
            if (worldTris.Count == 0) { sb.AppendLine($"  유효 메시 없음 (skip={skipped})"); return sb.ToString(); }

            int pass = 0, total = 0;
            var wt = worldTris.ToArray();
            var bf = new BruteForceOccluder(wt);
            ComputeBounds(wt, out Vector3 mn, out Vector3 mx);

            // ① 병합 단일레벨 BVH (씬 전체를 하나의 BVH로)
            foreach (var q in new[] { BVH.BuildQuality.Median, BVH.BuildQuality.SAH })
            {
                using var bvh = new BVH(wt, Unity.Collections.Allocator.Persistent, q);
                int miss = Fuzz(bf, bvh, mn, mx, rays, Eps, 99 + (int)q, out int hits);
                Check(sb, ref pass, ref total,
                    $"병합 단일레벨 q={q} (miss={miss}/{rays}, hits={hits}, nodes={bvh.NodeCount}, depth={bvh.MaxDepth()})", miss == 0);
            }

            // ② 유니크 메시 BLAS + 인스턴스 2단
            using (var tlas = new TwoLevelBVH(uniqueLocal.ToArray(), insts.ToArray()))
            {
                int miss = Fuzz(bf, tlas, mn, mx, rays, EpsTLAS, 7, out int hits);
                Check(sb, ref pass, ref total,
                    $"2단 인스턴스 (miss={miss}/{rays}, hits={hits}, BLAS={tlas.BlasCount}, inst={tlas.InstanceCount})", miss == 0);
            }

            sb.AppendLine($"  [info] unique meshes={meshToIdx.Count}, instances={insts.Count}, worldTris={wt.Length}, skipped(R/W off 등)={skipped}");
            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ── 1) 알려진 씬: 두 평면 최근접 (Valid/T/TriIndex 하드 검사) ──
        static void KnownTwoPlane(StringBuilder sb, ref int pass, ref int total)
        {
            var lower = new Tri { V0 = new Vector3(0, 0, 0), V1 = new Vector3(1, 0, 0), V2 = new Vector3(0, 0, 1) };
            var upper = new Tri { V0 = new Vector3(0, 2, 0), V1 = new Vector3(1, 2, 0), V2 = new Vector3(0, 2, 1) };
            var tris = new[] { lower, upper };
            foreach (var q in new[] { BVH.BuildQuality.Median, BVH.BuildQuality.SAH })
            {
                using var bvh = new BVH(tris, Unity.Collections.Allocator.Persistent, q);
                var r = bvh.Intersect(new Vector3(0.25f, 3, 0.25f), Vector3.down, 0f, 100f);
                Check(sb, ref pass, ref total, $"두 평면 최근접 q={q}", r.Valid && Approx(r.T, 1f, Eps) && r.TriIndex == 1);
            }
        }

        // ── 2) 단일레벨 퍼즈: 무작위 + flat(grid 평면 / box). Median·SAH 모두 brute와 일치 ──
        static void SingleLevelFuzz(StringBuilder sb, ref int pass, ref int total)
        {
            var rng = new System.Random(20240611);
            CrossCheckSingle(sb, ref pass, ref total, "무작위 (400)", MakeRandomTris(rng, 400, 10f, 1.2f), 3000);
            CrossCheckSingle(sb, ref pass, ref total, "grid 8×8 평면 (128, flat)", MakeGrid(8, 8), 3000);   // flat-box 회귀 게이트
            CrossCheckSingle(sb, ref pass, ref total, "box (12, 축정렬 평면)", MakeBoxTris(1f), 3000);        // flat-box 회귀 게이트
        }

        // 단일레벨 BVH(Median&SAH) vs brute. miss=0 이면 PASS. SahCost 는 정보로만 출력(품질 비교).
        static void CrossCheckSingle(StringBuilder sb, ref int pass, ref int total, string name, Tri[] tris, int rays)
        {
            var bf = new BruteForceOccluder(tris);
            ComputeBounds(tris, out Vector3 mn, out Vector3 mx);
            float costMedian = 0f, costSah = 0f;
            foreach (var q in new[] { BVH.BuildQuality.Median, BVH.BuildQuality.SAH })
            {
                using var bvh = new BVH(tris, Unity.Collections.Allocator.Persistent, q);
                if (q == BVH.BuildQuality.Median) costMedian = bvh.SahCost(); else costSah = bvh.SahCost();
                int miss = Fuzz(bf, bvh, mn, mx, rays, Eps, 99 + (int)q, out int hits);
                Check(sb, ref pass, ref total,
                    $"{name} q={q} (miss={miss}/{rays}, hits={hits}, nodes={bvh.NodeCount}, depth={bvh.MaxDepth()})", miss == 0);
            }
            sb.AppendLine($"  [info] {name} SahCost: SAH={costSah:0.0} / median={costMedian:0.0} (낮을수록 우수)");
        }

        // ── 3) 2단 TLAS/BLAS: 인스턴스 씬(회전+스케일) vs brute(월드 전개) ──
        static void TwoLevelFuzz(StringBuilder sb, ref int pass, ref int total)
        {
            var rng = new System.Random(424242);
            var meshes = new Tri[][]
            {
                MakeRandomTris(rng, 40, 1.0f, 0.5f),   // 메시0
                MakeBoxTris(0.8f),                     // 메시1 (박스 = 로컬 flat 면 → BLAS flat 리프 자극)
                MakeRandomTris(rng, 25, 0.6f, 0.4f),   // 메시2
            };
            int instN = 20;
            var insts = new TwoLevelBVH.Instance[instN];
            var worldTris = new List<Tri>();
            for (int i = 0; i < instN; i++)
            {
                int mi = rng.Next(meshes.Length);
                Matrix4x4 l2w = Matrix4x4.TRS(
                    RandPoint(rng, 8f),
                    Quaternion.Euler(Rand(rng, 180f), Rand(rng, 180f), Rand(rng, 180f)),
                    new Vector3(0.5f + (float)rng.NextDouble() * 1.5f,
                                0.5f + (float)rng.NextDouble() * 1.5f,
                                0.5f + (float)rng.NextDouble() * 1.5f));
                insts[i] = new TwoLevelBVH.Instance { MeshIndex = mi, LocalToWorld = l2w };
                foreach (var t in meshes[mi])
                    worldTris.Add(new Tri
                    {
                        V0 = l2w.MultiplyPoint3x4(t.V0),
                        V1 = l2w.MultiplyPoint3x4(t.V1),
                        V2 = l2w.MultiplyPoint3x4(t.V2),
                    });
            }

            var bf = new BruteForceOccluder(worldTris.ToArray());
            using var tlas = new TwoLevelBVH(meshes, insts);
            ComputeBounds(worldTris.ToArray(), out Vector3 mn, out Vector3 mx);
            int miss = Fuzz(bf, tlas, mn, mx, 5000, EpsTLAS, 7, out int hits);
            Check(sb, ref pass, ref total, $"2단 TLAS/BLAS ≡ brute (miss={miss}/5000, hits={hits})", miss == 0);
            sb.AppendLine($"  [info] TLAS nodes={tlas.TlasNodeCount}, inst={tlas.InstanceCount}, BLAS={tlas.BlasCount}, brute tris={worldTris.Count}");
        }

        // ── 4) 엣지: 빈 / 단일 / 인스턴스0 ──
        static void Edges(StringBuilder sb, ref int pass, ref int total)
        {
            using var empty = new BVH(new Tri[0]);
            Check(sb, ref pass, ref total, "빈 BVH Intersect 미스", !empty.Intersect(Vector3.zero, Vector3.up, 0f, 10f).Valid);

            using var single = new BVH(new[] { new Tri { V0 = Vector3.zero, V1 = Vector3.right, V2 = Vector3.up } });
            Check(sb, ref pass, ref total, "단일 삼각형 빌드(노드1)", single.NodeCount == 1);

            using var tEmpty = new TwoLevelBVH(new Tri[][] { MakeBoxTris(1f) }, new TwoLevelBVH.Instance[0]);
            Check(sb, ref pass, ref total, "2단 인스턴스0 Occluded false", !tEmpty.Occluded(Vector3.zero, Vector3.up, 10f));
        }

        // ── 공용 퍼즈: truth(정답) vs test(검증) 를 rays 개 무작위 레이로 대조. 반환 = 불일치 수 ──
        static int Fuzz(IOccluder truth, IOccluder test, Vector3 mn, Vector3 mx, int rays, float eps, int seed, out int hits)
        {
            var rr = new System.Random(seed);
            Vector3 c = (mn + mx) * 0.5f;
            Vector3 ext = Vector3.Max(mx - mn, Vector3.one) * 1.5f; // 바운드 1.5배(밖에서 들어오는 레이 포함)
            float md0 = Mathf.Max(ext.magnitude, 1f);
            int miss = 0; hits = 0;
            for (int k = 0; k < rays; k++)
            {
                Vector3 o = c + new Vector3(Rand01(rr) * ext.x, Rand01(rr) * ext.y, Rand01(rr) * ext.z);
                Vector3 d = RandDir(rr);

                var hb = truth.Intersect(o, d, 0f, 1000f);
                var hv = test.Intersect(o, d, 0f, 1000f);
                if (hb.Valid != hv.Valid || (hb.Valid && !Approx(hb.T, hv.T, eps))) miss++;
                else if (hb.Valid) hits++;

                float md = (0.2f + (float)rr.NextDouble()) * md0;
                if (truth.Occluded(o, d, md) != test.Occluded(o, d, md)) miss++;
            }
            return miss;
        }

        // ── 지오메트리 ──
        static Tri[] MakeGrid(int rows, int cols)
        {
            var tris = new Tri[rows * cols * 2];
            int k = 0;
            for (int r = 0; r < rows; r++)
                for (int col = 0; col < cols; col++)
                {
                    Vector3 o = new Vector3(col, 0, r);
                    Vector3 px = o + Vector3.right, pz = o + Vector3.forward, pxz = o + Vector3.right + Vector3.forward;
                    tris[k++] = new Tri { V0 = o, V1 = px, V2 = pxz };
                    tris[k++] = new Tri { V0 = o, V1 = pxz, V2 = pz };
                }
            return tris;
        }

        static Tri[] MakeBoxTris(float half) => LightmapEvaluateTests.MakeBox(Vector3.zero, half * 2f);

        static Tri[] MakeRandomTris(System.Random rng, int n, float extent, float size)
        {
            var tris = new Tri[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 c = RandPoint(rng, extent);
                tris[i] = new Tri { V0 = c + RandOffset(rng, size), V1 = c + RandOffset(rng, size), V2 = c + RandOffset(rng, size) };
            }
            return tris;
        }

        // 변환 없는 로컬 Tri[] (BLAS 입력용).
        static Tri[] LocalTris(Mesh mesh)
        {
            var v = mesh.vertices;
            var t = mesh.triangles;
            var tris = new Tri[t.Length / 3];
            for (int i = 0; i < tris.Length; i++)
                tris[i] = new Tri { V0 = v[t[i * 3]], V1 = v[t[i * 3 + 1]], V2 = v[t[i * 3 + 2]] };
            return tris;
        }

        static Tri[] MeshToWorldTris(Mesh mesh, Matrix4x4 l2w)
        {
            var v = mesh.vertices;
            var t = mesh.triangles;
            var tris = new Tri[t.Length / 3];
            for (int i = 0; i < tris.Length; i++)
                tris[i] = new Tri
                {
                    V0 = l2w.MultiplyPoint3x4(v[t[i * 3]]),
                    V1 = l2w.MultiplyPoint3x4(v[t[i * 3 + 1]]),
                    V2 = l2w.MultiplyPoint3x4(v[t[i * 3 + 2]]),
                };
            return tris;
        }

        static void ComputeBounds(Tri[] tris, out Vector3 mn, out Vector3 mx)
        {
            mn = new Vector3(1e30f, 1e30f, 1e30f);
            mx = -mn;
            foreach (var t in tris)
            {
                mn = Vector3.Min(mn, Vector3.Min(t.V0, Vector3.Min(t.V1, t.V2)));
                mx = Vector3.Max(mx, Vector3.Max(t.V0, Vector3.Max(t.V1, t.V2)));
            }
            if (tris.Length == 0) { mn = Vector3.zero; mx = Vector3.one; }
        }

        // ── 난수/유틸 (결정적: System.Random 시드 고정) ──
        static Vector3 RandPoint(System.Random rng, float e) => new Vector3(Rand(rng, e), Rand(rng, e), Rand(rng, e));
        static Vector3 RandOffset(System.Random rng, float s) => new Vector3(Rand(rng, s), Rand(rng, s), Rand(rng, s));
        static float Rand(System.Random rng, float half) => (float)(rng.NextDouble() * 2.0 - 1.0) * half;
        static float Rand01(System.Random rng) => (float)(rng.NextDouble() - 0.5); // [-0.5, 0.5]

        static Vector3 RandDir(System.Random rng)
        {
            float z = (float)(rng.NextDouble() * 2.0 - 1.0);
            float a = (float)(rng.NextDouble() * 2.0 * System.Math.PI);
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
            return new Vector3(r * Mathf.Cos(a), r * Mathf.Sin(a), z);
        }

        static bool Approx(float a, float b, float eps) => Mathf.Abs(a - b) < eps;

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        {
            total++;
            if (ok) pass++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }
    }
}
