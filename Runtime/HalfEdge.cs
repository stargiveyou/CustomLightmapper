using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;



namespace HuskyLibs.CustomLightmapper.Bake
{

    // Vertex, Edge, Face 로 변경.
    [System.Serializable]
    public struct HalfEdge_Vertex
    {
        public float3 position;     // Vector3 대신 Unity.Mathematics 사용 (Burst 최적화)
        public int edgeIndex;       // 이 정점에서 출발하는 하프 에지의 인덱스 (없으면 -1)
    }

    [System.Serializable]
    public struct HalfEdge_Edge
    {
        public int vertexIndex;     // 이 에지가 가리키는 (도착하는) 정점 인덱스
        public int pairIndex;       // 반대 방향 하프엣지의 인덱스 ( 외곽선 : -1)
        public int nextIndex;       // 같은 면에서의 다음 하프 에지 인덱스
        public int prevIndex;       // 같은 면에서의 이전 하프 에지 인덱스
        public int faceIndex;       // 이 에지가 속한 면 인덱스
    }

    [System.Serializable]
    public struct HalfEdge_Face
    {
        public int edgeIndex;       // 이 면을 구성하는 하프 에지 중 하나의 인덱스
        public float3 normal;       // 면 노멀 (CCW 와인딩 기준, 단위 벡터)
        public float area;          // 면(삼각형) 면적
    }



    [System.Serializable]
    public struct HalfEdge : System.IDisposable
    {
        //  일반 Mesh 구조로는 기하학적 위상 연산을 하기에는 매우 비효율적임
        //  인접한 정점, 면, 모서리 간의 연결 관계를 앞뒤(주소 포인터)로 추적할 수 있도록 만든 대표적인 자료구조가 바로 하프 에지(Half-Edge) 구조

        // 소유권 관리를 위해 Allocator 정보를 가짐
        [ReadOnly]
        public NativeArray<HalfEdge_Vertex> vertices;
        [ReadOnly]
        public NativeArray<HalfEdge_Edge> edges;
        [ReadOnly]
        public NativeArray<HalfEdge_Face> faces;


        #region Inspector Debug Field

        public HalfEdge_Vertex[] vertices_Debug;
        public HalfEdge_Edge[] edges_Debug;
        public HalfEdge_Face[] faces_Debug;

        #endregion


        private struct EdgePairKey : System.IEquatable<EdgePairKey>
        {
            public readonly int start;
            public readonly int end;

            public EdgePairKey(int start, int end)
            {
                this.start = start;
                this.end = end;
            }

            public bool Equals(EdgePairKey other) => start == other.start && end == other.end;
            public override bool Equals(object obj) => obj is EdgePairKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(start, end);
        }



