#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace HuskyLibs.CustomLightmapper.Bake.EditorTools
{
    /// <summary>
    /// C0-1 씬 수집기(Editor 어댑터) — 씬 순회 · StaticEditorFlags · LODGroup · receiveGI · <see cref="BakeSceneCollectOverride"/> 조회
    /// → <see cref="CollectCandidate"/> → <see cref="BakeSceneCollectPolicy.Run"/>. 분류 판단은 하지 않는다(결정 ③).
    ///
    /// 순서(결정 ②): roots 배열 순서 × root 별 GetComponentsInChildren(깊이 우선 계층 순서). <b>정렬하지 않는다</b> —
    ///   이 순서가 AtlasApplyDebug 인스턴스 인덱스 i(ST 할당 · per-lumel 시드 seed + li*2654435761u)가 된다.
    /// 비용(결정 ⑦): 메시 배열 복사 API 금지 — GetIndexCount · GetTopology · HasVertexAttribute · vertexCount · bounds 만
    ///   (non-readable 메시에서도 동작). LightmapAllocator.WorldArea 는 부르지 않는다(베이크가 어차피 다시 계산).
    /// </summary>
    public static class BakeSceneCollector
    {
        /// <param name="roots">null 이면 활성 씬의 루트 GameObject 전부(<see cref="SceneRoots"/>)</param>
        /// <param name="userOccluders">occluder-only 로 강제할 MeshFilter(<see cref="BakeSceneCollectOverride"/> 표식과 합집합)</param>
        /// <param name="userExcluded">제외할 MeshFilter(표식과 합집합)</param>
        /// <param name="outFilters">Entries 와 인덱스 1:1 인 실제 MeshFilter 목록(Clear 후 채움)</param>
        public static BakeSceneCollection Collect(GameObject[] roots,
                                              in BakeSceneCollectOptions opt,
                                              HashSet<MeshFilter> userOccluders = null,
                                              HashSet<MeshFilter> userExcluded = null,
                                              List<MeshFilter> outFilters = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            roots ??= SceneRoots(false);

            var lodChildren = LodChildRenderers(roots);
            var filters = outFilters ?? new List<MeshFilter>();
            filters.Clear();
            var cands = new List<CollectCandidate>();
            var seen = new HashSet<MeshFilter>();   // roots 가 서로의 조상/자손이면 같은 MeshFilter 가 두 번 — 첫 등장만

            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>(false))
                {
                    if (!seen.Add(mf)) continue;
                    cands.Add(BuildCandidate(mf, cands.Count, lodChildren, userOccluders, userExcluded));
                    filters.Add(mf);
                }
            }

            var col = BakeSceneCollectPolicy.Run(cands.ToArray(), opt);
            col.RootCount = roots.Length;
            col.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return col;
        }

        /// <summary>
        /// 수집 루트: 활성 씬의 루트(씬 파일 순서). <paramref name="allLoadedScenes"/> 면 로드된 씬 전부를 SceneManager 순서로 이어 붙인다.
        /// </summary>
        public static GameObject[] SceneRoots(bool allLoadedScenes)
        {
            if (!allLoadedScenes) return SceneManager.GetActiveScene().GetRootGameObjects();
            var list = new List<GameObject>();
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (scene.isLoaded) list.AddRange(scene.GetRootGameObjects());
            }
            return list.ToArray();
        }

        /// <summary>
        /// LOD1+ 전용 렌더러 집합(개발문서 §4.2). LOD0 에도 등록된 렌더러는 LOD0 이 이긴다(굽는 쪽 우선) —
        /// LODGroup 전체에 대해 모은 뒤 빼므로 그룹 순서와 무관. LODGroup 컴포넌트 활성 여부는 보지 않는다(LOD1+ 는 어느 쪽이든 LOD0 와 겹친 표면).
        /// </summary>
        public static HashSet<Renderer> LodChildRenderers(GameObject[] roots)
        {
            var lodN = new HashSet<Renderer>();
            var lod0 = new HashSet<Renderer>();
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var lg in root.GetComponentsInChildren<LODGroup>(false))
                {
                    var lods = lg.GetLODs();
                    for (int i = 0; i < lods.Length; i++)
                    {
                        var set = i == 0 ? lod0 : lodN;
                        foreach (var r in lods[i].renderers) if (r != null) set.Add(r);
                    }
                }
            }
            lodN.ExceptWith(lod0);
            return lodN;
        }

        static CollectCandidate BuildCandidate(MeshFilter mf, int order, HashSet<Renderer> lodChildren,
                                               HashSet<MeshFilter> userOccluders, HashSet<MeshFilter> userExcluded)
        {
            var mesh = mf.sharedMesh;
            var mr = mf.GetComponent<MeshRenderer>();
            var c = new CollectCandidate
            {
                Order = order,
                Name = mf.name,
                HasMeshFilter = true,
                HasRenderer = mr != null,
                MeshNull = mesh == null,
            };

            if (mesh != null)
            {
                c.MeshReadable = mesh.isReadable;
                c.VertexCount = mesh.vertexCount;
                c.SubMeshCount = mesh.subMeshCount;
                c.HasUv0 = mesh.HasVertexAttribute(VertexAttribute.TexCoord0);
                long indices = 0;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    if (mesh.GetTopology(s) != MeshTopology.Triangles) c.NonTriangleTopology = true;
                    indices += mesh.GetIndexCount(s);
                }
                c.TriangleCount = (int)System.Math.Min(indices / 3, int.MaxValue);
            }

            if (mr != null)
            {
                c.RendererDisabled = !mr.enabled;
                c.BoundsExtentSq = mr.bounds.extents.sqrMagnitude;
                c.ReceiveGiLightProbes = mr.receiveGI == ReceiveGI.LightProbes;
                c.IsLodChild = lodChildren.Contains(mr);
            }

            c.ContributeGI = (GameObjectUtility.GetStaticEditorFlags(mf.gameObject) & StaticEditorFlags.ContributeGI) != 0;

            var ov = OverrideOf(mf);
            c.UserOccluder = (userOccluders != null && userOccluders.Contains(mf)) || (ov != null && ov.mode == BakeSceneCollectOverride.Mode.OccluderOnly);
            c.UserExcluded = (userExcluded != null && userExcluded.Contains(mf)) || (ov != null && ov.mode == BakeSceneCollectOverride.Mode.Exclude);
            return c;
        }

        // 가장 가까운 표식: 자기 GameObject 것 → 조상 중 includeChildren 인 것(비활성 컴포넌트는 무시)
        static BakeSceneCollectOverride OverrideOf(MeshFilter mf)
        {
            var own = mf.GetComponent<BakeSceneCollectOverride>();
            if (own != null && own.enabled) return own;
            for (var t = mf.transform.parent; t != null; t = t.parent)
            {
                var ov = t.GetComponent<BakeSceneCollectOverride>();
                if (ov != null && ov.enabled && ov.includeChildren) return ov;
            }
            return null;
        }

        // ───────────────────────── C0-4 진단(정제 가능성 — 실행 아님) ─────────────────────────

        /// <summary>
        /// 메시 정제 진단: <see cref="WeldedHalfEdge"/> 와 같은 용접·퇴화제거를 돌려 줄어드는 정점/삼각형 수만 센다.
        /// 결과는 어디에도 반영하지 않는다 — 차폐 씬 Tri[] 는 원본 mesh.triangles 순서여야 α TriUV/TriSubmesh 가 맞는다(결정 ⑥).
        /// 배열 복사가 있어 비싸다 → 선택한 오브젝트에만(메뉴). readable 이 아니면 false.
        /// </summary>
        public static bool DiagnoseCleanup(Mesh mesh, out int weldedVertices, out int degenerateTriangles, out int weldedVertexCount)
        {
            weldedVertices = degenerateTriangles = weldedVertexCount = 0;
            if (mesh == null || !mesh.isReadable) return false;
            var tris = mesh.triangles;
            WeldedHalfEdge.WeldVertices(mesh.vertices, tris, WeldedHalfEdge.WeldEpsilon, out var weldV, out var weldT);
            WeldedHalfEdge.RemoveDegenerateTriangles(weldT, out var clean);
            weldedVertexCount = weldV.Length;
            weldedVertices = mesh.vertexCount - weldV.Length;
            degenerateTriangles = (tris.Length - clean.Length) / 3;
            return true;
        }
    }
}
#endif
