using System;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// G0 : 단일레벨 BVH 순회를 '인터페이스 없는 static 함수'로 추출 -> (Burst/GPU 이식 토대)
    /// BVH.Intersect/Occluded 와 '동일 로직·동일 프리미티브'를 쓰되, 인스턴스 필드(_nodes 등) 대신
    /// 전달받은 NativeArray(ReadOnly)로 동작.
    ///  RayAABB(BVH.internal)·RayGeometry.RayTri 를 그대로 재사용 -> 관리형 BVH와 비트 동일
    /// 
    /// 호출측이 BVH.NodeRO/TriIdxRO/TrisRO를 넘긴다. [BurstCompile] Job에서 호출 가능 (G1 AO부터 실제 Job 으로 래핑). 가상 디스패치/관리 타입 없음.
    /// 스택 : 고정 크기 (64) stackalloc - Burst.managed 공용.
    /// </summary>
    public static class BurstBVH
    {
        public static Hit Intersect(in NativeArray<BVH.Node>.ReadOnly nodes, in NativeArray<int>.ReadOnly triIdx, in NativeArray<Tri>.ReadOnly tris,
        //Ray
        Vector3 o, Vector3 d, float tmin, float tmax)
        {
            Hit best = new Hit { Valid = false, T = tmax };
            int nodeCount = nodes.Length;
            if (nodeCount == 0)
                return default;
            Vector3 invD = new Vector3(1f / d.x, 1f / d.y, 1f / d.z);
            Span<int> stack = stackalloc int[64];
            int sp = 0;
            stack[sp++] = 0;

            while (sp > 0) // 스택이 비지 않았으면
            {
                BVH.Node node = nodes[stack[--sp]];
                if (!BVH.RayAABB(o, invD, node.Min, node.Max, tmin, best.T))
                {
                    continue;
                }
                if (node.Count > 0)
                {
                    int end = node.LeftFirst + node.Count;
                    for (int s = node.LeftFirst; s < end; s++)
                    {
                        int orig = triIdx[s];
                        if (RayGeometry.RayTri(o, d, tris[orig], tmin, best.T, out var h))
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

        public static bool Occluded(
            in NativeArray<BVH.Node>.ReadOnly nodes,
            in NativeArray<int>.ReadOnly triIdx,
            in NativeArray<Tri>.ReadOnly tris,
            Vector3 o, Vector3 d, float maxDist)
        {
            int nodeCount = nodes.Length;
            if (nodeCount == 0)
                return false;
            Vector3 invD = new Vector3(1f / d.x, 1f / d.y, 1f / d.z);
            Span<int> stack = stackalloc int[64];
            int sp = 0;
            stack[sp++] = 0;
            float t = maxDist;
            while (sp > 0)
            {
                BVH.Node node = nodes[stack[--sp]];
                if (!BVH.RayAABB(o, invD, node.Min, node.Max, 0f, t))
                {
                    continue;
                }
                if (node.Count > 0)
                {
                    int end = node.LeftFirst + node.Count;
                    for (int s = node.LeftFirst; s < end; s++)
                    {
                        int orig = triIdx[s];
                        if (RayGeometry.RayTri(o, d, tris[orig], 0f, t, out var h))
                        {
                            return true;
                        }
                    }
                }
                else
                {
                    stack[sp++] = node.LeftFirst;
                    stack[sp++] = node.LeftFirst + 1;
                }
            }
            return false;
        }
    }

}