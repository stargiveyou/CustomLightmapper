using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 차트별 UV를 (UV면적 = 메시면적)이 되도록 스케일 → 메시 전역 텍셀 밀도 균일.
    /// 평면투영은 이미 등거리에 가까워 보정량이 작지만, 곡률/방법 차이를 흡수해
    /// 이후 LSCM이 다른 스케일 UV를 내도 자동으로 맞춰지는 핵심 단계.
    /// </summary>
    /// 
    public static class DensityNormalizer
    {
        public static void Normalize(ChartMesh[] charts)
        {
            foreach (var cm in charts)
            {
                float ma = MeshArea(cm), ua = UVArea(cm);
                float s = ua > 1e-12f ? Mathf.Sqrt(ma / ua) : 1f;
                for (int i = 0; i < cm.UV.Length; i++) // 면적에 의한 UV 보정?
                {
                    cm.UV[i] *= s;
                }
            }
        }

        private static float MeshArea(ChartMesh cm)
        {
            float a = 0f;
            var t = cm.Triangles;
            var p = cm.positions;

            for (int i = 0; i < t.Length; i += 3)
            {
                a += 0.5f * Vector3.Cross(p[t[i + 1]] - p[t[i]], p[t[i + 2]] - p[t[i]]).magnitude;
            }
            return a;
        }
        private static float UVArea(ChartMesh cm)
        {
            float a = 0f;
            var t = cm.Triangles;
            var u = cm.UV;

            for (int i = 0; i < t.Length; i += 3)
            {
                Vector2 A = u[t[i]], B = u[t[i + 1]], C = u[t[i + 2]];
                a += Mathf.Abs(0.5f * ((B.x - A.x) * (C.y - A.y) - (B.y - A.y) * (C.x - A.x)));
            }
            return a;
        }
    }


    /// <summary>
    /// 정규화된 차트들을 [0,1] 메시-아틀라스에 셸프(shelf) 패킹.
    /// v1은 높이 내림차순 행 배치 + 전체 균일 스케일. (개선: 회전/MaxRects)
    /// gutter는 차트 간 여백(후단 Push-Pull/시임 대비).
    /// </summary>
    /*
    1. Shelf Packer의 4단계 동작 원리
    이 알고리즘은 각각의 2D UV 차트들이 차지하는 바운딩 박스(Bounding Box, 차트를 감싸는 최소한의 직사각형)를 기준으로 작동합니다.

    차트 정렬 (Sorting): 가장 먼저 모든 차트를 높이(Height) 기준 내림차순으로 정렬합니다. 키가 가장 큰 녀석부터 배치해야 버려지는(낭비되는) 위쪽 공간을 최소화할 수 있습니다.

    첫 선반(Shelf) 개설: 가장 키가 큰 첫 번째 차트를 빈 아틀라스의 맨 아래쪽(보통 좌측 하단 원점)에 배치합니다. 이때 배치된 첫 차트의 높이가 곧 '1층 선반의 천장 높이'로 확정됩니다.

    가로로 이어 꽂기: 다음 차트를 꺼내서 1층 선반의 빈 공간(오른쪽)에 계속 밀어 넣습니다. 키 순서대로 정렬해 두었기 때문에, 뒤로 갈수록 차트들의 높이가 낮아져 1층 선반 천장 위로 튀어나갈 일은 절대 없습니다.

    새로운 층(층상) 쌓기: 가로폭이 꽉 차서 더 이상 1층에 차트를 꽂을 수 없게 되면, 1층 천장 바로 위를 바닥으로 삼아 '2층 선반'을 새로 만듭니다. 그리고 방금 넣지 못한 차트를 2층의 첫 타자로 배치하며, 이 차트의 높이가 2층의 천장 높이가 됩니다. 이 과정을 모든 차트가 배치될 때까지 반복합니다.

    */
    public static class ShelfPacker
    {
        public static void Pack(ChartMesh[] charts, float gutter = 0.01f)
        {
            int n = charts.Length;
            if (n == 0) return;

            var w = new float[n];
            var h = new float[n];
            var minp = new Vector2[n];

            float totalA = 0;
            for (int i = 0; i < n; i++)
            {
                Vector2 mn = charts[i].UV[0], mx = charts[i].UV[0];
                foreach (var p in charts[i].UV)
                {
                    mn = Vector2.Min(mn, p); mx = Vector2.Max(mx, p);
                }
                minp[i] = mn;
                w[i] = mx.x - mn.x;
                h[i] = mx.y - mn.y;

                totalA += (w[i] + gutter) * (h[i] + gutter);

            }

            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;   // 차트 인덱스로 초기화(이전: 전부 n → 범위초과/정렬불가)
            System.Array.Sort(order, (a, b) => h[b].CompareTo(h[a])); // 높이 내림차순

            float W = Mathf.Sqrt(totalA) * 1.1f; //행 폭 휴리스틱
            float x = 0f, y = 0f, shelfH = 0f, maxX = 0f;
            var ox = new float[n];
            var oy = new float[n];

            foreach (int i in order)
            {
                float ww = w[i] + gutter, hh = h[i] + gutter;
                if (x + ww > W && x > 0f)   // 빈 선반(x==0)에선 폭 초과라도 줄바꿈 안 함(무한루프/빈행 방지)
                {
                    x = 0f;
                    y += shelfH;
                    shelfH = 0f;
                }
                ox[i] = x;
                oy[i] = y;
                x += ww;
                maxX = Mathf.Max(maxX, x);     // 실제 사용된 최대 폭 추적
                shelfH = Mathf.Max(shelfH, hh);
            }

            // 휴리스틱 W 가 아니라 실제 사용 폭/높이로 나눠야 [0,1] 보장(넓은 차트 오버플로 방지)
            float scale = 1f / Mathf.Max(maxX, y + shelfH);
            for (int i = 0; i < n; i++)
            {
                var cm = charts[i];
                float gx = ox[i] + gutter * 0.5f;
                float gy = oy[i] + gutter * 0.5f;

                for (int k = 0; k < cm.UV.Length; k++)
                {
                    Vector2 p = cm.UV[k];
                    cm.UV[k] = new Vector2(((p.x - minp[i].x) + gx) * scale,
                                          ((p.y - minp[i].y) + gy) * scale);
                }
            }
        }
    }
}