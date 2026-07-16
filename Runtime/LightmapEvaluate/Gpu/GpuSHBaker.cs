using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// SH-G: <see cref="BurstSHBaker"/>(SH-2, per-instance SH9 프로젝션)의 GPU compute 가속 헬퍼.
    ///   PathTrace.compute 의 CSSHBake 커널을 구동. G4 검증 순회 + ClosestHit(tmin=1e-4) + DirectNEE + Sky 재사용.
    ///
    /// Burst 와 동일 방향셋을 쓰기 위해 피보나치 방향은 <b>CPU</b>(BurstSHBaker.FibonacchiSphere)에서 계산해
    /// _Dirs 로 업로드한다(GPU 재계산 금지 → sqrt/sin/cos 하드웨어 발산 제거). 방향·기저(다항식)·순회가
    /// 일치하므로 결과는 near-bit-identical(전이함수 없음).
    ///
    /// 결정적(RNG 없음). 시드/Valid 미사용 — 전 프로브 무조건 베이크.
    /// </summary>
    public static class GpuSHBaker
    {
        /// <summary>
        /// per-probe SH9 베이크(GPU). <paramref name="dirs"/> 는 BurstSHBaker.FibonacchiSphere 와 동일해야
        /// Burst 와 교차검증이 성립. <paramref name="weight"/> = 4π/dirs.Length.
        /// 버퍼는 per-call 생성·해제(간단·안전). 결과는 managed SH9[].
        /// </summary>
        public static SH9[] Bake(GpuScene gpu, ComputeShader cs, int kernel,
                                 DirectionalLight sun, BurstSky sky,
                                 Vector3[] points, Vector3[] dirs, float weight)
        {
            int n = points.Length;
            int dirCount = dirs.Length;
            var result = new SH9[n];
            if (n == 0 || dirCount == 0) return result;

            var ptsBuf = new ComputeBuffer(n, 12, ComputeBufferType.Structured);
            var dirBuf = new ComputeBuffer(dirCount, 12, ComputeBufferType.Structured);
            var shBuf  = new ComputeBuffer(n, SH9.Stride, ComputeBufferType.Structured); // 108
            ptsBuf.SetData(points);
            dirBuf.SetData(dirs);

            try
            {
                gpu.Bind(cs, kernel);           // 순회 SRV + _TlasCount
                gpu.BindLighting(cs, kernel);   // _InstNormals, _MeshAlbedo

                cs.SetBuffer(kernel, "_Points", ptsBuf);
                cs.SetBuffer(kernel, "_Dirs", dirBuf);
                cs.SetBuffer(kernel, "_ShOut", shBuf);
                cs.SetInt("_Count", n);
                cs.SetInt("_DirCount", dirCount);
                cs.SetFloat("_ShWeight", weight);

                // 태양(DirectAt=DirectNEE 미러)
                cs.SetVector("_SunDir", sun.Direction);
                cs.SetVector("_SunColor", sun.Color);
                cs.SetFloat("_SunIntensity", sun.Intensity);

                // 하늘(AmbientAt + miss)
                cs.SetInt("_SkyType", sky.Type);
                cs.SetVector("_SkyTop", sky.A);
                cs.SetVector("_SkyBottom", sky.B);

                cs.Dispatch(kernel, (n + 63) / 64, 1, 1);
                shBuf.GetData(result);
            }
            finally
            {
                ptsBuf.Dispose();
                dirBuf.Dispose();
                shBuf.Dispose();
            }
            return result;
        }
    }
}
