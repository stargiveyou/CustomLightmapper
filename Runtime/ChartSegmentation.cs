using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{

    /// <summary>Unity.Mathematics float3 용 보조 함수 (Vector3 의 normalized / sqrMagnitude 대응).</summary>
    public static class Float3Extensions
    {
        /// <summary>float3 정규화. 길이가 0에 가까우면 0 벡터를 반환(NaN 방지).</summary>
        public static float3 Normalized(this float3 v) => math.normalizesafe(v);

        /// <summary>길이 제곱 (Vector3.sqrMagnitude 대응).</summary>
        public static float SqrMagnitude(this float3 v) => math.lengthsq(v);
    }


    /*
    동작 원리 (Step-by-Step)
    1. 시임(Seam) 사전 정의: s.SeamAngleDeg(기본 40도)를 넘는 꺾임을 가진 모서리는 heSeam 배열에서 true로 마킹됩니다. 큐브의 직각 모서리(90도)는 여기서 즉각 시임으로 잘려나갑니다.

    2. Best-First 탐색: 0번 면(Face)부터 시작하여 차트를 만듭니다. 주변 면들을 탐색할 때, 기존 차트의 평균 노멀(chartN)과 방향이 가장 비슷한 면(Cost가 낮은 면)부터 Min-Heap에서 꺼내어 병합합니다.

    3. 가중치 기반 노멀 갱신: 면적이 큰 면이 차트의 전체 뱡향성에 더 큰 영향을 주도록 nSum += m.FaceNormals[f] * m.FaceAreas[f]; 연산을 수행하여 노이즈(작고 찌그러진 면)에 의한 방향 왜곡을 방어합니다.

    4. 한계 각도(MaxChartAngle) 도달: 면들이 계속 합쳐지면서 전체 차트가 둥글게 휘어지다가, 최초 차트 평균 노멀과 새 면의 각도 차이가 MaxChartAngleDeg(60도)를 넘게 되면 그 면은 편입이 거부(보류)됩니다.

    결과: 보류된 면들은 루프가 돌아가면서 새로운 시드(Seed)로 잡히고, 자신만의 새로운 차트를 형성합니다.
    */

    /// <summary>
    ///  차트 분할 파라미터
    /// </summary>
    public struct SegmentationSettings
    {
        /// <summary>이면각이 이 값을 넘으면 시임(차트 경계)으로 절단. 건물은 30~45°가 무난.</summary>
        public float SeamAngleDeg;
        /// <summary>차트 평균 노멀과 후보 face 노멀의 허용 편차. 차트가 평면적으로 유지되는 정도.</summary>
        public float MaxChartAngleDeg;
        public static SegmentationSettings Default => new SegmentationSettings()
        {
            SeamAngleDeg = 40,
            MaxChartAngleDeg = 60
        };
    }

    public sealed class Chart
    {
        public readonly List<int> faces = new List<int>();  // 반드시 초기화(미초기화 시 Add 에서 NRE)
        public Vector3 Normal; // 면적 가중 평균 노멀
        public float Area;      //메시 - 로컬 면적 합 ( 밀도 정규화에 사용)
    }

    public sealed class ChartSegmentationResult
    {
        public int[] FaceChart;        // face -> chart id
        public List<Chart> Charts;
        public bool[] HEseam;          // half-edge -> 시임 여부 (이후 시임 테이블/스티칭에서 재사용)
        public int ChartCount => Charts.Count;
    }
    /// <summary>
    /// 차트 분할기
    /// 듀얼 그래프(face = 노드, 비-시임 공유 에지 = 간선) 위의 영역 분할로, 노멀 정렬도가 높은 face 부터 best-first(min-heap) Greedy 방식
    /// </summary>
    public static class ChartSegementer
    {

        const float Epsilon = 1e-12f;



        private static void AssignFace(int f, int chartID, HalfEdge he, int[] faceChart, Chart chart, ref float3 nSum)
        {
            faceChart[f] = chartID;
            chart.faces.Add(f);
            chart.Area += he.faces[f].area;
            nSum += he.faces[f].normal * he.faces[f].area; // 면적 가중 -> 작은 삼각형 노이즈 억제
        }


        /// <summary>face f의 세 에지를 보고, 시임이 아니고 미할당인 이웃 face를 heap에 push.</summary>
        private static void PushNeighbors(int f, HalfEdge he, bool[] heSeam, int[] faceChart, MinHeap heap, Vector3 chartN)
        {
            he.FaceHalfEdges(f, out int e0, out int e1, out int e2);
            TryPush(e0, he, heSeam, faceChart, heap, chartN);
            TryPush(e1, he, heSeam, faceChart, heap, chartN);
            TryPush(e2, he, heSeam, faceChart, heap, chartN);
        }
 
        private static void TryPush(int e, HalfEdge  he, bool[] heSeam, int[] faceChart, MinHeap heap, Vector3 chartN)
        {
            if (heSeam[e]) return;
            int t = he.edges[e].pairIndex;
            if (t < 0) return;
            int nf = he.edges[t].faceIndex;
            if (faceChart[nf] >= 0) return;
            // cost = (1 - 정렬도) → 정렬도 높은(평면적) 이웃이 먼저 pop
            float cost = 1f - Vector3.Dot(he.faces[nf].normal, chartN);
            heap.Push(cost, nf);
        }
        public static ChartSegmentationResult GetResult(HalfEdge he, SegmentationSettings setting)
        {
            int F = he.faces.Length;
            var faceChart = new int[F];

            System.Array.Fill(faceChart, -1);

            // 1) 시임 half-edge 사전 계산: 경계이거나 이면각 초과
            float seamCos = Mathf.Cos(setting.SeamAngleDeg * Mathf.Deg2Rad);
            var heSeam = new bool[he.edges.Length];
            for (int e = 0; e < heSeam.Length; e++)
            {
                int t = he.edges[e].pairIndex;
                if (t < 0) { heSeam[e] = true; continue; }
                float d = Vector3.Dot(he.faces[he.edges[e].faceIndex].normal, he.faces[he.edges[t].faceIndex].normal);
                heSeam[e] = d < seamCos; // 각도 > 임계값
            }

            float chartCos = Mathf.Cos(setting.MaxChartAngleDeg * Mathf.Deg2Rad);
            var charts = new List<Chart>();
            var heap = new MinHeap();

            // 2) 미할당 face를 시드로 차트 성장 (face 순서 -> 결정적)

            for (int seed = 0; seed < F; ++seed)
            {
                if (faceChart[seed] >= 0) continue;
                int chartID = charts.Count;
                var chart = new Chart();
                charts.Add(chart);          // chartID == 인덱스가 되도록 즉시 등록
                float3 nSum = float3.zero; // 면적 가중 노멀 합

                //
                AssignFace(seed, chartID, he, faceChart, chart, ref nSum);
                //
                heap.Clear();
                PushNeighbors(seed, he, heSeam, faceChart, heap, nSum.Normalized());



                while (heap.Pop(out _, out int f))
                {
                    if (faceChart[f] >= 0) continue; // 다른 경로로 이미 편입됨.
                    Vector3 chartN = nSum.Normalized();
                    // 차트 평균 노멀과 너무 벌어지면 이번엔 보류(추후 자기 차트의 시드가 됨)
                    if (Vector3.Dot(he.faces[f].normal, chartN) < chartCos) continue;

                    AssignFace(f, chartID, he, faceChart, chart, ref nSum);
                    PushNeighbors(f, he, heSeam, faceChart, heap, nSum.Normalized());
                }

                chart.Normal = nSum.SqrMagnitude() > Epsilon ? nSum.Normalized() : he.faces[seed].normal;


            }

            return new ChartSegmentationResult
            {
                FaceChart = faceChart,
                Charts = charts,
                HEseam = heSeam
            };

        }



           private static void AssignFace(int f, int chartID, WeldedHalfEdge he, int[] faceChart, Chart chart, ref float3 nSum)
        {
            faceChart[f] = chartID;
            chart.faces.Add(f);
            chart.Area += he.faces[f].area;
            nSum += he.faces[f].normal * he.faces[f].area; // 면적 가중 -> 작은 삼각형 노이즈 억제
        }


        /// <summary>face f의 세 에지를 보고, 시임이 아니고 미할당인 이웃 face를 heap에 push.</summary>
        private static void PushNeighbors(int f, WeldedHalfEdge he, bool[] heSeam, int[] faceChart, MinHeap heap, Vector3 chartN)
        {
            he.FaceHalfEdges(f, out int e0, out int e1, out int e2);
            TryPush(e0, he, heSeam, faceChart, heap, chartN);
            TryPush(e1, he, heSeam, faceChart, heap, chartN);
            TryPush(e2, he, heSeam, faceChart, heap, chartN);
        }

          private static void TryPush(int e, WeldedHalfEdge  he, bool[] heSeam, int[] faceChart, MinHeap heap, Vector3 chartN)
        {
            if (heSeam[e]) return;
            int t = he.edges[e].pairIndex;
            if (t < 0) return;
            int nf = he.edges[t].faceIndex;
            if (faceChart[nf] >= 0) return;
            // cost = (1 - 정렬도) → 정렬도 높은(평면적) 이웃이 먼저 pop
            float cost = 1f - Vector3.Dot(he.faces[nf].normal, chartN);
            heap.Push(cost, nf);
        }
        public static ChartSegmentationResult GetResult(WeldedHalfEdge he, SegmentationSettings setting)
        {
            int F = he.faces.Length;
            var faceChart = new int[F];

            System.Array.Fill(faceChart, -1);

            // 1) 시임 half-edge 사전 계산: 경계이거나 이면각 초과
            float seamCos = Mathf.Cos(setting.SeamAngleDeg * Mathf.Deg2Rad);
            var heSeam = new bool[he.edges.Length];
            for (int e = 0; e < heSeam.Length; e++)
            {
                int t = he.edges[e].pairIndex;
                if (t < 0) { heSeam[e] = true; continue; }
                float d = Vector3.Dot(he.faces[he.edges[e].faceIndex].normal, he.faces[he.edges[t].faceIndex].normal);
                heSeam[e] = d < seamCos; // 각도 > 임계값
            }

            float chartCos = Mathf.Cos(setting.MaxChartAngleDeg * Mathf.Deg2Rad);
            var charts = new List<Chart>();
            var heap = new MinHeap();

            // 2) 미할당 face를 시드로 차트 성장 (face 순서 -> 결정적)

            for (int seed = 0; seed < F; ++seed)
            {
                if (faceChart[seed] >= 0) continue;
                int chartID = charts.Count;
                var chart = new Chart();
                charts.Add(chart);          // chartID == 인덱스가 되도록 즉시 등록
                float3 nSum = float3.zero; // 면적 가중 노멀 합

                //
                AssignFace(seed, chartID, he, faceChart, chart, ref nSum);
                //
                heap.Clear();
                PushNeighbors(seed, he, heSeam, faceChart, heap, nSum.Normalized());



                while (heap.Pop(out _, out int f))
                {
                    if (faceChart[f] >= 0) continue; // 다른 경로로 이미 편입됨.
                    Vector3 chartN = nSum.Normalized();
                    // 차트 평균 노멀과 너무 벌어지면 이번엔 보류(추후 자기 차트의 시드가 됨)
                    if (Vector3.Dot(he.faces[f].normal, chartN) < chartCos) continue;

                    AssignFace(f, chartID, he, faceChart, chart, ref nSum);
                    PushNeighbors(f, he, heSeam, faceChart, heap, nSum.Normalized());
                }

                chart.Normal = nSum.SqrMagnitude() > Epsilon ? nSum.Normalized() : he.faces[seed].normal;


            }

            return new ChartSegmentationResult
            {
                FaceChart = faceChart,
                Charts = charts,
                HEseam = heSeam
            };

        }

        // ── 검증용: 차트별 랜덤 색을 정점 컬러로 굽기 (Scene 뷰에서 경계 확인) ──
        public static void DebugColorize(Mesh mesh, ChartSegmentationResult r)
        {
            int[] tris = mesh.triangles;
            var colors = new UnityEngine.Color[mesh.vertexCount];
            var palette = new Dictionary<int, Color>();
            for (int f = 0; f < r.FaceChart.Length; f++)
            {
                int c = r.FaceChart[f];
                if (!palette.TryGetValue(c, out var col))
                {
                    UnityEngine.Random.InitState(c * 9973 + 1);
                    col = Color.HSVToRGB(UnityEngine.Random.value, 0.6f, 0.9f);
                    palette[c] = col;
                }
                int i0 = f * 3;
                colors[tris[i0]] = col;
                colors[tris[i0 + 1]] = col;
                colors[tris[i0 + 2]] = col;
            }
            mesh.colors = colors;
        }

    }








    /// <summary>2021.3에는 System PriorityQueue가 없어 최소 힙을 직접 구현.</summary>
    internal sealed class MinHeap
    {
        private struct Node { public float Key; public int Val; }
        private readonly List<Node> _h = new List<Node>(256);

        public void Clear() => _h.Clear();

        public void Push(float key, int val)
        {
            _h.Add(new Node { Key = key, Val = val });
            int i = _h.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_h[p].Key <= _h[i].Key) break;
                (_h[p], _h[i]) = (_h[i], _h[p]);
                i = p;
            }
        }

        public bool Pop(out float key, out int val)
        {
            if (_h.Count == 0) { key = 0f; val = -1; return false; }
            var root = _h[0];
            key = root.Key; val = root.Val;

            int last = _h.Count - 1;
            _h[0] = _h[last];
            _h.RemoveAt(last);

            int i = 0, n = _h.Count;
            while (true)
            {
                int l = 2 * i + 1, rgt = 2 * i + 2, mn = i;
                if (l < n && _h[l].Key < _h[mn].Key) mn = l;
                if (rgt < n && _h[rgt].Key < _h[mn].Key) mn = rgt;
                if (mn == i) break;
                (_h[mn], _h[i]) = (_h[i], _h[mn]);
                i = mn;
            }
            return true;
        }
    }
}