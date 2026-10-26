using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// step 8 최종 인코딩(RGBAHalf) 검증 — AtlasApplyDebug 를 숨김 큐브 1개로 실제 베이크(mode=Radiance).
    ///  A) hdrOutput=on  → 아틀라스 RGBAHalf + 1 초과 텍셀 존재(무클램프; 윗면 직사 ≈ 4·cosθ).
    ///  B) hdrOutput=off → RGBA32 + 전 텍셀 ≤ 1(구 동작).
    ///  C) 두 결과가 '반경 5텍셀 안에 밝은(≥0.9) 텍셀이 없는' 곳에선 8bit 양자화 범위로 일치
    ///     — 클램프가 닿지 않는 곳은 같은 값이어야 함(인코딩·스케일 오류 검출). 큐브 시임은 90° 하드 엣지라
    ///     노멀 게이팅으로 스티칭 제외 → 텍셀 간 영향은 dilation(반경 = 반복 수 4)뿐.
    ///  D) 매핑 검증 모드(PerInstanceColor)는 hdrOutput 무관 RGBA32.
    /// 결정적(같은 seed·같은 씬). 단계마다 RestoreOriginals, 끝에 임시 객체·베이커가 만든 런타임 객체 파괴.
    /// URP 셰이더 'CustomLightmapper/LightmapDebug' 필요(에디터).
    /// </summary>
    public static class AtlasHdrOutputTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Atlas HDR 출력 (step 8, RGBAHalf) ===");
            int pass = 0, total = 0;

            if (Shader.Find("CustomLightmapper/LightmapDebug") == null)
            { sb.AppendLine("  [SKIP] 셰이더 'CustomLightmapper/LightmapDebug' 없음"); return sb.ToString(); }
            if (!SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf))
            { sb.AppendLine("  [SKIP] RGBAHalf 미지원 플랫폼"); return sb.ToString(); }

            var root = new GameObject("AtlasHdrOutputTests") { hideFlags = HideFlags.HideAndDontSave };
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.hideFlags = HideFlags.HideAndDontSave;
            cube.transform.SetParent(root.transform, false);
            var mf = cube.GetComponent<MeshFilter>();
            var baker = root.AddComponent<AtlasApplyDebug>();
            var made = new List<Object>();   // 베이커가 만든 런타임 객체(아틀라스·머티리얼·uv2 메시)

            try
            {
                baker.saveBakeResult = false;       // 테스트는 디스크에 에셋·EXR 을 쓰지 않음
                baker.applyOnEnable = false;
                baker.denoise = false;              // 패키지 디노이즈(기본 on, À-trous 반경 ≫ 5텍셀)가 클램프 차이를 퍼뜨려 C 비교를 흐림 — 이 테스트는 인코딩만 본다
                baker.mode = AtlasApplyDebug.BakeMode.Radiance;
                baker.atlasResolution = 128;
                baker.texelsPerWorldUnit = 32f;      // 큐브 면 ~26px 차트 → 반경 5 제외 후에도 비교 텍셀 충분
                baker.aoSamples = 4;
                baker.lightColor = Color.white;
                baker.lightIntensity = 4f;
                baker.lightDirection = new Vector3(-0.3f, -1f, -0.2f);

                // ── A) HDR ──
                baker.hdrOutput = true;
                Color[] hdr = Bake(baker, mf, made, out TextureFormat hdrFmt);
                float hdrMax = MaxChannel(hdr);
                Check(sb, ref pass, ref total, $"A: hdrOutput=on → {hdrFmt} (RGBAHalf 기대)", hdrFmt == TextureFormat.RGBAHalf);
                Check(sb, ref pass, ref total, $"A: 무클램프 — 최대 텍셀 {hdrMax:0.000} > 1", hdrMax > 1.05f);

                // ── B) LDR ──
                baker.hdrOutput = false;
                Color[] ldr = Bake(baker, mf, made, out TextureFormat ldrFmt);
                float ldrMax = MaxChannel(ldr);
                Check(sb, ref pass, ref total, $"B: hdrOutput=off → {ldrFmt} (RGBA32 기대)", ldrFmt == TextureFormat.RGBA32);
                Check(sb, ref pass, ref total, $"B: 클램프 — 최대 텍셀 {ldrMax:0.000} ≤ 1", ldrMax <= 1.0001f);

                // ── C) 클램프가 닿지 않는 곳은 같은 값 ──
                if (hdr != null && ldr != null && hdr.Length == ldr.Length)
                {
                    int res = Mathf.RoundToInt(Mathf.Sqrt(hdr.Length));
                    Color bg = baker.background.linear;
                    int compared = 0, lit = 0, mismatch = 0; float maxDiff = 0f;
                    const float tol = 2f / 255f;   // 8bit 양자화(0.5/255) + half 반올림(~5e-4) 여유
                    for (int y = 0; y < res; y++)
                        for (int x = 0; x < res; x++)
                        {
                            if (NeighborhoodMax(hdr, res, x, y, 5) >= 0.9f) continue;
                            int i = y * res + x;
                            float d = MaxAbs(hdr[i], ldr[i]);
                            compared++;
                            if (MaxAbs(hdr[i], bg) > 1e-3f) lit++;   // 배경 아닌(차트·dilation) 텍셀
                            if (d > tol) mismatch++;
                            if (d > maxDiff) maxDiff = d;
                        }
                    Check(sb, ref pass, ref total, $"C: 비클램프 영역 HDR≡LDR (비교 {compared}, 차트 텍셀 {lit}, 불일치 {mismatch}, maxDiff={maxDiff:0.0000})",
                        mismatch == 0 && lit >= 20);
                }
                else Check(sb, ref pass, ref total, "C: 아틀라스 읽기 실패(길이 불일치/null)", false);

                // ── D) 매핑 검증 모드는 RGBA32 ──
                baker.mode = AtlasApplyDebug.BakeMode.PerInstanceColor;
                baker.hdrOutput = true;
                Bake(baker, mf, made, out TextureFormat mapFmt);
                Check(sb, ref pass, ref total, $"D: PerInstanceColor + hdrOutput=on → {mapFmt} (RGBA32 기대)", mapFmt == TextureFormat.RGBA32);
            }
            finally
            {
                Object.DestroyImmediate(root);
                foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
            }

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // 베이크 → 페이지0 픽셀·포맷 회수 → 원복. 다음 베이크가 포맷 변경 시 아틀라스를 교체(파괴)하므로 픽셀은 여기서 복사.
        static Color[] Bake(AtlasApplyDebug baker, MeshFilter mf, List<Object> made, out TextureFormat fmt)
        {
            var original = mf.sharedMesh;
            baker.BakeAndApply();
            var atlas = baker.Atlas;
            fmt = atlas != null ? atlas.format : (TextureFormat)0;
            Color[] px = atlas != null ? atlas.GetPixels(0) : null;
            made.Add(atlas); made.Add(baker.SharedMaterial);
            if (mf.sharedMesh != original) made.Add(mf.sharedMesh);   // 스왑된 uv2 메시만(내장 큐브 에셋은 파괴 금지)
            baker.RestoreOriginals();
            return px;
        }

        static float NeighborhoodMax(Color[] px, int res, int cx, int cy, int r)
        {
            float m = 0f;
            for (int y = Mathf.Max(0, cy - r); y <= Mathf.Min(res - 1, cy + r); y++)
                for (int x = Mathf.Max(0, cx - r); x <= Mathf.Min(res - 1, cx + r); x++)
                {
                    Color c = px[y * res + x];
                    m = Mathf.Max(m, Mathf.Max(c.r, Mathf.Max(c.g, c.b)));
                }
            return m;
        }

        static float MaxChannel(Color[] px)
        {
            if (px == null) return -1f;
            float m = 0f;
            foreach (var c in px) m = Mathf.Max(m, Mathf.Max(c.r, Mathf.Max(c.g, c.b)));
            return m;
        }

        static float MaxAbs(Color a, Color b) =>
            Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