        public HalfEdge(Mesh mesh)
        {

            int vCount = mesh.vertices.Length;
            int eCount = mesh.triangles.Length; //삼각형당 3개의 하프 엣지 생성됨
            int fCount = mesh.triangles.Length / 3;

            vertices = new NativeArray<HalfEdge_Vertex>(vCount, Allocator.Persistent);
            edges = new NativeArray<HalfEdge_Edge>(eCount, Allocator.Persistent);
            faces = new NativeArray<HalfEdge_Face>(fCount, Allocator.Persistent);


            var weldVertices = mesh.vertices;
            var weldTriangles = mesh.triangles;

            // 임시 복사본 리스트 (C# 영역에서 데이터를 먼저 채우기 위함)
            var tempVertices = new HalfEdge_Vertex[vCount];
            var tempEdges = new HalfEdge_Edge[eCount];
            var tempFaces = new HalfEdge_Face[fCount];

            // C# 배열 기본값은 0이므로, 연결되지 않은 상태(특히 경계 에지의 pairIndex)를 -1로 초기화
            for (int i = 0; i < eCount; i++)
            {
                tempEdges[i].vertexIndex = -1;
                tempEdges[i].pairIndex = -1;
                tempEdges[i].nextIndex = -1;
                tempEdges[i].prevIndex = -1;
                tempEdges[i].faceIndex = -1;
            }

            // 정점 기본 위치 데이터 세팅
            for (int i = 0; i < vCount; i++)
            {
                tempVertices[i].position = (float3)weldVertices[i];
                tempVertices[i].edgeIndex = -1;
            }

            // Pair 매칭을 추적할 매핑 딕셔너리 (Key: 시작ID -> 끝ID, Value: 에지 인덱스)
            Dictionary<EdgePairKey, int> edgeMap = new Dictionary<EdgePairKey, int>(eCount);
            // 같은 방향 엣지(v0→v1)가 두 번 나오면 비매니폴드/중복 면. 하프엣지는 매니폴드 전제라
            // 던지지 않고 첫 엣지를 유지(중복은 twin 미연결=경계 취급)하고 한 번만 경고한다.
            bool nonManifold = false;

            // 2. 면과 에지 기본 순환 구조 생성
            int currentEdgeIdx = 0;
            for (int f = 0; f < fCount; f++)
            {
                // 삼각형을 이루는 세 정점의 인덱스
                int v0 = weldTriangles[f * 3 + 0];
                int v1 = weldTriangles[f * 3 + 1];
                int v2 = weldTriangles[f * 3 + 2];

                // 현재 면 생성
                tempFaces[f].edgeIndex = currentEdgeIdx;

                // 면 노멀 + 면적 계산. cross 의 크기 = 평행사변형 넓이 → 삼각형 면적은 그 절반.
                float3 p0 = tempVertices[v0].position;
                float3 p1 = tempVertices[v1].position;
                float3 p2 = tempVertices[v2].position;
                float3 cross = math.cross(p1 - p0, p2 - p0);   // CCW 와인딩 가정
                tempFaces[f].normal = math.normalizesafe(cross); // 퇴화 면은 0 벡터
                tempFaces[f].area = math.length(cross) * 0.5f;

                // 세 개의 하프 에지 인덱스 계산
                int e0 = currentEdgeIdx + 0;
                int e1 = currentEdgeIdx + 1;
                int e2 = currentEdgeIdx + 2;

                // 에지 0 설정 (v0 -> v1)
                tempEdges[e0].vertexIndex = v1; // 에지가 도달하는 정점
                tempEdges[e0].nextIndex = e1;
                tempEdges[e0].prevIndex = e2;   // 삼각형이므로 이전 에지는 e2
                tempEdges[e0].faceIndex = f;
                tempVertices[v0].edgeIndex = e0; // 정점의 출발 에지로 우선 등록
                if (!edgeMap.TryAdd(new EdgePairKey(v0, v1), e0)) nonManifold = true;

                // 에지 1 설정 (v1 -> v2)
                tempEdges[e1].vertexIndex = v2;
                tempEdges[e1].nextIndex = e2;
                tempEdges[e1].prevIndex = e0;
                tempEdges[e1].faceIndex = f;
                tempVertices[v1].edgeIndex = e1;
                if (!edgeMap.TryAdd(new EdgePairKey(v1, v2), e1)) nonManifold = true;

                // 에지 2 설정 (v2 -> v0)
                tempEdges[e2].vertexIndex = v0;
                tempEdges[e2].nextIndex = e0;
                tempEdges[e2].prevIndex = e1;
                tempEdges[e2].faceIndex = f;
                tempVertices[v2].edgeIndex = e2;
                if (!edgeMap.TryAdd(new EdgePairKey(v2, v0), e2)) nonManifold = true;

                currentEdgeIdx += 3;
            }

            if (nonManifold)
                Debug.LogWarning($"[HalfEdge] 비매니폴드/중복 방향 엣지 감지 — 해당 엣지는 twin 미연결(경계)로 처리. 메시가 겹친 면/비매니폴드일 수 있음(faces={fCount}).");

            // 3. 역방향 추적을 통한 Pair 매칭 완성 단계
            currentEdgeIdx = 0;
            for (int f = 0; f < fCount; f++)
            {
                int v0 = weldTriangles[f * 3 + 0];
                int v1 = weldTriangles[f * 3 + 1];
                int v2 = weldTriangles[f * 3 + 2];

                int e0 = currentEdgeIdx + 0;
                int e1 = currentEdgeIdx + 1;
                int e2 = currentEdgeIdx + 2;

                // e0(v0->v1)의 반대인 (v1->v0) 에지가 있는지 찾기
                if (edgeMap.TryGetValue(new EdgePairKey(v1, v0), out int p0))
                {
                    tempEdges[e0].pairIndex = p0;
                    tempEdges[p0].pairIndex = e0;
                }

                // e1(v1->v2)의 반대인 (v2->v1) 에지가 있는지 찾기
                if (edgeMap.TryGetValue(new EdgePairKey(v2, v1), out int p1))
                {
                    tempEdges[e1].pairIndex = p1;
                    tempEdges[p1].pairIndex = e1;
                }

                // e2(v2->v0)의 반대인 (v0->v2) 에지가 있는지 찾기
                if (edgeMap.TryGetValue(new EdgePairKey(v0, v2), out int p2))
                {
                    tempEdges[e2].pairIndex = p2;
                    tempEdges[p2].pairIndex = e2;
                }

                currentEdgeIdx += 3;
            }


            vertices_Debug = tempVertices;
            edges_Debug = tempEdges;
            faces_Debug = tempFaces;


            // 4. 가공된 메모리를 가비지 컬렉터가 없는 NativeArray로 통째로 복사
            vertices.CopyFrom(tempVertices);
            edges.CopyFrom(tempEdges);
            faces.CopyFrom(tempFaces);
        }

