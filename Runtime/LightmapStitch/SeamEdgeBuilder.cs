using UnityEngine;
using System.Collections.Generic;
using System;
namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 시임 스티칭 Tier2(Route A) — uv2 토폴로지 + SeamTable.Groups 에서 '시임 모서리 쌍'을 유도.
    /// 등록 파일(UVAssembler) 변경 없이 자기완결.
    ///
    /// 원리: 3D에서 잘린 한 모서리는 UV에서 두 차트의 경계 모서리로 나타나며, 두 끝점이 각각
    ///       같은 그룹(같은 원본 정점)에 속한다. 따라서 '경계 모서리(삼각형 1개만 접함)' 중
    ///       양 끝점이 모두 시임 그룹인 것을 무순 그룹쌍 {gA,gB} 로 묶으면 한 시임이 된다.
    ///       끝점을 (gA측, gB측)으로 정규화해 미러/뒤집힌 UV에서도 파라미터 t 방향을 일치시킨다.
    public static class SeamEdgeBuilder
    {
        public struct SeamEdge { public int VA, VB; }

        public sealed class SeamEdgeGroup
        {
            public int GA, GB;
            public List<SeamEdge> edges = new();
            public SeamEdgeGroup(int gA, int gB) { GA = gA; GB = gB; }
        }

        public static List<SeamEdgeGroup> Build(int[] triangles, int vertexCount, List<int[]> groups)
        {
            var result = new List<SeamEdgeGroup>();
            if (triangles == null || groups == null || groups.Count == 0 || vertexCount <= 0)
                return result;

            // 1) 정점 -> groupID ( 시임 아닌 정점은 -1)
            var groupOf = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                groupOf[i] = -1;
            }
            for (int g = 0; g < groups.Count; g++)
            {
                var arr = groups[g];
                if (arr == null || arr.Length == 0) continue;
                foreach (var v in arr)
                {
                    if (v >= 0 && v < vertexCount)
                    {
                        groupOf[v] = g;
                    }
                }
            }

            // 2) 무향 모서리 -> 접하는 삼각형 수 ( 경계 모서리 = 1)
            var edgeCount = new Dictionary<long, int>();
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                Bump(edgeCount, a, b);
                Bump(edgeCount, b, c);
                Bump(edgeCount, c, a);
            }

            // 3) 경계 모서리 중 양 끝점이 모두 시임 그룹(서로 다른 그룹) → {gA,gB} 로 묶음
            var byPair = new Dictionary<long, SeamEdgeGroup>();
            foreach
            (var kv in edgeCount)
            {
                if (kv.Value != 1) continue; // 경계 모서리만
                DecodeEdge(kv.Key, out int va, out int vb);
                int ga = groupOf[va];
                int gb = groupOf[vb];

                if (ga < 0 || gb < 0 || ga == gb)
                    continue;

                int lo = Mathf.Min(ga, gb);
                int hi = Mathf.Max(ga, gb);
                long pKey = ((long)lo << 32) | (uint)hi;

                // 끝점 정규화: VA ∈ lo그룹, VB ∈ hi그룹 → 미러/뒤집힌 UV에서도 t 방향 일치
                int vA = (ga == lo) ? va : vb;
                int vB = (ga == lo) ? vb : va;

                if (!byPair.TryGetValue(pKey, out SeamEdgeGroup seg))
                {
                    seg = new SeamEdgeGroup(lo, hi);
                    byPair[pKey] = seg;
                }

                seg.edges.Add(new SeamEdge { VA = vA, VB = vB });

            }

            // 4) 양쪽(>=2 모서리)이 있는 시임만 채택
            foreach (var kv in byPair)
            {
                if (kv.Value.edges.Count >= 2)
                    result.Add(kv.Value);
            }
            return result;
        }

        /// <summary>
        /// 무향(undirected) 모서리 {a,b}를 64bit 키로 패킹: (lo &lt;&lt; 32) | hi.
        /// 작은 인덱스를 상위에 두어 (a,b)와 (b,a)가 같은 키가 되게 한다(방향 무시).
        /// </summary>
        /// <summary>
        /// Build 결과(정점 인덱스 모서리)를 StitchEdges 입력(아틀라스 텍셀좌표 segment)으로 변환.
        /// 한 시임 그룹의 각 모서리 = 차트별 한 변 → Seg, 그룹의 모든 변을 하나의 Seg[]로 묶는다.
        /// 끝점은 Build에서 (VA∈lo그룹, VB∈hi그룹)으로 정규화돼 있어 segment 방향(t)이 서로 일치.
        /// </summary>
        /// <param name="seamGroups">Build 산출 시임 모서리 그룹</param>
        /// <param name="uv2">uv2 좌표 배열(정점 인덱스로 접근)</param>
        /// <param name="ox">인스턴스 ST 타일 원점 X(픽셀)</param>
        /// <param name="oy">인스턴스 ST 타일 원점 Y(픽셀)</param>
        /// <param name="sidePx">타일 한 변 크기(픽셀)</param>
        public static List<LightmapSeamStitch.Seg[]> BuildSegments(
            List<SeamEdgeGroup> seamGroups, Vector2[] uv2, int ox, int oy, int sidePx)
        {
            var result = new List<LightmapSeamStitch.Seg[]>();
            if (seamGroups == null || uv2 == null)
                return result;

            foreach (var sg in seamGroups)
            {
                if (sg == null || sg.edges.Count < 2)
                    continue;

                var segs = new LightmapSeamStitch.Seg[sg.edges.Count];
                bool ok = true;
                for (int i = 0; i < sg.edges.Count; i++)
                {
                    var e = sg.edges[i];
                    if (e.VA < 0 || e.VA >= uv2.Length || e.VB < 0 || e.VB >= uv2.Length)
                    {
                        ok = false;
                        break;
                    }
                    Vector2 a = LightmapSeamStitch.UvToTexelCoord(uv2[e.VA], ox, oy, sidePx);
                    Vector2 b = LightmapSeamStitch.UvToTexelCoord(uv2[e.VB], ox, oy, sidePx);
                    segs[i] = new LightmapSeamStitch.Seg(a, b);
                }
                if (ok)
                    result.Add(segs);
            }
            return result;
        }

        /// <summary>
        /// 노멀 각도 게이팅: 시임 양쪽(차트별 변)의 끝점 노멀이 maxAngleDeg 이내로 일치하는
        /// '부드러운 시임'만 남긴다. 하드 엣지(문틈·필러·큐브 모서리 등 노멀 불연속)는 제외 →
        /// 스티칭이 서로 다른 면을 평균해 밝은 테두리(rim)를 만드는 것을 방지.
        /// normals 가 null 이면 게이팅 불가 → 입력을 그대로 반환.
        /// </summary>
        public static List<SeamEdgeGroup> FilterByNormal(List<SeamEdgeGroup> seamGroups, Vector3[] normals, float maxAngleDeg)
        {
            if (seamGroups == null) return new List<SeamEdgeGroup>();
            if (normals == null) return seamGroups;

            float cosThresh = Mathf.Cos(maxAngleDeg * Mathf.Deg2Rad);
            var result = new List<SeamEdgeGroup>();
            foreach (var sg in seamGroups)
            {
                if (sg == null || sg.edges.Count < 2) continue;

                // 그룹 내 모든 변의 끝점 노멀이 기준과 임계 이내인지(차트 간 노멀 차이로 하드 엣지 판별)
                Vector3 refN = Vector3.zero;
                bool haveRef = false, smooth = true;
                for (int i = 0; i < sg.edges.Count && smooth; i++)
                {
                    var e = sg.edges[i];
                    int v0 = e.VA, v1 = e.VB;
                    if (v0 >= 0 && v0 < normals.Length)
                    {
                        if (!haveRef) { refN = normals[v0]; haveRef = true; }
                        else if (Vector3.Dot(refN, normals[v0]) < cosThresh) { smooth = false; break; }
                    }
                    if (v1 >= 0 && v1 < normals.Length)
                    {
                        if (!haveRef) { refN = normals[v1]; haveRef = true; }
                        else if (Vector3.Dot(refN, normals[v1]) < cosThresh) { smooth = false; break; }
                    }
                }
                if (smooth) result.Add(sg);
            }
            return result;
        }

        static long EdgeKey(int a, int b)
        {
            int lo = Mathf.Min(a, b), hi = Mathf.Max(a, b);
            return ((long)lo << 32) | (uint)hi;
        }

        /// <summary>
        /// 모서리 {a,b}의 접촉 삼각형 수를 1 증가시킨다(없으면 1로 시작).
        /// 누적 결과에서 값이 1인 모서리가 '경계 모서리'(삼각형 1개만 접함).
        /// </summary>
        static void Bump(Dictionary<long, int> d, int a, int b)
        {
            long k = EdgeKey(a, b);
            d[k] = d.TryGetValue(k, out int n) ? n + 1 : 1;
        }

        /// <summary>
        /// EdgeKey로 패킹된 64bit 키를 두 정점 인덱스로 역변환.
        /// lo=상위 32bit(작은 인덱스), hi=하위 32bit(큰 인덱스).
        /// </summary>
        static void DecodeEdge(long key, out int lo, out int hi)
        {
            lo = (int)(key >> 32);
            hi = (int)(key & 0xFFFFFFFF);
        }
    }
}