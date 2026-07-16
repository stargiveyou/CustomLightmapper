using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 단일레벨 BVH (C1 1단계). 월드 공간 삼각형에 대한 가속 차폐 질의.
    ///
    /// 설계 의도
    ///  - 노드를 NativeArray 평탄 배열로 보관 → 추후 Burst Job + GPU StructuredBuffer가
    ///    동일 레이아웃을 그대로 사용(⑩). 1단계는 정확성·교차검증이 우선이라 Burst는 미적용.
    ///  - 순회는 명시적 스택(재귀 X) → Burst 이식 시 그대로 사용 가능.
    ///  - 프리미티브 교차는 RayGeometry.RayTri 재사용 → BruteForceOccluder와 '동일 프리미티브',
    ///    따라서 교차검증이 '순회/컬링'만 검사하게 된다(Occluder.cs 주석 계약).
    ///  - 빌드는 object-median 분할(개수 반반) → 균형 보장, degenerate(한쪽 0개) 없음.
    ///    binned SAH는 C1 2단계에서 '분할 함수만' 교체(순회 코드 불변).
    ///
    /// 주의: TriIndex는 '원본 Tri[] 인덱스'를 반환한다(BruteForce와 동일 기준 → 교차검증 가능).
    /// </summary>
    /// 
    /// ---->
    /// 
    ///   /// <summary>
    /// 단일레벨 BVH (C1). 월드 또는 로컬(=BLAS) 공간 삼각형의 가속 차폐 질의.
    ///
    /// 설계
    ///  - NativeArray 평탄 노드 → Burst Job + GPU StructuredBuffer 동일 레이아웃(⑩).
    ///  - 순회는 명시적 스택(재귀 X). 프리미티브 교차는 RayGeometry.RayTri 재사용
    ///    → BruteForce와 동일 프리미티브, 교차검증이 '순회/컬링'만 검사.
    ///  - 분할: Median(개수 반반) 또는 BinnedSAH(표면적 휴리스틱, 12 bins). 순회 코드는 공통.
    ///  - 레이/방향은 정규화 가정하지 않음 → 2단에서 인스턴스 로컬로 변환된(비정규화) 방향도
    ///    그대로 사용 가능(파라메트릭 T 일관성 유지).
    ///
    /// 주의: Intersect의 Hit.TriIndex는 '이 BVH가 받은 Tri[] 인덱스'(=메시-로컬).
    /// </summary>
    public sealed class BVH : IOccluder, System.IDisposable
    {
        // ── 평탄 노드 ───────────────────────────────────────────────
        // Count == 0  → 내부 노드, LeftFirst = 왼쪽 자식 인덱스(오른쪽 = +1)
        // Count >  0  → 리프,      LeftFirst = _triIdx 시작 슬롯

        public enum BuildQuality { Median, SAH }

        public struct Node
        {
            public Vector3 Min, Max;
            public int LeftFirst; // 내부 왼쪽 자식 인덱스 (오른쪽 +=1) / 리프 : _triIdx 시작 슬롯
            public int Count; //0= 내부, >0 = 리프
        }

        NativeArray<Node> _nodes;
        NativeArray<int> _triIdx;
        NativeArray<Tri> _tris;


        // G0: Burst/POD 경로용 평탄 데이터 읽기전용 접근(인터페이스 없는 순회 함수가 사용).
        public NativeArray<Node>.ReadOnly NodesRO => _nodes.AsReadOnly();
        public NativeArray<int>.ReadOnly TriIdxRO => _triIdx.AsReadOnly();
        public NativeArray<Tri>.ReadOnly TrisRO => _tris.AsReadOnly();

        int _nodeCount;

        public int NodeCount => _nodeCount;
        public int TriCount => _tris.IsCreated ? _tris.Length : 0;
        public bool IsCreated => _nodes.IsCreated;
        public Vector3 RootMin => _nodeCount > 0 ? _nodes[0].Min : Vector3.zero;
        public Vector3 RootMax => _nodeCount > 0 ? _nodes[0].Max : Vector3.zero;

        const int LeafMax = 4; // SAH가 더 큰 리프를 선호할 수 있어서 4로 완화?
        const int SahBins = 12;
        const float CTrav = 1.0f; // 순회 비용(상대). 리프 비용 = count * Cisect(=1)

        class AxisComparer : IComparer<int>
        {
            public Vector3[] Centroid;
            public int Axis;

            public int Compare(int x, int y)
            {
                int c = Centroid[x][Axis].CompareTo(Centroid[y][Axis]);
                return c != 0 ? c : x.CompareTo(y);
            }

        }

        public BVH(Tri[] tris, Allocator allocator = Allocator.Persistent, BuildQuality quality = BuildQuality.SAH)
        {
            if (tris == null)
                tris = System.Array.Empty<Tri>();

            int n = tris.Length;

            _tris = new NativeArray<Tri>(n, allocator);

            for (int i = 0; i < n; i++)
            {
                _tris[i] = tris[i]; //Array 배열을 NativeArray로 전달
            }

            if (n == 0)
            {
                _nodes = new NativeArray<Node>(0, allocator);
                _triIdx = new NativeArray<int>(0, allocator);
                _nodeCount = 0;
                return;
            }

            _triIdx = new NativeArray<int>(n, allocator);

            //managed 빌드 버퍼
            var idx = new int[n];
            var centroid = new Vector3[n];
            var triMin = new Vector3[n];
            var triMax = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                idx[i] = i;
                Vector3 a = tris[i].V0, b = tris[i].V1, c = tris[i].V2;
                centroid[i] = (a + b + c) / 3f;
                triMin[i] = Vector3.Min(a, Vector3.Min(b, c));
                triMax[i] = Vector3.Max(a, Vector3.Max(b, c));

            }

            var nodes = new System.Collections.Generic.List<Node>(2 * n);
            nodes.Add(new Node { LeftFirst = 0, Count = n });
            // UpdateBounds 함수 생성
            UpdateBounds(nodes, 0, idx, triMin, triMax);


            var ctx = new BuildCtx
            {
                idx = idx,
                centroid = centroid,
                triMin = triMin,
                triMax = triMax,
                cmp = new AxisComparer { Centroid = centroid }
            };

            //빌드 스택 (managed) -> 깊이 오버 플로 걱정 없음 ?
            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                int ni = stack.Pop();
                Node node = nodes[ni];
                int first = node.LeftFirst;
                int count = node.Count;

                if (count <= LeafMax) continue;


                int splitAt = (quality == BuildQuality.SAH) ? SahSplit(ctx, first, count, Area(node.Min, node.Max)) : MedianSplit(ctx, first, count);


                if (splitAt < 0) continue; //SAH가 리프 유지 판단

                int li = nodes.Count;
                int ri = li + 1;
                nodes.Add(new Node { LeftFirst = first, Count = splitAt - first });
                nodes.Add(new Node { LeftFirst = splitAt, Count = first + count - splitAt });
                UpdateBounds(nodes, li, idx, triMin, triMax);
                UpdateBounds(nodes, ri, idx, triMin, triMax);

                node.LeftFirst = li;
                node.Count = 0;
                nodes[ni] = node;

                stack.Push(li);
                stack.Push(ri);

            }

            _nodeCount = nodes.Count;
            _nodes = new NativeArray<Node>(_nodeCount, allocator);
            for (int i = 0; i < _nodeCount; i++)
            {
                _nodes[i] = nodes[i];
            }
            for (int i = 0; i < n; i++)
            {
                _triIdx[i] = idx[i];
            }
        }



        // 노드가 가리키는 삼각형 범위 [LeftFirst, LeftFirst+Count) 의 AABB 를 계산해 Min/Max 갱신.
        //  - 분할 직전 호출: 이 시점 LeftFirst 는 idx 의 '범위 시작'(리프 후보)을 가리킨다.
        //  - Node 는 struct → 수정 후 반드시 nodes[i] 에 다시 써야 반영된다(값 복사 함정).
        //  - triMin/triMax 는 빌드 버퍼(원본 Tri 인덱스 기준), idx 는 정렬/분할된 순서.
        static void UpdateBounds(List<Node> nodes, int nodeIndex, int[] idx, Vector3[] triMin, Vector3[] triMax)
        {
            Node node = nodes[nodeIndex];

            if (node.Count <= 0) // 빈 범위 방어: 퇴화 박스(어떤 레이에도 안 걸림)
            {
                node.Min = node.Max = Vector3.zero;
                nodes[nodeIndex] = node;
                return;
            }

            Vector3 mn = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 mx = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

            int start = node.LeftFirst;
            int end = start + node.Count;
            for (int i = start; i < end; i++)
            {
                int t = idx[i];                 // 분할된 순서 → 원본 Tri 인덱스
                mn = Vector3.Min(mn, triMin[t]);
                mx = Vector3.Max(mx, triMax[t]);
            }

            node.Min = mn;
            node.Max = mx;
            nodes[nodeIndex] = node;            // struct 값 다시 기록(필수)
        }


        // -- IOccluder --
        public Hit Intersect(Vector3 o, Vector3 d, float tmin, float tmax)
        {
            Hit best = new Hit { Valid = false, T = tmax };
            if (_nodeCount == 0) return best;
            Vector3 invD = new Vector3(1f / d.x, 1f / d.y, 1f / d.z);

            Span<int> stack = stackalloc int[64];
            int sp = 0; stack[sp++] = 0;
            while (sp > 0)
            {
                Node node = _nodes[stack[--sp]];
                if (!RayAABB(o, invD, node.Min, node.Max, tmin, best.T))
                    continue;
                if (node.Count > 0)
                {
                    int end = node.LeftFirst + node.Count;
                    for (int s = node.LeftFirst; s < end; s++)
                    {
                        int orig = _triIdx[s];
                        if (RayGeometry.RayTri(o, d, _tris[orig], tmin, best.T, out float h))
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
            return best;
        }

        // -- Occluded -- 
        public bool Occluded(Vector3 o, Vector3 d, float maxDist)
        {
            if (_nodeCount == 0) return false;
            Vector3 invD = new Vector3(1f / d.x, 1f / d.y, 1f / d.z);

            Span<int> stack = stackalloc int[64];
            int sp = 0; stack[sp++] = 0;
            while (sp > 0)
            {
                Node node = _nodes[stack[--sp]];
                if (!RayAABB(o, invD, node.Min, node.Max, 0f, maxDist)) continue;
                if (node.Count > 0)
                {
                    int end = node.LeftFirst + node.Count;
                    for (int s = node.LeftFirst; s < end; s++)
                        if (RayGeometry.RayTri(o, d, _tris[_triIdx[s]], 0f, maxDist, out _)) return true;
                }
                else { stack[sp++] = node.LeftFirst; stack[sp++] = node.LeftFirst + 1; }
            }
            return false;
        }
        public void Dispose()
        {
            if (_nodes.IsCreated) _nodes.Dispose();
            if (_triIdx.IsCreated) _triIdx.Dispose();
            if (_tris.IsCreated) _tris.Dispose();
            _nodeCount = 0;
        }

        #region 분할 전략
        // -- 분할 전략 --
        struct BuildCtx
        {
            public int[] idx;
            public Vector3[] centroid, triMin, triMax;
            public AxisComparer cmp;
        }

        //반환 : 분할 인덱스 ( >first, end <), 또는 -1 (리프 유지)
        #region Median 분할 전략
        static int LongestCentroidAxis(BuildCtx ctx, int first, int count)
        {
            Vector3 mn = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 mx = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

            int end = first + count;
            for (int s = first; s < end; s++)
            {
                Vector3 ce = ctx.centroid[ctx.idx[s]];
                mn = Vector3.Min(mn, ce);
                mx = Vector3.Max(mx, ce);
            }
            Vector3 ext = mx - mn;
            if (ext.x >= ext.y && ext.x >= ext.z) return 0;
            return ext.y >= ext.z ? 1 : 2;
        }
        static int MedianSplit(BuildCtx ctx, int first, int count)
        {
            int axis = LongestCentroidAxis(ctx, first, count);
            ctx.cmp.Axis = axis;
            System.Array.Sort(ctx.idx, first, count, ctx.cmp);
            return first + count / 2;
        }

        #endregion

        #region Surface Area Heuristic

        // AABB 표면적 (SAH 비용식 가중). 비어있으면 0.
        static float Area(Vector3 min, Vector3 max)
        {
            Vector3 e = max - min;
            if (e.x < 0f || e.y < 0f || e.z < 0f) return 0f;
            return 2f * (e.x * e.y + e.y * e.z + e.z * e.x);
        }

        // 반환: 분할 인덱스(>first, <end), 또는 -1(리프 유지)
        static int SahSplit(BuildCtx ctx, int first, int count, float nodeArea)
        {
            int end = first + count;

            Vector3 cmin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 cmax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

            for (int s = first; s < end; s++)
            {
                Vector3 ce = ctx.centroid[ctx.idx[s]];
                cmin = Vector3.Min(cmin, ce);
                cmax = Vector3.Max(cmax, ce);
            }

            float bestCost = float.MaxValue;
            int bestAxis = -1;
            float bestPos = 0f;

            for (int axis = 0; axis < 3; axis++)
            {
                float lo = cmin[axis];
                float hi = cmax[axis];

                if (hi - lo < 1e-12f)
                    continue;
                float scale = SahBins / (hi - lo);

                Span<int> bc = stackalloc int[SahBins];
                Span<Vector3> bmin = stackalloc Vector3[SahBins];
                Span<Vector3> bmax = stackalloc Vector3[SahBins];

                for (int i = 0; i < SahBins; i++)
                {
                    bc[i] = 0;
                    bmin[i] = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                    bmax[i] = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                }

                for (int s = first; s < end; s++)
                {
                    int t = ctx.idx[s];
                    int b = (int)((ctx.centroid[t][axis] - lo) * scale); // (centroid-lo)*scale: 연산자 우선순위 주의
                    if (b < 0) b = 0;
                    if (b >= SahBins) b = SahBins - 1;

                    bc[b] = bc[b] + 1;
                    bmin[b] = Vector3.Min(bmin[b], ctx.triMin[t]);
                    bmax[b] = Vector3.Max(bmax[b], ctx.triMax[t]);
                }

                // 좌/우 누적
                Span<float> leftArea = stackalloc float[SahBins - 1];
                Span<int> leftCnt = stackalloc int[SahBins - 1];
                Span<float> rightArea = stackalloc float[SahBins - 1];
                Span<int> rightCnt = stackalloc int[SahBins - 1];

                Vector3 lmn = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                Vector3 lmx = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                int lc = 0;


                //좌측 지연 면적 계산
                for (int i = 0; i < SahBins - 1; i++)
                {
                    lc += bc[i];
                    lmn = Vector3.Min(lmn, bmin[i]);
                    lmx = Vector3.Max(lmx, bmax[i]);
                    leftArea[i] = Area(lmn, lmx);
                    leftCnt[i] = lc;
                }

                Vector3 rmn = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                Vector3 rmx = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                int rc = 0;
                //우측 지연 면적 계산
                for (int i = SahBins - 1; i >= 1; i--)
                {
                    rc += bc[i];
                    rmn = Vector3.Min(rmn, bmin[i]);
                    rmx = Vector3.Max(rmx, bmax[i]);
                    rightArea[i - 1] = Area(rmn, rmx);
                    rightCnt[i - 1] = rc;
                }

                for (int i = 0; i < SahBins - 1; i++)
                {
                    if (leftCnt[i] == 0 || rightCnt[i] == 0)
                        continue;
                    float cost = leftArea[i] * leftCnt[i] + rightArea[i] * rightCnt[i];
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestAxis = axis;
                        bestPos = lo + (i + 1) / scale;
                    }
                }
            }

            // 리프 비용과 비교(CTrav 포함). 분할 이득 없으면 리프 유지
            float leafCost = count * nodeArea;
            if (bestAxis < 0 || bestCost + CTrav * nodeArea >= leafCost)
                return -1;
            //bestPos 기준 in-place 분할
            int io = first;
            int jo = end - 1;
            while (io <= jo)
            {
                if (ctx.centroid[ctx.idx[io]][bestAxis] < bestPos)
                {
                    io++;
                }
                else
                {
                    int tmp = ctx.idx[io];
                    ctx.idx[io] = ctx.idx[jo];
                    ctx.idx[jo] = tmp;
                    jo--;
                }
            }
            if (io == first || io == end) //degenerate -> median 풀백
            {
                ctx.cmp.Axis = bestAxis;
                System.Array.Sort(ctx.idx, first, count, ctx.cmp);
                return first + count / 2;
            }
            return io;
        }

        #endregion

        #endregion

        // -- 헬퍼 -- //
        public static bool RayAABB(Vector3 o, Vector3 invD, Vector3 bmin, Vector3 bmax, float tmin, float tmax)
        {
            float t0 = (bmin.x - o.x) * invD.x, t1 = (bmax.x - o.x) * invD.x;
            if (invD.x < 0f) { float tmp = t0; t0 = t1; t1 = tmp; }
            tmin = t0 > tmin ? t0 : tmin; tmax = t1 < tmax ? t1 : tmax;
            if (tmax < tmin) return false;   // < (not <=): flat(두께0) 박스도 통과시켜야 함(보수적 컬링)
            t0 = (bmin.y - o.y) * invD.y; t1 = (bmax.y - o.y) * invD.y;
            if (invD.y < 0f) { float tmp = t0; t0 = t1; t1 = tmp; }
            tmin = t0 > tmin ? t0 : tmin; tmax = t1 < tmax ? t1 : tmax;
            if (tmax < tmin) return false;   // < (not <=): flat(두께0) 박스도 통과시켜야 함(보수적 컬링)
            t0 = (bmin.z - o.z) * invD.z; t1 = (bmax.z - o.z) * invD.z;
            if (invD.z < 0f) { float tmp = t0; t0 = t1; t1 = tmp; }
            tmin = t0 > tmin ? t0 : tmin; tmax = t1 < tmax ? t1 : tmax;
            return tmax >= tmin;   // >= : 두께0(coplanar) 박스 통과(false negative 방지)
        }

        // -- 디버그/품질 --
        public int MaxDepth() => _nodeCount == 0 ? 0 : DepthOf(0);
        int DepthOf(int ni)
        {
            Node n = _nodes[ni];
            if (n.Count > 0)
                return 1;
            return 1 + Mathf.Max(DepthOf(n.LeftFirst), DepthOf(n.LeftFirst + 1));
        }

        // -- 트리 SAH 비용 (루트 면적 정규화). 낮을 수록 좋음 Median <-> SAH  비교용.
        public float SahCost()
        {
            if (_nodeCount == 0)
                return 0f;

            float rootArea = Area(_nodes[0].Min, _nodes[0].Max);
            if (rootArea <= 0f)
                return 0f;

            float sum = 0f;
            for (int i = 0; i < _nodeCount; i++)
            {
                Node n = _nodes[i];
                float a = Area(n.Min, n.Max) / rootArea;
                sum += n.Count > 0 ? a * n.Count : a * CTrav;
            }
            return sum;
        }
    }
}