         public void FaceHalfEdges(int f, out int e0, out int e1, out int e2)
        {
            e0 = faces[f].edgeIndex;
            e1 = edges[e0].nextIndex;
            e2 = edges[e1].nextIndex;
        }

         public void Dispose()
        {
            vertices.Dispose();
            edges.Dispose();
            faces.Dispose();

            vertices_Debug = null;
            edges_Debug = null;
            faces_Debug = null;
        }
    }


    [System.Serializable]
    public struct WeldedHalfEdge : System.IDisposable
    {
        //  일반 Mesh 구조로는 기하학적 위상 연산을 하기에는 매우 비효율적임
        //  인접한 정점, 면, 모서리 간의 연결 관계를 앞뒤(주소 포인터)로 추적할 수 있도록 만든 대표적인 자료구조가 바로 하프 에지(Half-Edge) 구조

        // 소유권 관리를 위해 Allocator 정보를 가짐
        [ReadOnly]
        public NativeArray<HalfEdge_Vertex> vertices;
        [ReadOnly]
        public NativeArray<HalfEdge_Edge> edges;
        [ReadOnly]
        public NativeArray<HalfEdge_Face> faces;


        #region Inspector Debug Field

        public HalfEdge_Vertex[] vertices_Debug;
        public HalfEdge_Edge[] edges_Debug;
        public HalfEdge_Face[] faces_Debug;

        #endregion


        private struct EdgePairKey : System.IEquatable<EdgePairKey>
        {
            public readonly int start;
            public readonly int end;

            public EdgePairKey(int start, int end)
            {
                this.start = start;
                this.end = end;
            }

            public bool Equals(EdgePairKey other) => start == other.start && end == other.end;
            public override bool Equals(object obj) => obj is EdgePairKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(start, end);
        }



        // 용접 거리 임계값(월드 단위). float.Epsilon 이 아니라 실제 거리여야 한다.
        private const float WeldEpsilon = 1e-4f;

