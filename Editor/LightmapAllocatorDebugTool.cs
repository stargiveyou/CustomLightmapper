using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 선택한 씬 오브젝트들을 인스턴스로 모아 per-instance ST를 할당하고
    /// (1) InstanceLM 테이블 로그, (2) 페이지별 점유 미리보기 PNG를 만든다.
    /// 같은 메시를 여러 번 배치(=인스턴싱)해도 각자 월드 면적으로 영역이 정해진다.
    /// Tools ▸ Custom Lightmapper ▸ A4 ▸ Allocate Selected (per-instance ST)
    /// </summary>
    public static class LightmapAllocatorDebugTool
    {
        [MenuItem("Husky/Tool/Custom Lightmapper//Allocate Selected (per-instance ST)")]
        private static void AllocateSelected()
        {
            var gos = Selection.gameObjects;
            var insts = new List<LightmapInstance>();
            var info = new List<(string name, float area)>();
            foreach (var go in gos)
            {
                var mf = go.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                float area = LightmapAllocator.WorldArea(mf.sharedMesh, go.transform.localToWorldMatrix);
                insts.Add(new LightmapInstance { InstanceId = insts.Count, WorldArea = area });
                info.Add((go.name, area));
            }
            if (insts.Count == 0)
            {
                Debug.LogWarning("[A4] MeshFilter를 가진 오브젝트를 선택하세요.");
                return;
            }

            var res = LightmapAllocator.Allocate(insts.ToArray(), AllocationSettings.Default);

            // 테이블 로그
            var sb = new StringBuilder();
            sb.AppendLine($"[A4] instances={res.Instances.Length}, pages={res.PageCount}, util={res.Utilization:P1}, overflow={res.Overflow}");
            for (int i = 0; i < res.Instances.Length; i++)
            {
                var lm = res.Instances[i]; var st = lm.ScaleOffset;
                sb.AppendLine($"  #{lm.InstanceId} {info[i].name}: area={info[i].area:0.00} page={lm.LightmapIndex} " +
                              $"ST=({st.x:0.000},{st.y:0.000},{st.z:0.000},{st.w:0.000}) side={Mathf.RoundToInt(st.x * res.Resolution)}px");
            }
            Debug.Log(sb.ToString());

            // 페이지별 점유 미리보기
            for (int page = 0; page < res.PageCount; page++)
            {
                var tex = BakePageOccupancy(res, page, 512);
                string path = $"Assets/A4_atlas_page{page}.png";
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(path);
            }
            AssetDatabase.Refresh();
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/A4_atlas_page0.png"));
        }

        [MenuItem("Husky/Tool/Custom Lightmapper/Run Allocator Self-Tests")]
        private static void SelfTests() => Debug.Log(LightmapAllocator.RunSelfTests());

        private static Texture2D BakePageOccupancy(AllocationResult res, int page, int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            var bg = new Color32(18, 18, 18, 255);
            for (int i = 0; i < px.Length; i++) px[i] = bg;

            foreach (var lm in res.Instances)
            {
                if (lm.LightmapIndex != page) continue;
                var st = lm.ScaleOffset;
                int x0 = Mathf.RoundToInt(st.z * size), y0 = Mathf.RoundToInt(st.w * size);
                int w = Mathf.RoundToInt(st.x * size), h = Mathf.RoundToInt(st.y * size);
                Color32 col = Col(lm.InstanceId);
                for (int y = y0; y < y0 + h && y < size; y++)
                    for (int x = x0; x < x0 + w && x < size; x++)
                    {
                        bool border = (x == x0 || x == x0 + w - 1 || y == y0 || y == y0 + h - 1);
                        px[y * size + x] = border ? new Color32(255, 255, 255, 255) : col;
                    }
            }
            tex.SetPixels32(px); tex.Apply();
            return tex;
        }

        private static Color32 Col(int id)
        {
            float h = (id * 0.61803398875f) % 1f;
            Color c = Color.HSVToRGB(h, 0.55f, 0.85f);
            return new Color32((byte)(c.r * 255), (byte)(c.g * 255), (byte)(c.b * 255), 255);
        }
    }
}