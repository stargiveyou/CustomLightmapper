using System;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 인스턴싱 경로추적 씬. TwoLevelBVH(TLAS/BLAS) + 메시별 로컬 면노멀/알베도.
    ///
    /// ClosestHit:
    ///   2단 BVH로 (인스턴스, 메시, 메시-로컬 삼각형) 식별 →
    ///   월드 위치 = o + d·T (T는 변환 불변),
    ///   월드 노멀 = 인스턴스 역전치 행렬 · 로컬 면노멀 (비균등 스케일 정확),
    ///   알베도 = 메시별 값.
    /// 
    /// 
    /// 
    /// 알베도 모드:
    ///   A) 메시 균일      : meshAlbedo[meshIndex]                         (테스트/단순)
    ///   B) 인스턴스·submesh: instanceSubmeshAlbedo[instanceIndex][submesh] (베이크 — 머티리얼 충실)
    ///      submesh는 meshTriSubmesh[meshIndex][meshTriIndex]로 역참조.
    /// </summary>
    public sealed class InstancedRadianceScene : IRadianceScene, IDisposable
    {
        readonly TwoLevelBVH _bvh;
        readonly Vector3[][] _meshFaceN;  // [meshIndex][triIndex] 로컬 면노멀
readonly bool _ownsBvh;

        //모드 A
        readonly Vector3[] _meshAlbedo;   // [meshIndex] 균일 알베도
        //모드 B
        readonly int [][] _meshTriSubmesh; // [mesh][tri] -> subMesh
        readonly Vector3[][] _instanceSubMeshAlbedo; //[instance][submesh]
 static readonly Vector3 Fallback = new Vector3(0.5f, 0.5f, 0.5f);
        public IOccluder Occluder => _bvh;
        public TwoLevelBVH Bvh => _bvh;

         // ── 모드 A: 메시 균일 알베도 ──────────────────────────────
        /// <param name="uniqueMeshes">메시별 로컬 삼각형(면노멀 계산용, BVH와 동일해야 함)</param>
        /// <param name="meshAlbedo">메시별 알베도(uniqueMeshes와 같은 길이)</param>
        /// <param name="instances">배치</param>
        public InstancedRadianceScene(Tri[][] uniqueMeshes, Vector3[] meshAlbedo,
                                      TwoLevelBVH.Instance[] instances,
                                      TwoLevelBVH bvh = null)
        {
            int meshCount = uniqueMeshes?.Length ?? 0;
            _meshFaceN = new Vector3[meshCount][];
            for (int m = 0; m < meshCount; m++)
                _meshFaceN[m] = BuildFaceNormals(uniqueMeshes[m] ?? Array.Empty<Tri>());

            _meshAlbedo = meshAlbedo ?? new Vector3[meshCount];

            if (bvh != null) { _bvh = bvh; _ownsBvh = false; }
            else { _bvh = new TwoLevelBVH(uniqueMeshes, instances); _ownsBvh = true; }
        }

       

         // ── 모드 B: 인스턴스·submesh 알베도 ───────────────────────
         public InstancedRadianceScene(Tri[][] uniqueMeshes, int[][] meshTriSubmesh, Vector3[][] instanceSubmeshAlbedo, TwoLevelBVH.Instance[] instances, TwoLevelBVH bvh = null)
        {
            _meshFaceN = BuildFaceNormalses(uniqueMeshes);
            _meshAlbedo = null;
            _meshTriSubmesh = meshTriSubmesh;
            _instanceSubMeshAlbedo = instanceSubmeshAlbedo;
            (_bvh, _ownsBvh) = Resolve(uniqueMeshes, instances, bvh);
        }

        static (TwoLevelBVH, bool) Resolve(Tri[][] uniqueMeshes, TwoLevelBVH.Instance[] instances, TwoLevelBVH bvh = null)
        {
            if (bvh != null) return (bvh, false);
            return (new TwoLevelBVH(uniqueMeshes, instances), true);
        }

    
        static Vector3[] BuildFaceNormals(Tri[] tris)
        {
            var fn = new Vector3[tris.Length];
            for (int i = 0; i < tris.Length; i++)
            {
                Vector3 e1 = tris[i].V1 - tris[i].V0;
                Vector3 e2 = tris[i].V2 - tris[i].V0;
                fn[i] = Vector3.Cross(e1, e2).normalized;
            }
            return fn;
        }

        static Vector3[][] BuildFaceNormalses(Tri[][] meshes)
        {
            int mc = meshes?.Length ??0;
            var outN = new Vector3[mc][];
            for(int m =0; m < mc; m++)
            {
                var tris = meshes[m] ?? Array.Empty<Tri>();
                outN[m] = BuildFaceNormals(tris);
            }
            return outN;
        }

        public bool ClosestHit(Vector3 o, Vector3 d, float tmin, float tmax,
                               out Vector3 pos, out Vector3 nrm, out Vector3 albedo)
        {
            TwoLevelBVH.InstancedHit h = _bvh.IntersectInstanced(o, d, tmin, tmax);
            if (!h.Valid)
            {
                pos = default; nrm = default; albedo = default;
                return false;
            }
            pos = o + d * h.T;

            Vector3 localN = _meshFaceN[h.MeshIndex][h.MeshTriIndex];
            Vector3 wn = _bvh.TransformNormalToWorld(h.InstanceIndex, localN);
            if (Vector3.Dot(wn, d) > 0f) wn = -wn; // 레이를 향하도록
            nrm = wn;

            albedo = LookupAlbedo(h.InstanceIndex, h.MeshIndex, h.MeshTriIndex);
            return true;
        }

        Vector3 LookupAlbedo(int instance, int mesh, int tri)
        {
            if(_instanceSubMeshAlbedo != null)
            {
                // submesh 역참조: 모든 단계 경계 검사. 누락/범위초과는 sm=0 또는 Fallback 으로 안전 처리.
                // ((uint) 캐스트로 음수 인덱스도 한 번에 거른다.)
                int sm = 0;
                if (_meshTriSubmesh != null && (uint)mesh < (uint)_meshTriSubmesh.Length)
                {
                    int[] triSm = _meshTriSubmesh[mesh];
                    if (triSm != null && (uint)tri < (uint)triSm.Length)
                        sm = triSm[tri];
                }
                if((uint)instance < (uint)_instanceSubMeshAlbedo.Length)
                {
                    var arr = _instanceSubMeshAlbedo[instance];
                    if(arr != null && (uint)sm < (uint)arr.Length)
                        return arr[sm];
                }
                return Fallback;
            }
            //모드 A
            return (_meshAlbedo != null && (uint)mesh < (uint)_meshAlbedo.Length) ? _meshAlbedo[mesh] : Fallback;
        }

        public void Dispose()
        {
            if (_ownsBvh) _bvh?.Dispose();
        }
    }
}