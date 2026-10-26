#if UNITY_EDITOR
using UnityEditor;

namespace HuskyLibs.CustomLightmapper.Bake.EditorTools
{
    /// <summary>
    /// AtlasApplyDebug 인스펙터 우클릭 메뉴 — 백엔드 교차검증(AtlasApplyDiffTests) 3종 + SSAA 자체테스트.
    /// 구 AtlasApplyDebug 내장 [ContextMenu] 와 같은 메뉴명. 베이커 코드에는 테스트가 남지 않는다.
    /// </summary>
    public static class AtlasApplyDiffTestsMenu
    {
        [MenuItem("CONTEXT/AtlasApplyDebug/Radiance Diff Test (BruteForce vs BVH)")]
        static void RadianceDiff(MenuCommand c) => AtlasApplyDiffTests.RadianceDiff((AtlasApplyDebug)c.context);

        [MenuItem("CONTEXT/AtlasApplyDebug/RadianceGI Backend Diff (CPU vs Burst)")]
        static void RadianceGiBackendDiff(MenuCommand c) => AtlasApplyDiffTests.RadianceGiBackendDiff((AtlasApplyDebug)c.context);

        [MenuItem("CONTEXT/AtlasApplyDebug/RadianceGI Backend Diff (Burst vs GPU)")]
        static void RadianceGiBackendDiffGpu(MenuCommand c) => AtlasApplyDiffTests.RadianceGiBackendDiffGpu((AtlasApplyDebug)c.context);

        // SSAA 기하/로직 자체테스트(레이 없음) — 패키지에서 AtlasApplyDebug 내장 [ContextMenu] 였던 것을 테스트 분리 규약대로 이동(같은 메뉴명)
        [MenuItem("CONTEXT/AtlasApplyDebug/SSAA Self-Tests")]
        static void SsaaSelfTests(MenuCommand c) => UnityEngine.Debug.Log(LightmapSSAATests.RunAll(), c.context);
    }
}
#endif