        public WeldedHalfEdge(Mesh mesh)
        {
            vertices_Debug = null;
            edges_Debug = null;
            faces_Debug = null;

            // 1. 정점 용접 + 퇴화 삼각형 제거를 먼저 수행해야 최종 개수를 알 수 있다.
            //    (static 메서드라 struct 필드 할당 전에 호출 가능)
            WeldVertices(mesh.vertices, mesh.triangles, WeldEpsilon, out Vector3[] weldVertices, out int[] immidiateWeldTriangles);
            RemoveDegenerateTriangles(immidiateWeldTriangles, out int[] weldTriangles);

            // 2. 용접/정제 결과 기준으로 개수 계산 (원본 mesh 개수가 아니라 가공 후 개수)
            int vCount = weldVertices.Length;
            int eCount = weldTriangles.Length;      // 삼각형당 3개의 하프 엣지
            int fCount = weldTriangles.Length / 3;

            vertices = new NativeArray<HalfEdge_Vertex>(vCount, Allocator.Persistent);
            edges = new NativeArray<HalfEdge_Edge>(eCount, Allocator.Persistent);
            faces = new NativeArray<HalfEdge_Face>(fCount, Allocator.Persistent);

            // 임시 복사본 리스트 (C# 영역에서 데이터를 먼저 채우기 위함)
            var tempVertices = new HalfEdge_Vertex[vCount];
            var tempEdges = new HalfEdge_Edge[eCount];
            var tempFaces = new HalfEdge_Face[fCount];

            // C# 배열 기본값은 0이므로, 연결되지 않은 상태(특히 경계 에지의 pairIndex)를 -1로 초기화
            for (int i = 0; i < eCount; i++)
            {
                tempEdges[i].vertexIndex = -1;
                tempEdges[i].pairIndex = -1;
                tempEdges[i].nextIndex = -1;
                tempEdges[i].prevIndex = -1;
                tempEdges[i].faceIndex = -1;
            }

            // 정점 기본 위치 데이터 세팅
            for (int i = 0; i < vCount; i++)
            {
                tempVertices[i].position = (float3)weldVertices[i];
                tempVertices[i].edgeIndex = -1;
            }

            // Pair 매칭을 추적할 매핑 딕셔너리 (Key: 시작ID -> 끝ID, Value: 에지 인덱스)
            Dictionary<EdgePairKey, int> edgeMap = new Dictionary<EdgePairKey, int>(eCount);
            // 같은 방향 엣지(v0→v1)가 두 번 나오면 비매니폴드/중복 면. 하프엣지는 매니폴드 전제라
            // 던지지 않고 첫 엣지를 유지(중복은 twin 미연결=경계 취급)하고 한 번만 경고한다.
            bool nonManifold = false;

            // 2. 면과 에지 기본 순환 구조 생성
            int currentEdgeIdx = 0;
            for (int f = 0; f < fCount; f++)
            {
                // 삼각형을 이루는 세 정점의 인덱스
                int v0 = weldTriangles[f * 3 + 0];
                int v1 = weldTriangles[f * 3 + 1];
                int v2 = weldTriangles[f * 3 + 2];

                // 현재 면 생성
                tempFaces[f].edgeIndex = currentEdgeIdx;

                // 면 노멀 + 면적 계산. cross 의 크기 = 평행사변형 넓이 → 삼각형 면적은 그 절반.
                float3 p0 = tempVertices[v0].position;
                float3 p1 = tempVertices[v1].position;
                float3 p2 = tempVertices[v2].position;
                float3 cross = math.cross(p1 - p0, p2 - p0);   // CCW 와인딩 가정
                tempFaces[f].normal = math.normalizesafe(cross); // 퇴화 면은 0 벡터
                tempFaces[f].area = math.length(cross) * 0.5f;

                // 세 개의 하프 에지 인덱스 계산
                int e0 = currentEdgeIdx + 0;
                int e1 = currentEdgeIdx + 1;
                int e2 = currentEdgeIdx + 2;

                // 에지 0 설정 (v0 -> v1)
                tempEdges[e0].vertexIndex = v1; // 에지가 도달하는 정점
                tempEdges[e0].nextIndex = e1;
                tempEdges[e0].prevIndex = e2;   // 삼각형이므로 이전 에지는 e2
                tempEdges[e0].faceIndex = f;
                tempVertices[v0].edgeIndex = e0; // 정점의 출발 에지로 우선 등록
                if (!edgeMap.TryAdd(new EdgePairKey(v0, v1), e0)) nonManifold = true;

                // 에지 1 설정 (v1 -> v2)
                tempEdges[e1].vertexIndex = v2;
                tempEdges[e1].nextIndex = e2;
                tempEdges[e1].prevIndex = e0;
                tempEdges[e1].faceIndex = f;
                tempVertices[v1].edgeIndex = e1;
                if (!edgeMap.TryAdd(new EdgePairKey(v1, v2), e1)) nonManifold = true;

                // 에지 2 설정 (v2 -> v0)
                tempEdges[e2].vertexIndex = v0;
                tempEdges[e2].nextIndex = e0;
                tempEdges[e2].prevIndex = e1;
                tempEdges[e2].faceIndex = f;
                tempVertices[v2].edgeIndex = e2;
                if (!edgeMap.TryAdd(new EdgePairKey(v2, v0), e2)) nonManifold = true;

                currentEdgeIdx += 3;
            }

            if (nonManifold)
                Debug.LogWarning($"[HalfEdge] 비매니폴드/중복 방향 엣지 감지 — 해당 엣지는 twin 미연결(경계)로 처리. 메시가 겹친 면/비매니폴드일 수 있음(faces={fCount}).");

            // 3. 역방향 추적을 통한 Pair 매칭 완성 단계
            currentEdgeIdx = 0;
            for (int f = 0; f < fCount; f++)
            {
                int v0 = weldTriangles[f * 3 + 0];
                int v1 = weldTriangles[f * 3 + 1];
                int v2 = weldTriangles[f * 3 + 2];

                int e0 = currentEdgeIdx + 0;
                int e1 = currentEdgeIdx + 1;
                int e2 = currentEdgeIdx + 2;

                // e0(v0->v1)의 반대인 (v1->v0) 에지가 있는지 찾기
                if (edgeMap.TryGetValue(new EdgePairKey(v1, v0), out int p0))
                {
                    tempEdges[e0].pairIndex = p0;
                    tempEdges[p0].pairIndex = e0;
                }

                // e1(v1->v2)의 반대인 (v2->v1) 에지가 있는지 찾기
                if (edgeMap.TryGetValue(new EdgePairKey(v2, v1), out int p1))
                {
                    tempEdges[e1].pairIndex = p1;
                    tempEdges[p1].pairIndex = e1;
                }

                // e2(v2->v0)의 반대인 (v0->v2) 에지가 있는지 찾기
                if (edgeMap.TryGetValue(new EdgePairKey(v0, v2), out int p2))
                {
                    tempEdges[e2].pairIndex = p2;
                    tempEdges[p2].pairIndex = e2;
                }

                currentEdgeIdx += 3;
            }


            vertices_Debug = tempVertices;
            edges_Debug = tempEdges;
            faces_Debug = tempFaces;


            // 4. 가공된 메모리를 가비지 컬렉터가 없는 NativeArray로 통째로 복사
            vertices.CopyFrom(tempVertices);
            edges.CopyFrom(tempEdges);
            faces.CopyFrom(tempFaces);
        }



