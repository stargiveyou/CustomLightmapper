using System;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Track B(B1-1): 균일 3D 프로브 격자 — 공간에 고정된 SH9 프로브 볼륨의 배치·인덱싱·CPU 샘플·베이크 연결.
    ///
    /// 배치: 프로브 (i,j,k) 위치 = origin + (i,j,k)⊙spacing (셀 코너). 선형 인덱스 idx = i + dims.x·(j + dims.y·k)
    ///   = 베이크 points 순서 = SH 배열 순서 = 다바운스 시드 인덱스(seed + idx·2654435761u) = Texture3D 텍셀 순서(B1-3).
    /// 텍스처 정렬(B1-4 셰이더 계약): uvw = (g + 0.5) / dims, g = (p - origin) / spacing — 프로브 = 텍셀 중심이라
    ///   HW trilinear(Clamp) ≡ <see cref="Sample(SH9[], bool[], Vector3)"/>(볼륨 밖은 경계 프로브로 클램프).
    /// 베이크: 격자 위치를 그대로 <see cref="BurstSHBaker"/>/<see cref="GpuSHBaker"/> 의 points 로 넘김 — 신규 커널 없음.
    /// SH 계수는 입사 radiance 에 선형이라 위치에 대한 선형 보간도 유효한 SH(= 조도 보간).
    /// </summary>
    [Serializable]
    public struct ProbeGrid
    {
        /// <summary>GpuSHBaker 1D 디스패치 한도: (n+63)/64 그룹 ≤ 65535.</summary>
        public const int MaxGpuProbes = 65535 * 64;

        public Vector3 origin;      // 프로브 (0,0,0) 위치(최소 코너)
        public Vector3 spacing;     // 축별 간격(> 0) — 수평/수직 분리 가능
        public Vector3Int dims;     // 축별 프로브 수(≥ 1)

        public ProbeGrid(Vector3 origin, Vector3 spacing, Vector3Int dims)
        {
            this.origin = origin;
            this.spacing = spacing;
            this.dims = dims;
        }

        public readonly int Count => dims.x * dims.y * dims.z;
        public readonly bool IsValid => dims.x >= 1 && dims.y >= 1 && dims.z >= 1 && spacing.x > 0f && spacing.y > 0f && spacing.z > 0f;
        /// <summary>마지막 프로브 (dims-1) 위치.</summary>
        public readonly Vector3 MaxCorner => origin + Vector3.Scale(spacing, (Vector3)(dims - Vector3Int.one));

        /// <summary>
        /// bounds 를 덮는 최소 격자 — 축별 dims = ⌈size/spacing⌉ + 1, 격자 범위 (dims-1)·spacing 을 bounds 중심에 맞춘다
        /// (size 가 spacing 의 정수배면 코너가 bounds 와 정확히 일치).
        /// </summary>
        public static ProbeGrid FromBounds(Bounds b, Vector3 spacing)
        {
            if (spacing.x <= 0f || spacing.y <= 0f || spacing.z <= 0f)
                throw new ArgumentException("spacing 은 축별 > 0 이어야 함", nameof(spacing));
            Vector3 size = b.size;
            var d = new Vector3Int(
                Mathf.Max(0, Mathf.CeilToInt(size.x / spacing.x - 1e-4f)) + 1,
                Mathf.Max(0, Mathf.CeilToInt(size.y / spacing.y - 1e-4f)) + 1,
                Mathf.Max(0, Mathf.CeilToInt(size.z / spacing.z - 1e-4f)) + 1);
            Vector3 extent = Vector3.Scale(spacing, (Vector3)(d - Vector3Int.one));
            return new ProbeGrid(b.center - extent * 0.5f, spacing, d);
        }

        // ───────────────────────── 인덱싱 ─────────────────────────

        public readonly int Index(int i, int j, int k) => i + dims.x * (j + dims.y * k);

        public readonly Vector3Int Coord(int index)
        {
            int i = index % dims.x;
            int t = index / dims.x;
            return new Vector3Int(i, t % dims.y, t / dims.y);
        }

        public readonly Vector3 Position(int i, int j, int k) => origin + new Vector3(i * spacing.x, j * spacing.y, k * spacing.z);

        public readonly Vector3 Position(int index)
        {
            var c = Coord(index);
            return Position(c.x, c.y, c.z);
        }

        /// <summary>전 프로브 위치(선형 인덱스 순서 = 베이크 points 순서).</summary>
        public readonly Vector3[] Positions()
        {
            var p = new Vector3[Count];
            int idx = 0;
            for (int k = 0; k < dims.z; k++)
                for (int j = 0; j < dims.y; j++)
                    for (int i = 0; i < dims.x; i++)
                        p[idx++] = Position(i, j, k);
            return p;
        }

        /// <summary>연속 격자 좌표 g = (p - origin) / spacing (클램프 없음). 프로브 (i,j,k) 에서 정확히 (i,j,k).</summary>
        public readonly Vector3 GridCoord(Vector3 p) =>
            new Vector3((p.x - origin.x) / spacing.x, (p.y - origin.y) / spacing.y, (p.z - origin.z) / spacing.z);

        /// <summary>Texture3D 샘플 좌표(B1-4 셰이더 계약): (g + 0.5) / dims — 프로브 = 텍셀 중심.</summary>
        public readonly Vector3 TextureUVW(Vector3 p)
        {
            Vector3 g = GridCoord(p);
            return new Vector3((g.x + 0.5f) / dims.x, (g.y + 0.5f) / dims.y, (g.z + 0.5f) / dims.z);
        }

        // ───────────────────────── CPU 샘플 ─────────────────────────

        public readonly SH9 Sample(SH9[] sh, Vector3 p) => Sample(sh, null, p);

        /// <summary>
        /// 8탭 trilinear. <paramref name="valid"/>(길이 Count)가 주어지면 무효 탭 가중치 0 + 유효 가중치 합으로 재정규화
        /// (dilation 과 이중 방어 — 벽·지면 내부 프로브 누수 차단). 유효 탭이 전무하면 무가중 trilinear(= dilation 값) 폴백.
        /// 볼륨 밖 위치는 경계로 클램프(Texture3D Clamp 와 동일).
        /// </summary>
        public readonly SH9 Sample(SH9[] sh, bool[] valid, Vector3 p)
        {
            Vector3 g = GridCoord(p);
            Axis(g.x, dims.x, out int i0, out float fx);
            Axis(g.y, dims.y, out int j0, out float fy);
            Axis(g.z, dims.z, out int k0, out float fz);
            int i1 = Mathf.Min(i0 + 1, dims.x - 1);
            int j1 = Mathf.Min(j0 + 1, dims.y - 1);
            int k1 = Mathf.Min(k0 + 1, dims.z - 1);

            var all = new SH9();
            var acc = new SH9();
            float wValid = 0f;
            for (int c = 0; c < 8; c++)
            {
                bool hx = (c & 1) != 0, hy = (c & 2) != 0, hz = (c & 4) != 0;
                float w = (hx ? fx : 1f - fx) * (hy ? fy : 1f - fy) * (hz ? fz : 1f - fz);
                if (w == 0f) continue;
                int idx = Index(hx ? i1 : i0, hy ? j1 : j0, hz ? k1 : k0);
                AddScaled(ref all, sh[idx], w);
                if (valid != null && valid[idx]) { AddScaled(ref acc, sh[idx], w); wValid += w; }
            }
            if (valid == null || wValid <= 1e-6f) return all;
            Scale(ref acc, 1f / wValid);
            return acc;
        }

        // 축 하나: g → (하위 프로브, 분율). 볼륨 밖은 경계로 클램프(NaN → 0). n=1 축은 항상 (0, 0).
        static void Axis(float g, int n, out int i0, out float f)
        {
            if (n <= 1) { i0 = 0; f = 0f; return; }
            g = g > 0f ? Mathf.Min(g, n - 1) : 0f;
            i0 = Mathf.Min((int)g, n - 2);   // g = n-1 이면 i0 = n-2, f = 1
            f = g - i0;
        }

        internal static void AddScaled(ref SH9 a, in SH9 b, float w)
        {
            a.c0 += b.c0 * w; a.c1 += b.c1 * w; a.c2 += b.c2 * w;
            a.c3 += b.c3 * w; a.c4 += b.c4 * w; a.c5 += b.c5 * w;
            a.c6 += b.c6 * w; a.c7 += b.c7 * w; a.c8 += b.c8 * w;
        }

        internal static void Scale(ref SH9 a, float s)
        {
            a.c0 *= s; a.c1 *= s; a.c2 *= s;
            a.c3 *= s; a.c4 *= s; a.c5 *= s;
            a.c6 *= s; a.c7 *= s; a.c8 *= s;
        }

        // ───────────────────────── 베이크 (재사용) ─────────────────────────

        /// <summary>GPU 커널 이름 — <see cref="BakeGpu"/> 에 넘길 kernel 은 이 이름으로 FindKernel.</summary>
        public static string GpuKernelName(BakeQualitySettings q) => q.MaxBounces > 1 ? "CSSHBakeMulti" : "CSSHBake";

        /// <summary>
        /// 전 프로브 Burst 베이크(선형 인덱스 순서). q.MaxBounces ≤ 1 → <see cref="BurstSHBaker.Bake"/>(1바운스+앰비언트 근사, 결정적),
        /// ≥ 2 → <see cref="BurstSHBaker.BakeMultiBounce"/>(경로추적) — <see cref="InstanceSceneBaker"/> 의 bounces 규약과 동일.
        /// </summary>
        public readonly SH9[] BakeBurst(in BurstScene scene, BurstSky sky, DirectionalLight sun, BakeQualitySettings q, int dirCount, uint seed)
        {
            var pts = new NativeArray<Vector3>(Positions(), Allocator.TempJob);
            try
            {
                var outp = q.MaxBounces > 1
                    ? BurstSHBaker.BakeMultiBounce(scene, sky, sun, q, pts, dirCount, seed, Allocator.TempJob)
                    : BurstSHBaker.Bake(scene, sky, sun, pts, dirCount, Allocator.TempJob);
                var arr = outp.ToArray();
                outp.Dispose();
                return arr;
            }
            finally { pts.Dispose(); }
        }

        /// <summary>
        /// 전 프로브 GPU 베이크 — kernel 은 <see cref="GpuKernelName"/>(q) 로 찾은 것. 방향셋은 Burst 와 같은 피보나치를 CPU 에서 계산해
        /// 업로드(교차검증 계약). 프로브 수 ≤ <see cref="MaxGpuProbes"/>.
        /// </summary>
        public readonly SH9[] BakeGpu(GpuScene gpu, ComputeShader cs, int kernel, DirectionalLight sun, BurstSky sky,
                                      BakeQualitySettings q, int dirCount, uint seed)
        {
            if (Count > MaxGpuProbes)
                throw new ArgumentOutOfRangeException(nameof(dims), $"프로브 {Count}개 > GPU 디스패치 한도 {MaxGpuProbes}");
            var dirsNa = BurstSHBaker.FibonacchiSphere(dirCount, Allocator.Temp);
            var dirs = dirsNa.ToArray();
            dirsNa.Dispose();
            float w = 4f * Mathf.PI / dirCount;
            var pts = Positions();
            return q.MaxBounces > 1
                ? GpuSHBaker.BakeMultiBounce(gpu, cs, kernel, sun, sky, q, seed, pts, dirs, w)
                : GpuSHBaker.Bake(gpu, cs, kernel, sun, sky, pts, dirs, w);
        }

        public override readonly string ToString() =>
            $"ProbeGrid(origin={origin}, spacing={spacing}, dims={dims.x}×{dims.y}×{dims.z}={Count})";
    }
}
