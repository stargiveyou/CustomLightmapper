using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// G0 Burst 경로 교차검증.
    ///   - BurstScene(POD 평탄화) + BurstTwoLevelBVH(인터페이스 없는 static 순회)
    ///     == 관리형 TwoLevelBVH (ground truth).
    ///   둘 다 동일 노드 레이아웃 + 동일 RayGeometry.RayTri 를 쓰므로 '비트 동일'이 계약.
    ///   따라서 불일치 = 'POD 평탄화/오프셋/순회' 버그 (수치 오차 아님 → Eps 사실상 0).
    ///
    /// 검증 항목
    ///   ① IntersectInstanced : Valid / T / InstanceIndex / MeshIndex / MeshTriIndex 전부 일치
    ///   ② Occluded           : bool 일치
    ///   ③ TransformNormalToWorld : 월드 노멀 일치
    ///   ④ 엣지                : 인스턴스0 씬, Create/Dispose 누수 없음
    ///
    /// 호출: Debug.Log(BurstSceneTests.RunAll());
    /// </summary>
    public static class BurstSceneTests
    {
        const float Eps = 1e-4f;   // 동일 좌표계·동일 RayTri·동일 노드 → 사실상 0

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== G0 BurstScene / BurstTwoLevelBVH ≡ TwoLevelBVH (managed) ===");
            int pass = 0, total = 0;

            EquivFuzz(sb, ref pass, ref total);
            Edges(sb, ref pass, ref total);

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ── 1) 관리형 vs Burst 퍼즈: 인스턴스 씬(회전+비균등 스케일) ──
        // BVHTests.TwoLevelFuzz 와 동일 분포 → 같은 시드로 회귀 비교 가능.
        static void EquivFuzz(StringBuilder sb, ref int pass, ref int total)
        {
            var rng = new System.Random(424242);
            var meshes = new Tri[][]
            {
                MakeRandomTris(rng, 40, 1.0f, 0.5f),   // 메시0
                MakeBoxTris(0.8f),                     // 메시1 (로컬 flat 면 → BLAS flat 리프 자극)
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

            using var tlas = new TwoLevelBVH(meshes, insts);
            using var scene = BurstScene.Create(tlas, Allocator.Persistent);

            ComputeBounds(worldTris.ToArray(), out Vector3 mn, out Vector3 mx);
            Vector3 c = (mn + mx) * 0.5f;
            Vector3 ext = Vector3.Max(mx - mn, Vector3.one) * 1.5f;
            float md0 = Mathf.Max(ext.magnitude, 1f);

            const int Rays = 5000;
            var rr = new System.Random(7);
            int validMiss = 0, tMiss = 0, idxMiss = 0, occMiss = 0, nrmMiss = 0, hits = 0;
            for (int k = 0; k < Rays; k++)
            {
                Vector3 o = c + new Vector3(Rand01(rr) * ext.x, Rand01(rr) * ext.y, Rand01(rr) * ext.z);
                Vector3 d = RandDir(rr);

                var hm = tlas.IntersectInstanced(o, d, 0f, 1000f);
                var hb = BurstTwoLevelBVH.IntersectInstanced(scene, o, d, 0f, 1000f);

                if (hm.Valid != hb.Valid) { validMiss++; }
                else if (hm.Valid)
                {
                    hits++;
                    if (!Approx(hm.T, hb.T, Eps)) tMiss++;
                    if (hm.InstanceIndex != hb.InstanceIndex ||
                        hm.MeshIndex != hb.MeshIndex ||
                        hm.MeshTriIndex != hb.MeshTriIndex) idxMiss++;

                    // ③ 동일 instance·동일 로컬 노멀 → 월드 노멀 일치
                    Vector3 localN = new Vector3(0.3f, 0.7f, -0.5f);
                    Vector3 nm = tlas.TransformNormalToWorld(hm.InstanceIndex, localN);
                    Vector3 nb = BurstTwoLevelBVH.TransformNormalToWorld(scene, hb.InstanceIndex, localN);
                    if ((nm - nb).sqrMagnitude > Eps * Eps) nrmMiss++;
                }

                // ② Occluded
                float md = (0.2f + (float)rr.NextDouble()) * md0;
                if (tlas.Occluded(o, d, md) != BurstTwoLevelBVH.Occluded(scene, o, d, md)) occMiss++;
            }

            Check(sb, ref pass, ref total, $"IntersectInstanced Valid 일치 (miss={validMiss}/{Rays}, hits={hits})", validMiss == 0);
            Check(sb, ref pass, ref total, $"IntersectInstanced T 일치 (miss={tMiss})", tMiss == 0);
            Check(sb, ref pass, ref total, $"IntersectInstanced 인덱스(inst/mesh/tri) 일치 (miss={idxMiss})", idxMiss == 0 && hits > 0);
            Check(sb, ref pass, ref total, $"Occluded 일치 (miss={occMiss}/{Rays})", occMiss == 0);
            Check(sb, ref pass, ref total, $"TransformNormalToWorld 일치 (miss={nrmMiss})", nrmMiss == 0);
            sb.AppendLine($"  [info] TLAS nodes={tlas.TlasNodeCount}, inst={tlas.InstanceCount}, BLAS={tlas.BlasCount}, worldTris={worldTris.Count}");
        }

        // ── 2) 엣지: 인스턴스0 씬 / Create·Dispose ──
        static void Edges(StringBuilder sb, ref int pass, ref int total)
        {
            using (var tEmpty = new TwoLevelBVH(new Tri[][] { MakeBoxTris(1f) }, new TwoLevelBVH.Instance[0]))
            using (var sEmpty = BurstScene.Create(tEmpty, Allocator.Persistent))
            {
                bool occ = BurstTwoLevelBVH.Occluded(sEmpty, Vector3.zero, Vector3.up, 10f);
                var hit = BurstTwoLevelBVH.IntersectInstanced(sEmpty, Vector3.zero, Vector3.up, 0f, 10f);
                Check(sb, ref pass, ref total, "인스턴스0: Occluded false & Intersect 미스", !occ && !hit.Valid);
            }

            // Create → Dispose 후 NativeArray 누수 없음(IsCreated=false). 단순 빌드/해제 왕복.
            var t = new TwoLevelBVH(new Tri[][] { MakeBoxTris(1f) },
                new[] { new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = Matrix4x4.identity } });
            var s = BurstScene.Create(t, Allocator.Persistent);
            s.Dispose();
            t.Dispose();
            Check(sb, ref pass, ref total, "Create/Dispose 왕복(누수 방지)", !s.tlasNodes.IsCreated && !s.blasTris.IsCreated);
        }

        // ── 지오메트리(BVHTests 와 동일) ──
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

        static void ComputeBounds(Tri[] tris, out Vector3 mn, out Vector3 mx)
        {
            mn = new Vector3(1e30f, 1e30f, 1e30f); mx = -mn;
            foreach (var t in tris)
            {
                mn = Vector3.Min(mn, Vector3.Min(t.V0, Vector3.Min(t.V1, t.V2)));
                mx = Vector3.Max(mx, Vector3.Max(t.V0, Vector3.Max(t.V1, t.V2)));
            }
            if (tris.Length == 0) { mn = Vector3.zero; mx = Vector3.one; }
        }

        // ── 난수/유틸 (결정적) ──
        static Vector3 RandPoint(System.Random rng, float e) => new Vector3(Rand(rng, e), Rand(rng, e), Rand(rng, e));
        static Vector3 RandOffset(System.Random rng, float s) => new Vector3(Rand(rng, s), Rand(rng, s), Rand(rng, s));
        static float Rand(System.Random rng, float half) => (float)(rng.NextDouble() * 2.0 - 1.0) * half;
        static float Rand01(System.Random rng) => (float)(rng.NextDouble() - 0.5);
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
            total++; if (ok) pass++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }
    }
}
