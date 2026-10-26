using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using ReadOnlyAttribute = Unity.Collections.ReadOnlyAttribute;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Track B(B1-2): 프로브 유효성 — 지형·건물 <b>내부</b>에 놓인 프로브(뒷면만 봄 → 틀린 SH → 보간 누수)를 찾아
    /// 무효 처리하고, 무효 프로브 값을 유효 이웃으로 채운다(dilation).
    ///
    /// 판정: 프로브당 K 개 피보나치 방향(<see cref="BurstSHBaker.FibonacchiSphere"/>, 베이크와 같은 tmin=1e-4)으로
    ///   <see cref="BurstTwoLevelBVH.IntersectInstanced"/> → 히트 삼각형의 <b>뒤집지 않은</b> 월드 면 노멀
    ///   n = NormalMatrix·cross(v1-v0, v2-v0)(Unity 와인딩에서 외향) 이 dot(n, d) &gt; 0 이면 뒷면 히트.
    ///   비율 r = 뒷면 히트 수 / K (미스 포함 분모). r &gt; τ(기본 0.25) → 무효.
    ///   <see cref="BurstTwoLevelBVH.ClosestHit"/> 는 노멀을 레이 쪽으로 뒤집으므로 쓰지 않는다(코어 무수정).
    ///   역전치 노멀 행렬은 음수 스케일(미러) 인스턴스에서도 외향을 보존한다.
    /// 양면 얇은 메시(잎 카드·단면 플레인): 뒷면 판정이 무의미 → 메시(템플릿)별 countBackface=false 로 뒷면 히트를 세지 않음.
    /// Dilation: 링 단위 — 매 반복에서 '알려진'(유효 또는 이전 링에서 채운) 6-이웃이 있는 미지 프로브를 그 평균으로 채운다
    ///   (반복 시작 시점 스냅샷 기준이라 순회 순서 무관·결정적). valid 마스크는 바꾸지 않음 → 런타임 valid 가중 샘플과 이중 방어.
    /// </summary>
    public static class BurstProbeValidity
    {
        public const int DefaultDirCount = 64;
        public const float DefaultThreshold = 0.25f;

        [BurstCompile]
        public struct BackfaceJob : IJobParallelFor
        {
            public BurstScene Scene;
            [ReadOnly] public NativeArray<Vector3> Points;
            [ReadOnly] public NativeArray<Vector3> Dirs;
            [ReadOnly] public NativeArray<bool> CountBackface;   // 메시(BLAS)별 — false 면 그 메시의 뒷면 히트를 세지 않음
            public float MaxDist;
            [WriteOnly] public NativeArray<float> Ratio;

            public void Execute(int index)
            {
                Vector3 p = Points[index];
                int back = 0;
                for (int s = 0; s < Dirs.Length; s++)
                {
                    Vector3 d = Dirs[s];
                    var h = BurstTwoLevelBVH.IntersectInstanced(Scene, p, d, 1e-4f, MaxDist);
                    if (!h.Valid || !CountBackface[h.MeshIndex]) continue;

                    Tri tri = Scene.blasTris[Scene.blasTriStart[h.MeshIndex] + h.MeshTriIndex];
                    Vector3 localN = Vector3.Cross(tri.V1 - tri.V0, tri.V2 - tri.V0);   // 부호만 필요(정규화 불필요)
                    Vector3 wn = Scene.instNormalMatrix[h.InstanceIndex].MultiplyVector(localN);
                    if (Vector3.Dot(wn, d) > 0f) back++;
                }
                Ratio[index] = (float)back / Dirs.Length;
            }
        }

        /// <summary>
        /// 프로브별 뒷면 히트 비율 r ∈ [0,1]. <paramref name="countBackfacePerMesh"/>: 메시(BLAS = 템플릿) 인덱스별 플래그,
        /// null 이면 전부 true. <paramref name="maxDist"/>: 판정 레이 길이(먼 뒷면 무시가 필요하면 셀 크기 몇 배로).
        /// </summary>
        public static float[] BackfaceRatio(in BurstScene scene, Vector3[] points, int dirCount = DefaultDirCount,
                                            bool[] countBackfacePerMesh = null, float maxDist = float.MaxValue, int batch = 16)
        {
            int meshCount = scene.blasNodeCount.IsCreated ? scene.blasNodeCount.Length : 0;
            var pts = new NativeArray<Vector3>(points, Allocator.TempJob);
            var dirs = BurstSHBaker.FibonacchiSphere(dirCount, Allocator.TempJob);
            var flags = new NativeArray<bool>(meshCount, Allocator.TempJob);
            var ratio = new NativeArray<float>(points.Length, Allocator.TempJob);
            try
            {
                for (int m = 0; m < meshCount; m++)
                    flags[m] = countBackfacePerMesh == null || m >= countBackfacePerMesh.Length || countBackfacePerMesh[m];
                var job = new BackfaceJob
                {
                    Scene = scene,
                    Points = pts,
                    Dirs = dirs,
                    CountBackface = flags,
                    MaxDist = maxDist,
                    Ratio = ratio,
                };
                job.Schedule(points.Length, batch).Complete();
                return ratio.ToArray();
            }
            finally
            {
                pts.Dispose(); dirs.Dispose(); flags.Dispose(); ratio.Dispose();
            }
        }

        /// <summary>r &gt; threshold → 무효(false).</summary>
        public static bool[] Classify(float[] ratio, float threshold = DefaultThreshold)
        {
            var valid = new bool[ratio.Length];
            for (int i = 0; i < ratio.Length; i++) valid[i] = !(ratio[i] > threshold);
            return valid;
        }

        /// <summary>격자 전 프로브 유효성(<see cref="BackfaceRatio"/> → <see cref="Classify"/>). ratio 는 디버그·리포트용.</summary>
        public static bool[] ComputeGrid(in BurstScene scene, in ProbeGrid grid, out float[] ratio, int dirCount = DefaultDirCount,
                                         float threshold = DefaultThreshold, bool[] countBackfacePerMesh = null, float maxDist = float.MaxValue)
        {
            ratio = BackfaceRatio(scene, grid.Positions(), dirCount, countBackfacePerMesh, maxDist);
            return Classify(ratio, threshold);
        }

        /// <summary>
        /// 무효 프로브 SH 를 유효 6-이웃 평균으로 채움(링 단위, 최대 <paramref name="maxIterations"/> 링). sh 를 제자리 수정,
        /// valid 는 그대로. 반환 = 채운 프로브 수(유효 프로브가 하나도 닿지 않는 프로브는 원래 값 유지).
        /// </summary>
        public static int Dilate(in ProbeGrid grid, SH9[] sh, bool[] valid, int maxIterations = 64)
        {
            int n = grid.Count;
            var known = (bool[])valid.Clone();
            var next = new SH9[n];
            var fillList = new System.Collections.Generic.List<int>();
            int filled = 0;

            for (int it = 0; it < maxIterations; it++)
            {
                fillList.Clear();
                for (int idx = 0; idx < n; idx++)
                {
                    if (known[idx]) continue;
                    var c = grid.Coord(idx);
                    var sum = new SH9();
                    int cnt = 0;
                    Gather(grid, sh, known, c.x - 1, c.y, c.z, ref sum, ref cnt);
                    Gather(grid, sh, known, c.x + 1, c.y, c.z, ref sum, ref cnt);
                    Gather(grid, sh, known, c.x, c.y - 1, c.z, ref sum, ref cnt);
                    Gather(grid, sh, known, c.x, c.y + 1, c.z, ref sum, ref cnt);
                    Gather(grid, sh, known, c.x, c.y, c.z - 1, ref sum, ref cnt);
                    Gather(grid, sh, known, c.x, c.y, c.z + 1, ref sum, ref cnt);
                    if (cnt == 0) continue;
                    ProbeGrid.Scale(ref sum, 1f / cnt);
                    next[idx] = sum;
                    fillList.Add(idx);
                }
                if (fillList.Count == 0) break;
                // 스냅샷 적용: 이번 링의 값은 다음 링부터 이웃으로 쓰임
                foreach (int idx in fillList) { sh[idx] = next[idx]; known[idx] = true; }
                filled += fillList.Count;
            }
            return filled;
        }

        static void Gather(in ProbeGrid g, SH9[] sh, bool[] known, int i, int j, int k, ref SH9 sum, ref int cnt)
        {
            if ((uint)i >= (uint)g.dims.x || (uint)j >= (uint)g.dims.y || (uint)k >= (uint)g.dims.z) return;
            int idx = g.Index(i, j, k);
            if (!known[idx]) return;
            ProbeGrid.AddScaled(ref sum, sh[idx], 1f);
            cnt++;
        }
    }
}
