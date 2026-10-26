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

        /// <summary>
        /// 라이트맵 조도 커널(CSRadiance / CSRadianceAccum) 공통 바인딩: 씬 SRV·조명·α + 품질·태양·하늘 uniform.
        /// uniform 규약은 BurstRadianceBaker 정합. 입출력 버퍼·_Count·_Base 는 호출측.
        /// </summary>
        public static void BindRadiance(ComputeShader cs, int kernel, GpuScene gpuScene, DirectionalLight sun, BurstSky sky, BakeQualitySettings q)
        {
            gpuScene.Bind(cs, kernel);          // 순회 SRV + _TlasCount
            gpuScene.BindLighting(cs, kernel);  // _InstNormals, _MeshAlbedo
            gpuScene.BindAlpha(cs, kernel);     // α: 알파 컷아웃(꺼진 씬은 _AlphaEnabled=0 → 무영향)
            cs.SetInt("_IndirectSamples", q.IndirectSamples);
            cs.SetInt("_MaxBounces", q.MaxBounces);
            cs.SetInt("_RRStartDepth", q.RRStartDepth);
            cs.SetFloat("_RayBias", q.RayBias);
            cs.SetVector("_SunDir", sun.Direction);
            cs.SetVector("_SunColor", sun.Color);
            cs.SetFloat("_SunIntensity", sun.Intensity);
            // 태양 원반 샘플링(1이면 셰이더가 기존 단발 경로를 탄다)
            cs.SetInt("_DirectSamples", q.DirectSamples);
            cs.SetFloat("_SunHalfAngle", sun.AngularDiameterDeg * 0.5f * Mathf.Deg2Rad);
            cs.SetInt("_SkyType", sky.Type);
            cs.SetVector("_SkyTop", sky.A);
            cs.SetVector("_SkyBottom", sky.B);
        }
    }
}
