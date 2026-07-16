using UnityEngine;
using System.Collections.Generic;

namespace HuskyLibs.CustomLightmapper.Bake
{
   /// <summary>같은 원본 정점을 공유하는 조립 정점 묶음(시임). 후단 시임 스티칭에서 사용.</summary>
    public sealed class SeamTable
    {
        public readonly List<int[]> Groups = new List<int[]>(); // 각 group = 조립 메시 정점 인덱스들
    }
 
    /// <summary>
    /// 차트들을 하나의 런타임 메시로 통합. 차트마다 자기 로컬 정점을 그대로 쓰므로
    /// 시임 정점은 자동 복제(정점 수 증가). uv2(채널1)에 패킹된 UV를 기록하고,
    /// 원본 정점 공유 관계로 SeamTable을 만든다. 노멀은 분리된 정점 기준 재계산.
    /// </summary>
    public static class UVAssembly
    {
        public static (Mesh mesh, SeamTable semas) Assemble(ChartMesh[] charts, Mesh source)
        {
            var verts = new List<Vector3>();
            var uv2 = new List<Vector2>();
            var uv0 = new List<Vector2>();
            var tris = new List<int>();
            var src = new List<int>();


            Vector2[] srcUV = (source != null) && source.uv != null && source.uv.Length == source.vertexCount 
            ? source.uv : null;

            int baseV =0 ;
            foreach(var cm in charts)
            {
                int n = cm.positions.Length;
                for(int j =0; j<n ;j++)
                {
                    verts.Add(cm.positions[j]);
                    uv2.Add(cm.UV[j]);
                    src.Add(cm.MeshVertex[j]);
                     if (srcUV != null) uv0.Add(srcUV[cm.MeshVertex[j]]);
                }
                foreach(int t in cm .Triangles) tris.Add(baseV+t);
                baseV += n;
            }

            var m = new Mesh {name = (source ? source.name : "chart") + "_uv2"};
            //Chart Mesh 생성
            m.indexFormat = verts.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.SetUVs(1, uv2);                 // uv2 = UV 채널 1 (Unity 라이트맵 UV)
            if (srcUV != null) m.SetUVs(0, uv0);
            m.RecalculateNormals();           // 차트 경계서 정점이 분리돼 있어 하드 노멀로 복원됨
            m.RecalculateBounds();

            //Seam Table : 원본 정점별 조립 정점 묶음
            var groups = new Dictionary<int, List<int>>();
            for (int vi = 0; vi < src.Count; vi++)
            {
                if (!groups.TryGetValue(src[vi], out var l)) { l = new List<int>(); groups[src[vi]] = l; }
                l.Add(vi);
            }
            var seams = new SeamTable();
            foreach (var kv in groups)
                if (kv.Value.Count >= 2) seams.Groups.Add(kv.Value.ToArray());
 
            return (m, seams);


        }
    }
}