using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 텍스처 라이트맵 결과 저장·적용(step 9) 검증 — 숨김 큐브를 실제 베이크(mode=Radiance) 후 디스크 저장 없이 BuildBakeAsset.
    ///  A) 에셋: Validate · 엔트리 1개 · 경로 "Cube" · 원본 메시/머티리얼 기록 · 아틀라스 포맷·픽셀 ≡ 베이크 아틀라스.
    ///  B) ApplyFromAsset: 메시 = 저장 uv2 · 머티리얼 = 에셋 머티리얼 · MPB _LightmapST/_LightmapIndex = 엔트리 값.
    ///  C) Restore: 에셋 적용 후에도 진짜 원본 메시·머티리얼로 복원.
    ///  D) 재베이크(에셋 적용 상태에서): 원본 추적 — 새 기록의 원본이 uv2/라이트맵 머티리얼이 아니라 진짜 원본.
    ///  E) 페이지 EXR 인코딩 매직(76 2F 31 01).
    /// 단계마다 생성된 런타임 객체(아틀라스·머티리얼·uv2 메시·에셋)를 모아 끝에 파괴. URP 셰이더 'CustomLightmapper/LightmapDebug' 필요.
    /// </summary>
    public static class AtlasBakeAssetTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Atlas 결과 저장·적용 (step 9, SO + EXR) ===");
            int pass = 0, total = 0;
            if (Shader.Find("CustomLightmapper/LightmapDebug") == null)
            { sb.AppendLine("  [SKIP] 셰이더 'CustomLightmapper/LightmapDebug' 없음"); return sb.ToString(); }

            var root = new GameObject("AtlasBakeAssetTests") { hideFlags = HideFlags.HideAndDontSave };
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.hideFlags = HideFlags.HideAndDontSave;
            cube.transform.SetParent(root.transform, false);
            var mf = cube.GetComponent<MeshFilter>();
            var rnd = cube.GetComponent<MeshRenderer>();
            var origMesh = mf.sharedMesh;
            var origMat = rnd.sharedMaterial;
            var baker = root.AddComponent<AtlasApplyDebug>();
            var made = new List<Object>();

            try
            {
                baker.saveBakeResult = false;    // 디스크 저장 없이 BuildBakeAsset 으로 검증
                baker.applyOnEnable = false;
                baker.mode = AtlasApplyDebug.BakeMode.Radiance;
                baker.atlasResolution = 64;
                baker.texelsPerWorldUnit = 16f;
                baker.aoSamples = 2;
                baker.lightIntensity = 2f;

                baker.BakeAndApply();
                Track(made, baker, mf, origMesh);

                // ── A) 에셋 ──
                var asset = baker.BuildBakeAsset();
                TrackAsset(made, asset);
                bool okA = asset != null && asset.Validate(out _);
                Check(sb, ref pass, ref total, "A: BuildBakeAsset · Validate", okA);
                if (asset != null)
                {
                    var e = asset.entries.Length == 1 ? asset.entries[0] : null;
                    Check(sb, ref pass, ref total, $"A: 엔트리 {asset.entries.Length}=1, 경로 '{e?.path}'='Cube'", e != null && e.path == "Cube");
                    Check(sb, ref pass, ref total, "A: 원본 메시·머티리얼 기록", e != null && e.sourceMesh == origMesh && e.sourceMaterial == origMat);
                    bool samePx = asset.atlas.format == baker.Atlas.format && SamePixels(asset.atlas.GetPixels(0), baker.Atlas.GetPixels(0));
                    Check(sb, ref pass, ref total, $"A: 아틀라스 {asset.atlas.format} ≡ 베이크 아틀라스(포맷·픽셀)", samePx);

                    // ── B) 에셋 적용 ──
                    baker.RestoreOriginals();
                    baker.bakeAsset = asset;
                    baker.ApplyFromAsset();
                    var mpb = new MaterialPropertyBlock();
                    rnd.GetPropertyBlock(mpb);
                    bool okB = mf.sharedMesh == e.uv2Mesh && rnd.sharedMaterial == asset.material
                               && mpb.GetVector("_LightmapST") == e.scaleOffset && Mathf.RoundToInt(mpb.GetFloat("_LightmapIndex")) == e.page;
                    Check(sb, ref pass, ref total, "B: ApplyFromAsset → uv2 메시·에셋 머티리얼·ST/페이지 MPB", okB);

                    // ── D) 에셋 적용 상태에서 재베이크 → 원본 추적 ──
                    baker.BakeAndApply();
                    Track(made, baker, mf, origMesh);
                    var asset2 = baker.BuildBakeAsset();
                    TrackAsset(made, asset2);
                    var e2 = asset2 != null && asset2.entries.Length == 1 ? asset2.entries[0] : null;
                    Check(sb, ref pass, ref total, "D: 재베이크 기록의 원본 = 진짜 원본(uv2·라이트맵 머티리얼 아님)",
                        e2 != null && e2.sourceMesh == origMesh && e2.sourceMaterial == origMat);

                    // ── C) 복원 ──
                    baker.RestoreOriginals();
                    Check(sb, ref pass, ref total, "C: Restore → 원본 메시·머티리얼", mf.sharedMesh == origMesh && rnd.sharedMaterial == origMat);

                    // ── E) 페이지 EXR ──
                    var exr = BakeOutputUtil.EncodeExr(asset.atlas.GetPixels(0), asset.resolution, asset.resolution, fullFloat: false);
                    bool magic = exr != null && exr.Length > 4 && exr[0] == 0x76 && exr[1] == 0x2F && exr[2] == 0x31 && exr[3] == 0x01;
                    Check(sb, ref pass, ref total, $"E: 페이지 EXR {exr?.Length ?? 0} bytes, 매직 76 2F 31 01", magic);
                }
            }
            finally
            {
                baker.RestoreOriginals();
                Object.DestroyImmediate(root);
                foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
            }

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // 베이커가 만든 런타임 객체(아틀라스·머티리얼·적용된 uv2 메시) 수집 — 내장 큐브 메시(에셋)는 파괴 금지라 제외
        static void Track(List<Object> made, AtlasApplyDebug baker, MeshFilter mf, Mesh original)
        {
            made.Add(baker.Atlas); made.Add(baker.SharedMaterial);
            if (mf.sharedMesh != null && mf.sharedMesh != original) made.Add(mf.sharedMesh);   // BakeAndApply 직후 = 런타임 uv2
        }

        static void TrackAsset(List<Object> made, AtlasBakeAsset a)
        {
            if (a == null) return;
            made.Add(a.atlas); made.Add(a.material);
            foreach (var e in a.entries) made.Add(e.uv2Mesh);
            made.Add(a);
        }

        static bool SamePixels(Color[] a, Color[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
