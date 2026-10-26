using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// GPU 이어 굽기 백엔드(<see cref="IPathAccumBackend"/>, PathTrace.compute <c>CSRadianceAccum</c>).
    /// 점·시드·RNG 상태·합·휘도 제곱합·직사광은 GPU 상주 — 패스마다 활성 인덱스(<c>_Active</c>, 4B/점)만 올리고
    /// 수렴 판정용 합·제곱합만 읽어 온다(상태는 GPU 밖으로 나오지 않음). 디스패치는 <see cref="GpuBakeLimits.Tiles"/> 분할(<c>_Base</c>).
    /// 버퍼는 grow-on-demand 로 재사용(여러 배치에 다시 Bind). 수명 = 호출측(GpuScene 과 같이 Dispose).
    /// </summary>
    public sealed class GpuPathAccum : IPathAccumBackend
    {
        readonly GpuScene _scene;
        readonly ComputeShader _cs;
        readonly int _kernel;
        readonly DirectionalLight _sun;
        readonly BurstSky _sky;
        readonly BakeQualitySettings _q;
        readonly int _chunk;

        ComputeBuffer _pts, _nrm, _seed, _active, _state, _sum, _m2, _direct;
        int[] _identity;
        int _cap, _n;

        public GpuPathAccum(GpuScene scene, ComputeShader cs, int kernel, DirectionalLight sun, BurstSky sky,
                            BakeQualitySettings q, int chunk = GpuBakeLimits.DefaultChunk)
        {
            _scene = scene; _cs = cs; _kernel = kernel; _sun = sun; _sky = sky; _q = q; _chunk = chunk;
        }

        void Ensure(int n)
        {
            if (_pts != null && n <= _cap) return;
            Release();
            _cap = Mathf.NextPowerOfTwo(Mathf.Max(1, n));
            _pts = new ComputeBuffer(_cap, 12, ComputeBufferType.Structured);
            _nrm = new ComputeBuffer(_cap, 12, ComputeBufferType.Structured);
            _seed = new ComputeBuffer(_cap, sizeof(uint), ComputeBufferType.Structured);
            _active = new ComputeBuffer(_cap, sizeof(int), ComputeBufferType.Structured);
            _state = new ComputeBuffer(_cap, sizeof(uint), ComputeBufferType.Structured);
            _sum = new ComputeBuffer(_cap, 12, ComputeBufferType.Structured);
            _m2 = new ComputeBuffer(_cap, sizeof(float), ComputeBufferType.Structured);
            _direct = new ComputeBuffer(_cap, 12, ComputeBufferType.Structured);
            _identity = new int[_cap];
            for (int i = 0; i < _cap; i++) _identity[i] = i;
        }

        public void Bind(Vector3[] points, Vector3[] normals, uint[] seeds, int n)
        {
            _n = n;
            if (n == 0) return;
            Ensure(n);
            _pts.SetData(points, 0, 0, n);
            _nrm.SetData(normals, 0, 0, n);
            _seed.SetData(seeds, 0, 0, n);
        }

        public void First(int samples, Vector3[] direct, Vector3[] sum, float[] m2)
        {
            if (_n == 0) return;
            _active.SetData(_identity, 0, 0, _n);
            Dispatch(_n, samples, first: true);
            Readback(_direct, direct, _n, 12);
            Readback(_sum, sum, _n, 12);
            Readback(_m2, m2, _n, 4);
        }

        public void Next(int[] active, int count, int samples, Vector3[] sum, float[] m2)
        {
            if (count <= 0) return;
            _active.SetData(active, 0, 0, count);
            Dispatch(count, samples, first: false);
            // 비활성 점은 GPU 값 = 직전 readback 값이라 전체를 읽어도 같은 결과(부분 readback 보다 단순).
            Readback(_sum, sum, _n, 12);
            Readback(_m2, m2, _n, 4);
        }

        void Dispatch(int count, int samples, bool first)
        {
            var cs = _cs; int k = _kernel;
            PathTraceShader.BindRadiance(cs, k, _scene, _sun, _sky, _q);
            cs.SetBuffer(k, "_Points", _pts);
            cs.SetBuffer(k, "_Normals", _nrm);
            cs.SetBuffer(k, "_Seeds", _seed);
            cs.SetBuffer(k, "_Active", _active);
            cs.SetBuffer(k, "_AccState", _state);
            cs.SetBuffer(k, "_AccSum", _sum);
            cs.SetBuffer(k, "_AccM2", _m2);
            cs.SetBuffer(k, "_AccDirect", _direct);
            cs.SetInt("_Count", count);
            cs.SetInt("_PassSamples", samples);
            cs.SetInt("_AccumFirst", first ? 1 : 0);
            try
            {
                foreach (var (start, c) in GpuBakeLimits.Tiles(count, _chunk))
                {
                    cs.SetInt("_Base", start);
                    cs.Dispatch(k, GpuBakeLimits.Groups(c), 1, 1);
                }
            }
            finally { cs.SetInt("_Base", 0); cs.SetInt("_AccumFirst", 0); }
        }

        // 앞 n 개만 동기 회수(AsyncGPUReadback + Wait, 실패 시 GetData). dst 길이 = n.
        static void Readback<T>(ComputeBuffer src, T[] dst, int n, int stride) where T : struct
        {
            var req = UnityEngine.Rendering.AsyncGPUReadback.Request(src, n * stride, 0);
            req.WaitForCompletion();
            if (req.hasError) { src.GetData(dst, 0, 0, n); return; }
            var data = req.GetData<T>();
            for (int i = 0; i < n; i++) dst[i] = data[i];
        }

        void Release()
        {
            _pts?.Dispose(); _nrm?.Dispose(); _seed?.Dispose(); _active?.Dispose();
            _state?.Dispose(); _sum?.Dispose(); _m2?.Dispose(); _direct?.Dispose();
            _pts = _nrm = _seed = _active = _state = _sum = _m2 = _direct = null;
            _identity = null;
            _cap = 0;
        }

        public void Dispose() { Release(); _n = 0; }
    }
}
