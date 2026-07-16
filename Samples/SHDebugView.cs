using System;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper
{
    /// <summary>
    /// SH-4: per-instance SH9 육안 검증 뷰(에디터 기즈모). 파이프라인·GLSL·SSBO 무관.
    /// 각 인스턴스 대표점에 SH 를 방향별로 평가한 색을 표시 → 베이크 값이 그럴듯한지 확인.
    ///
    /// 모드:
    ///  - Point   : 대표점에 "위 노멀 조도색" 점 하나(빠른 개관).
    ///  - Cross   : ±X/±Y/±Z 6방향 조도색 십자(방향성 확인).
    ///  - Sphere  : 저해상도 구(Fibonacci) 각 방향 노멀 조도색(분포 확인).
    ///
    /// 데이터는 코드로 주입(SetData). 씬 오브젝트에 붙여 OnDrawGizmos 로 표시.
    /// 노출값(exposure)·감마로 HDR 조도를 눈에 맞게 보정(표시 전용).
    /// </summary>

    public class SHDebugView : MonoBehaviour
    {
        public enum Mode { Point, Cross, Sphere }

        [Header("표시")]
        public Mode mode = Mode.Cross;
        [Min(0.001f)]
        public float gizmoScale = 0.01f;
        [Tooltip("조도(π 스케일) 보정용. 1/π≈0.3 근처가 하늘색이 자연스럽게 보임. 1.0이면 흰색으로 클램프됨.")]
        [Min(0f)]
        public float exposure = 0.3f;
        public bool applyGamma = true;
        [Range(8, 128)]
        public int sphereDirs = 48;
        [Min(1)]
        public int MaxDraw = 2000; // 대량 인스턴스 성능 가드

        [SerializeField]
        Vector3[] _points;
        SH9[] _sh;

        /// 베이크 결과 주입. points/sh 는 인스턴스 순서 동일 (길이 일치).
        public void SetData(Vector3[] points, SH9[] sh)
        {
            _points = points;
            _sh = sh;
        }


        /// <summary>SHPacked 버퍼로부터 주입(GPU 업로드 전 CPU 배열).</summary>
        public void SetData(Vector3[] points, SHPacked[] packed)
        {
            _points = points;
            _sh = new SH9[packed.Length];
            for (int i = 0; i < packed.Length; i++)
            {
                _sh[i] = packed[i].Unpacked();
            }
        }

        void OnDrawGizmos()
        {
            if (_points == null || _points.Length == 0)
                return;
            if (_sh == null || _sh.Length == 0)
                return;
            int n = Mathf.Min(_points.Length, _sh.Length);
            int drawn = 0;
            for (int i = 0; i < n && drawn < MaxDraw; i++, drawn++)
            {
                Vector3 p = _points[i];
                switch (mode)
                {
                    case Mode.Point:
                        Gizmos.color = Tone(_sh[i].Evaluate(Vector3.up));
                        Gizmos.DrawSphere(p, gizmoScale);
                        break;

                    case Mode.Cross:
                        DrawDir(p, Vector3.up, _sh[i]);
                        DrawDir(p, Vector3.down, _sh[i]);
                        DrawDir(p, Vector3.left, _sh[i]);
                        DrawDir(p, Vector3.right, _sh[i]);
                        DrawDir(p, Vector3.forward, _sh[i]);
                        DrawDir(p, Vector3.back, _sh[i]);
                        break;

                    case Mode.Sphere:
                        var dirs = Fib(sphereDirs);
                        for (int d = 0; d < dirs.Length; d++)
                        {
                            Gizmos.color = Tone(_sh[i].Evaluate(dirs[d]));
                            Gizmos.DrawCube(p + dirs[d] * gizmoScale, Vector3.one * (gizmoScale * 0.25f));
                        }
                        break;
                }
            }
        }
        private void DrawDir(Vector3 p, Vector3 dir, SH9 sh)
        {
            Gizmos.color = Tone(sh.Evaluate(dir));
            Gizmos.DrawSphere(p + dir * gizmoScale, gizmoScale * 0.35f);
        }

        // HDR 조도 → 표시색(노출 + 옵션 감마 + 클램프)
        private Color Tone(Vector3 e)
        {

            Vector3 c = e * exposure;
            if (applyGamma)
            {
                float inv = 1f / 2.2f;
                c = new Vector3(Mathf.Pow(Mathf.Max(0, c.x), inv),
                                Mathf.Pow(Mathf.Max(0, c.y), inv),
                                Mathf.Pow(Mathf.Max(0, c.z), inv));
            }
            return new Color(Mathf.Clamp01(c.x), Mathf.Clamp01(c.y), Mathf.Clamp01(c.z), 1f);
        }

        private Vector3[] Fib(int n)
        {
            var pts = new Vector3[n];
            float g = Mathf.PI * (3f - Mathf.Sqrt(5f)); // 황금각 ≈ 2.39996 (√5, √0.5 아님)
            for (int i = 0; i < n; i++)
            {
                float y = 1f - (i + 0.5f) / n * 2f;
                float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float t = g * i;
                pts[i] = new Vector3(Mathf.Cos(t) * r, y, Mathf.Sin(t) * r);
            }
            return pts;
        }


    }
}
