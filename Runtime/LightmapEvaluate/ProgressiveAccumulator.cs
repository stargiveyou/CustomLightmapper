using System;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>적응 샘플링 · 진행형 누적 설정(<see cref="ProgressiveAccumulator"/>).</summary>
    [Serializable]
    public struct AdaptiveSampling
    {
        /// <summary>점당 간접 경로 상한(= BakeQualitySettings.IndirectSamples).</summary>
        public int MaxSamples;
        /// <summary>한 패스에 점마다 더하는 경로 수(마지막 패스는 남은 만큼).</summary>
        public int SamplesPerPass;
        /// <summary>true = 수렴한 점은 다음 패스부터 빠진다(조기 종료). false = 전 점이 MaxSamples 까지(일괄 베이크와 비트 동일).</summary>
        public bool Adaptive;
        /// <summary>수렴 판정을 시작하는 최소 경로 수(분산 추정이 믿을 만해지는 표본 크기).</summary>
        public int MinSamples;
        /// <summary>수렴 기준: 평균 휘도의 표준오차 ≤ Threshold × 텍셀 휘도(직사 + 간접).</summary>
        public float Threshold;
    }

    /// <summary>
    /// 이어 굽기 백엔드(CPU/Burst/GPU). 점 집합을 한 번 묶고, 점별 RNG 상태는 백엔드가 들고 있다(GPU 는 상주 버퍼).
    /// 합(sum)·경로 휘도 제곱합(m2)은 패스마다 호출측 배열로 되돌려 수렴 판정·미리보기에 쓴다.
    /// </summary>
    public interface IPathAccumBackend : IDisposable
    {
        /// <summary>점 n 개 바인딩(평가 원점 = 표면 + bias 가 이미 더해진 점).</summary>
        void Bind(Vector3[] points, Vector3[] normals, uint[] seeds, int n);
        /// <summary>첫 패스(전 점): 직사광 → direct, 상태 = 시드, 경로 samples 개 누적 → sum/m2(덮어씀).</summary>
        void First(int samples, Vector3[] direct, Vector3[] sum, float[] m2);
        /// <summary>이후 패스: active[0..count) 점에만 경로 samples 개를 더 누적 → sum/m2 의 그 칸 갱신.</summary>
        void Next(int[] active, int count, int samples, Vector3[] sum, float[] m2);
    }

    /// <summary>
    /// 진행형 누적 + 적응 샘플링 엔진(백엔드 무관, 순수 C# — 헤드리스 테스트 대상 <c>ProgressiveAccumulatorTests</c>).
    ///
    /// 패스 k 는 아직 활성인 점마다 경로 SamplesPerPass 개를 같은 RNG 스트림에서 '이어서' 추적한다(점별 상태 보존).
    /// 그래서 Adaptive=false 로 끝까지 돌리면 덧셈 순서가 일괄 베이크(EvaluateRadiance / BurstRadianceBaker / CSRadiance)와
    /// 같아 최종값 direct + sum/N 이 CPU·Burst 에서 일괄 결과와 비트 동일하다(GPU 는 커널이 달라 통계 일치).
    /// Adaptive=true 면 MinSamples 이후 매 패스 끝에 점별 표준오차를 보고 수렴한 점을 활성 목록에서 뺀다 —
    /// 판정은 전부 여기(관리 코드, double)에서 하므로 같은 백엔드면 일괄/진행형 어느 경로로 돌려도 같은 점이 같은 패스에 멈춘다.
    /// </summary>
    public sealed class ProgressiveAccumulator
    {
        /// <summary>어두운 텍셀의 상대 기준 하한(휘도). 이보다 어두우면 절대 오차 Threshold×하한으로 판정 → 0 근처에서 무한 샘플링 방지.</summary>
        public const float LuminanceFloor = 1e-4f;

        readonly IPathAccumBackend _backend;
        readonly AdaptiveSampling _s;
        readonly int _n;
        readonly int _max, _perPass, _min;

        public readonly Vector3[] Direct;
        public readonly Vector3[] Sum;
        public readonly float[] M2;
        public readonly int[] Count;
        readonly int[] _active;
        int _activeCount;
        int _done;      // 활성 점이 지금까지 누적한 경로 수(활성 점끼리는 항상 같다)
        int _passes;

        public int PointCount => _n;
        public int ActiveCount => _activeCount;
        public int SamplesDone => _done;
        public int Passes => _passes;
        public int MaxSamples => _max;
        public bool Finished { get; private set; }

        /// <summary>지금까지 쏜 경로 수 합(적응 절감률 = 1 - TotalSamples / (n·MaxSamples)).</summary>
        public long TotalSamples
        {
            get { long t = 0; for (int i = 0; i < _n; i++) t += Count[i]; return t; }
        }

        public ProgressiveAccumulator(IPathAccumBackend backend, AdaptiveSampling s,
                                      Vector3[] points, Vector3[] normals, uint[] seeds, int n)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _s = s;
            _n = Math.Max(0, n);
            _max = Math.Max(1, s.MaxSamples);
            _perPass = Math.Max(1, Math.Min(s.SamplesPerPass, _max));
            _min = Math.Max(2, Math.Min(s.MinSamples, _max));
            Direct = new Vector3[_n];
            Sum = new Vector3[_n];
            M2 = new float[_n];
            Count = new int[_n];
            _active = new int[_n];
            if (_n == 0) { Finished = true; return; }
            _backend.Bind(points, normals, seeds, _n);
        }

        /// <summary>패스 1회. 남은 패스가 있으면 true.</summary>
        public bool Step()
        {
            if (Finished) return false;
            int k = Math.Min(_perPass, _max - _done);
            if (_passes == 0)
            {
                _backend.First(k, Direct, Sum, M2);
                for (int i = 0; i < _n; i++) { Count[i] = k; _active[i] = i; }
                _activeCount = _n;
            }
            else
            {
                _backend.Next(_active, _activeCount, k, Sum, M2);
                for (int j = 0; j < _activeCount; j++) Count[_active[j]] += k;
            }
            _done += k;
            _passes++;

            if (_s.Adaptive && _done >= _min && _done < _max)
            {
                // 순서 보존 압축(활성 목록이 오름차순 유지 → 백엔드 접근 패턴·결과 결정적)
                int w = 0;
                for (int j = 0; j < _activeCount; j++)
                {
                    int i = _active[j];
                    if (!Converged(Direct[i], Sum[i], M2[i], Count[i], _s.Threshold)) _active[w++] = i;
                }
                _activeCount = w;
            }

            if (_done >= _max || _activeCount == 0) Finished = true;
            return !Finished;
        }

        /// <summary>끝까지 실행.</summary>
        public void Run() { while (Step()) { } }

        /// <summary>점 i 의 현재 추정 조도 = direct + sum / count(일괄 베이크의 direct + indirect 와 같은 연산 순서).</summary>
        public Vector3 Estimate(int i) => Direct[i] + Sum[i] / (float)Count[i];

        /// <summary>전 점의 현재 추정(패스 0 전이면 0).</summary>
        public Vector3[] Result()
        {
            var r = new Vector3[_n];
            if (_passes == 0) return r;
            for (int i = 0; i < _n; i++) r[i] = Estimate(i);
            return r;
        }

        /// <summary>
        /// 수렴 판정(순수): 경로 휘도 표본분산 → 평균의 표준오차 se = √(var/N) ≤ threshold × max(직사+간접 휘도, 하한).
        /// var = (m2/N − mean²)·N/(N−1) (음수는 0). N &lt; 2 면 판정 불가 → false.
        /// </summary>
        public static bool Converged(Vector3 direct, Vector3 sum, float m2, int count, float threshold)
        {
            if (count < 2) return false;
            double mean = RadianceCore.Luminance(sum) / (double)count;
            double var = (m2 / (double)count - mean * mean) * count / (count - 1.0);
            if (var < 0) var = 0;
            double se = Math.Sqrt(var / count);
            double total = RadianceCore.Luminance(direct) + mean;
            return se <= threshold * Math.Max(total, LuminanceFloor);
        }
    }

    /// <summary>CPU 이어 굽기 백엔드(RadianceCore.AccumulateIndirect) — 일괄 CPU 경로(EvaluateRadiance)와 비트 동일 규약.</summary>
    public sealed class CpuPathAccum : IPathAccumBackend
    {
        readonly IRadianceScene _scene;
        readonly DirectionalLight _sun;
        readonly ISky _sky;
        readonly BakeQualitySettings _q;
        readonly BakeLight[] _lights;
        Vector3[] _p, _nrm;
        uint[] _seed, _state;

        public CpuPathAccum(IRadianceScene scene, DirectionalLight sun, ISky sky, BakeQualitySettings q, BakeLight[] lights = null)
        { _scene = scene; _sun = sun; _sky = sky; _q = q; _lights = lights; }

        public void Bind(Vector3[] points, Vector3[] normals, uint[] seeds, int n)
        {
            _p = points; _nrm = normals; _seed = seeds;
            _state = new uint[n];
        }

        public void First(int samples, Vector3[] direct, Vector3[] sum, float[] m2)
        {
            for (int i = 0; i < _state.Length; i++)
            {
                // EvaluateRadiance 와 같은 순서: 직사(원반 샘플링 위상 = 시드) → 간접(RNG = 시드)
                direct[i] = RadianceCore.EvaluateDirectPrimary(_scene.Occluder, _p[i], _nrm[i], _sun, _q.DirectSamples, _seed[i], _lights);
                _state[i] = new Rng(_seed[i]).State;
                Vector3 s = Vector3.zero; float q2 = 0f;
                RadianceCore.AccumulateIndirect(_scene, _p[i], _nrm[i], _sun, _sky, _q, ref _state[i], samples, ref s, ref q2, _lights);
                sum[i] = s; m2[i] = q2;
            }
        }

        public void Next(int[] active, int count, int samples, Vector3[] sum, float[] m2)
        {
            for (int j = 0; j < count; j++)
            {
                int i = active[j];
                RadianceCore.AccumulateIndirect(_scene, _p[i], _nrm[i], _sun, _sky, _q, ref _state[i], samples, ref sum[i], ref m2[i], _lights);
            }
        }

        public void Dispose() { }
    }
}
