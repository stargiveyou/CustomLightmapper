
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{

    /// <summary>
    /// 라이트맵 후처리 -> Push-Pull dilation(거터 채우기), Burst Job 병렬
    /// 
    /// 베이크는 valid 텍셀만 칠하고 거터/무효 텍셀은 배경색으로 남는다 -> 차트 경계에서 bilinear 샘플이 배경을 끌어와 검은 시임이 보인다.
    /// Dilate는 무효 텍셀을 인접 valid 평균으로 링 단위로 확장해 경계 안전지대를 만든다.
    /// 
    /// 구조 : 패스마다 '이전 패스의 valid'만 소스로 읽고 다음 버퍼에 쓰는 더블버퍼
    /// 한 패스 내 읽기/쓰기 충돌 없음 -> IJobParallelFor + Burst.
    /// 패스 간은 핸들 체이닝
    /// </summary>
    public static class LightmapPostProcessBurstJob
    {
        // --- 관리형 진입점(기존 시그니처 유지) -> 내부에서 Job 실행 ----
        public static void Dilate(Color[] px, bool[] valid, int w, int h, int iterations)
        {
            if (px == null || valid == null || iterations <= 0)
                return;
            if (px.Length != w * h || valid.Length != w * h) return;


            int n = w * h;
            var a = new NativeArray<float4>(n, Allocator.TempJob);
            var va = new NativeArray<bool>(n, Allocator.TempJob);

            for (int i = 0; i < n; i++)
            {
                Color c = px[i];
                a[i] = new float4(c.r, c.g, c.b, c.a);
                va[i] = valid[i];
            }


            //실제 Burst + Job 함수?
            DilateBurst(a, va, w, h, iterations);

            for (int i =0; i< n; i++)
            {
                float4 v = a[i];
                px[i] = new Color(v.x, v.y, v.z, v.w);
                valid[i] = va[i];
            }

            a.Dispose();
            va.Dispose();
        }


        // ── NativeArray 진입점 — 결과를 px/valid 에 in-place 기록(Job 파이프라인 직결용) ──
        public static void DilateBurst(NativeArray<float4> px, NativeArray<bool> valid, int w, int h, int iterations)
        {
            // 파라매터의 null 처리  
            if (px.IsCreated == false || valid.IsCreated == false || iterations <= 0)
                return;
            if (px.Length != w * h || valid.Length != w * h) return;

            var scratchPx = new NativeArray<float4>(px.Length, Allocator.TempJob);
            var scratchValid = new NativeArray<bool>(valid.Length, Allocator.TempJob);


            // 패스 간 더블버퍼
            NativeArray<float4> srcPx = px;
            NativeArray<bool> srcValid = valid;
            NativeArray<float4> dstPx = scratchPx;
            NativeArray<bool> dstValid = scratchValid;

            bool resultInOriginal = true; // srcP == px ?
 

            JobHandle lastHandle = default;

            for(int it =0; it < iterations; it++){
                var job = new DiliateJob()
                {
                    W = w,
                    H = h,
                    SrcPx = srcPx,
                    SrcValid = srcValid,
                    DstPx = dstPx,
                    DstValid = dstValid,
                };

                lastHandle = job.ScheduleParallel(px.Length, 256, lastHandle);

                // dst를 다음 src 로 (더블버퍼 스왑) — 항상 src가 '직전 패스 결과'를 가리킨다.
                (srcPx, dstPx) = (dstPx, srcPx);
                (srcValid, dstValid) = (dstValid, srcValid);
                resultInOriginal = !resultInOriginal;
            }

            // 파이프라인 직결 진입점이지만 시그니처가 void라 여기서 완료시킨다.
            lastHandle.Complete();

            // 최종 결과가 scratch에 있으면 원본 px/valid로 복사.
            // (실제 스왑 횟수를 추적하므로 루프 조기 종료가 추가돼도 안전)
            if (!resultInOriginal)
            {
                scratchPx.CopyTo(px);
                scratchValid.CopyTo(valid);
            }

            scratchPx.Dispose();
            scratchValid.Dispose();
        }


        /// <summary>
        /// 한 패스: '직전 패스의 valid(SrcValid)'만 소스로 읽어 무효 텍셀을 8방 valid 평균으로 채운다.
        /// 각 인덱스는 자기 위치(DstPx[index])에만 쓰므로 병렬 안전.
        /// valid 텍셀과 채우지 못한 무효 텍셀은 Src 값을 그대로 Dst로 넘겨 더블버퍼 일관성을 유지한다.
        /// </summary>
        [BurstCompile]
        public struct DiliateJob : IJobFor
        {
            public int W;
            public int H;

            [ReadOnly] public NativeArray<float4> SrcPx;
            [ReadOnly] public NativeArray<bool> SrcValid;

            [WriteOnly] public NativeArray<float4> DstPx;
            [WriteOnly] public NativeArray<bool> DstValid;

            public void Execute(int index)
            {
                // valid 텍셀은 절대 덮어쓰지 않고 그대로 전달
                if (SrcValid[index])
                {
                    DstPx[index] = SrcPx[index];
                    DstValid[index] = true;
                    return;
                }

                int x = index % W;
                int y = index / W;

                float4 sum = float4.zero;
                int cnt = 0;

                // 3x3 이웃 (중앙 제외), 직전 패스 valid 만 누적
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= H) continue;

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx;
                        if (nx < 0 || nx >= W) continue;

                        int nidx = ny * W + nx;
                        if (!SrcValid[nidx]) continue;

                        sum += SrcPx[nidx];
                        cnt++;
                    }
                }

                if (cnt > 0)
                {
                    DstPx[index] = sum / cnt;
                    DstValid[index] = true;
                }
                else
                {
                    // 채우지 못한 무효 텍셀: 배경값 보존
                    DstPx[index] = SrcPx[index];
                    DstValid[index] = false;
                }
            }
        }
    }


}