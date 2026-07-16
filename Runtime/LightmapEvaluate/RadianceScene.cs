using System;
using Unity.Collections;
using UnityEngine;


namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 경로추적 씬 공통 인터페이스. 단일레벨/인스턴싱 씬을 통일
    /// </summary>
    public interface IRadianceScene
    {
        IOccluder Occluder { get; } //NEE 그림자 레이용
        bool ClosestHit(Vector3 o, Vector3 d, float tmin, float tmax, out Vector3 pos, out Vector3 nrm, out Vector3 albedo);
    }


    /// <summary>
    /// 경로추적용 씬 (단일 레벨), IOccluder(차폐)에 더해 최근접 히트의 면노멀.알베도를 제공
    ///     - 월드 공간 Tri[] 기준. 면노멀은 빌드시 1회 계산
    ///     - 알베도는 균일 또는 per-tri
    ///     - 차폐 가속 구조(BVH)는 내부 생성하거나 외부 주입 가능
    /// </summary>
    public sealed class RadianceScene : IRadianceScene, System.IDisposable
    {
        readonly Tri[] _tris;
        readonly Vector3[] _faceN;
        readonly Vector3[] _albedo;   // per-tri (null이면 _uniform)
        readonly Vector3 _uniform;
        readonly IOccluder _occ;
        readonly bool _ownsOcc;

        public IOccluder Occluder => _occ;
        public int TriCount => _tris.Length;

        public RadianceScene(Tri[] worldTris, Vector3 uniformAlbedo,
                         IOccluder occluder = null, Allocator alloc = Allocator.Persistent)
        {
            _tris = worldTris ?? Array.Empty<Tri>();
            _uniform = uniformAlbedo;
            _albedo = null;
            _faceN = BuildFaceNormals(_tris);
            if (occluder != null) { _occ = occluder; _ownsOcc = false; }
            else { _occ = new BVH(_tris, alloc); _ownsOcc = true; }
        }

        public RadianceScene(Tri[] worldTris, Vector3[] albedoPerTri,
                             IOccluder occluder = null, Allocator alloc = Allocator.Persistent)
        {
            _tris = worldTris ?? Array.Empty<Tri>();
            _albedo = albedoPerTri;
            _uniform = Vector3.one * 0.5f;
            _faceN = BuildFaceNormals(_tris);
            if (occluder != null) { _occ = occluder; _ownsOcc = false; }
            else { _occ = new BVH(_tris, alloc); _ownsOcc = true; }
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
        public bool ClosestHit(Vector3 o, Vector3 d, float tmin, float tmax, out Vector3 pos, out Vector3 nrm, out Vector3 albedo)
        {
            Hit h = _occ.Intersect(o, d, tmin, tmax); // BVH 에 대한 Ray-Triangle 콜리전
            if (!h.Valid)
            {
                pos = default; nrm = default; albedo = default;
                return false;
            }
            pos = o + d * h.T;
            Vector3 fn = _faceN[h.TriIndex];
            if (Vector3.Dot(fn, d) > 0f) fn = -fn; // 레이를 향하도록
            nrm = fn;
            albedo = _albedo != null ? _albedo[h.TriIndex] : _uniform;
            return true;
        }

        public void Dispose()
        {
            if (_ownsOcc && _occ is IDisposable)
                (_occ as IDisposable)?.Dispose();
        }

    }
}