using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// PathTrace.compute 로더(GPU 베이크 공용). 우선순위: 컴포넌트 지정 → Resources("PathTrace") → (에디터) 패키지 경로.
    /// 패키지에선 셰이더가 `Shaders/Resources/` 에 있어 Resources.Load 로 에디터·빌드 공통 로드된다(패키지 0.14 규약).
    /// 에디터 경로 폴백은 Resources 배치가 깨졌을 때의 안전망.
    /// </summary>
    public static class PathTraceShader
    {
        public const string EditorAssetPath = "Packages/com.huskylibs.customlightmapper/Shaders/Resources/PathTrace.compute";

        public static ComputeShader Resolve(ComputeShader assigned = null)
        {
            if (assigned != null) return assigned;
            var cs = Resources.Load<ComputeShader>("PathTrace");
#if UNITY_EDITOR
            if (cs == null) cs = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(EditorAssetPath);
#endif
            return cs;
        }
    }
}