        // ==========================================
        // [STEP 2-1] Vertex Welding (정점 병합)
        // ==========================================
        private static void WeldVertices(Vector3[] vertices, int[] triangles, float epsilon, out Vector3[] weldVertices, out int[] immidiateWeldTriangles)
        {
            float invEpsilon = 1f / epsilon;
            List<Vector3> uniqueVertices = new List<Vector3>();
            Dictionary<Vector3Int, int> gridMap = new Dictionary<Vector3Int, int>();
            int[] indexRemap = new int[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 v = vertices[i];
                Vector3Int gridKey = new Vector3Int(
                Mathf.RoundToInt(v.x * invEpsilon),
                Mathf.RoundToInt(v.y * invEpsilon),
                Mathf.RoundToInt(v.z * invEpsilon)
            );

                if (gridMap.TryGetValue(gridKey, out int existingIndex))
                {
                    indexRemap[i] = existingIndex;
                }
                else
                {
                    int newIndex = uniqueVertices.Count;
                    uniqueVertices.Add(v);
                    gridMap.Add(gridKey, newIndex);
                    indexRemap[i] = newIndex;
                }
            }

            immidiateWeldTriangles = new int[triangles.Length];
            for (int i = 0; i < triangles.Length; i++)
            {
                immidiateWeldTriangles[i] = indexRemap[triangles[i]];
            }
            weldVertices = new Vector3[uniqueVertices.Count];
            for (int i = 0; i < uniqueVertices.Count; i++)
            {
                weldVertices[i] = uniqueVertices[i];
            }
            // vertices.Clear();
            // vertices.AddRange(uniqueVertices);
        }


        // ==========================================
        // [STEP 2-2] Degenerate Triangle 제거
        // ==========================================
        private static void RemoveDegenerateTriangles(int[] triangles, out int[] degeneratedTriangles)
        {
            List<int> validTriangles = new List<int>();
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int i0 = triangles[i + 0];
                int i1 = triangles[i + 1];
                int i2 = triangles[i + 2];

                if (i0 == i1 || i1 == i2 || i2 == i0) continue; // 찌그러진 면 스킵

                validTriangles.Add(i0);
                validTriangles.Add(i1);
                validTriangles.Add(i2);
            }
            // triangles.Clear();
            // triangles.AddRange(validTriangles);
            degeneratedTriangles = new int[validTriangles.Count];
            for (int i = 0; i < validTriangles.Count; i++)
            {
                degeneratedTriangles[i] = validTriangles[i];
            }
        }
        public void FaceHalfEdges(int f, out int e0, out int e1, out int e2)
        {
            e0 = faces[f].edgeIndex;
            e1 = edges[e0].nextIndex;
            e2 = edges[e1].nextIndex;
        }



        public void Dispose()
        {
            vertices.Dispose();
            edges.Dispose();
            faces.Dispose();

            vertices_Debug = null;
            edges_Debug = null;
            faces_Debug = null;
        }
    }



}