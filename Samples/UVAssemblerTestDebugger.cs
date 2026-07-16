using System.Collections.Generic;
using System.Text;
using UnityEngine;
using HuskyLibs.CustomLightmapper.Bake;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// A1~A3(ParameterizationPipeline) → A5(UVAssembly.Assemble) 까지 구동해
    /// 조립된 uv2 메시와 SeamTable 의 정합성을 점검하는 디버그 컴포넌트.
    /// 컴포넌트를 붙이고 인스펙터 우클릭 → "Run UV Assemble" 로 실행한다.
    ///
    /// 주의: A4(밀도 정규화/패킹)가 아직 없어 차트들이 UV 공간에서 겹친다.
    /// 여기선 '조립 정합성'(정점 분리·uv2 채움·삼각형 보존·시임 묶음)만 검증한다.
    /// </summary>
    [ExecuteAlways]
    public class UVAssemblerTestDebugger : MonoBehaviour
    {
        [Tooltip("비우면 같은 오브젝트의 MeshFilter.sharedMesh 를 사용.")]
        [SerializeField] Mesh targetMesh;

        public SegmentationSettings settings = SegmentationSettings.Default;

        [Header("Apply")]
        [Tooltip("조립된 uv2 메시를 같은 오브젝트 MeshFilter 에 적용(육안 확인용).")]
        [SerializeField] bool applyToMeshFilter = false;
        [Tooltip("uv2 를 씬에서 보려고 uv0(메인 UV)에도 복사해 적용.")]
        [SerializeField] bool copyUV2ToUV0 = false;

        [Header("Result (read-only)")]
        [SerializeField] int chartCount;
        [SerializeField] int foldoverCharts;
        [SerializeField] int srcVertexCount;       // 원본 메시 정점 수
        [SerializeField] int expectedVertexCount;  // Σ chart.positions (시임 분리 후 기대 정점 수)
        [SerializeField] int assembledVertexCount; // 실제 조립 메시 정점 수
        [SerializeField] int srcTriangleCount;
        [SerializeField] int assembledTriangleCount;
        [SerializeField] int seamGroups;           // 2개 이상 정점이 묶인 시임 그룹 수
        [SerializeField] int seamVertices;         // 시임 그룹에 속한 총 조립 정점 수
        [SerializeField] bool uv2Filled;           // uv2 채널이 정점 수만큼 채워졌는가

        Mesh ResolveMesh()
        {
            if (targetMesh != null) return targetMesh;
            var mf = GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        [ContextMenu("Run UV Assemble")]
        public void Run()
        {
            var mesh = ResolveMesh();
            if (mesh == null)
            {
                Debug.LogWarning("[UVAssemble] 대상 Mesh 가 없습니다. targetMesh 를 지정하거나 MeshFilter 를 붙이세요.", this);
                return;
            }

            // A1~A3: 차트별 평탄화된 UV 까지 생성
            var pr = ParameterizationPipeline.Run(mesh, settings);
            if (pr.Charts == null || pr.Charts.Length == 0)
            {
                Debug.LogWarning("[UVAssemble] 차트가 0개입니다. 세그멘테이션/입력 메시를 확인하세요.", this);
                return;
            }

            // A5: 차트 → uv2 메시 + SeamTable 조립 (A4 패킹은 미구현 → 차트가 UV 공간에서 겹칠 수 있음)
            var (m, seams) = UVAssembly.Assemble(pr.Charts, mesh);

            AnalyzeAndReport(mesh, pr, m, seams);

            if (applyToMeshFilter) ApplyToMeshFilter(m);
        }

        void AnalyzeAndReport(Mesh src, ParamResult pr, Mesh m, SeamTable seams)
        {
            chartCount = pr.ChartCount;
            foldoverCharts = pr.FoldoverCharts;

            // 기대 정점 수 = 차트 로컬 정점 합 (시임에서 분리되어 복제됨)
            expectedVertexCount = 0;
            int expectedTris = 0;
            for (int i = 0; i < pr.Charts.Length; i++)
            {
                expectedVertexCount += pr.Charts[i].positions.Length;
                expectedTris += pr.Charts[i].Triangles.Length / 3;
            }

            srcVertexCount = src.vertexCount;
            assembledVertexCount = m.vertexCount;
            srcTriangleCount = src.triangles.Length / 3;
            assembledTriangleCount = m.triangles.Length / 3;

            // uv2 = 채널 1 (Unity 라이트맵 UV) — UVAssembly 가 SetUVs(1, ...) 로 채움
            var uv2 = new List<Vector2>();
            m.GetUVs(1, uv2);
            uv2Filled = uv2.Count == m.vertexCount && m.vertexCount > 0;

            // 시임 통계
            seamGroups = seams != null ? seams.Groups.Count : 0;
            seamVertices = 0;
            bool seamGroupsValid = true;
            int maxSeamIndex = -1;
            if (seams != null)
            {
                foreach (var g in seams.Groups)
                {
                    if (g.Length < 2) seamGroupsValid = false; // 시임 그룹은 정의상 2개 이상
                    seamVertices += g.Length;
                    for (int k = 0; k < g.Length; k++)
                        if (g[k] > maxSeamIndex) maxSeamIndex = g[k];
                }
            }

            // ── 검증 (정합성 invariant) ──
            bool vCountOk = assembledVertexCount == expectedVertexCount;
            bool tCountOk = assembledTriangleCount == expectedTris;
            bool seamIndexOk = maxSeamIndex < assembledVertexCount; // 시임 인덱스가 메시 범위 내
            bool degenerateUV = AllUVSame(uv2);                     // uv2 가 전부 동일하면 평탄화 실패 의심

            var sb = new StringBuilder();
            sb.AppendLine($"=== UV Assemble Check: '{src.name}' (charts={chartCount}) ===");
            sb.AppendLine($"[{Mark(vCountOk)}] vertices  : assembled={assembledVertexCount}, expected(Σchart)={expectedVertexCount}, src={srcVertexCount}");
            sb.AppendLine($"[{Mark(tCountOk)}] triangles : assembled={assembledTriangleCount}, expected={expectedTris}, src={srcTriangleCount}");
            sb.AppendLine($"[{Mark(uv2Filled)}] uv2       : count={uv2.Count} (vertexCount={m.vertexCount})");
            sb.AppendLine($"[{Mark(!degenerateUV)}] uv2 spread: {(degenerateUV ? "DEGENERATE(전부 동일)" : "OK")}");
            sb.AppendLine($"[{Mark(seamGroupsValid)}] seam grps : {seamGroups} groups, {seamVertices} verts, maxIdx={maxSeamIndex}");
            sb.AppendLine($"[{Mark(seamIndexOk)}] seam range: maxIdx < vertexCount ({maxSeamIndex} < {assembledVertexCount})");
            sb.AppendLine($"     foldoverCharts={foldoverCharts} (A4 패킹 미구현이라 차트는 UV 공간에서 겹칠 수 있음)");

            bool allOk = vCountOk && tCountOk && uv2Filled && !degenerateUV && seamGroupsValid && seamIndexOk;
            if (allOk) Debug.Log(sb.ToString(), this);
            else Debug.LogWarning(sb.ToString(), this);
        }

        // uv2 가 전부 같은 값이면(차트 평탄화/조립 실패) degenerate 로 본다.
        static bool AllUVSame(List<Vector2> uv)
        {
            if (uv.Count < 2) return false;
            Vector2 first = uv[0];
            for (int i = 1; i < uv.Count; i++)
                if ((uv[i] - first).sqrMagnitude > 1e-12f) return false;
            return true;
        }

        static string Mark(bool ok) => ok ? " OK " : "FAIL";

        void ApplyToMeshFilter(Mesh m)
        {
            var mf = GetComponent<MeshFilter>();
            if (mf == null)
            {
                Debug.LogWarning("[UVAssemble] applyToMeshFilter=true 인데 MeshFilter 가 없습니다.", this);
                return;
            }

            if (copyUV2ToUV0)
            {
                var uv2 = new List<Vector2>();
                m.GetUVs(1, uv2);
                m.SetUVs(0, uv2); // 메인 UV 로 복사 → 기본 셰이더에서 uv2 레이아웃 육안 확인
            }
            mf.sharedMesh = m;
        }

        [ContextMenu("Clear Result")]
        public void ClearResult()
        {
            chartCount = foldoverCharts = 0;
            srcVertexCount = expectedVertexCount = assembledVertexCount = 0;
            srcTriangleCount = assembledTriangleCount = 0;
            seamGroups = seamVertices = 0;
            uv2Filled = false;
        }
    }
}
