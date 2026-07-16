using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 라이트맵 후 처리 (step 8) Push-Pull Dilation (거터 채우기) -> Black Seam 채우기
    /// 
    /// 베이크는 valid 텍셀만 칠하고 거터/무효 텍셀은 배경색으로 남는다 ( 차트 경계 )
    /// bilinear 샘플이 배경을 끌어와 검은 시임이 보인다.
    /// Dilate는 무효 텍셀으 인접 Valid 텍셀의 평균으로 링 단위로 바깥을 채워, 경계 1~수 텍셀의 안전지대를 만든다.
    /// 
    /// 순수 함수 (Unity 씬 비 의존) -> 헤드리스 단위테스트 가능
    /// </summary>
    public class LightmapPostProcess
    {
        /// <summary>
        /// valid 텍셀을 보존하면서, 무효 텍셀을 인접(8방) valid 평균으로 iterations 회 확장.
        /// 한 패스는 '패스 시작 시점의 valid'만 소스로 써서 정확한 링 확장
        /// </summary>
        public static void Dilate(Color[] px, bool[] valid, int w, int h, int iterations)
        {
            if (px == null || valid == null || iterations <= 0)
                return;

            if (px.Length != w * h || valid.Length != w * h)
                return;

            var cur = (bool[])valid.Clone();
            for (int it = 0; it < iterations; it++)
            {
                var next = (bool[])cur.Clone();
                bool changed = false;

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * h + x;
                        if (cur[idx]) continue; // valid 텍셀은 절대 덮어쓰지 않음

                        float r = 0, g = 0, b = 0, a = 0; // Color value
                        int cnt = 0;

                        // 3x3 filter 
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue; // 중앙값 제외
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                                int nidx = ny * w + nx;
                                if (!cur[nidx]) continue; // 패스 시작 시점 valid 만 소스
                                Color c = px[nidx];
                                r += c.r; g += c.g; b += c.b; a += c.a;
                                cnt++;
                            }
                        }
                        if (cnt > 0)
                        {
                            px[idx] = new Color(r / cnt, g / cnt, b / cnt, a / cnt);
                            next[idx] = true;
                            changed = true;
                        }

                    }
                }
                cur = next;
                if (!changed) break; // 직렬판 전용: 조기 종료
            }

            // 채운 텍셀은 이제 valid → 입력 마스크에 되쓴다(Burst판과 동일 의미). 안 쓰면 호출자는 원본 마스크를 받음.
            System.Array.Copy(cur, valid, valid.Length);
        }
    }
}