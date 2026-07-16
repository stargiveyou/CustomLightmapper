using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// LSCM(Least-Squares Conformal Map). 약한 곡률 차트용 등각 자유경계 평탄화.
    /// 각 삼각형 로컬 등거리 좌표 → Cauchy-Riemann 잔차의 희소 최소제곱을 조립하고,
    /// 외곽 루프 최원 2점을 pin 으로 고정해 AᵀA x = Aᵀb (sparse Cholesky)로 푼다.
    ///
    /// [STUB] 아직 미구현. 현재는 서브분할 폴백 자리표시자로, UV 를 건드리지 않아
    /// (= 직전 Planar UV 유지) HasFoldover 가 그대로 true 면 디스패처가 MVC 로 넘어간다.
    /// 실제 솔버가 들어오면 이 시그니처에 그대로 채우면 된다.
    /// </summary>
    public static class LSCMSolver
    {
        static bool _warned;

        public static void Solve(ref ChartMesh cm)
        {
            // TODO: 등각 최소제곱 조립 + 2-pin + sparse Cholesky.
            // 현재는 no-op (Planar UV 유지) → foldover 시 MVC 로 폴백되도록 둔다.
            if (!_warned)
            {
                Debug.LogWarning("[LSCMSolver] 미구현 stub — Planar UV 유지(폴백은 MVC가 처리). chart " + cm.chartID);
                _warned = true;
            }
        }
    }
}
