using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{

    public struct Tri { public Vector3 V0, V1, V2; }   // 월드 공간 (alpha matId는 추후)
    public struct Hit { public bool Valid; public float T; public int TriIndex; }

    /// <summary>
    /// 차폐 질의 추상화. BVH(C1, 가속)와 BruteForceOccluder(레퍼런스) 구현
    /// </summary>
    public interface IOccluder
    {
        Hit Intersect(Vector3 o, Vector3 d, float tmin, float tmax);
        bool Occluded(Vector3 o, Vector3 d, float maxDist);
    }


     /// <summary>공유 레이-삼각형(Möller–Trumbore). 
     /// BVH와 BruteForce가 동일 프리미티브 사용 → 교차검증이 '순회/컬링'만 검사.</summary>
    public static class RayGeometry
    {
        public static bool RayTri(Vector3 o, Vector3 d, Tri t, float tmin, float tmax, out float hit)
        {
            hit = 0f;
            Vector3 e1 = t.V1 - t.V0;
            Vector3 e2 = t.V2 - t.V0;

            Vector3 p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-12f)
                return false;

            float inv = 1f /det;
            Vector3 tv = o -t.V0;
            float u = Vector3.Dot(tv, p) * inv;
            if (u < 0f || u > 1f) return false;

            Vector3 q = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            float tt =  Vector3.Dot(e2 , q) * inv;
            if(tt <= tmin || tt>= tmax)
                return false;

            hit = tt;
            return true;
        }

        /// <summary>
        /// <see cref="RayTri"/> 와 동일한 교차 판정에 **barycentric 을 함께 반환**하는 변형(α 트랙).
        ///
        /// 왜 오버로드가 아니라 본체 복제인가: <see cref="RayTri"/> 는 CPU/Burst/GPU 비트동일의
        /// 기준점이라 시그니처를 바꾸면 모든 호출부와 HLSL 미러가 연쇄로 흔들린다. 연산 순서가
        /// 같으면 결과도 같으므로, 본체를 복제해 기존 경로를 0의 위험으로 보존한다(α 결정 ④).
        ///
        /// 규약: bu↔V1, bv↔V2 (e1=V1-V0, e2=V2-V0 기준) → w0 = 1-bu-bv ↔ V0.
        /// </summary>
        public static bool RayTriUV(Vector3 o, Vector3 d, Tri t, float tmin, float tmax,
                                    out float hit, out float bu, out float bv)
        {
            hit = 0f; bu = 0f; bv = 0f;
            Vector3 e1 = t.V1 - t.V0;
            Vector3 e2 = t.V2 - t.V0;

            Vector3 p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-12f)
                return false;

            float inv = 1f / det;
            Vector3 tv = o - t.V0;
            float u = Vector3.Dot(tv, p) * inv;
            if (u < 0f || u > 1f) return false;

            Vector3 q = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            float tt = Vector3.Dot(e2, q) * inv;
            if (tt <= tmin || tt >= tmax)
                return false;

            hit = tt;
            bu = u;
            bv = v;
            return true;
        }
    }



}