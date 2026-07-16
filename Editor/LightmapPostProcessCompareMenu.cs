#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake.EditorTools
{
    public static class LightmapPostProcessCompareMenu
    {
        [MenuItem("HuskyLibs/CustomLightmapper/PostCompare Dilate (Burst vs Serial)")]
        public static void Run()
        {
            string log = LightmapPostProcessCompare.RunAll();
            if (log.Contains("[FAIL]")) Debug.LogError(log);
            else Debug.Log(log);
        }
    }
}
#endif