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
    /// 높이 내림차순 행 배치 + 전체 균일 스케일. 배치 후보 3종 — 그대로 / 차트별 최소 bbox 방향(가로) / 같은 방향(세로) — 중
    /// 균일 스케일(= 점유율, UV 면적은 회전 불변)이 가장 큰 것을 쓴다. LSCM·MVC 는 경계 정점 2개를 고정해 풀어 차트가 기울어진 채
    /// 나오므로(원통 옆면 bbox 채움 50%) 회전이 필요하고, 셸프 배치는 종횡비에 민감해 가로/세로 중 하나가 늘 나은 건 아니라 둘 다 본다.
    /// '그대로'가 첫 후보(동률이면 유지)라 어떤 메시도 회전 도입 전보다 점유가 줄지 않고, 고르면 결과도 그때와 비트 동일.
    /// 베이커는 <see cref="ChartPacker"/>(기본 Best = 이 셸프와 MaxRects 중 점유가 큰 쪽)를 거쳐 부른다.
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
            if (charts.Length == 0) return;
            Choose(charts, gutter, out int best, out var angle);
            Apply(charts, gutter, best, angle);
        }

        /// <summary>
        /// 후보 3종의 균일 스케일을 비교해 고른다(UV 는 건드리지 않음). 반환 = 고른 후보의 스케일 — <see cref="ChartPacker"/> 가 MaxRects 와 비교.
        /// 후보 0 = 그대로. 후보 1·2 는 차트별 최소 bbox 방향(가로 ≥ 세로) / +90°(세로). 스케일이 '엄격히' 커질 때만 교체.
        /// </summary>
        internal static float Choose(ChartMesh[] charts, float gutter, out int best, out float[] angle)
        {
            int n = charts.Length;
            angle = new float[n];
            for (int i = 0; i < n; i++) angle[i] = MinBoxAngle(charts[i].UV);

            best = 0;
            float bestScale = Layout(charts, null, 0f, gutter, null, null, null);
            for (int cand = 1; cand <= 2; cand++)
            {
                float s = Layout(charts, angle, cand == 2 ? Mathf.PI * 0.5f : 0f, gutter, null, null, null);
                if (s > bestScale) { bestScale = s; best = cand; }
            }
            return bestScale;
        }

        /// <summary><see cref="Choose"/> 결과로 회전 → 셸프 배치 → [0,1] 적용(Pack 의 뒷부분 — 결과는 구 Pack 과 비트 동일).</summary>
        internal static void Apply(ChartMesh[] charts, float gutter, int best, float[] angle)
        {
            int n = charts.Length;
            if (best != 0)
            {
                float extra = best == 2 ? Mathf.PI * 0.5f : 0f;
                for (int i = 0; i < n; i++) Rotate(charts[i].UV, angle[i] + extra);
            }

            var minp = new Vector2[n];
            var ox = new float[n];
            var oy = new float[n];
            float scale = Layout(charts, null, 0f, gutter, minp, ox, oy);
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

        // 셸프 배치 → 균일 스케일(클수록 점유↑). angle == null 이면 UV 그대로, 아니면 차트 i 를 angle[i] + extra 만큼 돌린 bbox 로 계산.
        // minp/ox/oy 가 주어지면 배치 결과를 채운다(최종 적용용 — 이때는 UV 가 이미 회전돼 있어 angle == null).
        static float Layout(ChartMesh[] charts, float[] angle, float extra, float gutter, Vector2[] minp, float[] ox, float[] oy)
        {
            int n = charts.Length;
            var w = new float[n];
            var h = new float[n];

            float totalA = 0;
            for (int i = 0; i < n; i++)
            {
                Vector2 mn, mx;
                if (angle == null) Bounds(charts[i].UV, out mn, out mx);
                else RotatedBounds(charts[i].UV, angle[i] + extra, out mn, out mx);
                if (minp != null) minp[i] = mn;
                w[i] = mx.x - mn.x;
                h[i] = mx.y - mn.y;

                totalA += (w[i] + gutter) * (h[i] + gutter);
            }

            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;   // 차트 인덱스로 초기화(이전: 전부 n → 범위초과/정렬불가)
            System.Array.Sort(order, (a, b) => h[b].CompareTo(h[a])); // 높이 내림차순

            float W = Mathf.Sqrt(totalA) * 1.1f; //행 폭 휴리스틱
            float x = 0f, y = 0f, shelfH = 0f, maxX = 0f;

            foreach (int i in order)
            {
                float ww = w[i] + gutter, hh = h[i] + gutter;
                if (x + ww > W && x > 0f)   // 빈 선반(x==0)에선 폭 초과라도 줄바꿈 안 함(무한루프/빈행 방지)
                {
                    x = 0f;
                    y += shelfH;
                    shelfH = 0f;
                }
                if (ox != null) { ox[i] = x; oy[i] = y; }
                x += ww;
                maxX = Mathf.Max(maxX, x);     // 실제 사용된 최대 폭 추적
                shelfH = Mathf.Max(shelfH, hh);
            }

            // 휴리스틱 W 가 아니라 실제 사용 폭/높이로 나눠야 [0,1] 보장(넓은 차트 오버플로 방지)
            return 1f / Mathf.Max(maxX, y + shelfH);
        }

        internal static void Bounds(Vector2[] uv, out Vector2 mn, out Vector2 mx)
        {
            mn = uv[0]; mx = uv[0];
            foreach (var p in uv) { mn = Vector2.Min(mn, p); mx = Vector2.Max(mx, p); }
        }

        // p 를 -a 만큼 돌린 좌표의 bbox(Rotate 와 같은 식이라 후보 평가 = 최종 적용)
        internal static void RotatedBounds(Vector2[] uv, float a, out Vector2 mn, out Vector2 mx)
        {
            float c = Mathf.Cos(-a), s = Mathf.Sin(-a);
            mn = new Vector2(float.MaxValue, float.MaxValue); mx = new Vector2(float.MinValue, float.MinValue);
            foreach (var p in uv)
            {
                var r = new Vector2(p.x * c - p.y * s, p.x * s + p.y * c);
                mn = Vector2.Min(mn, r); mx = Vector2.Max(mx, r);
            }
        }

        internal static void Rotate(Vector2[] uv, float a)
        {
            float c = Mathf.Cos(-a), s = Mathf.Sin(-a);
            for (int k = 0; k < uv.Length; k++)
            {
                var p = uv[k];
                uv[k] = new Vector2(p.x * c - p.y * s, p.x * s + p.y * c);
            }
        }

        /// <summary>
        /// 최소 면적 bbox 방향(라디안): 볼록 껍질의 각 변 방향을 후보로 본다(최소 면적 사각형의 한 변은 껍질 변과 겹친다 — 회전 캘리퍼스와 같은 해).
        /// 반환각만큼 -회전하면 bbox 가로 ≥ 세로. 점이 3개 미만이거나 껍질이 퇴화면 0.
        /// </summary>
        internal static float MinBoxAngle(Vector2[] uv)
        {
            var hull = ConvexHull(uv);
            if (hull.Count < 3) return 0f;
            float bestArea = float.MaxValue, bestAng = 0f;
            for (int i = 0; i < hull.Count; i++)
            {
                Vector2 e = hull[(i + 1) % hull.Count] - hull[i];
                if (e.sqrMagnitude < 1e-20f) continue;
                float a = Mathf.Atan2(e.y, e.x);
                float c = Mathf.Cos(-a), s = Mathf.Sin(-a);
                float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
                foreach (var p in hull)
                {
                    float x = p.x * c - p.y * s, y = p.x * s + p.y * c;
                    if (x < x0) x0 = x; if (x > x1) x1 = x;
                    if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
                float area = (x1 - x0) * (y1 - y0);
                if (area < bestArea) { bestArea = area; bestAng = (x1 - x0) >= (y1 - y0) ? a : a + Mathf.PI * 0.5f; }
            }
            return bestAng;
        }

        // Andrew 단조 체인(반시계, 공선 점 제외)
        static System.Collections.Generic.List<Vector2> ConvexHull(Vector2[] uv)
        {
            var p = (Vector2[])uv.Clone();
            System.Array.Sort(p, (a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            var h = new System.Collections.Generic.List<Vector2>(p.Length + 1);
            if (p.Length < 3) { h.AddRange(p); return h; }
            for (int i = 0; i < p.Length; i++)
            {
                while (h.Count >= 2 && Cross(h[h.Count - 2], h[h.Count - 1], p[i]) <= 0f) h.RemoveAt(h.Count - 1);
                h.Add(p[i]);
            }
            int lower = h.Count + 1;
            for (int i = p.Length - 2; i >= 0; i--)
            {
                while (h.Count >= lower && Cross(h[h.Count - 2], h[h.Count - 1], p[i]) <= 0f) h.RemoveAt(h.Count - 1);
                h.Add(p[i]);
            }
            h.RemoveAt(h.Count - 1);
            return h;
        }

        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
    }
}