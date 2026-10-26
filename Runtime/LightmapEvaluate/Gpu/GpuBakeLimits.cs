using System;
using System.Collections.Generic;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// GPU 베이크의 하드 한계와 그 계산(순수 C# — 헤드리스 테스트 대상, <c>GpuBakeLimitsTests</c>).
    ///
    /// ① 디스패치 크기: 그룹 수 ≤ 65535(D3D11/Metal 축 한도) × 스레드 64(<c>numthreads(64,1,1)</c>) → 한 Dispatch ≈ 4.19M 점.
    ///    <see cref="Tiles"/> 로 쪼개 같은 버퍼를 오프셋(<c>_Base</c>)만 바꿔 연속 디스패치 → 한도 해소 + 긴 단일 디스패치의
    ///    TDR(GPU 타임아웃) 위험 완화. 스레드 i 는 점 _Base+i 만 쓰고 시드도 점별이라 분할과 무관하게 비트 동일.
    /// ② 순회 스택: PathTrace.compute 의 <c>BVH_STACK</c>(= <see cref="TraversalStack"/>). 순회는 '하나 꺼내고 자식 둘(left, left+1) 넣기'.
    ///    <see cref="StackUsage"/> = 컬링 없는 전체 순회를 같은 순서로 흉내 낸 최대 스택 칸 수 — AABB 컬링은 서브트리를 통째로 건너뛰어
    ///    스택을 그 전 상태로 되돌릴 뿐이라 이보다 커질 수 없다(정확한 최악값). 깊이 H 트리의 상한은 H+1(<see cref="RequiredStack"/>,
    ///    오른쪽으로 깊은 사슬에서 도달 — 왼쪽으로 깊은 사슬은 깊이와 무관하게 2칸). TLAS·BLAS 는 스택 배열이 따로라 각각 맞아야 한다.
    ///    넘치면 GPU 결과가 틀리므로 호출측이 Burst 로 폴백(<see cref="GpuScene.TraversalFits"/>).
    /// </summary>
    public static class GpuBakeLimits
    {
        /// <summary>PathTrace.compute / BvhTraverse.compute 의 <c>#define BVH_STACK</c> 와 반드시 같은 값.</summary>
        public const int TraversalStack = 32;
        /// <summary>CPU/Burst 순회 스택(비교·문서용).</summary>
        public const int CpuTraversalStack = 64;
        public const int ThreadsPerGroup = 64;
        public const int MaxGroupsPerDispatch = 65535;
        public const int MaxPointsPerDispatch = MaxGroupsPerDispatch * ThreadsPerGroup;
        /// <summary>기본 분할 크기(점) — 1024 그룹. 점당 비용(spp·바운스)이 커도 디스패치 하나가 짧게 끝나는 크기.</summary>
        public const int DefaultChunk = 1 << 16;

        /// <summary>
        /// [0, n) 을 chunk 이하 연속 구간으로 분할(마지막만 짧을 수 있음). chunk ≤ 0 이면 <see cref="DefaultChunk"/>,
        /// 한 구간은 <see cref="MaxPointsPerDispatch"/> 를 넘지 않는다.
        /// </summary>
        public static List<(int start, int count)> Tiles(int n, int chunk)
        {
            var tiles = new List<(int, int)>();
            if (n <= 0) return tiles;
            int c = chunk > 0 ? chunk : DefaultChunk;
            if (c > MaxPointsPerDispatch) c = MaxPointsPerDispatch;
            for (int s = 0; s < n; s += c) tiles.Add((s, Math.Min(c, n - s)));
            return tiles;
        }

        public static int Groups(int count) => (count + ThreadsPerGroup - 1) / ThreadsPerGroup;

        /// <summary>
        /// 평탄 BVH(노드 start..start+nodeCount, 자식 인덱스는 start 기준 로컬: 내부 노드 count == 0 → 자식 leftFirst, leftFirst+1)의
        /// 가장 깊은 리프 깊이(루트 0). 노드 0개 = -1. 잘못된 인덱스·순환은 예외(조용히 틀린 깊이를 내지 않음).
        /// </summary>
        public static int MaxLeafDepth(int[] leftFirst, int[] count, int start, int nodeCount)
        {
            if (nodeCount <= 0) return -1;
            var stack = new Stack<(int node, int depth)>();
            stack.Push((0, 0));
            int maxDepth = 0, visited = 0;
            while (stack.Count > 0)
            {
                var (node, depth) = stack.Pop();
                if ((uint)node >= (uint)nodeCount) throw new ArgumentOutOfRangeException(nameof(leftFirst), $"자식 인덱스 {node} ≥ 노드 수 {nodeCount}");
                if (++visited > nodeCount) throw new InvalidOperationException("BVH 순환(방문 수 > 노드 수)");
                if (count[start + node] > 0) { if (depth > maxDepth) maxDepth = depth; continue; }
                int l = leftFirst[start + node];
                stack.Push((l, depth + 1));
                stack.Push((l + 1, depth + 1));
            }
            return maxDepth;
        }

        /// <summary>깊이 H 트리의 GPU 순회 스택 상한 = H + 1(빈 트리 0). 실제 요구량은 <see cref="StackUsage"/>(≤ 이 값).</summary>
        public static int RequiredStack(int maxLeafDepth) => maxLeafDepth < 0 ? 0 : maxLeafDepth + 1;

        /// <summary>
        /// HLSL 순회와 같은 순서(루트 push → pop → 내부면 leftFirst, leftFirst+1 push)로 전체 트리를 돌 때의 최대 스택 칸 수(빈 트리 0).
        /// 잘못된 인덱스·순환은 예외.
        /// </summary>
        public static int StackUsage(int[] leftFirst, int[] count, int start, int nodeCount)
        {
            if (nodeCount <= 0) return 0;
            var stack = new Stack<int>();
            stack.Push(0);
            int max = 1, visited = 0;
            while (stack.Count > 0)
            {
                int node = stack.Pop();
                if ((uint)node >= (uint)nodeCount) throw new ArgumentOutOfRangeException(nameof(leftFirst), $"자식 인덱스 {node} ≥ 노드 수 {nodeCount}");
                if (++visited > nodeCount) throw new InvalidOperationException("BVH 순환(방문 수 > 노드 수)");
                if (count[start + node] > 0) continue;
                int l = leftFirst[start + node];
                stack.Push(l);
                stack.Push(l + 1);
                if (stack.Count > max) max = stack.Count;
            }
            return max;
        }
    }
}
