using System.Collections.Generic;
using System.Text;
using HuskyLibs.CustomLightmapper;   // TemplateInstanceSource (어댑터)
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// P1 등가성 교차검증: TemplateInstanceSource(Mesh+Matrix 어댑터) ≡ MeshFilter 경로(BuildGiScene 방식).
    ///
    /// 레퍼런스(ground truth) = 현행 BuildGiScene 과 동일 조립: uniqueLocal(Tri[][]) + Instance{mi, L2W} +
    /// per-mesh 알베도 → new InstancedRadianceScene(...). 어댑터는 동일 지오메트리를 Mesh 로 감싸 입력.
    /// 동일 입력 → 동일 TwoLevelBVH → 레이 결과 '비트 동일'이 계약(불일치 = 어댑터 조립/순서/대표점 버그).
    ///
    /// 검증 항목
    ///   ① 구조   : instances(개수·MeshIndex·L2W) / uniqueMeshes(Tri round-trip) / instanceTemplate
    ///   ② 대표점 : instancePoints[k] == M·mesh.bounds.center
    ///   ③ 거동   : IntersectInstanced(Valid/T/inst/mesh/tri) + Occluded ≡ 레퍼런스 BVH
    ///   ④ 알베도 : templateAlbedo=null → 0.5 회색 폴백
    ///
    /// 호출: Debug.Log(TemplateInstanceSourceTests.RunAll());
    /// </summary>
    /// 
    public static class TemplateInstanceSourceTests
    {
        const float Eps = 1e-4f;

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== P1 TemplateInstanceSource(Mesh+Matrix) ≡ MeshFilter 경로(BuildGiScene) ===");
            int pass = 0, total = 0;

            Equivalence(sb, ref pass, ref total);
            AlbedoFallback(sb, ref pass, ref total);

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        static void Equivalence(StringBuilder sb, ref int pass, ref int total)
        {
            var rng = new System.Random(424242);

            // ── 템플릿 지오메트리(Tri[][]) — BurstSceneTests 와 동일 스타일 ──
            var meshesTris = new Tri[][]
            {
                MakeRandomTris(rng, 40, 1.0f, 0.5f),
                MakeBoxTris(0.8f),
                MakeRandomTris(rng, 25, 0.6f, 0.4f),
            };
            int T = meshesTris.Length;

            // ── per-template 인스턴스 행렬(회전+비균등 스케일+이동) ──
            var mats = new Matrix4x4[T][];
            int[] perTemplate = { 8, 6, 6 };
            for (int t = 0; t < T; t++)
            {
                mats[t] = new Matrix4x4[perTemplate[t]];
                for (int i = 0; i < mats[t].Length; i++)
                    mats[t][i] = Matrix4x4.TRS(
                        RandPoint(rng, 8f),
                        Quaternion.Euler(Rand(rng, 180f), Rand(rng, 180f), Rand(rng, 180f)),
                        new Vector3(0.5f + (float)rng.NextDouble() * 1.5f,
                                    0.5f + (float)rng.NextDouble() * 1.5f,
                                    0.5f + (float)rng.NextDouble() * 1.5f));
            }

            var albedo = new Vector3[] { new(0.8f, 0.2f, 0.2f), new(0.2f, 0.8f, 0.2f), new(0.2f, 0.2f, 0.8f) };

            // ── 레퍼런스: BuildGiScene 방식으로 직접 조립 ──
            var refInsts = new List<TwoLevelBVH.Instance>();
            var worldTris = new List<Tri>();
            for (int t = 0; t < T; t++)
                for (int i = 0; i < mats[t].Length; i++)
                {
                    var l2w = mats[t][i];
                    refInsts.Add(new TwoLevelBVH.Instance { MeshIndex = t, LocalToWorld = l2w });
                    foreach (var tri in meshesTris[t])
                        worldTris.Add(new Tri
                        {
                            V0 = l2w.MultiplyPoint3x4(tri.V0),
                            V1 = l2w.MultiplyPoint3x4(tri.V1),
                            V2 = l2w.MultiplyPoint3x4(tri.V2),
                        });
                }
            var refInstArr = refInsts.ToArray();
            using var refBvh = new TwoLevelBVH(meshesTris, refInstArr);

            // ── 어댑터: 동일 지오메트리를 Mesh 로 감싸 입력 ──
            var templates = new Mesh[T];
            var centers = new Vector3[T];
            for (int t = 0; t < T; t++)
            {
                templates[t] = MeshFromTris(meshesTris[t], $"tmpl{t}");
                centers[t] = templates[t].bounds.center;
            }
            var input = new MatrixInstanceInput
            {
                templates = templates,
                templateAlbedo = albedo,
                instanceMatrices = mats,
            };
            using var built = TemplateInstanceSource.BuildScene(input);

            // ① 구조: 인스턴스 개수·MeshIndex·L2W
            bool instOk = built.instances.Length == refInstArr.Length;
            if (instOk)
                for (int k = 0; k < refInstArr.Length; k++)
                    if (built.instances[k].MeshIndex != refInstArr[k].MeshIndex ||
                        !MatApprox(built.instances[k].LocalToWorld, refInstArr[k].LocalToWorld, Eps))
                    { instOk = false; break; }
            Check(sb, ref pass, ref total, $"instances 개수·MeshIndex·L2W 일치 (n={built.instances.Length}/{refInstArr.Length})", instOk);

            // ① uniqueMeshes: Tri round-trip (MeshToLocalTris)
            bool triOk = built.uniqueMeshes.Length == T;
            if (triOk)
                for (int t = 0; t < T && triOk; t++)
                {
                    if (built.uniqueMeshes[t].Length != meshesTris[t].Length) { triOk = false; break; }
                    for (int j = 0; j < meshesTris[t].Length; j++)
                        if (!VApprox(built.uniqueMeshes[t][j].V0, meshesTris[t][j].V0) ||
                            !VApprox(built.uniqueMeshes[t][j].V1, meshesTris[t][j].V1) ||
                            !VApprox(built.uniqueMeshes[t][j].V2, meshesTris[t][j].V2))
                        { triOk = false; break; }
                }
            Check(sb, ref pass, ref total, "uniqueMeshes Tri round-trip 일치", triOk);

            // ① instanceTemplate == MeshIndex
            bool tmplOk = built.instanceTemplate.Length == built.instances.Length;
            if (tmplOk)
                for (int k = 0; k < built.instances.Length; k++)
                    if (built.instanceTemplate[k] != built.instances[k].MeshIndex) { tmplOk = false; break; }
            Check(sb, ref pass, ref total, "instanceTemplate ≡ MeshIndex", tmplOk);

            // ② 대표점: M·bounds.center
            bool ptOk = built.instancePoints.Length == refInstArr.Length;
            if (ptOk)
                for (int k = 0; k < refInstArr.Length; k++)
                {
                    int t = refInstArr[k].MeshIndex;
                    Vector3 expect = refInstArr[k].LocalToWorld.MultiplyPoint3x4(centers[t]);
                    if (!VApprox(built.instancePoints[k], expect)) { ptOk = false; break; }
                }
            Check(sb, ref pass, ref total, "instancePoints ≡ M·bounds.center", ptOk);

            // ③ 거동: 레이 퍼즈 (레퍼런스 BVH vs 어댑터 BVH)
            ComputeBounds(worldTris.ToArray(), out Vector3 mn, out Vector3 mx);
            Vector3 c = (mn + mx) * 0.5f;
            Vector3 ext = Vector3.Max(mx - mn, Vector3.one) * 1.5f;
            float md0 = Mathf.Max(ext.magnitude, 1f);

            const int Rays = 5000;
            var rr = new System.Random(7);
            int validMiss = 0, tMiss = 0, idxMiss = 0, occMiss = 0, hits = 0;
            var adBvh = built.bvh;
            for (int k = 0; k < Rays; k++)
            {
                Vector3 o = c + new Vector3(Rand01(rr) * ext.x, Rand01(rr) * ext.y, Rand01(rr) * ext.z);
                Vector3 d = RandDir(rr);

                var ha = refBvh.IntersectInstanced(o, d, 0f, 1000f);
                var hb = adBvh.IntersectInstanced(o, d, 0f, 1000f);
                if (ha.Valid != hb.Valid) validMiss++;
                else if (ha.Valid)
                {
                    hits++;
                    if (!Approx(ha.T, hb.T, Eps)) tMiss++;
                    if (ha.InstanceIndex != hb.InstanceIndex || ha.MeshIndex != hb.MeshIndex || ha.MeshTriIndex != hb.MeshTriIndex) idxMiss++;
                }
                float md = (0.2f + (float)rr.NextDouble()) * md0;
                if (refBvh.Occluded(o, d, md) != adBvh.Occluded(o, d, md)) occMiss++;
            }
            Check(sb, ref pass, ref total, $"IntersectInstanced Valid/T/idx ≡ (validMiss={validMiss}, tMiss={tMiss}, idxMiss={idxMiss}, hits={hits})", validMiss == 0 && tMiss == 0 && idxMiss == 0 && hits > 0);
            Check(sb, ref pass, ref total, $"Occluded ≡ (miss={occMiss}/{Rays})", occMiss == 0);
            sb.AppendLine($"  [info] {TemplateInstanceSource.Summary(built)}");

            foreach (var m in templates) Object.DestroyImmediate(m);
        }

        // ④ 알베도 폴백: templateAlbedo=null → 0.5 회색
        static void AlbedoFallback(StringBuilder sb, ref int pass, ref int total)
        {
            var tris = MakeBoxTris(1f);
            var mesh = MeshFromTris(tris, "fallbackBox");
            var input = new MatrixInstanceInput
            {
                templates = new[] { mesh },
                templateAlbedo = null,   // 폴백 유도
                instanceMatrices = new[] { new[] { Matrix4x4.identity } },
            };
            using (var built = TemplateInstanceSource.BuildScene(input))
            {
                bool ok = built.meshAlbedo.Length == 1 && VApprox(built.meshAlbedo[0], new Vector3(0.5f, 0.5f, 0.5f));
                Check(sb, ref pass, ref total, "templateAlbedo=null → 0.5 회색 폴백", ok);
            }
            Object.DestroyImmediate(mesh);
        }

        // ── 지오메트리/메시 ──
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

        // Tri[] → 비인덱스 Mesh(3정점/삼각형). MeshToLocalTris 가 동일 순서로 복원 가능해야 함.
        static Mesh MeshFromTris(Tri[] tris, string name)
        {
            int n = tris.Length;
            var verts = new Vector3[n * 3];
            var idx = new int[n * 3];
            for (int i = 0; i < n; i++)
            {
                verts[i * 3 + 0] = tris[i].V0; verts[i * 3 + 1] = tris[i].V1; verts[i * 3 + 2] = tris[i].V2;
                idx[i * 3 + 0] = i * 3 + 0; idx[i * 3 + 1] = i * 3 + 1; idx[i * 3 + 2] = i * 3 + 2;
            }
            var m = new Mesh { name = name };
            if (verts.Length > 65535) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.vertices = verts;
            m.triangles = idx;
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
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

        // ── 난수/유틸(결정적) ──
        static Vector3 RandPoint(System.Random rng, float e) => new(Rand(rng, e), Rand(rng, e), Rand(rng, e));
        static Vector3 RandOffset(System.Random rng, float s) => new(Rand(rng, s), Rand(rng, s), Rand(rng, s));
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
        static bool VApprox(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < Eps * Eps;
        static bool MatApprox(Matrix4x4 a, Matrix4x4 b, float eps)
        {
            for (int i = 0; i < 16; i++) if (Mathf.Abs(a[i] - b[i]) > eps) return false;
            return true;
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        {
            total++; if (ok) pass++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }
    }
}
