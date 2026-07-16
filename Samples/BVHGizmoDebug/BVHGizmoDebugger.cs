using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// C1 단일레벨 BVH 노드 AABB 기즈모 시각화 (1단계).
    ///
    /// 사용법
    ///  1. 씬 오브젝트에 부착, targets 지정(비우면 자식 MeshFilter 수집).
    ///  2. ContextMenu "Build BVH" → 월드 Tri[] 스냅샷으로 BVH 빌드(트랜스폼 이동 시 재빌드 필요).
    ///  3. 씬 뷰에서 노드 AABB 와이어박스 확인. minDepth/maxDepth 로 층 필터,
    ///     leafOnly 로 리프만, 깊이별 색 그라데이션(shallow→deep).
    ///  4. quality(Median/SAH) 바꿔 재빌드 → 트리 모양·SahCost 시각 비교.
    ///
    /// 레이 순회 시각화 (2단계)
    ///  1. ContextMenu "Create Ray Handles" → 자식 RayOrigin/RayTarget 생성(수동 지정도 가능).
    ///  2. drawRay 켜고 핸들을 씬에서 이동 → 매 기즈모 프레임 순회 재실행(라이브).
    ///  3. RayAABB 를 통과한 '방문 노드'만 강조, 컬링된 노드는 회색(옵션), 히트 지점 구체
    ///     + 히트 삼각형 강조. 라벨에 visited/culled/triTests/T 통계.
    ///
    /// 설계
    ///  - BVH 코어 무수정: NodesRO/TriIdxRO/TrisRO 읽기전용 접근 + internal RayAABB 재사용
    ///    (동일 asmdef). 순회는 Intersect 를 복제하되 '방문 기록'만 추가 — push 순서·best.T
    ///    가지치기까지 동일해야 실제 컬링 거동을 그대로 보여준다.
    ///  - Allocator.Persistent → OnDisable 에서 반드시 Dispose (ExecuteAlways 로
    ///    에디터 도메인 리로드 전에도 해제 보장, NativeArray 누수 방지).
    ///  - 순회는 BVH 와 동일하게 명시적 스택(재귀 X).
    /// </summary>
    [ExecuteAlways]
    public sealed class BVHGizmoDebugger : MonoBehaviour
    {
        [Header("입력")]
        [Tooltip("비우면 자식(비활성 포함)에서 MeshFilter 수집")]
        public MeshFilter[] targets;
        public BVH.BuildQuality quality = BVH.BuildQuality.SAH;

        [Header("표시")]
        public bool drawNodes = true;
        [Min(0)] public int minDepth = 0;
        [Min(0)] public int maxDepth = 8;
        [Tooltip("리프 노드만 표시 (depth 필터와 AND)")]
        public bool leafOnly = false;
        public Color shallowColor = new Color(0.2f, 1f, 0.4f, 1f);
        public Color deepColor = new Color(1f, 0.25f, 0.15f, 1f);

        [Header("레이 순회 (2단계)")]
        public bool drawRay = false;
        [Tooltip("레이 시작점. Create Ray Handles 로 자동 생성 가능")]
        public Transform rayOrigin;
        [Tooltip("레이 방향 지정점(origin→target 방향, 정규화)")]
        public Transform rayTarget;
        [Tooltip("레이 최대 거리. 0 = origin→target 거리 사용")]
        [Min(0)] public float rayMaxDist = 0f;
        [Tooltip("RayAABB 에 걸렸지만 통과 못한(컬링된) 노드도 회색으로 표시")]
        public bool drawCulledNodes = false;
        public Color rayColor = Color.white;
        public Color visitedColor = new Color(0.2f, 0.8f, 1f, 1f);
        public Color culledColor = new Color(0.5f, 0.5f, 0.5f, 0.35f);
        public Color hitColor = Color.magenta;

        [Header("통계 (Build 시 갱신)")]
        [SerializeField] string stats = "(미빌드)";

        BVH _bvh;
        int _builtDepth;

        // 레이 순회 기록 버퍼 (기즈모 프레임마다 재사용, GC 방지)
        readonly List<int> _visited = new List<int>();
        readonly List<int> _culled = new List<int>();

        [ContextMenu("Build BVH")]
        public void Build()
        {
            ReleaseBvh();

            var filters = (targets != null && targets.Length > 0)
                ? targets
                : GetComponentsInChildren<MeshFilter>(true);

            var tris = new List<Tri>();
            int skipped = 0;
            foreach (var mf in filters)
            {
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null || !mesh.isReadable) { skipped++; continue; }
                AppendWorldTris(tris, mesh, mf.transform.localToWorldMatrix);
            }

            _bvh = new BVH(tris.ToArray(), Allocator.Persistent, quality);
            _builtDepth = _bvh.MaxDepth();
            stats = $"{quality} | nodes={_bvh.NodeCount} tris={_bvh.TriCount} depth={_builtDepth} sahCost={_bvh.SahCost():F3}"
                    + (skipped > 0 ? $" (skip={skipped}: null/Read-Write 꺼짐)" : "");
            Debug.Log($"[BVHGizmoDebugger] {stats}", this);
        }

        [ContextMenu("Clear")]
        public void Clear()
        {
            ReleaseBvh();
            stats = "(미빌드)";
        }

        [ContextMenu("Create Ray Handles")]
        public void CreateRayHandles()
        {
            // 루트 AABB 기준: origin 은 바깥, target 은 중심 → 바로 뭔가에 맞는 레이
            Vector3 c = _bvh != null && _bvh.IsCreated && _bvh.NodeCount > 0
                ? (_bvh.RootMin + _bvh.RootMax) * 0.5f
                : transform.position;
            Vector3 ext = _bvh != null && _bvh.IsCreated && _bvh.NodeCount > 0
                ? (_bvh.RootMax - _bvh.RootMin)
                : Vector3.one;

            if (rayOrigin == null)
            {
                var go = new GameObject("RayOrigin");
                go.transform.SetParent(transform, false);
                rayOrigin = go.transform;
                rayOrigin.position = c + new Vector3(ext.x, ext.y * 0.5f, 0f);
            }
            if (rayTarget == null)
            {
                var go = new GameObject("RayTarget");
                go.transform.SetParent(transform, false);
                rayTarget = go.transform;
                rayTarget.position = c;
            }
            drawRay = true;
        }

        void OnDisable() => ReleaseBvh();

        void ReleaseBvh()
        {
            if (_bvh != null) { _bvh.Dispose(); _bvh = null; }
            _builtDepth = 0;
        }

        static void AppendWorldTris(List<Tri> dst, Mesh mesh, Matrix4x4 l2w)
        {
            var v = mesh.vertices;
            var t = mesh.triangles;
            for (int i = 0; i < t.Length; i += 3)
            {
                dst.Add(new Tri
                {
                    V0 = l2w.MultiplyPoint3x4(v[t[i]]),
                    V1 = l2w.MultiplyPoint3x4(v[t[i + 1]]),
                    V2 = l2w.MultiplyPoint3x4(v[t[i + 2]])
                });
            }
        }

        void OnDrawGizmos()
        {
            if (_bvh == null || !_bvh.IsCreated || _bvh.NodeCount == 0)
                return;

            if (drawNodes) DrawNodeBoxes();
            if (drawRay) DrawRayTraversal();

#if UNITY_EDITOR
            UnityEditor.Handles.Label(_bvh.RootMax, stats);
#endif
        }

        void DrawNodeBoxes()
        {
            var nodes = _bvh.NodesRO;
            float denom = Mathf.Max(1, _builtDepth - 1); // 깊이→색 정규화

            var stack = new Stack<(int ni, int depth)>();
            stack.Push((0, 0));
            while (stack.Count > 0)
            {
                var (ni, depth) = stack.Pop();
                BVH.Node n = nodes[ni];
                bool leaf = n.Count > 0;

                // maxDepth 초과 층은 그리지도 내려가지도 않음(대형 트리 방어)
                if (!leaf && depth < maxDepth)
                {
                    stack.Push((n.LeftFirst, depth + 1));
                    stack.Push((n.LeftFirst + 1, depth + 1));
                }

                if (depth < minDepth || depth > maxDepth) continue;
                if (leafOnly && !leaf) continue;

                Gizmos.color = Color.Lerp(shallowColor, deepColor, depth / denom);
                Gizmos.DrawWireCube((n.Min + n.Max) * 0.5f, n.Max - n.Min);
            }
        }

        // ── 2단계: 레이 순회 시각화 ─────────────────────────────────
        // BVH.Intersect 복제 + 방문/컬링 기록. push 순서·best.T 가지치기를 코어와
        // 동일하게 유지해야 '실제로 걸러지는 가지'가 그대로 보인다.
        void DrawRayTraversal()
        {
            if (rayOrigin == null || rayTarget == null) return;

            Vector3 o = rayOrigin.position;
            Vector3 to = rayTarget.position - o;
            float dist = to.magnitude;
            if (dist < 1e-6f) return; // origin == target

            Vector3 d = to / dist;
            float tmax = rayMaxDist > 0f ? rayMaxDist : dist;
            Vector3 invD = new Vector3(1f / d.x, 1f / d.y, 1f / d.z);

            var nodes = _bvh.NodesRO;
            var triIdx = _bvh.TriIdxRO;
            var tris = _bvh.TrisRO;

            _visited.Clear();
            _culled.Clear();
            int triTests = 0;
            Hit best = new Hit { Valid = false, T = tmax };

            Span<int> stack = stackalloc int[64];
            int sp = 0; stack[sp++] = 0;
            while (sp > 0)
            {
                int ni = stack[--sp];
                BVH.Node node = nodes[ni];
                if (!BVH.RayAABB(o, invD, node.Min, node.Max, 0f, best.T))
                {
                    _culled.Add(ni);
                    continue;
                }
                _visited.Add(ni);
                if (node.Count > 0)
                {
                    int end = node.LeftFirst + node.Count;
                    for (int s = node.LeftFirst; s < end; s++)
                    {
                        triTests++;
                        int orig = triIdx[s];
                        if (RayGeometry.RayTri(o, d, tris[orig], 0f, best.T, out float h))
                        {
                            best.Valid = true;
                            best.T = h;
                            best.TriIndex = orig;
                        }
                    }
                }
                else
                {
                    stack[sp++] = node.LeftFirst;
                    stack[sp++] = node.LeftFirst + 1;
                }
            }

            // 컬링된 노드 (옵션, 흐리게)
            if (drawCulledNodes)
            {
                Gizmos.color = culledColor;
                foreach (int ni in _culled)
                {
                    BVH.Node n = nodes[ni];
                    Gizmos.DrawWireCube((n.Min + n.Max) * 0.5f, n.Max - n.Min);
                }
            }

            // 방문 노드 강조
            Gizmos.color = visitedColor;
            foreach (int ni in _visited)
            {
                BVH.Node n = nodes[ni];
                Gizmos.DrawWireCube((n.Min + n.Max) * 0.5f, n.Max - n.Min);
            }

            // 레이: 히트까지 본색, 나머지는 흐리게
            float handleSize = tmax * 0.01f;
            Vector3 rayEnd = o + d * tmax;
            if (best.Valid)
            {
                Vector3 hp = o + d * best.T;
                Gizmos.color = rayColor;
                Gizmos.DrawLine(o, hp);
                Gizmos.color = new Color(rayColor.r, rayColor.g, rayColor.b, 0.25f);
                Gizmos.DrawLine(hp, rayEnd);

                // 히트 지점 + 히트 삼각형
                Gizmos.color = hitColor;
                Gizmos.DrawSphere(hp, handleSize);
                Tri ht = tris[best.TriIndex];
                Gizmos.DrawLine(ht.V0, ht.V1);
                Gizmos.DrawLine(ht.V1, ht.V2);
                Gizmos.DrawLine(ht.V2, ht.V0);
            }
            else
            {
                Gizmos.color = rayColor;
                Gizmos.DrawLine(o, rayEnd);
            }
            Gizmos.color = rayColor;
            Gizmos.DrawSphere(o, handleSize * 0.7f);

#if UNITY_EDITOR
            string hitStr = best.Valid ? $"HIT t={best.T:F3} tri={best.TriIndex}" : "MISS";
            UnityEditor.Handles.Label(o,
                $"{hitStr} | visited={_visited.Count} culled={_culled.Count} " +
                $"({_visited.Count + _culled.Count}/{_bvh.NodeCount} nodes) triTests={triTests}/{_bvh.TriCount}");
#endif
        }
    }
}
