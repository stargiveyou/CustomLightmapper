using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>차트 평탄화에 실제로 사용된 방법. 디버거/통계에서 경로 가시화용.</summary>
    public enum FlattenMethod
    {
        Planar, // 근평면 정사영 (무왜곡, foldover 불가) — 건물 prop 기본
        LSCM,   // 등각 자유경계 (약한 곡률) — 현재 서브분할 폴백 자리표시자
        MVC,    // 볼록 경계 + 양수 가중 → 전단사 보장 — 최종 안전망
    }

    /// <summary>
    /// 차트별 평탄화 디스패처. 기획서 계약(평면투영 &gt; LSCM &gt; MVC)을 차트마다 적용한다.
    ///
    ///   1) Planar 먼저 시도 (평면 차트면 여기서 끝, 무왜곡)
    ///   2) UVValidator.HasFoldover 면 LSCM (자유경계라 여전히 겹칠 수 있어 재검사)
    ///   3) 그래도 겹치면 MVC — 전단사 보장이므로 재검사 없이 종료
    ///
    /// MVC는 distortion을 주입하므로 주 방법이 아니라 '겹침 제거' 최후 폴백으로만 쓴다.
    /// </summary>
    public static class ChartFlattener
    {
        public static FlattenMethod Flatten(ref ChartMesh cm)
        {
            // 1) 근평면 정사영 — 평면 차트엔 이게 최적. foldover 불가.
            PlanarProjector.Projector(ref cm);
            if (!UVValidator.HasFoldover(cm)) return FlattenMethod.Planar;

            // 2) 곡률 차트 → LSCM (등각). 자유경계라 결과가 여전히 겹칠 수 있음.
            LSCMSolver.Solve(ref cm);
            if (!UVValidator.HasFoldover(cm)) return FlattenMethod.LSCM;

            // 3) 최후 폴백 → MVC. 볼록 경계 고정 + 양수 가중으로 전단사 보장.
            MVCFallback.Solve(ref cm);
            return FlattenMethod.MVC;
        }

        /// <summary>차트 배열 전체를 평탄화하고 차트별 사용 방법을 반환.</summary>
        public static FlattenMethod[] FlattenAll(ChartMesh[] charts)
        {
            var methods = new FlattenMethod[charts.Length];
            for (int i = 0; i < charts.Length; i++)
                methods[i] = Flatten(ref charts[i]);
            return methods;
        }
    }
}
