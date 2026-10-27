using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Burst 이어 굽기 백엔드(<see cref="IPathAccumBackend"/>). 점·상태·합·휘도 제곱합을 Persistent NativeArray 로 상주시키고
    /// 패스마다 활성 인덱스만 바꿔 <see cref="BurstIndirect.AccumJob"/> 실행. 직사광은 일괄 경로와 같은 <see cref="BurstDirect.Compute"/>.
    /// → Adaptive=false 로 끝까지 돌리면 <see cref="BurstRadianceBaker.Bake"/> 와 비트 동일. 사용 후 Dispose.
    /// </summary>
    public sealed class BurstPathAccum : IPathAccumBackend
    {
        readonly BurstScene _scene;
        readonly BurstSky _sky;
        readonly DirectionalLight _sun;
        readonly BakeQualitySettings _q;

        NativeArray<Vector3> _p, _nrm, _sum;
        NativeArray<uint> _seed, _state;
        NativeArray<float> _m2;
        NativeArray<int> _active;
        int _n;

        public BurstPathAccum(in BurstScene scene, BurstSky sky, DirectionalLight sun, BakeQualitySettings q)
        { _scene = scene; _sky = sky; _sun = sun; _q = q; }

        public void Bind(Vector3[] points, Vector3[] normals, uint[] seeds, int n)
        {
            Dispose();
            _n = n;
            _p = new NativeArray<Vector3>(n, Allocator.Persistent);
            _nrm = new NativeArray<Vector3>(n, Allocator.Persistent);
            _seed = new NativeArray<uint>(n, Allocator.Persistent);
            _state = new NativeArray<uint>(n, Allocator.Persistent);
            _sum = new NativeArray<Vector3>(n, Allocator.Persistent);
            _m2 = new NativeArray<float>(n, Allocator.Persistent);
            _active = new NativeArray<int>(n, Allocator.Persistent);
            for (int i = 0; i < n; i++) { _p[i] = points[i]; _nrm[i] = normals[i]; _seed[i] = seeds[i]; }
        }

        public void First(int samples, Vector3[] direct, Vector3[] sum, float[] m2)
        {
            var valid = new NativeArray<bool>(_n, Allocator.TempJob);
            for (int i = 0; i < _n; i++) valid[i] = true;
            var d = BurstDirect.Compute(_scene, _p, _nrm, valid, _sun, _q.DirectSamples, _seed, Allocator.TempJob);
            for (int i = 0; i < _n; i++)
            {
                direct[i] = d[i];
                _state[i] = new Rng(_seed[i]).State;
                _sum[i] = Vector3.zero;
                _m2[i] = 0f;
                _active[i] = i;
            }
            d.Dispose();
            valid.Dispose();

            BurstIndirect.Accumulate(_scene, _sky, _sun, _q, _p, _nrm, _state, _sum, _m2, _active, _n, samples);
            for (int i = 0; i < _n; i++) { sum[i] = _sum[i]; m2[i] = _m2[i]; }
        }

        public void Next(int[] active, int count, int samples, Vector3[] sum, float[] m2)
        {
            for (int j = 0; j < count; j++) _active[j] = active[j];
            BurstIndirect.Accumulate(_scene, _sky, _sun, _q, _p, _nrm, _state, _sum, _m2, _active, count, samples);
            for (int j = 0; j < count; j++) { int i = active[j]; sum[i] = _sum[i]; m2[i] = _m2[i]; }
        }

        public void Dispose()
        {
            if (_p.IsCreated) _p.Dispose();
            if (_nrm.IsCreated) _nrm.Dispose();
            if (_seed.IsCreated) _seed.Dispose();
            if (_state.IsCreated) _state.Dispose();
            if (_sum.IsCreated) _sum.Dispose();
            if (_m2.IsCreated) _m2.Dispose();
            if (_active.IsCreated) _active.Dispose();
            _n = 0;
        }
    }
}
