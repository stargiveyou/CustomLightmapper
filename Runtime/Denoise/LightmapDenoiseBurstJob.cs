using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 라이트맵 후처리 — À-trous Joint Bilateral 디노이즈, Burst Job 병렬판.
    /// 알고리즘·가중치 식은 직렬판(LightmapDenoise)과 동일 — 백엔드 교차검증 가능(ε 내 일치).
    ///
    /// 구조: 패스마다 직전 패스 결과(SrcPx)만 읽고 자기 인덱스(DstPx[index])에만 쓰는 더블버퍼
    /// → 한 패스 내 읽기/쓰기 충돌 없음 → IJobFor.ScheduleParallel + Burst. 패스 간 핸들 체이닝.
    /// 가이드(노멀·월드위치)와 valid 는 전 패스 불변(ReadOnly 공유).
    /// </summary>
    public static class LightmapDenoiseBurstJob
    {
        // ── 관리형 진입점(직렬판과 동일 시그니처) → 내부에서 Job 실행 ──
        public static void Denoise(Color[] px, bool[] valid, Vector3[] normal, Vector3[] worldPos,
                                   int w, int h, in DenoiseSettings s)
        {
            if (px == null || valid == null || normal == null || worldPos == null || s.Iterations <= 0)
                return;
            int n = w * h;
            if (px.Length != n || valid.Length != n || normal.Length != n || worldPos.Length != n)
                return;

            var a = new NativeArray<float4>(n, Allocator.TempJob);
            var va = new NativeArray<bool>(n, Allocator.TempJob);
            var na = new NativeArray<float3>(n, Allocator.TempJob);
            var pa = new NativeArray<float3>(n, Allocator.TempJob);
            for (int i = 0; i < n; i++)
            {
                Color c = px[i];
                a[i] = new float4(c.r, c.g, c.b, c.a);
                va[i] = valid[i];
                Vector3 nv = normal[i]; na[i] = new float3(nv.x, nv.y, nv.z);
                Vector3 pv = worldPos[i]; pa[i] = new float3(pv.x, pv.y, pv.z);
            }

            DenoiseBurst(a, va, na, pa, w, h, s);

            for (int i = 0; i < n; i++)
            {
                float4 v = a[i];
                px[i] = new Color(v.x, v.y, v.z, v.w);
            }

            a.Dispose(); va.Dispose(); na.Dispose(); pa.Dispose();
        }

        // ── NativeArray 진입점 — 결과를 px 에 in-place 기록(Job 파이프라인 직결용). valid/가이드는 불변 ──
        public static void DenoiseBurst(NativeArray<float4> px, NativeArray<bool> valid,
                                        NativeArray<float3> normal, NativeArray<float3> worldPos,
                                        int w, int h, in DenoiseSettings s)
        {
            if (!px.IsCreated || !valid.IsCreated || !normal.IsCreated || !worldPos.IsCreated || s.Iterations <= 0)
                return;
            int n = w * h;
            if (px.Length != n || valid.Length != n || normal.Length != n || worldPos.Length != n)
                return;

            var scratch = new NativeArray<float4>(n, Allocator.TempJob);

            NativeArray<float4> src = px;
            NativeArray<float4> dst = scratch;
            bool resultInOriginal = true;

            JobHandle lastHandle = default;
            for (int it = 0; it < s.Iterations; it++)
            {
                var job = new DenoiseJob
                {
                    W = w,
                    H = h,
                    Step = 1 << it,
                    NormalPower = s.NormalPower,
                    // σ는 step 배율(직렬판 동일) — 연속 표면에서 감쇠 step 불변, 차트 점프만 기각.
                    InvTwoSigP2 = 1f / (2f * math.max(1e-6f, s.PositionSigma * (1 << it)) * math.max(1e-6f, s.PositionSigma * (1 << it))),
                    InvTwoSigC2 = 1f / (2f * math.max(1e-6f, s.ColorSigma) * math.max(1e-6f, s.ColorSigma)),
                    SrcPx = src,
                    Valid = valid,
                    Normal = normal,
                    WorldPos = worldPos,
                    DstPx = dst,
                };
                lastHandle = job.ScheduleParallel(n, 256, lastHandle);

                (src, dst) = (dst, src);
                resultInOriginal = !resultInOriginal;
            }

            lastHandle.Complete();

            if (!resultInOriginal)
                scratch.CopyTo(px);

            scratch.Dispose();
        }

        /// <summary>
        /// 한 패스: 직전 패스 결과(SrcPx)만 읽어 valid 텍셀을 5×5 à-trous(간격 Step) 가중 평균으로 평활.
        /// 가중치 = B3 × pow(max(0,n·n'),p) × exp(-‖Δp‖²/2σp²) × exp(-‖Δrgb‖²/2σc²) — 직렬판과 동일 식.
        /// 무효 텍셀은 Src 값을 그대로 Dst 로 넘겨 더블버퍼 일관성 유지.
        /// </summary>
        [BurstCompile]
        public struct DenoiseJob : IJobFor
        {
            public int W;
            public int H;
            public int Step;
            public float NormalPower;
            public float InvTwoSigP2;
            public float InvTwoSigC2;

            [ReadOnly] public NativeArray<float4> SrcPx;
            [ReadOnly] public NativeArray<bool> Valid;
            [ReadOnly] public NativeArray<float3> Normal;
            [ReadOnly] public NativeArray<float3> WorldPos;

            [WriteOnly] public NativeArray<float4> DstPx;

            public void Execute(int index)
            {
                if (!Valid[index]) { DstPx[index] = SrcPx[index]; return; } // 무효/배경 불변

                int x = index % W;
                int y = index / W;

                float3 cN = Normal[index];
                float3 cP = WorldPos[index];
                float3 cC = SrcPx[index].xyz;

                float4 sum = float4.zero;
                float wsum = 0f;

                for (int dy = -2; dy <= 2; dy++)
                {
                    int ny = y + dy * Step;
                    if (ny < 0 || ny >= H) continue;
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int nx = x + dx * Step;
                        if (nx < 0 || nx >= W) continue;
                        int nidx = ny * W + nx;
                        if (!Valid[nidx]) continue;

                        float wt = K(dx) * K(dy);
                        if (nidx != index)
                        {
                            float ndot = math.max(0f, math.dot(cN, Normal[nidx]));
                            wt *= math.pow(ndot, NormalPower);

                            float3 dp = WorldPos[nidx] - cP;
                            wt *= math.exp(-math.dot(dp, dp) * InvTwoSigP2);

                            float3 dc = SrcPx[nidx].xyz - cC;
                            wt *= math.exp(-math.dot(dc, dc) * InvTwoSigC2);
                        }

                        sum += SrcPx[nidx] * wt;
                        wsum += wt;
                    }
                }
                // 중앙 탭이 항상 포함되므로 valid 텍셀에서 wsum>0.
                DstPx[index] = sum / wsum;
            }

            // B3 스플라인 이항 커널 {1,4,6,4,1}/16 — 직렬판 K[] 와 동일.
            static float K(int d)
            {
                int i = d < 0 ? -d : d;
                return i == 0 ? 6f / 16f : (i == 1 ? 4f / 16f : 1f / 16f);
            }
        }
    }
}
