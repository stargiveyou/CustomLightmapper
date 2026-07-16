using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// A1~A3 파라미터화 결과. (A4 Pack / A5 Assemble 은 후속 단계라 여기엔 없음)
    /// 차트마다 UV 가 채워진 ChartMesh[] 가 핵심 산출물.
    /// </summary>
    public struct ParamResult
    {
        public ChartMesh[] Charts;       // A2~A3 결과 (각 차트 UV 채워짐, 경계 루프 포함)
        public FlattenMethod[] Methods;  // A3 에서 차트별 실제 사용된 평탄화 방법
        public int ChartCount;
        public int FoldoverCharts;       // A3 후에도 남은 겹침(foldover) 차트 수 (정상=0)
    }

    /// <summary>
    /// 파라미터화 파이프라인 드라이버 (Track A: A1~A3).
    /// 입력 Mesh → 차트별로 평탄화된 UV 를 가진 ChartMesh[] 까지 구동한다.
    ///
    ///   A1  he   = new WeldedHalfEdge(mesh)            // 용접 + 하프에지 위상 빌드
    ///   A2  seg  = ChartSegementer.GetResult(he, seg)  // 시임/차트 분할
    ///       chs  = ChartMeshBuilder.BuildAll(he, seg)  // 차트-로컬 메시 + 경계 루프 추출
    ///   A3  mth  = ChartFlattener.FlattenAll(chs)      // Planar →(foldover) LSCM → MVC 디스패치
    ///
    /// A4(밀도 정규화/Pack), A5(UV2 메시 Assemble)는 후속 모듈에서 ParamResult.Charts 를 받아 처리.
    /// </summary>
    public static class ParameterizationPipeline
    {
        public static ParamResult Run(Mesh sourceMesh, SegmentationSettings seg)
        {
            if (sourceMesh == null)
            {
                Debug.LogWarning("[ParamPipeline] sourceMesh 가 null 입니다.");
                return new ParamResult { Charts = System.Array.Empty<ChartMesh>() };
            }

            // A1) 용접 + 하프에지 빌드. NativeArray 를 쓰므로 반드시 Dispose.
            var he = new WeldedHalfEdge(sourceMesh);
            try
            {
                // A2) 차트 분할 → 차트-로컬 메시/경계 루프 추출
                var s = ChartSegementer.GetResult(he, seg);
                var charts = ChartMeshBuilder.BuildAll(he, s);

                // A3) 차트별 평탄화 (Planar 우선, foldover 시 LSCM → 최후 MVC)
                var methods = ChartFlattener.FlattenAll(charts);

                // 평탄화 후에도 남은 겹침 집계 (정상 파이프라인이면 0)
                int foldovers = 0;
                for (int i = 0; i < charts.Length; i++)
                    if (UVValidator.HasFoldover(charts[i])) foldovers++;

                return new ParamResult
                {
                    Charts = charts,
                    Methods = methods,
                    ChartCount = charts.Length,
                    FoldoverCharts = foldovers,
                };
            }
            finally
            {
                he.Dispose(); // A1 에서 잡은 NativeArray 해제
            }
        }
    }
}
