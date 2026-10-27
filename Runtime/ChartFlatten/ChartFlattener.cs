using System;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>차트 평탄화에 실제로 사용된 방법. 디버거/통계에서 경로 가시화용.</summary>
    public enum FlattenMethod
    {
        Planar, // 근평면 정사영 (무왜곡, foldover 불가) — 건물 prop 기본
        LSCM,   // 등각 자유경계 (곡률 차트) — 2점 고정 + CG
        MVC,    // 볼록 경계 + 양수 가중 → 전단사 보장 — 최종 안전망
    }

    /// <summary>
    /// 평탄화 디스패치 설정. 왜곡 척도 = L2 stretch(Sander 2001, 면적 정규화 — 등거리 = 1, 균일 스케일 무관).
    /// </summary>
    [Serializable]
    public struct FlattenSettings
    {
        [Tooltip("평면투영 결과가 겹침 없음 ∧ L2 stretch ≤ 이 값이면 Planar 유지(무왜곡 차트는 1). 넘으면 LSCM 을 풀어 왜곡이 더 작은 쪽을 채택. " +
                 "원통 호 기준 평면투영 L2: 60° ≈ 1.002 · 75° ≈ 1.005 · 90° ≈ 1.012 · 120° ≈ 1.047. Infinity = 겹침일 때만 LSCM(구 디스패치).")]
        public float PlanarStretchThreshold;
        [Tooltip("LSCM(등각 자유경계) 사용.")]
        public bool EnableLSCM;
        [Tooltip("MVC(볼록 경계 고정, 전단사) 최종 폴백 사용.")]
        public bool EnableMVC;
        [Tooltip("반복 솔버 최대 반복(0 = 자동: 미지수 수 기반).")]
        [Min(0)] public int MaxIterations;
        [Tooltip("반복 솔버 상대 잔차 허용치(0 이하 = 1e-10).")]
        public double Tolerance;

        public static FlattenSettings Default => new FlattenSettings
        {
            PlanarStretchThreshold = 1.005f,   // ≈ 원통 75° 호(가장자리 늘어남 ~26%)부터 LSCM
            EnableLSCM = true,
            EnableMVC = true,
            MaxIterations = 0,
            Tolerance = 1e-10,
        };

        /// <summary>구 디스패치(겹침일 때만 LSCM) — 회귀 비교용.</summary>
        public static FlattenSettings FoldoverOnly
        {
            get { var s = Default; s.PlanarStretchThreshold = float.PositiveInfinity; return s; }
        }

        internal int Iterations(int unknowns) => MaxIterations > 0 ? MaxIterations : Mathf.Max(200, 4 * unknowns);
        internal double Tol => Tolerance > 0.0 ? Tolerance : 1e-10;
    }

    /// <summary>차트 1개 평탄화 기록. Stretch 는 돌리지 않은 단계면 NaN.</summary>
    public struct FlattenStats
    {
        public FlattenMethod Method;
        public float PlanarStretch, LscmStretch, MvcStretch;
        public bool PlanarFoldover, LscmFoldover, MvcFoldover;
        public bool LscmSolved, MvcSolved;          // 솔버가 해를 냈는지(경계 없음·위상 불가·수렴 실패면 false)
        public int LscmIterations, MvcIterations;

        public float FinalStretch => Method == FlattenMethod.Planar ? PlanarStretch : Method == FlattenMethod.LSCM ? LscmStretch : MvcStretch;

        internal static FlattenStats Empty => new FlattenStats
        {
            PlanarStretch = float.NaN, LscmStretch = float.NaN, MvcStretch = float.NaN,
        };

        public override string ToString() =>
            $"{Method} stretch planar={PlanarStretch:0.000}{(PlanarFoldover ? "(fold)" : "")}" +
            (float.IsNaN(LscmStretch) ? "" : $" lscm={LscmStretch:0.000}{(LscmFoldover ? "(fold)" : "")} it={LscmIterations}") +
            (float.IsNaN(MvcStretch) ? "" : $" mvc={MvcStretch:0.000}{(MvcFoldover ? "(fold)" : "")} it={MvcIterations}");
    }

    /// <summary>
    /// 차트별 평탄화 디스패처. 기획서 계약(평면투영 &gt; LSCM &gt; MVC)을 차트마다 적용한다.
    ///
    ///   1) Planar 먼저 — 겹침 없음 ∧ L2 stretch ≤ 임계(기본 1.005)면 끝. 평면 차트는 stretch = 1 → 기존과 같은 결과.
    ///   2) LSCM — 겹침이 없고 (Planar 가 겹쳤거나 stretch 가 더 작으면) 채택. 아니면 Planar 가 유효했을 때 Planar 로 복귀.
    ///   3) Planar·LSCM 이 모두 겹치면 MVC — 원판 위상이면 전단사 보장. 그래도 겹치면(비원판 등) 뒤집힘이 가장 적은 후보.
    ///
    /// MVC는 왜곡을 주입하므로 주 방법이 아니라 '겹침 제거' 최후 폴백으로만 쓴다.
    /// 솔버는 로그를 남기지 않는다 — 결과는 <see cref="FlattenStats"/>(파이프라인·디버거가 집계).
    /// </summary>
    public static class ChartFlattener
    {
        public static FlattenMethod Flatten(ref ChartMesh cm) => Flatten(ref cm, FlattenSettings.Default, out _);

        public static FlattenMethod Flatten(ref ChartMesh cm, in FlattenSettings s, out FlattenStats st)
        {
            st = FlattenStats.Empty;

            // 1) 근평면 정사영 — 평면 차트엔 이게 최적. foldover 불가.
            PlanarProjector.Projector(ref cm);
            var planarUV = cm.UV;
            var rp = UVValidator.Validate(cm);
            st.PlanarFoldover = rp.HasFoldover;
            st.PlanarStretch = UVValidator.Stretch(cm);
            if (!rp.HasFoldover && st.PlanarStretch <= s.PlanarStretchThreshold) return st.Method = FlattenMethod.Planar;

            // 2) 곡률 차트 → LSCM (등각). 자유경계라 결과가 여전히 겹칠 수 있음 → 재검사.
            Vector2[] lscmUV = null;
            UVReport rl = default;
            if (s.EnableLSCM && LSCMSolver.Solve(ref cm, s, out st.LscmIterations))
            {
                st.LscmSolved = true;
                lscmUV = cm.UV;
                rl = UVValidator.Validate(cm);
                st.LscmFoldover = rl.HasFoldover;
                st.LscmStretch = UVValidator.Stretch(cm);
                if (!rl.HasFoldover && (rp.HasFoldover || st.LscmStretch < st.PlanarStretch)) return st.Method = FlattenMethod.LSCM;
            }
            if (!rp.HasFoldover) { cm.UV = planarUV; return st.Method = FlattenMethod.Planar; }

            // 3) 최후 폴백 → MVC. 볼록 경계 고정 + 양수 가중 → 원판 위상이면 전단사.
            Vector2[] mvcUV = null;
            UVReport rm = default;
            if (s.EnableMVC && MVCFallback.Solve(ref cm, s, out st.MvcIterations))
            {
                st.MvcSolved = true;
                mvcUV = cm.UV;
                rm = UVValidator.Validate(cm);
                st.MvcFoldover = rm.HasFoldover;
                st.MvcStretch = UVValidator.Stretch(cm);
                if (!rm.HasFoldover) return st.Method = FlattenMethod.MVC;
            }

            // 모두 겹침 — 뒤집힌 삼각형이 가장 적은 후보(동률이면 Planar → LSCM → MVC 순)
            var bestUV = planarUV;
            var best = FlattenMethod.Planar;
            int bestFlip = rp.Flipped;
            if (lscmUV != null && rl.Flipped < bestFlip) { bestUV = lscmUV; best = FlattenMethod.LSCM; bestFlip = rl.Flipped; }
            if (mvcUV != null && rm.Flipped < bestFlip) { bestUV = mvcUV; best = FlattenMethod.MVC; }
            cm.UV = bestUV;
            return st.Method = best;
        }

        /// <summary>차트 배열 전체를 평탄화하고 차트별 사용 방법을 반환.</summary>
        public static FlattenMethod[] FlattenAll(ChartMesh[] charts) => FlattenAll(charts, FlattenSettings.Default, out _);

        public static FlattenMethod[] FlattenAll(ChartMesh[] charts, in FlattenSettings s, out FlattenStats[] stats)
        {
            var methods = new FlattenMethod[charts.Length];
            stats = new FlattenStats[charts.Length];
            for (int i = 0; i < charts.Length; i++)
                methods[i] = Flatten(ref charts[i], s, out stats[i]);
            return methods;
        }
    }
}
