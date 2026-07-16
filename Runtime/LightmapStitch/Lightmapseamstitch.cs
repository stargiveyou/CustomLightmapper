using System.Collections.Generic;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 시임 스티칭 — Tier 1(정점 단위). 같은 원본 정점에서 갈라진 uv2 정점들(SeamTable.Groups)이
    /// 아틀라스에서 서로 다른 텍셀에 떨어져 독립적으로 구워진 값을 갖는다. 그 그룹 텍셀 값을
    /// 평균 내어 되써넣어 차트 경계 '정점'의 불연속을 제거한다.
    ///
    /// 코어는 아틀라스 텍셀 인덱스 그룹만 받는 순수 함수(헤드리스 검증). uv2→텍셀 매핑은
    /// 호출측(AtlasApplyDebug)이 인스턴스 ST 로 수행해 그룹을 만든다.
    ///
    /// 한계: 정점만 맞춤(모서리 중간 텍셀은 Tier 2에서). 평균은 slice 에 저장된 값 그대로 —
    ///       RGBA32(감마)면 근사, RGBAHalf(선형)면 정확.
    /// </summary>
    public static class LightmapSeamStitch
    {

        /// <summary>
        /// uv2(0~1) → 인스턴스 ST 영역 내 아틀라스 선형 텍셀 인덱스. 범위 밖이면 -1.
        /// 패킹 포맷:
        ///   packedST = (ox &lt;&lt; 16) | oy   — 페이지 내 타일 원점(픽셀)
        ///   sliceW   = (pageW &lt;&lt; 16) | pageH — 타일 한 변 크기(픽셀)
        ///   res      = 아틀라스 한 변 해상도(픽셀)
        /// 언팩 후 로직은 명시 좌표 오버로드와 동일.
        /// </summary>
        /// 
        /// 

        /// 아틀라스 택셀좌표 공간(정부수=텍셀)의 segment. 끝점 A,B
        public struct Seg
        {
            public Vector2 A, B;
            public Seg(Vector2 a, Vector2 b) { A = a; B = b; }
        }


        public static int Uv2ToTexelIndex(float u, float v, int packedST, int sliceW, int res)
        {
            int ox = (packedST >> 16) & 0xFFFF;
            int oy = packedST & 0xFFFF;

            int pageW = (sliceW >> 16) & 0xFFFF;
            int pageH = sliceW & 0xFFFF;

            int tx = Mathf.Clamp(Mathf.FloorToInt(u * pageW), 0, pageW - 1);
            int ty = Mathf.Clamp(Mathf.FloorToInt(v * pageH), 0, pageH - 1);

            int ax = ox + tx, ay = oy + ty;
            if (ax < 0 || ax >= res || ay < 0 || ay >= res)
                return -1;

            return ay * res + ax;
        }


        public static int Uv2ToTexelIndex(Vector2 uv, int ox, int oy, int sidePx, int res)
        {
            int tx = Mathf.Clamp(Mathf.FloorToInt(uv.x * sidePx), 0, sidePx - 1);
            int ty = Mathf.Clamp(Mathf.FloorToInt(uv.y * sidePx), 0, sidePx - 1);

            int ax = ox + tx, ay = oy + ty;
            if (ax < 0 || ax >= res || ay < 0 || ay >= res)
                return -1;

            return ay * res + ax;
        }

        /// <summary>uv2(0~1) → 아틀라스 '연속' 텍셀좌표(정수부=텍셀). DDA segment 끝점용.</summary>
        public static Vector2 UvToTexelCoord(Vector2 uv, int ox, int oy, int sidePx)
            => new Vector2(ox + uv.x * sidePx, oy + uv.y * sidePx);


        /// <summary>
        /// 단일 페이지(slice)에 대해 정점 시임 스티칭을 수행합니다. (Tier 1)
        /// </summary>
        /// <param name="pixels">아틀라스 페이지의 픽셀 버퍼 (Color[])</param>
        /// <param name="valid">아틀라스 페이지의 valid 마스크 (bool[])</param>
        /// <param name="texelGroups">아틀라스 텍셀 인덱스 그룹들의 목록</param>
        public static void Stitch(Color[] pixels, bool[] valid, System.Collections.Generic.List<int[]> texelGroups)
        {
            if (pixels == null || valid == null || texelGroups == null)
                return;

            foreach (var g in texelGroups)
            {
                if (g == null || g.Length < 2)
                    continue;

                Color sum = Color.clear;
                int validCount = 0;

                for (int i = 0; i < g.Length; i++)
                {
                    int idx = g[i];
                    if (idx < 0 || idx >= pixels.Length)
                        continue;

                    if (valid[idx])
                    {
                        sum += pixels[idx];
                        validCount++;
                    }
                }
                if (validCount < 2) //평균 낼 valid 가 2개 미만이면 의미없기 때문에 continue
                    continue;

                Color average = sum / validCount;
                for (int i = 0; i < g.Length; i++)
                {
                    int idx = g[i];
                    if (idx < 0 || idx >= pixels.Length || !valid[idx]) continue;

                    pixels[idx] = average;
                    valid[idx] = true;
                }
            }
        }

        /// <summary>
        /// Tier2 모서리 segment 평균 (Jacobi 누적).
        /// 각 그룹(>=2 segment)을 공유 t로 순회 — t마다 각 segment의 텍셀을 모아 valid 평균 P를 구하고,
        /// P를 그 t에 참여한 모든 텍셀에 누적한다. 한 iteration의 누적 평균을 slice에 일괄 기록(Jacobi)하므로
        /// 길이 불일치·다중 시임 교차 텍셀도 순서 무관하게 안정적으로 수렴한다.
        /// </summary>
        public static void StitchEdges(Color[] slice, bool[] valid, int res, List<Seg[]> segmentGroups, int iterations = 1)
        {
            if (slice == null || valid == null || segmentGroups == null || res <= 0)
            {
                return;
            }

            var sum = new Dictionary<int, Color>();
            var count = new Dictionary<int, int>();
            var perT = new List<int>(8);

            for (int iter = 0; iter < Mathf.Max(1, iterations); iter++)
            {
                sum.Clear();
                count.Clear();

                foreach (var segs in segmentGroups)
                {
                    if (segs == null || segs.Length < 2)
                        continue;

                    // 가장 긴 segment 기준 샘플 수 → 짧은 쪽도 빠짐없이 같은 t로 매핑
                    float maxLen = 0;
                    for (int s = 0; s < segs.Length; s++)
                        maxLen = Mathf.Max(maxLen, Vector2.Distance(segs[s].A, segs[s].B));
                    int N = Mathf.Max(1, Mathf.CeilToInt(maxLen));

                    for (int i = 0; i <= N; i++)
                    {
                        perT.Clear();
                        float t = i / (float)N;
                        Color r = Color.clear;
                        int vc = 0;

                        // 공유 t에서 각 segment의 텍셀을 모아 valid 합산
                        for (int s = 0; s < segs.Length; s++)
                        {
                            Vector2 p = Vector2.Lerp(segs[s].A, segs[s].B, t);
                            int tx = Mathf.Clamp(Mathf.FloorToInt(p.x), 0, res - 1);
                            int ty = Mathf.Clamp(Mathf.FloorToInt(p.y), 0, res - 1);
                            int idx = ty * res + tx;

                            if (idx < 0 || idx >= slice.Length || !valid[idx]) continue;
                            if (perT.Contains(idx)) continue; // 같은 텍셀 중복 합산 방지
                            perT.Add(idx);
                            r += slice[idx];
                            vc++;
                        }

                        if (vc < 2) continue; // 평균낼 valid 2개 미만

                        Color avg = r / vc; // 이 t의 대표값 P
                        for (int k = 0; k < perT.Count; k++)
                        {
                            int idx = perT[k];
                            if (sum.TryGetValue(idx, out var acc))
                            {
                                sum[idx] = acc + avg;
                                count[idx] += 1;
                            }
                            else
                            {
                                sum[idx] = avg;
                                count[idx] = 1;
                            }
                        }
                    }
                }

                // Jacobi: 이번 iteration의 누적 평균을 일괄 기록(다음 iteration이 갱신값을 읽음)
                foreach (var kv in sum)
                {
                    int idx = kv.Key;
                    slice[idx] = kv.Value / count[idx];
                }
            }
        }
    }
}
