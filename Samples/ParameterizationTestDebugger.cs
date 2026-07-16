using System.Text;
using UnityEngine;
using HuskyLibs.CustomLightmapper.Bake;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 입력 메시를 파라미터화 파이프라인(세그멘테이션 → 차트 메시 → 평면 투영 → UV 검사)에
    /// 통과시켜 각 차트의 경계 루프·foldover·UV 면적을 점검하는 디버그 컴포넌트.
    /// 컴포넌트를 붙이고 인스펙터 우클릭 → "Run Parameterization Check" 로 실행한다.
    /// </summary>
    [ExecuteAlways]
    public class ParameterizationTestDebugger : MonoBehaviour
    {
        [Tooltip("비우면 같은 오브젝트의 MeshFilter.sharedMesh 를 사용.")]
        [SerializeField] Mesh targetMesh;

        public SegmentationSettings settings = SegmentationSettings.Default;

        [Header("Debug View")]
        [Tooltip("선택 시 씬뷰에 차트별 경계 루프를 폴리라인으로 그린다.")]
        public bool drawChartLoops = true;
        [Tooltip("각 차트의 외곽 루프 Loops[0] 색.")]
        public Color outerLoopColor = Color.cyan;
        [Tooltip("내부 홀 루프 Loops[1+] 색.")]
        public Color holeLoopColor = Color.magenta;
        [Tooltip("foldover(겹침) 가 검출된 차트는 이 색으로 강조.")]
        public Color foldoverColor = Color.red;
        [Tooltip("루프 라인을 차트 노멀 방향으로 띄워 면과의 z-fighting 을 줄인다.")]
        public float loopOffset = 0.002f;

        [Header("Result (read-only)")]
        [SerializeField] int chartCount;
        [SerializeField] int foldoverCharts;
        [SerializeField] int degenerateTriangles;

        // 기즈모용 결과 캐시. he 는 실행 직후 Dispose 하므로 ChartMesh 만 보관한다.
        ChartMesh[] cachedCharts;
        bool[] cachedFoldover;            // 차트별 foldover 여부
        FlattenMethod[] cachedMethods;    // 차트별 사용된 평탄화 방법

        Mesh ResolveMesh()
        {
            if (targetMesh != null) return targetMesh;
            var mf = GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        [ContextMenu("Run Parameterization Check")]
        public void Run()
        {
            var mesh = ResolveMesh();
            if (mesh == null)
            {
                Debug.LogWarning("[ParamCheck] 대상 Mesh 가 없습니다. targetMesh 를 지정하거나 MeshFilter 를 붙이세요.", this);
                return;
            }

            // WeldedHalfEdge 빌드 → 세그멘테이션 → 차트 메시/루프 추출 → 평면 투영
            var he = new WeldedHalfEdge(mesh);
            try
            {
                var seg = ChartSegementer.GetResult(he, settings);
                cachedCharts = ChartMeshBuilder.BuildAll(he, seg);   // ChartMesh 가 데이터를 복사 보관
                cachedMethods = ChartFlattener.FlattenAll(cachedCharts); // Planar→(foldover)LSCM→MVC 디스패치
            }
            finally
            {
                he.Dispose(); // WeldedHalfEdge NativeArray 해제
            }

            AnalyzeAndReport(mesh);
        }

        // 차트별 UV 검사 결과를 집계하고 Console 에 출력. 기즈모용 플래그도 채운다.
        void AnalyzeAndReport(Mesh mesh)
        {
            chartCount = cachedCharts.Length;
            cachedFoldover = new bool[chartCount];
            foldoverCharts = 0;
            degenerateTriangles = 0;

            var sb = new StringBuilder();
            sb.AppendLine($"=== Parameterization Check: '{mesh.name}' (charts={chartCount}) ===");
            for (int i = 0; i < cachedCharts.Length; i++)
            {
                var cm = cachedCharts[i];
                var report = UVValidator.Validate(cm);
                cachedFoldover[i] = report.HasFoldover;
                if (report.HasFoldover) foldoverCharts++;
                degenerateTriangles += report.Degenerate;

                int loopCnt = cm.Loops != null ? cm.Loops.Count : 0;
                int outerVerts = (loopCnt > 0) ? cm.Loops[0].Length : 0;
                string method = (cachedMethods != null && i < cachedMethods.Length) ? cachedMethods[i].ToString() : "?";
                sb.AppendLine($"[{(report.HasFoldover ? "FOLD" : " OK ")}] chart {i}: via={method}, loops={loopCnt}, outerVerts={outerVerts}, {report}");
            }
            sb.AppendLine($"--- foldoverCharts={foldoverCharts}, degenerateTriangles={degenerateTriangles}");

            if (foldoverCharts > 0)
                Debug.LogWarning(sb.ToString(), this);
            else
                Debug.Log(sb.ToString(), this);
        }

        [ContextMenu("Clear Result")]
        public void ClearResult()
        {
            cachedCharts = null;
            cachedFoldover = null;
            cachedMethods = null;
            chartCount = 0;
            foldoverCharts = 0;
            degenerateTriangles = 0;
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            if (!drawChartLoops || cachedCharts == null) return;

            Matrix4x4 m = transform.localToWorldMatrix;
            for (int i = 0; i < cachedCharts.Length; i++)
            {
                var cm = cachedCharts[i];
                if (cm?.Loops == null || cm.positions == null) continue;

                bool fold = cachedFoldover != null && i < cachedFoldover.Length && cachedFoldover[i];
                Vector3 off = cm.PlaneNormal * loopOffset; // 면 위로 살짝 띄움
                var pos = cm.positions;
                for (int li = 0; li < cm.Loops.Count; li++)
                {
                    // foldover 차트는 전부 강조색, 정상 차트는 외곽/홀 구분색
                    Gizmos.color = fold ? foldoverColor : (li == 0 ? outerLoopColor : holeLoopColor);
                    int[] loop = cm.Loops[li];
                    for (int k = 0; k < loop.Length; k++)
                    {
                        Vector3 a = m.MultiplyPoint3x4(pos[loop[k]] + off);
                        Vector3 b = m.MultiplyPoint3x4(pos[loop[(k + 1) % loop.Length]] + off);
                        Gizmos.DrawLine(a, b);
                    }
                }
            }
        }
#endif
    }
}
