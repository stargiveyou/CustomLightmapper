using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using HuskyLibs.CustomLightmapper.Bake;

namespace HuskyLibs.CustomLightmapper.Bake.EditorTools
{
    /// <summary>
    /// A1~A3(ParameterizationPipeline) 결과의 차트별 UV 를 2D 캔버스에 그려
    /// 레이아웃·겹침(foldover/차트 간 overlap)을 육안으로 확인하는 에디터 윈도우.
    /// 메뉴: Tools ▸ HuskyLibs.CustomLightmapper.Bake ▸ UV Layout Viewer
    ///
    /// A4(패킹) 미구현이라 차트들이 UV 원점 근처에 겹쳐 그려지는 게 정상.
    /// 빨강 강조(foldover) 차트는 평탄화 단계에서 삼각형이 뒤집힌 차트.
    /// </summary>
    /// 
    /*
    메뉴: Tools ▸ HuskyLibs.CustomLightmapper.Bake ▸ UV Layout Viewer
    Mesh 슬롯에 대상 메시 지정 (예: Cube)
    Seam Angle / Max Chart Angle 조정 (실시간 반영은 Run 버튼)
    Run A1~A3 → 차트별 UV가 2D 캔버스에 그려집니다
    
    */
    public class UVLayoutViewer : EditorWindow
    {
        [MenuItem("Husky/Tools/UV Layout Viewer")]
        static void Open() => GetWindow<UVLayoutViewer>("UV Layout");

        Mesh _mesh;
        SegmentationSettings _settings = SegmentationSettings.Default;

        bool _drawFill = true;
        bool _drawEdges = true;
        bool _drawUnitSquare = true;
        bool _highlightFoldover = true;
        bool _applyA4 = false;       // A3 뒤에 A4(DensityNormalizer→ShelfPacker) 실행해 패킹 검증
        float _gutter = 0.01f;       // ShelfPacker 차트 간 여백
        [Range(0f, 1f)] float _fillAlpha = 0.25f;
        int _selectedChart = -1; // -1 = 전체

        // 파이프라인 결과 캐시
        ChartMesh[] _charts;
        FlattenMethod[] _methods;
        bool[] _foldover;
        Rect _uvBounds; // 전체 차트 UV 의 결합 바운드

        Material _glMat;
        Vector2 _scroll;

        void OnDisable()
        {
            if (_glMat != null) DestroyImmediate(_glMat);
        }

        void OnGUI()
        {
            DrawToolbar();
            DrawCanvas();
        }

        // ── 상단 컨트롤 ─────────────────────────────────────────
        void DrawToolbar()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginChangeCheck();
                _mesh = (Mesh)EditorGUILayout.ObjectField("Mesh", _mesh, typeof(Mesh), false);

                _settings.SeamAngleDeg = EditorGUILayout.Slider("Seam Angle", _settings.SeamAngleDeg, 1f, 180f);
                _settings.MaxChartAngleDeg = EditorGUILayout.Slider("Max Chart Angle", _settings.MaxChartAngleDeg, 1f, 180f);
                if (EditorGUI.EndChangeCheck()) { /* 값만 갱신, Run 은 버튼으로 */ }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Run A1~A3", GUILayout.Height(24))) Run();
                    if (GUILayout.Button("Clear", GUILayout.Width(60), GUILayout.Height(24))) Clear();
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    _drawFill = GUILayout.Toggle(_drawFill, "Fill", EditorStyles.miniButtonLeft);
                    _drawEdges = GUILayout.Toggle(_drawEdges, "Edges", EditorStyles.miniButtonMid);
                    _drawUnitSquare = GUILayout.Toggle(_drawUnitSquare, "0~1 Box", EditorStyles.miniButtonMid);
                    _highlightFoldover = GUILayout.Toggle(_highlightFoldover, "Foldover", EditorStyles.miniButtonRight);
                }
                _fillAlpha = EditorGUILayout.Slider("Fill Alpha", _fillAlpha, 0f, 1f);

                // A4 검증 토글: 켜고 Run 하면 A3 결과에 DensityNormalizer→ShelfPacker 를 적용해 본다.
                using (new EditorGUILayout.HorizontalScope())
                {
                    _applyA4 = GUILayout.Toggle(_applyA4, "Apply A4 (Normalize + Pack)", EditorStyles.miniButton);
                    using (new EditorGUI.DisabledScope(!_applyA4))
                        _gutter = EditorGUILayout.Slider("Gutter", _gutter, 0f, 0.1f);
                }

