using UnityEngine;
using Unity.Collections;
using HuskyLibs.CustomLightmapper.Bake;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// ChartSegementer 를 Unity 에디터/플레이에서 실행·시각화하기 위한 디버그 컴포넌트.
    /// 컴포넌트를 붙이고 인스펙터 우클릭 → "Run Segmentation" 으로 실행한다.
    /// </summary>
    [ExecuteAlways]
    public class ChartSegmentationDebugger : MonoBehaviour
    {
        [Tooltip("비우면 같은 오브젝트의 MeshFilter.sharedMesh 를 사용.")]
        [SerializeField] Mesh targetMesh;

        public SegmentationSettings settings = SegmentationSettings.Default;

        [Header("Debug View")]
        [Tooltip("선택 시 씬뷰에 차트별 색으로 삼각형 외곽선을 그린다.")]
        public bool drawGizmos = true;
        [Tooltip("시임(차트 경계) half-edge 를 별도 색으로 강조.")]
        public bool drawSeams = true;
        public Color seamColor = Color.black;
        [Tooltip("결과를 메시 정점 컬러로도 굽는다(정점 컬러 셰이더 필요).")]
        public bool bakeVertexColors;

        [Header("Chart Loops (Welded 전용)")]
        [Tooltip("'Run Segmentation - Welded' 실행 시 차트 경계 루프를 폴리라인으로 그린다.")]
        public bool drawChartLoops = true;
        [Tooltip("각 차트의 외곽 루프 Loops[0] 색.")]
        public Color outerLoopColor = Color.cyan;
        [Tooltip("내부 홀 루프 Loops[1+] 색.")]
        public Color holeLoopColor = Color.magenta;
        [Tooltip("루프 라인을 차트 노멀 방향으로 띄워 면과의 z-fighting 을 줄인다.")]
        public float loopOffset = 0.002f;

        [Header("Result (read-only)")]
        [SerializeField] int chartCount;
        [SerializeField] int faceCount;

        // HalfEdge 는 실행 직후 Dispose 하므로, 기즈모용으로 결과만 캐시한다.
        int[] faceChart;
        bool[] heSeam;
        int[] cachedFaceVerts;   // 면 f 의 3 정점 인덱스 (f*3..f*3+2), half-edge 구조 기준
        Vector3[] cachedVertices;
        Color[] palette;
        ChartMesh[] cachedCharts; // 차트별 로컬 메시 + 경계 루프 (Welded 실행에서만 생성)

        Mesh ResolveMesh()
        {
            if (targetMesh != null) return targetMesh;
            var mf = GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        [ContextMenu("Run Segmentation")]
        public void Run()
        {
            var mesh = ResolveMesh();
            if (mesh == null)
            {
                Debug.LogWarning("[ChartSeg] 대상 Mesh 가 없습니다. targetMesh 를 지정하거나 MeshFilter 를 붙이세요.", this);
                return;
            }

            // HalfEdge 빌드 → 세그멘테이션 → 즉시 Dispose (NativeArray 누수 방지)
            var he = new HalfEdge(mesh);
            try
            {
                var result = ChartSegementer.GetResult(he, settings);

                faceChart = result.FaceChart;
                heSeam = result.HEseam;
                chartCount = result.ChartCount;
                faceCount = faceChart.Length;

                // 기즈모용 지오메트리 캐시 — 원본 mesh 가 아니라 half-edge 구조에서 직접 추출.
                // (용접본은 정점/면 순서가 바뀌므로 mesh.triangles 로는 매핑이 어긋난다)
                CacheGeometry(he.vertices, he.edges, he.faces);
                BuildPalette(chartCount);

                // 경계 루프는 WeldedHalfEdge 기반(ChartMeshBuilder)이라 비용접 경로에선 생성하지 않음
                cachedCharts = null;

                Debug.Log($"[ChartSeg] charts={chartCount}, faces={faceCount}", this);
                for (int i = 0; i < result.Charts.Count; i++)
                {
                    var c = result.Charts[i];
                    Debug.Log($"  chart {i}: faces={c.faces.Count}, area={c.Area:F4}, normal={c.Normal}", this);
                }

                if (bakeVertexColors)
                    ChartSegementer.DebugColorize(mesh, result);
            }
            finally
            {
                he.Dispose();
            }
        }

        [ContextMenu("Run Segmentation - Welded")]
        public void Run_Welded()
        {
                 var mesh = ResolveMesh();
            if (mesh == null)
            {
                Debug.LogWarning("[ChartSeg] 대상 Mesh 가 없습니다. targetMesh 를 지정하거나 MeshFilter 를 붙이세요.", this);
                return;
            }

            // HalfEdge 빌드 → 세그멘테이션 → 즉시 Dispose (NativeArray 누수 방지)
            var he = new WeldedHalfEdge(mesh);
            try
            {
                var result = ChartSegementer.GetResult(he, settings);

                faceChart = result.FaceChart;
                heSeam = result.HEseam;
                chartCount = result.ChartCount;
                faceCount = faceChart.Length;

                // 기즈모용 지오메트리 캐시 — 원본 mesh 가 아니라 half-edge 구조에서 직접 추출.
                // (용접본은 정점/면 순서가 바뀌므로 mesh.triangles 로는 매핑이 어긋난다)
                CacheGeometry(he.vertices, he.edges, he.faces);
                BuildPalette(chartCount);

                // 차트별 로컬 메시 + 경계 루프 추출 (he 가 살아있는 동안 수행)
                cachedCharts = ChartMeshBuilder.BuildAll(he, result);

                Debug.Log($"[ChartSeg] charts={chartCount}, faces={faceCount}", this);
                for (int i = 0; i < result.Charts.Count; i++)
                {
                    var c = result.Charts[i];
                    int loopCnt = (cachedCharts != null && i < cachedCharts.Length) ? cachedCharts[i].Loops.Count : 0;
                    Debug.Log($"  chart {i}: faces={c.faces.Count}, area={c.Area:F4}, loops={loopCnt}, normal={c.Normal}", this);
                }

                if (bakeVertexColors)
                    ChartSegementer.DebugColorize(mesh, result);
            }
            finally
            {
                he.Dispose();
            }
        }


        [ContextMenu("Clear Result")]
        public void ClearResult()
        {
            faceChart = null;
            heSeam = null;
            cachedFaceVerts = null;
            cachedVertices = null;
            palette = null;
            cachedCharts = null;
            chartCount = 0;
            faceCount = 0;
        }

        // half-edge 구조(HalfEdge/WeldedHalfEdge 공통)에서 기즈모용 지오메트리를 추출.
        // 원본 mesh.triangles 에 의존하지 않으므로 용접본에서도 정확하다.
        void CacheGeometry(NativeArray<HalfEdge_Vertex> verts, NativeArray<HalfEdge_Edge> edges, NativeArray<HalfEdge_Face> faces)
        {
            cachedVertices = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++)
                cachedVertices[i] = verts[i].position;   // float3 → Vector3 (암시적)

            int fCount = faces.Length;
            cachedFaceVerts = new int[fCount * 3];
            for (int f = 0; f < fCount; f++)
            {
                int e0 = faces[f].edgeIndex;
                int e1 = edges[e0].nextIndex;
                int e2 = edges[e1].nextIndex;
                // half-edge.vertexIndex = '도착' 정점. 면 사이클 a0->a1->a2 를 복원:
                //   e0: a0->a1, e1: a1->a2, e2: a2->a0
                cachedFaceVerts[f * 3 + 0] = edges[e2].vertexIndex; // a0 (e0 의 시작 = e2 의 도착)
                cachedFaceVerts[f * 3 + 1] = edges[e0].vertexIndex; // a1
                cachedFaceVerts[f * 3 + 2] = edges[e1].vertexIndex; // a2
            }
        }

        void BuildPalette(int count)
        {
            palette = new Color[Mathf.Max(1, count)];
            for (int i = 0; i < palette.Length; i++)
            {
                // 차트 id 기반 결정적 색 (DebugColorize 와 동일 규칙)
                Random.InitState(i * 9973 + 1);
                palette[i] = Color.HSVToRGB(Random.value, 0.6f, 0.9f);
            }
        }

        Color ChartColor(int id) =>
            (palette != null && id >= 0 && id < palette.Length) ? palette[id] : Color.gray;

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            if (!drawGizmos || faceChart == null || cachedFaceVerts == null || cachedVertices == null)
                return;

            Matrix4x4 m = transform.localToWorldMatrix;
            int faces = cachedFaceVerts.Length / 3;

            // 차트별 색으로 각 삼각형 외곽선
            for (int f = 0; f < faces && f < faceChart.Length; f++)
            {
                Gizmos.color = ChartColor(faceChart[f]);
                int i0 = cachedFaceVerts[f * 3 + 0];
                int i1 = cachedFaceVerts[f * 3 + 1];
                int i2 = cachedFaceVerts[f * 3 + 2];
                Vector3 a = m.MultiplyPoint3x4(cachedVertices[i0]);
                Vector3 b = m.MultiplyPoint3x4(cachedVertices[i1]);
                Vector3 c = m.MultiplyPoint3x4(cachedVertices[i2]);
                Gizmos.DrawLine(a, b);
                Gizmos.DrawLine(b, c);
                Gizmos.DrawLine(c, a);
            }

            // 시임(차트 경계) 강조: half-edge e 는 (prev 의 도착정점 → 자신의 도착정점)
            if (drawSeams && heSeam != null)
            {
                Gizmos.color = seamColor;
                // face f 의 3 half-edge 인덱스는 f*3, f*3+1, f*3+2 (빌드 규칙).
                // cachedFaceVerts 가 half-edge 사이클(a0->a1->a2) 그대로라 끝점이 정확히 일치:
                //   e(f*3+0): a0->a1, e(f*3+1): a1->a2, e(f*3+2): a2->a0
                for (int f = 0; f < faces; f++)
                {
                    int v0 = cachedFaceVerts[f * 3 + 0];
                    int v1 = cachedFaceVerts[f * 3 + 1];
                    int v2 = cachedFaceVerts[f * 3 + 2];
                    Vector3 p0 = m.MultiplyPoint3x4(cachedVertices[v0]);
                    Vector3 p1 = m.MultiplyPoint3x4(cachedVertices[v1]);
                    Vector3 p2 = m.MultiplyPoint3x4(cachedVertices[v2]);

                    if (SeamAt(f * 3 + 0)) Gizmos.DrawLine(p0, p1);
                    if (SeamAt(f * 3 + 1)) Gizmos.DrawLine(p1, p2);
                    if (SeamAt(f * 3 + 2)) Gizmos.DrawLine(p2, p0);
                }
            }

            // 차트 경계 루프(Welded 전용): Loops[0]=외곽, Loops[1+]=홀
            if (drawChartLoops && cachedCharts != null)
            {
                foreach (var cm in cachedCharts)
                {
                    if (cm?.Loops == null || cm.positions == null) continue;
                    Vector3 off = cm.PlaneNormal * loopOffset; // 면 위로 살짝 띄움
                    var pos = cm.positions;
                    for (int li = 0; li < cm.Loops.Count; li++)
                    {
                        Gizmos.color = (li == 0) ? outerLoopColor : holeLoopColor;
                        int[] loop = cm.Loops[li];
                        for (int k = 0; k < loop.Length; k++)
                        {
                            Vector3 a = m.MultiplyPoint3x4(pos[loop[k]] + off);
                            Vector3 b = m.MultiplyPoint3x4(pos[loop[(k + 1) % loop.Length]] + off);
                            Gizmos.DrawLine(a, b);
                        }
                    }
                }
            }
        }

        bool SeamAt(int e) => heSeam != null && e >= 0 && e < heSeam.Length && heSeam[e];
#endif
    }
}
