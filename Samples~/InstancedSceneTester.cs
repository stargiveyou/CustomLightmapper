using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 씬 기반 2단(TwoLevelBVH + InstancedRadianceScene) 교차검증.
    /// 씬 MeshFilter 들을 유니크 메시 + 인스턴스로 모아:
    ///   - 정답(brute): BruteForceOccluder + RadianceScene(월드 Tri[], per-tri 알베도)
    ///   - 검증(2단): TwoLevelBVH + InstancedRadianceScene
    /// 무작위 레이로 ① ClosestHit(위치·노멀·알베도) ② Indirect 를 대조한다.
    ///
    /// 같은 메시를 여러 번 인스턴싱(BLAS 공유)하면 2단의 핵심 경로(변환·역전치 노멀)를 검증.
    /// 인스펙터 우클릭 → "Run Instanced Scene Test".
    /// </summary>
    [ExecuteAlways]
    public class InstancedSceneTester : MonoBehaviour
    {
        [Header("Input")]
        [Tooltip("비우면 자기 자신+자식 MeshFilter 전부 수집. 같은 메시를 여러 개 두면 인스턴싱(BLAS 공유) 검증.")]
        [SerializeField] MeshFilter[] targets;

        [Header("Test")]
        [Min(1)] public int rays = 3000;
        [Tooltip("Indirect 비교할 표면 샘플점 수(첫 N개 히트 지점).")]
        [Min(0)] public int indirectSamplePoints = 16;
        [Min(1)] public int indirectSamples = 64;
        [Min(1)] public int maxBounces = 2;
        public uint seed = 12345;

        [Header("Light (Indirect 검증용)")]
        public Vector3 lightDirection = new Vector3(-0.3f, -1f, -0.2f);
        public float lightIntensity = 1f;
        public Color skyColor = new Color(0.3f, 0.35f, 0.45f);

        [ContextMenu("Run Instanced Scene Test")]
        public void Run()
        {
            var filters = ResolveTargets();
            if (filters.Count == 0) { Debug.LogWarning("[InstScene] MeshFilter 없음.", this); return; }

            // 유니크 메시(로컬) + per-mesh 알베도 + 인스턴스 + 월드 tris + per-tri 알베도
            var meshToIdx = new Dictionary<Mesh, int>();
            var uniqueLocal = new List<Tri[]>();
            var meshAlbedo = new List<Vector3>();
            var insts = new List<TwoLevelBVH.Instance>();
            var worldTris = new List<Tri>();
            var worldAlbedo = new List<Vector3>();
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
                    meshAlbedo.Add(MeshAlbedo(mi));     // 결정적 per-mesh 색(Linear, ≤1)
                }
                Matrix4x4 l2w = mf.transform.localToWorldMatrix;
                insts.Add(new TwoLevelBVH.Instance { MeshIndex = mi, LocalToWorld = l2w });
                foreach (var t in uniqueLocal[mi])
                {
                    worldTris.Add(new Tri
                    {
                        V0 = l2w.MultiplyPoint3x4(t.V0),
                        V1 = l2w.MultiplyPoint3x4(t.V1),
                        V2 = l2w.MultiplyPoint3x4(t.V2),
                    });
                    worldAlbedo.Add(meshAlbedo[mi]);
                }
            }
            if (worldTris.Count == 0) { Debug.LogWarning($"[InstScene] 유효 메시 없음(skip={skipped}).", this); return; }

            var wt = worldTris.ToArray();
            var um = uniqueLocal.ToArray();
            var inst = insts.ToArray();

            var brute = new BruteForceOccluder(wt);
            using var bruteScene = new RadianceScene(wt, worldAlbedo.ToArray(), brute);     // 정답
            using var tlas = new TwoLevelBVH(um, inst);
            using var instScene = new InstancedRadianceScene(um, meshAlbedo.ToArray(), inst, tlas); // 검증

            ComputeBounds(wt, out Vector3 mn, out Vector3 mx);
            Vector3 c = (mn + mx) * 0.5f;
            Vector3 ext = Vector3.Max(mx - mn, Vector3.one) * 1.5f;

            var sun = new DirectionalLight
            {
                Direction = lightDirection.sqrMagnitude > 1e-8f ? lightDirection.normalized : Vector3.down,
                Color = Vector3.one,
                Intensity = lightIntensity,
            };
            var sky = new UniformSky(new Vector3(skyColor.r, skyColor.g, skyColor.b));
            var q = new BakeQualitySettings { IndirectSamples = indirectSamples, MaxBounces = maxBounces, RRStartDepth = 3, RayBias = 1e-4f, AoSamples = 1 };

            var sb = new StringBuilder();
            sb.AppendLine("=== Instanced Scene Cross-Validation (TwoLevelBVH + InstancedRadianceScene) ===");
            int pass = 0, total = 0;

            // ── ① ClosestHit: 위치·노멀·알베도 == brute ──
            var rng = new System.Random((int)seed);
            int posMiss = 0, nrmMiss = 0, albMiss = 0, validMiss = 0, hits = 0;
            var hitPts = new List<(Vector3 p, Vector3 n)>();
            for (int k = 0; k < rays; k++)
            {
                Vector3 o = c + new Vector3(Rand(rng) * ext.x, Rand(rng) * ext.y, Rand(rng) * ext.z);
                Vector3 d = RandDir(rng);

                bool vb = bruteScene.ClosestHit(o, d, 0f, 1000f, out Vector3 pb, out Vector3 nb, out Vector3 ab);
                bool vi = instScene.ClosestHit(o, d, 0f, 1000f, out Vector3 pi, out Vector3 ni, out Vector3 ai);

                if (vb != vi) { validMiss++; continue; }
                if (!vb) continue;
                hits++;

                if ((pb - pi).magnitude > 1e-3f * Mathf.Max(1f, pb.magnitude)) posMiss++;
                if (Vector3.Dot(nb.normalized, ni.normalized) < 0.999f) nrmMiss++;
                if ((ab - ai).sqrMagnitude > 1e-6f) albMiss++;

                if (hitPts.Count < indirectSamplePoints) hitPts.Add((pb, nb));
            }
            Check(sb, ref pass, ref total, $"ClosestHit Valid 일치 (miss={validMiss}/{rays}, hits={hits})", validMiss == 0);
            Check(sb, ref pass, ref total, $"ClosestHit 위치 일치 (miss={posMiss})", posMiss == 0);
            Check(sb, ref pass, ref total, $"ClosestHit 노멀 일치 (miss={nrmMiss})", nrmMiss == 0);
            Check(sb, ref pass, ref total, $"ClosestHit 알베도 일치 (miss={albMiss})", albMiss == 0);

            // ── ② Indirect: InstancedScene ≡ bruteScene (같은 seed → ClosestHit 일치면 경로 동일) ──
            if (hitPts.Count > 0)
            {
                double maxRel = 0; int idxWorst = -1;
                for (int i = 0; i < hitPts.Count; i++)
                {
                    var (p, n) = hitPts[i];
                    uint s = seed + (uint)i * 2654435761u;
                    Vector3 ib = RadianceCore.EvaluateIndirect(bruteScene, p, n, sun, sky, q, s);
                    Vector3 ii = RadianceCore.EvaluateIndirect(instScene, p, n, sun, sky, q, s);
                    float m = Mathf.Max(Mathf.Max(ib.x, ib.y), Mathf.Max(ib.z, Mathf.Max(Mathf.Max(ii.x, ii.y), ii.z)));
                    float d = Mathf.Max(Mathf.Abs(ib.x - ii.x), Mathf.Max(Mathf.Abs(ib.y - ii.y), Mathf.Abs(ib.z - ii.z)));
                    float rel = m < 1e-6f ? 0f : d / m;
                    if (rel > maxRel) { maxRel = rel; idxWorst = i; }
                }
                Check(sb, ref pass, ref total, $"Indirect inst≡brute ({hitPts.Count}점, maxRel={maxRel:P2}, worst#{idxWorst})", maxRel < 0.02f);
            }

            sb.AppendLine($"  [info] unique meshes={meshToIdx.Count}, instances={insts.Count}, worldTris={wt.Length}, BLAS={tlas.BlasCount}, skipped={skipped}");
            sb.AppendLine($"--- {pass}/{total} PASS ---");
            if (sb.ToString().Contains("FAIL")) Debug.LogWarning(sb.ToString(), this);
            else Debug.Log(sb.ToString(), this);
        }

        // ── 헬퍼 ──
        List<MeshFilter> ResolveTargets()
        {
            var list = new List<MeshFilter>();
            if (targets != null && targets.Length > 0)
            { foreach (var mf in targets) if (mf != null && mf.sharedMesh != null) list.Add(mf); }
            else
            { foreach (var mf in GetComponentsInChildren<MeshFilter>()) if (mf.sharedMesh != null) list.Add(mf); }
            return list;
        }

        static Tri[] LocalTris(Mesh mesh)
        {
            var v = mesh.vertices; var t = mesh.triangles;
            var tris = new Tri[t.Length / 3];
            for (int i = 0; i < tris.Length; i++)
                tris[i] = new Tri { V0 = v[t[i * 3]], V1 = v[t[i * 3 + 1]], V2 = v[t[i * 3 + 2]] };
            return tris;
        }

        static Vector3 MeshAlbedo(int mi)
        {
            Color c = Color.HSVToRGB((mi * 0.6180339887f) % 1f, 0.6f, 0.85f).linear; // Linear, ≤1 → 에너지 보존
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

        static float Rand(System.Random rng) => (float)(rng.NextDouble() - 0.5);
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