using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 모드 B(인스턴스·submesh 알베도) 검증. InstancedRadianceScene 의 두 번째 생성자
    /// (meshTriSubmesh + instanceSubmeshAlbedo) 경로는 모드 A 위주 테스트(InstancedSceneTester)
    /// 가 다루지 않으므로 여기서 따로 검증한다.
    ///   1) 정확성  : 모드 B ClosestHit 알베도 == per-tri ground truth(BruteForce+RadianceScene).
    ///                같은 메시 다중 인스턴스(BLAS 공유) + 면별 submesh 분리로 역참조 경로를 자극.
    ///   2) 경계 안전: meshTriSubmesh / instanceSubmeshAlbedo 가 null·짧음·음수 매핑이어도
    ///                예외 없이 Fallback(0.5,0.5,0.5) 으로 처리(LookupAlbedo 경계검사).
    ///
    /// 호출: Debug.Log(InstancedSceneModeBTests.RunAll());
    /// </summary>
    public static class InstancedSceneModeBTests
    {
        // InstancedRadianceScene.Fallback 와 동일해야 한다(경계 진입 확인용).
        static readonly Vector3 Fallback = new Vector3(0.5f, 0.5f, 0.5f);

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Instanced Scene Mode-B (per-instance submesh albedo) Tests ===");
            int pass = 0, total = 0;

            CorrectnessVsGroundTruth(sb, ref pass, ref total);
            BoundarySafety(sb, ref pass, ref total);

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ── 1) 정확성: 모드 B 알베도 == per-tri ground truth ──
        static void CorrectnessVsGroundTruth(StringBuilder sb, ref int pass, ref int total)
        {
            // 유니크 메시 2종(박스 12 tris). 면(2 tris)마다 submesh → 6 submesh.
            var meshes = new Tri[][]
            {
                LightmapEvaluateTests.MakeBox(Vector3.zero, 1.0f),
                LightmapEvaluateTests.MakeBox(Vector3.zero, 0.7f),
            };
            const int SubmeshCount = 6;
            var meshTriSubmesh = new int[meshes.Length][];
            for (int m = 0; m < meshes.Length; m++)
            {
                var sm = new int[meshes[m].Length];
                for (int t = 0; t < sm.Length; t++) sm[t] = t / 2;   // 면 인덱스 = submesh (0..5)
                meshTriSubmesh[m] = sm;
            }

            // 인스턴스 10개(회전+비균등 스케일), 메시 무작위 → 같은 메시 다중 인스턴싱(BLAS 공유) 유도.
            var rng = new System.Random(13579);
            const int InstN = 10;
            var insts = new TwoLevelBVH.Instance[InstN];
            var instAlb = new Vector3[InstN][];
            var worldTris = new List<Tri>();
            var worldAlb = new List<Vector3>();
            for (int i = 0; i < InstN; i++)
            {
                int mi = rng.Next(meshes.Length);
                Matrix4x4 l2w = Matrix4x4.TRS(
                    new Vector3(Rand(rng, 5f), Rand(rng, 5f), Rand(rng, 5f)),
                    Quaternion.Euler(Rand(rng, 180f), Rand(rng, 180f), Rand(rng, 180f)),
                    new Vector3(0.6f + (float)rng.NextDouble(), 0.6f + (float)rng.NextDouble(), 0.6f + (float)rng.NextDouble()));
                insts[i] = new TwoLevelBVH.Instance { MeshIndex = mi, LocalToWorld = l2w };

                // 인스턴스×submesh 고유 알베도
                var alb = new Vector3[SubmeshCount];
                for (int s = 0; s < SubmeshCount; s++) alb[s] = SubmeshAlbedo(i, s);
                instAlb[i] = alb;

                // ground truth: 월드 tri 마다 (instance, submesh) 알베도 태깅
                var local = meshes[mi];
                for (int t = 0; t < local.Length; t++)
                {
                    int sm = meshTriSubmesh[mi][t];
                    worldTris.Add(new Tri
                    {
                        V0 = l2w.MultiplyPoint3x4(local[t].V0),
                        V1 = l2w.MultiplyPoint3x4(local[t].V1),
                        V2 = l2w.MultiplyPoint3x4(local[t].V2),
                    });
                    worldAlb.Add(alb[sm]);
                }
            }

            var wt = worldTris.ToArray();
            var brute = new BruteForceOccluder(wt);
            using var bruteScene = new RadianceScene(wt, worldAlb.ToArray(), brute);          // 정답
            using var tlas = new TwoLevelBVH(meshes, insts);
            using var instScene = new InstancedRadianceScene(meshes, meshTriSubmesh, instAlb, insts, tlas); // 검증(모드 B)

            ComputeBounds(wt, out Vector3 mn, out Vector3 mx);
            Vector3 c = (mn + mx) * 0.5f;
            Vector3 ext = Vector3.Max(mx - mn, Vector3.one) * 1.5f;

            var rr = new System.Random(2468);
            const int Rays = 5000;
            int hits = 0, validMiss = 0, albMiss = 0, posMiss = 0;
            for (int k = 0; k < Rays; k++)
            {
                Vector3 o = c + new Vector3(Rand01(rr) * ext.x, Rand01(rr) * ext.y, Rand01(rr) * ext.z);
                Vector3 d = RandDir(rr);
                bool vb = bruteScene.ClosestHit(o, d, 0f, 1000f, out Vector3 pb, out _, out Vector3 ab);
                bool vi = instScene.ClosestHit(o, d, 0f, 1000f, out Vector3 pi, out _, out Vector3 ai);
                if (vb != vi) { validMiss++; continue; }
                if (!vb) continue;
                hits++;
                if ((pb - pi).magnitude > 1e-3f * Mathf.Max(1f, pb.magnitude)) posMiss++;
                if ((ab - ai).sqrMagnitude > 1e-6f) albMiss++;
            }
            Check(sb, ref pass, ref total, $"모드B Valid 일치 (miss={validMiss}/{Rays}, hits={hits})", validMiss == 0);
            Check(sb, ref pass, ref total, $"모드B 위치 일치 (miss={posMiss})", posMiss == 0);
            Check(sb, ref pass, ref total, $"모드B submesh 알베도 일치 (miss={albMiss})", albMiss == 0 && hits > 0);
        }

        // ── 2) 경계 안전: null/짧음/음수 매핑에도 예외 없이 Fallback ──
        static void BoundarySafety(StringBuilder sb, ref int pass, ref int total)
        {
            var meshes = new Tri[][] { LightmapEvaluateTests.MakeBox(Vector3.zero, 2f) }; // 12 tris, 면=submesh 0..5
            var fullSubmesh = new int[12];
            for (int t = 0; t < 12; t++) fullSubmesh[t] = t / 2;
            var insts = new[] { new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = Matrix4x4.identity } };

            // (a) 안쪽 알베도 배열이 짧음(submesh 0만 정의) → submesh≥1 면은 Fallback
            {
                var meshTriSubmesh = new int[][] { fullSubmesh };
                var instAlb = new Vector3[][] { new[] { new Vector3(0.9f, 0.1f, 0.1f) } }; // length 1
                bool ok = NoThrowFallback(meshes, meshTriSubmesh, instAlb, insts, expectSomeFallback: true, out string d);
                Check(sb, ref pass, ref total, $"(a) 짧은 submesh 알베도 → 예외X·Fallback {d}", ok);
            }
            // (b) meshTriSubmesh == null → sm=0 으로 폴백, 항상 submesh0 알베도, 예외X(Fallback 없음)
            {
                var instAlb = new Vector3[][] { new[] { new Vector3(0.2f, 0.7f, 0.3f), new Vector3(0.4f, 0.4f, 0.9f) } };
                bool ok = NoThrowFallback(meshes, null, instAlb, insts, expectSomeFallback: false, out string d);
                Check(sb, ref pass, ref total, $"(b) meshTriSubmesh=null → 예외X {d}", ok);
            }
            // (c) 안쪽 매핑/알베도 배열 null → 전부 Fallback, 예외X
            {
                var meshTriSubmesh = new int[][] { null };
                var instAlb = new Vector3[][] { null };
                bool ok = NoThrowFallback(meshes, meshTriSubmesh, instAlb, insts, expectSomeFallback: true, out string d);
                Check(sb, ref pass, ref total, $"(c) 매핑/알베도 null → 예외X·Fallback {d}", ok);
            }
            // (d) 음수 submesh 매핑 → (uint) 캐스트로 거부 → Fallback, 예외X
            {
                var neg = new int[12];
                for (int t = 0; t < 12; t++) neg[t] = -1;
                var meshTriSubmesh = new int[][] { neg };
                var instAlb = new Vector3[][] { new[] { new Vector3(0.5f, 0.6f, 0.7f) } };
                bool ok = NoThrowFallback(meshes, meshTriSubmesh, instAlb, insts, expectSomeFallback: true, out string d);
                Check(sb, ref pass, ref total, $"(d) 음수 submesh 매핑 → 예외X·Fallback {d}", ok);
            }
        }

        // 박스 내부에서 사방으로 레이를 쏴 ClosestHit 을 강제. 예외가 나면 false.
        // expectSomeFallback: 적어도 한 번 Fallback 알베도가 나와야 통과(경계 경로에 실제 진입했는지 확인).
        static bool NoThrowFallback(Tri[][] meshes, int[][] meshTriSubmesh, Vector3[][] instAlb,
                                    TwoLevelBVH.Instance[] insts, bool expectSomeFallback, out string detail)
        {
            detail = "";
            try
            {
                using var scene = new InstancedRadianceScene(meshes, meshTriSubmesh, instAlb, insts);
                var rr = new System.Random(99);
                int hits = 0, fallbacks = 0;
                for (int k = 0; k < 2000; k++)
                {
                    Vector3 o = new Vector3(Rand01(rr), Rand01(rr), Rand01(rr)) * 0.2f; // 박스(half=1) 내부
                    Vector3 dir = RandDir(rr);
                    if (scene.ClosestHit(o, dir, 0f, 100f, out _, out _, out Vector3 alb))
                    {
                        hits++;
                        if ((alb - Fallback).sqrMagnitude < 1e-10f) fallbacks++;
                    }
                }
                detail = $"(hits={hits}, fallbacks={fallbacks})";
                if (hits == 0) return false;                          // 레이가 박스를 못 맞춤 → 테스트 무효
                return expectSomeFallback ? fallbacks > 0 : true;
            }
            catch (System.Exception e)
            {
                detail = $"EXCEPTION: {e.GetType().Name}";
                return false;
            }
        }

        // ── 유틸 ──
        static Vector3 SubmeshAlbedo(int instance, int submesh)
        {
            Color c = Color.HSVToRGB(((instance * 6 + submesh) * 0.13f) % 1f, 0.7f, 0.85f).linear;
            return new Vector3(c.r, c.g, c.b);
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

        static float Rand(System.Random rng, float half) => (float)(rng.NextDouble() * 2.0 - 1.0) * half;
        static float Rand01(System.Random rng) => (float)(rng.NextDouble() - 0.5);
        static Vector3 RandDir(System.Random rng)
        {
            float z = (float)(rng.NextDouble() * 2.0 - 1.0);
            float a = (float)(rng.NextDouble() * 2.0 * System.Math.PI);
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
            return new Vector3(r * Mathf.Cos(a), r * Mathf.Sin(a), z);
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        {
            total++; if (ok) pass++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }
    }
}
