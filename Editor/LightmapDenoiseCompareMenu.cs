#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake.EditorTools
{
    public static class LightmapDenoiseCompareMenu
    {
        [MenuItem("HuskyLibs/CustomLightmapper/PostCompare Denoise (Burst vs Serial + Quality)")]
        public static void Run()
        {
            string log = LightmapDenoiseCompare.RunAll();
            if (log.Contains("[FAIL]")) Debug.LogError(log);
            else Debug.Log(log);
        }
    }
}
#endif
