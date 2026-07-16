using UnityEngine;
namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 브루트포스 차폐(모든 삼각형 검사). O(N)/레이라 작은 테스트 씬용.
    /// C2를 C1과 독립으로 검증하고, BVH의 ground-truth 교차검증 기준이 된다.
    /// </summary>
    public sealed class BruteForceOccluder : IOccluder
    {
        private readonly Tri[] _tris;
        public BruteForceOccluder(Tri[] tris) { _tris = tris; }




        public Hit Intersect(Vector3 o, Vector3 d, float tmin, float tmax)
        {
            Hit best = new Hit { Valid = false, T = tmax };
            for (int i = 0; i < _tris.Length; i++)
            {
                if (RayGeometry.RayTri(o, d, _tris[i], tmin, best.T, out float hit))
                {
                    best.Valid = true;
                    best.T = hit;
                    best.TriIndex = i;
                }
            }
            return best;

        }

        public bool Occluded(Vector3 o, Vector3 d, float maxDist)
        {
            for (int i = 0; i < _tris.Length; i++)
            {
                if (RayGeometry.RayTri(o, d, _tris[i], 0f, maxDist, out float hit))
                {
                    return true;
                }
            }
            return false;
        }
    }
}