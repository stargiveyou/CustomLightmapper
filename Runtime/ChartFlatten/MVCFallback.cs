using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// MVC(Mean Value Coordinates) 폴백. foldover(겹침)가 끝까지 남았을 때의 최후 안전망.
    /// 외곽 루프를 볼록 다각형(원/사각)에 고정하고, 내부 정점은 항상 양수인 MVC 가중
    ///   w_ij = (tan(αij/2)+tan(βij/2)) / |vi-vj|
    /// 으로 이웃의 볼록결합 u_i = Σ_j (w_ij/Σw) u_j 를 선형 solve → 전단사(겹침 제거) 보장.
    ///
    /// [STUB] 아직 미구현. 디스패처 체인의 종착점이므로, 채워질 때까지는
    /// Planar/LSCM 결과(여전히 겹칠 수 있음)를 그대로 둔다.
    /// </summary>
    public static class MVCFallback
    {
        static bool _warned;

        public static void Solve(ref ChartMesh cm)
        {
            // TODO: 외곽 루프(cm.Loops[0]) 볼록 경계 고정 → 내부 MVC 가중 선형 solve.
            if (!_warned)
            {
                Debug.LogWarning("[MVCFallback] 미구현 stub — 겹침이 남은 채로 둔다. chart " + cm.chartID);
                _warned = true;
            }
        }
    }
}