                // 차트 선택 드롭다운 (전체 / 개별)
                if (_charts != null && _charts.Length > 0)
                {
                    var names = new string[_charts.Length + 1];
                    names[0] = "All";
                    for (int i = 0; i < _charts.Length; i++)
                    {
                        string m = (_methods != null && i < _methods.Length) ? _methods[i].ToString() : "?";
                        string f = (_foldover != null && i < _foldover.Length && _foldover[i]) ? " [FOLD]" : "";
                        names[i + 1] = $"chart {i} ({m}){f}";
                    }
                    int sel = EditorGUILayout.Popup("Show", _selectedChart + 1, names);
                    _selectedChart = sel - 1;

                    int fold = 0;
                    if (_foldover != null) foreach (var b in _foldover) if (b) fold++;
                    EditorGUILayout.LabelField(
                        $"charts={_charts.Length}, foldover={fold}, uvBounds=({_uvBounds.xMin:0.00},{_uvBounds.yMin:0.00})~({_uvBounds.xMax:0.00},{_uvBounds.yMax:0.00})",
                        EditorStyles.miniLabel);

                    // mesh.bounds.size 와 비교용: UV(미터 단위)가 메시 크기와 자릿수가 맞아야 정상.
                    // uvSpan 이 meshSize 와 10배 이상 차이나면 foldover/노멀 퇴화 의심.
                    if (_mesh != null)
                    {
                        Vector3 sz = _mesh.bounds.size;
                        EditorGUILayout.LabelField(
                            $"mesh.bounds.size=({sz.x:0.00}, {sz.y:0.00}, {sz.z:0.00}), uvSpan=({_uvBounds.width:0.00} x {_uvBounds.height:0.00})",
                            EditorStyles.miniLabel);
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("Mesh 를 지정하고 Run A1~A3 을 누르세요.", EditorStyles.miniLabel);
                }
            }
        }

        // ── 파이프라인 실행 ─────────────────────────────────────
        void Run()
        {
            if (_mesh == null)
            {
                ShowNotification(new GUIContent("Mesh 가 없습니다"));
                return;
            }

            var pr = ParameterizationPipeline.Run(_mesh, _settings);
            _charts = pr.Charts;
            _methods = pr.Methods;
            _selectedChart = -1;

            if (_charts == null || _charts.Length == 0) { _foldover = null; return; }

            // A4 검증: A3 결과에 밀도 정규화 → 셸프 패킹 적용 (파이프라인엔 미편입, 뷰어 한정)
            if (_applyA4)
            {
                DensityNormalizer.Normalize(_charts);
                ShelfPacker.Pack(_charts, _gutter);
            }

            // 차트별 foldover 재판정 + 전체 UV 바운드 계산
            _foldover = new bool[_charts.Length];
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < _charts.Length; i++)
            {
                var cm = _charts[i];
                _foldover[i] = UVValidator.HasFoldover(cm);
                if (cm.UV == null) continue;
                foreach (var uv in cm.UV)
                {
                    if (uv.x < minX) minX = uv.x; if (uv.x > maxX) maxX = uv.x;
                    if (uv.y < minY) minY = uv.y; if (uv.y > maxY) maxY = uv.y;
                }
            }
            // 0~1 박스도 항상 보이도록 바운드에 포함
            minX = Mathf.Min(minX, 0f); minY = Mathf.Min(minY, 0f);
            maxX = Mathf.Max(maxX, 1f); maxY = Mathf.Max(maxY, 1f);
            _uvBounds = Rect.MinMaxRect(minX, minY, maxX, maxY);

            Repaint();
        }

        void Clear()
        {
            _charts = null; _methods = null; _foldover = null;
            _selectedChart = -1; _uvBounds = default;
            Repaint();
        }

        // ── 2D 캔버스 ──────────────────────────────────────────
        void DrawCanvas()
        {
            // 정사각형에 가까운 뷰 영역 확보
            Rect view = GUILayoutUtility.GetRect(position.width, position.height,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            EditorGUI.DrawRect(view, new Color(0.12f, 0.12f, 0.12f));

            if (_charts == null || Event.current.type != EventType.Repaint) return;

            EnsureMat();
            // UV 바운드를 view 안에 등비율로 맞추는 매핑 (정사각형 유지)
            float pad = 12f;
            Rect inner = new Rect(view.x + pad, view.y + pad, view.width - pad * 2, view.height - pad * 2);
            float span = Mathf.Max(_uvBounds.width, _uvBounds.height, 1e-4f);
            float scale = Mathf.Min(inner.width, inner.height) / span;

            Vector2 ToScreen(Vector2 uv)
            {
                float sx = inner.x + (uv.x - _uvBounds.xMin) * scale;
                float sy = inner.y + inner.height - (uv.y - _uvBounds.yMin) * scale; // y 뒤집기
                return new Vector2(sx, sy);
            }

            GL.PushMatrix();
            _glMat.SetPass(0);
            GL.LoadPixelMatrix();

            // 0~1 단위 박스 (아틀라스 목표 영역 기준선)
            if (_drawUnitSquare)
            {
                var c = new Color(1f, 1f, 1f, 0.35f);
                DrawRectLines(ToScreen(new Vector2(0, 0)), ToScreen(new Vector2(1, 0)),
                              ToScreen(new Vector2(1, 1)), ToScreen(new Vector2(0, 1)), c);
            }

            for (int i = 0; i < _charts.Length; i++)
            {
                if (_selectedChart >= 0 && i != _selectedChart) continue;
                var cm = _charts[i];
                if (cm.UV == null || cm.Triangles == null) continue;

                bool fold = _highlightFoldover && _foldover != null && i < _foldover.Length && _foldover[i];
                Color baseCol = fold ? new Color(1f, 0.2f, 0.2f) : ChartColor(i);

                // 채움 (반투명 → 겹치는 영역이 진하게 보임)
                if (_drawFill)
                {
                    GL.Begin(GL.TRIANGLES);
                    GL.Color(new Color(baseCol.r, baseCol.g, baseCol.b, _fillAlpha));
                    var t = cm.Triangles;
                    for (int k = 0; k < t.Length; k += 3)
                    {
                        Vector2 a = ToScreen(cm.UV[t[k]]);
                        Vector2 b = ToScreen(cm.UV[t[k + 1]]);
                        Vector2 c = ToScreen(cm.UV[t[k + 2]]);
                        GL.Vertex3(a.x, a.y, 0); GL.Vertex3(b.x, b.y, 0); GL.Vertex3(c.x, c.y, 0);
                    }
                    GL.End();
                }

                // 에지
                if (_drawEdges)
                {
                    GL.Begin(GL.LINES);
                    GL.Color(new Color(baseCol.r, baseCol.g, baseCol.b, 0.9f));
                    var t = cm.Triangles;
                    for (int k = 0; k < t.Length; k += 3)
                    {
                        Vector2 a = ToScreen(cm.UV[t[k]]);
                        Vector2 b = ToScreen(cm.UV[t[k + 1]]);
                        Vector2 c = ToScreen(cm.UV[t[k + 2]]);
                        Line(a, b); Line(b, c); Line(c, a);
                    }
                    GL.End();
                }
            }

            GL.PopMatrix();
        }

        // ── GL 헬퍼 ────────────────────────────────────────────
        static void Line(Vector2 a, Vector2 b)
        {
            GL.Vertex3(a.x, a.y, 0); GL.Vertex3(b.x, b.y, 0);
        }

        static void DrawRectLines(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Color c)
        {
            GL.Begin(GL.LINES);
            GL.Color(c);
            Line(p0, p1); Line(p1, p2); Line(p2, p3); Line(p3, p0);
            GL.End();
        }

        void EnsureMat()
        {
            if (_glMat != null) return;
            var shader = Shader.Find("Hidden/Internal-Colored");
            _glMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _glMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _glMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _glMat.SetInt("_Cull", (int)CullMode.Off);
            _glMat.SetInt("_ZWrite", 0);
        }

        // 차트 id 기반 결정적 색 (DebugColorize / 다른 디버거와 동일 규칙)
        static Color ChartColor(int id)
        {
            Random.State prev = Random.state;
            Random.InitState(id * 9973 + 1);
            Color c = Color.HSVToRGB(Random.value, 0.6f, 0.95f);
            Random.state = prev;
            return c;
        }
    }
}
