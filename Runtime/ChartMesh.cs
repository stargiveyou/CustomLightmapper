using System.Collections.Generic;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 차트-로컬 메시 + 경계 루프. 평탄화(Planar/LSCM/MVC)와 검증의 입력 단위.
    /// 한 차트의 face들만 모아 로컬 인덱스 공간을 만든다 → 시임 정점은 차트마다
    /// 자연히 분리(다른 차트는 자기 로컬 공간을 따로 가짐).
    /// </summary>
    public class ChartMesh
    {
        public int chartID;
        public Vector3[] positions; // 차트-로컬 정점 위치
        public int[] MeshVertex;
        public int[] Triangles;
        public List<int[]> Loops; //경계 루프 (local indices) [0]= 외곽(둘레 최대)
        public Vector3 PlaneNormal; //평균 노멀(평면 투영 기준)
        public Vector2[] UV; //평탄화 결과 (PlanarProjector / LSCM / MVC 가 채움)

        public int VertexCount => positions.Length;
        public int TriangleCount => Triangles.Length / 3;
    }

    /// <summary>HEMesh + 차트 분할 결과로부터 차트별 ChartMesh(경계 루프 포함)를 추출.</summary>
    public static class ChartMeshBuilder
    {
        public static ChartMesh[] BuildAll(WeldedHalfEdge m, ChartSegmentationResult r)
        {
            var arr = new ChartMesh[r.ChartCount];
            for (int c = 0; c < r.ChartCount; c++) arr[c] = Build(m, r, c);
            return arr;
        }

        public static ChartMesh Build(WeldedHalfEdge m, ChartSegmentationResult r, int chartId)
        {
            var chart = r.Charts[chartId];

            // 1) 차트-로컬 정점 맵 + 삼각형
            var g2l = new Dictionary<int, int>(chart.faces.Count * 2);
            var positions = new List<Vector3>();
            var meshVertex = new List<int>();

            int Local(int gv)
            {
                if (!g2l.TryGetValue(gv, out int li))
                {
                    li = positions.Count;
                    g2l[gv] = li;
                    positions.Add(m.vertices[gv].position);
                    meshVertex.Add(gv);
                }
                return li;
            }

            var tris = new List<int>(chart.faces.Count * 3);
            foreach (int f in chart.faces)
            {
                m.FaceHalfEdges(f, out int e0, out int e1, out int e2);
                tris.Add(Local(m.edges[e0].vertexIndex));
                tris.Add(Local(m.edges[e1].vertexIndex));
                tris.Add(Local(m.edges[e2].vertexIndex));
            }

            // 2) 경계 루프 추출 (전역 HE 워크 → 로컬 인덱스로 remap)
            var loops = ExtractLoops(m, r, chartId, g2l);

            // 3) 둘레 길이 내림차순 정렬 → [0] = 외곽, 나머지 = 홀
            float Per(int[] loop)
            {
                float s = 0f;
                for (int i = 0; i < loop.Length; i++)
                    s += Vector3.Distance(positions[loop[i]], positions[loop[(i + 1) % loop.Length]]);
                return s;
            }
            loops.Sort((x, y) => Per(y).CompareTo(Per(x)));

            return new ChartMesh
            {
                chartID = chartId,
                positions = positions.ToArray(),
                MeshVertex = meshVertex.ToArray(),
                Triangles = tris.ToArray(),
                Loops = loops,
                PlaneNormal = chart.Normal,
                UV = null,
            };
        }

        /// <summary>
        /// e가 차트 경계인가 : 맞은편이 없거나(경계) 다른 차트면 true
        /// </summary>
        private static bool IsChartBoundary(WeldedHalfEdge he, ChartSegmentationResult r, int e)
        {
            int t = he.edges[e].pairIndex;
            if (t < 0) return true; //경계?
            return r.FaceChart[he.edges[e].faceIndex] != r.FaceChart[he.edges[t].faceIndex];
        }
        /// <summary>
        /// 경계 he e의 Dest 정점을 중심으로 차트 내부 fan을 회전하며,
        /// 그 정점에서 시작하는 '다음 차트 경계 he'를 찾는다.
        /// </summary>
        private static int NextChartBoundary(WeldedHalfEdge m, ChartSegmentationResult r, int e)
        {
            int h = m.edges[e].nextIndex; //Dest(e)에서 시작, 같은 face(=차트)
            int guard = 0;
            while(!IsChartBoundary(m,r,h))   // h 가 경계가 될 때까지 Dest(e) 중심으로 fan 회전
            {
                int t = m.edges[h].pairIndex;
                if(t<0)return -1;            // 방어: h 가 경계가 아니면 pair 는 항상 존재
                h = m.edges[t].nextIndex; // v를 중심으로 한 칸 회전
                if(++guard>1_000_000) return -1;
            }
            return h;
        }

        // ── 경계 루프 워크 ───────────────────────────────────────
        private static List<int[]> ExtractLoops(WeldedHalfEdge he, ChartSegmentationResult r, int chartId, Dictionary<int, int> g2l)
        {
            var loops = new List<int[]>();
            var visited = new HashSet<int>();
            int maxGuard = he.edges.Length + 1;

            for (int e = 0; e < he.edges.Length; e++)
            {
                if (he.edges[e].faceIndex < 0 || r.FaceChart[he.edges[e].faceIndex] != chartId) continue;
                if (!IsChartBoundary(he, r, e) || visited.Contains(e)) continue;

                var loop = new List<int>();
                int cur = e, guard =0;
                do
                {
                    visited.Add(cur);
                    loop.Add(g2l[he.edges[cur].vertexIndex]);
                    cur = NextChartBoundary(he,r,cur);
                    if(cur <0 || ++guard> maxGuard)break;

                }while(cur != e && !visited.Contains(cur));

                if (loop.Count >= 3) loops.Add(loop.ToArray());
            }
            return loops;
        }

    }

}