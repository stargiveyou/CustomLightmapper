using System;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 근평면 차트용 평탄화. 차트 평균 노멀 평면에 정사영.
    /// 평면 차트엔 무왜곡(등거리)·foldover 불가. 건물 prop의 기본 방법.
    /// 차트가 평면 위 height-field가 아니면(90° 넘게 말림) foldover가 생기며,
    /// 이는 UVValidator가 잡아 A3에서 LSCM/MVC로 폴백한다.
    public static class PlanarProjector
    {
        public static void Projector(ref ChartMesh cm)
        {
            Vector3 n = cm.PlaneNormal.sqrMagnitude > 1e-12f ? cm.PlaneNormal.normalized:Vector3.up;
            //노멀과 비평행한 보조축으로 직교 기저 구성
            Vector3 up = MathF.Abs(n.x) < 0.9f ? Vector3.right : Vector3.up;
            Vector3 t = Vector3.Cross(n,up).normalized;
            Vector3 b = Vector3.Cross(n,t);

            //원점= 차트 중심 (UV 중심 정렬, 이후 정규화/패킹에서 재배치)
            Vector3 c = Vector3.zero;
            for(int i =0; i<cm.positions.Length; i++) c += cm.positions[i] ;
            c /= Mathf.Max(1, cm.positions.Length);

            var uv = new Vector2[cm.positions.Length];
            for(int i =0; i< cm.positions.Length; i++)
            {
                Vector3 p = cm.positions[i]-c;
                uv[i] = new Vector2(Vector3.Dot(p,t), Vector3.Dot(p,b));
            }
            cm.UV = uv;
        }


        public static void ProjectAll(ChartMesh[] charts)
        {
            for(int i =0; i<charts.Length; i++)
            {
                Projector(ref charts[i]);   
            }
        }
    }

}