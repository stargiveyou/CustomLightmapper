using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 베이크 결과 저장 공용(InstanceSceneBaker · AtlasApplyDebug).
    /// EXR 인코딩은 런타임에서도 동작, 파일 쓰기·임포트 설정·폴더 생성은 에디터 전용.
    /// </summary>
    public static class BakeOutputUtil
    {
        /// <summary>선형 float 픽셀 → EXR 바이트. fullFloat 면 32-bit(SH 계수 등 데이터), 아니면 16-bit half(라이트맵 — RGBAHalf 와 같은 정밀도).</summary>
        public static byte[] EncodeExr(Color[] px, int width, int height, bool fullFloat)
        {
            var tex = new Texture2D(width, height, fullFloat ? TextureFormat.RGBAFloat : TextureFormat.RGBAHalf, false, true);
            try
            {
                tex.SetPixels(px);
                tex.Apply(false);
                return tex.EncodeToEXR(fullFloat ? Texture2D.EXRFlags.OutputAsFloat : Texture2D.EXRFlags.None);
            }
            finally
            {
                if (Application.isPlaying) Object.Destroy(tex); else Object.DestroyImmediate(tex);
            }
        }

#if UNITY_EDITOR
        /// <summary>Assets/ 하위 폴더를 재귀 생성. Assets 밖이면 false.</summary>
        public static bool EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (folder != "Assets" && !folder.StartsWith("Assets/")) return false;
            if (UnityEditor.AssetDatabase.IsValidFolder(folder)) return true;
            int slash = folder.LastIndexOf('/');
            string parent = folder.Substring(0, slash);
            if (!EnsureFolder(parent)) return false;
            UnityEditor.AssetDatabase.CreateFolder(parent, folder.Substring(slash + 1));
            return true;
        }

        public static string SanitizeFileName(string s)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }

        /// <summary>EXR 바이트를 path 에 쓰고 데이터 텍스처로 임포트(선형·무압축·밉 없음·Clamp) → 로드해 반환.</summary>
        public static Texture2D WriteExrAsset(string path, byte[] exr, FilterMode filter)
        {
            System.IO.File.WriteAllBytes(path, exr);
            UnityEditor.AssetDatabase.ImportAsset(path, UnityEditor.ImportAssetOptions.ForceUpdate);
            if (UnityEditor.AssetImporter.GetAtPath(path) is UnityEditor.TextureImporter imp)
            {
                imp.sRGBTexture = false;
                imp.mipmapEnabled = false;
                imp.filterMode = filter;
                imp.wrapMode = TextureWrapMode.Clamp;
                imp.npotScale = UnityEditor.TextureImporterNPOTScale.None;
                imp.textureCompression = UnityEditor.TextureImporterCompression.Uncompressed;
                imp.SaveAndReimport();
            }
            return UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
#endif
    }
}
