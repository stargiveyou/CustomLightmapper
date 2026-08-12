using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// C0-α : 머티리얼의 알파 컷아웃을 **사전 이진화한 비트마스크**로 굽고, 씬의 삼각형 속성
    /// (UV0 · 서브메시)과 인스턴스 머티리얼 슬롯 테이블을 조립해 <see cref="AlphaSceneData"/> 를 만든다.
    ///
    /// 왜 사전 이진화인가(α 결정 ①): 런타임 판정이 순수 정수 비트 연산이 되어 CPU/Burst/GPU 가
    /// 구조적으로 같은 결과를 낸다. 텍스처 필터링·색공간·컷오프 부동소수 비교가 백엔드마다 갈릴
    /// 여지를 베이크 시점에 전부 없앤다. (SH-G 가 피보나치 방향셋을 CPU 계산 후 업로드한 것과 동형.)
    /// </summary>
    public sealed class AlphaMaskBuilder
    {
        /// <summary>알파 블렌딩(Transparent) 머티리얼의 차폐 처리 정책.</summary>
        public enum TransparentPolicy
        {
            /// <summary>차폐에서 제외(레이가 통과). 유리·물이 통짜 그림자를 만드는 문제도 함께 해소.</summary>
            Ignore = 0,
            /// <summary>불투명 취급(기존 거동 유지).</summary>
            Opaque = 1,
        }

        public int MaskResolution = 256;
        public TransparentPolicy Transparent = TransparentPolicy.Ignore;

        /// <summary>자동 판별이 실패하는 머티리얼을 강제로 컷아웃 취급(에셋 참조 지정).</summary>
        public HashSet<Material> ForceCutout;

        /// <summary>
        /// 머티리얼/셰이더 **이름 부분일치**로 강제 컷아웃 지정(대소문자 무시).
        /// 참조 지정이 통하지 않는 경우(예: `.st`/FBX 임포터가 만든 임베드 서브에셋 머티리얼은
        /// 폴더의 동명 `.mat` 파일과 서로 다른 오브젝트다)를 위한 탈출구.
        /// </summary>
        public string[] ForceCutoutNames;

        /// <summary>셰이더가 컷오프를 프로퍼티로 노출하지 않을 때 쓸 기본 임계값.</summary>
        public float DefaultCutoff = 0.5f;

        // SpeedTree8 은 잎 알파를 셰이더 코드에서 clip(alpha - 0.3333) 로 하드코딩한다.
        const float SpeedTreeCutoff = 0.3333f;

        // 원본이 과대할 때 1차 축소 상한(메모리 보호). 이 크기까지는 1:1 blit → 필터링 없음.
        const int ReadCap = 2048;

        readonly Dictionary<Material, int> _matToId = new Dictionary<Material, int>();
        readonly List<uint> _bits = new List<uint>();
        readonly List<int> _word = new List<int>();
        readonly List<int> _w = new List<int>();
        readonly List<int> _h = new List<int>();
        readonly List<Vector4> _st = new List<Vector4>();

        int _cutoutCount, _transparentCount, _nonIdentityST, _skippedOpaque;
        readonly List<string> _perMaterial = new List<string>();   // 진단용 머티리얼별 요약

        /// <summary>
        /// 머티리얼 → matId. -1 = 불투명(마스크 불필요). 같은 머티리얼은 마스크 1개만 만든다.
        /// </summary>
        public int GetOrCreateMask(Material mat)
        {
            if (mat == null) return -1;
            if (_matToId.TryGetValue(mat, out int cached)) return cached;

            int id = -1;
            string renderType = mat.GetTag("RenderType", false, "");

            string shaderName = mat.shader != null ? mat.shader.name : "";
            bool isSpeedTree = shaderName.IndexOf("SpeedTree", System.StringComparison.OrdinalIgnoreCase) >= 0;

            // 컷아웃 판별 — 셰이더 계열마다 표현이 완전히 달라서 우선순위를 둔다.
            //
            //  ⓪ 사용자가 강제 지정한 머티리얼이 최우선.
            //  ① SpeedTree: **프로퍼티로는 절대 못 잡는다.** 잎 알파를 셰이더 코드에서
            //     clip(alpha - 0.3333) 로 하드코딩하므로 RenderType=Opaque · _AlphaClip 없음 ·
            //     _Cutoff 프로퍼티 없음(.mat 에 남은 _Cutoff 값은 임포터가 써 넣은 잔재라
            //     Material.HasProperty 로는 보이지 않는다). → 셰이더 이름으로 판별.
            //  ② `_AlphaClip` 을 가진 셰이더(URP Lit 계열)는 그 값이 곧 정답.
            //     이 계열은 클립이 꺼져 있어도 `_Cutoff` 가 항상 존재하므로 _Cutoff 유무로 판단하면 오탐.
            //  ③ 그 밖의 커스텀 셰이더는 `_Cutoff` 가 있으면 '후보'로 넓게 잡는다.
            //     오탐은 TryBuildCutoutMask 의 all-opaque 폐기가 걸러내므로 결과를 바꾸지 못한다.
            bool isCutout;
            if ((ForceCutout != null && ForceCutout.Contains(mat)) || NameForced(mat.name, shaderName))
                isCutout = true;
            else if (isSpeedTree)
                isCutout = true;
            else if (mat.HasProperty("_AlphaClip"))
                isCutout = mat.GetFloat("_AlphaClip") > 0.5f;
            else
                isCutout = renderType == "TransparentCutout"
                           || mat.IsKeywordEnabled("_ALPHATEST_ON")
                           || mat.HasProperty("_Cutoff");

            bool isBlend = !isCutout
                           && (renderType == "Transparent"
                               || mat.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT")
                               || mat.IsKeywordEnabled("_ALPHABLEND_ON")
                               || mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"));

            if (isCutout)
            {
                if (TryBuildCutoutMask(mat, isSpeedTree, out id)) _cutoutCount++;
            }
            else if (isBlend && Transparent == TransparentPolicy.Ignore)
            {
                id = AddFullyTransparentMask();     // 1×1 전부 0 → 항상 통과
                _transparentCount++;
            }

            if (id < 0 && !isCutout && !isBlend)
                _perMaterial.Add($"'{mat.name}' 불투명 취급 (shader='{shaderName}', RenderType={renderType}, _AlphaClip={(mat.HasProperty("_AlphaClip") ? mat.GetFloat("_AlphaClip").ToString("F0") : "없음")}, _Cutoff={(mat.HasProperty("_Cutoff") ? "있음" : "없음")})");

            _matToId[mat] = id;
            return id;
        }

        bool NameForced(string matName, string shaderName)
        {
            if (ForceCutoutNames == null) return false;
            for (int i = 0; i < ForceCutoutNames.Length; i++)
            {
                string k = ForceCutoutNames[i];
                if (string.IsNullOrEmpty(k)) continue;
                if (matName.IndexOf(k, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (shaderName.IndexOf(k, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // ── 마스크 생성 ─────────────────────────────────────────────────────────

        int AddFullyTransparentMask()
        {
            int id = _w.Count;
            _word.Add(_bits.Count);
            _bits.Add(0u);                          // 1비트만 쓰지만 워드 단위로 보관
            _w.Add(1); _h.Add(1);
            _st.Add(new Vector4(1f, 1f, 0f, 0f));
            return id;
        }

        bool TryBuildCutoutMask(Material mat, bool isSpeedTree, out int id)
        {
            id = -1;

            string texProp = mat.HasProperty("_BaseMap") ? "_BaseMap"
                           : mat.HasProperty("_MainTex") ? "_MainTex"
                           : null;
            if (texProp == null)
            {
                _perMaterial.Add($"'{mat.name}' 베이스 텍스처 프로퍼티(_BaseMap/_MainTex) 없음 → 스킵");
                return false;
            }

            var tex = mat.GetTexture(texProp) as Texture2D;
            if (tex == null)
            {
                _perMaterial.Add($"'{mat.name}' [{texProp}] 텍스처 미할당 → 스킵");
                return false;
            }

            // 셰이더가 컷오프를 노출하지 않으면(SpeedTree 등) 하드코딩 값을 쓴다.
            float cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff")
                         : isSpeedTree ? SpeedTreeCutoff
                         : DefaultCutoff;
            Vector2 scale = mat.GetTextureScale(texProp);
            Vector2 offset = mat.GetTextureOffset(texProp);
            if (scale != Vector2.one || offset != Vector2.zero) _nonIdentityST++;

            byte[] alpha = ReadAlphaChannel(tex, out int srcW, out int srcH);
            if (alpha == null) return false;

            int mw = Mathf.Max(1, Mathf.Min(srcW, MaskResolution));
            int mh = Mathf.Max(1, Mathf.Min(srcH, MaskResolution));

            byte cut = (byte)Mathf.Clamp(Mathf.RoundToInt(cutoff * 255f), 0, 255);

            // 최근접(point) 축소 후 이진화 — bilinear 축소 뒤 임계하면 잎이 두꺼워지거나 얇아진다.
            // 아직 커밋하지 않는다: 전부 불투명으로 밝혀지면 통째로 버려야 하므로 로컬에 먼저 만든다.
            int words = (mw * mh + 31) / 32;
            var bits = new uint[words];
            int opaqueCount = 0;

            for (int y = 0; y < mh; y++)
            {
                int sy = (int)((long)y * srcH / mh);
                for (int x = 0; x < mw; x++)
                {
                    int sx = (int)((long)x * srcW / mw);
                    if (alpha[sy * srcW + sx] < cut) continue;      // 투명 → 비트 0 유지
                    int bit = y * mw + x;
                    bits[bit >> 5] |= 1u << (bit & 31);
                    opaqueCount++;
                }
            }

            // 전부 불투명이면 마스크가 아무 일도 하지 않는다 → 버린다.
            // 판별을 넓게 잡아 생긴 오탐(예: _Cutoff 만 있고 실제로는 불투명한 머티리얼)이
            // 여기서 걸러지므로, 넓은 판별이 결과를 바꾸지 못한다.
            if (opaqueCount == mw * mh)
            {
                _skippedOpaque++;
                _perMaterial.Add($"'{mat.name}' [{texProp} {srcW}×{srcH}] 알파 전부 불투명 → 스킵");
                return false;
            }

            id = _w.Count;
            _word.Add(_bits.Count);
            _w.Add(mw); _h.Add(mh);
            _st.Add(new Vector4(scale.x, scale.y, offset.x, offset.y));
            _bits.AddRange(bits);

            float opaquePct = 100f * opaqueCount / (mw * mh);
            _perMaterial.Add($"'{mat.name}' [{texProp}] {mw}×{mh} cutoff={cutoff:F3} 불투명={opaquePct:F1}%");
            return true;
        }

        /// <summary>
        /// 임포트 설정(Read/Write)에 의존하지 않고 알파 채널만 뽑는다.
        /// RT blit → ReadPixels. 알파는 색공간 변환 대상이 아니므로 Linear RT 로 안전하다.
        /// </summary>
        static byte[] ReadAlphaChannel(Texture2D tex, out int w, out int h)
        {
            w = Mathf.Min(tex.width, ReadCap);
            h = Mathf.Min(tex.height, ReadCap);
            if (w <= 0 || h <= 0) return null;

            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            RenderTexture prev = RenderTexture.active;
            Texture2D readback = null;
            try
            {
                Graphics.Blit(tex, rt);             // 동일 크기면 1:1(필터링 없음)
                RenderTexture.active = rt;
                readback = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                readback.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                readback.Apply(false, false);

                Color32[] px = readback.GetPixels32();
                var a = new byte[w * h];
                int count = Mathf.Min(a.Length, px.Length);
                for (int i = 0; i < count; i++) a[i] = px[i].a;
                return a;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[AlphaMask] '{tex.name}' 알파 읽기 실패 → 불투명 취급. {e.Message}");
                return null;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (readback != null)
                {
                    if (Application.isPlaying) Object.Destroy(readback);
                    else Object.DestroyImmediate(readback);
                }
            }
        }

        // ── 씬 조립 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 유니크 메시 + 인스턴스로부터 <see cref="AlphaSceneData"/> 를 조립한다.
        /// </summary>
        /// <param name="uniqueMeshes">BLAS 순서와 동일한 유니크 메시 배열</param>
        /// <param name="meshTriCount">메시별 삼각형 수(= BLAS 입력 Tri[] 길이). 정합 검증용</param>
        /// <param name="instMesh">인스턴스 → 유니크 메시 인덱스</param>
        /// <param name="instMaterials">인스턴스 → sharedMaterials(널 허용)</param>
        public AlphaSceneData BuildScene(IReadOnlyList<Mesh> uniqueMeshes,
                                         IReadOnlyList<int> meshTriCount,
                                         IReadOnlyList<int> instMesh,
                                         IReadOnlyList<Material[]> instMaterials,
                                         out string log)
        {
            int meshCount = uniqueMeshes.Count;
            int instCount = instMesh.Count;

            var data = new AlphaSceneData
            {
                MeshTriStart = new int[meshCount],
                MeshHasCutout = new byte[meshCount],
                InstMatBase = new int[instCount],
            };

            // 1) 메시 오프셋 — BurstScene.blasTriStart 와 동일한 누적합이어야 한다.
            int totalTris = 0;
            for (int m = 0; m < meshCount; m++)
            {
                data.MeshTriStart[m] = totalTris;
                totalTris += meshTriCount[m];
            }
            data.TriUV = new TriUV[totalTris];
            data.TriSubmesh = new byte[totalTris];

            var sb = new StringBuilder();
            int noUvMeshes = 0, submeshOverflow = 0;

            // 2) 삼각형 속성 — 서브메시 순서대로 채우면 mesh.triangles 순서와 정확히 일치한다.
            var meshHasUv = new bool[meshCount];
            for (int m = 0; m < meshCount; m++)
            {
                Mesh mesh = uniqueMeshes[m];
                int baseTri = data.MeshTriStart[m];
                int nTri = meshTriCount[m];
                if (mesh == null || nTri == 0) continue;

                int[] tris = mesh.triangles;
                Vector2[] uv = mesh.uv;
                bool hasUv = uv != null && uv.Length > 0;
                meshHasUv[m] = hasUv;
                if (!hasUv) noUvMeshes++;

                if (tris.Length / 3 != nTri)
                {
                    sb.Append($"[정합오류] mesh '{mesh.name}' tri {tris.Length / 3} != BLAS {nTri}. 이 메시 알파 비활성. ");
                    continue;
                }

                if (hasUv)
                {
                    for (int t = 0; t < nTri; t++)
                    {
                        int i0 = tris[t * 3 + 0], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                        data.TriUV[baseTri + t] = new TriUV
                        {
                            UV0 = (uint)i0 < (uint)uv.Length ? uv[i0] : Vector2.zero,
                            UV1 = (uint)i1 < (uint)uv.Length ? uv[i1] : Vector2.zero,
                            UV2 = (uint)i2 < (uint)uv.Length ? uv[i2] : Vector2.zero,
                        };
                    }
                }

                int subCount = mesh.subMeshCount;
                for (int s = 0; s < subCount; s++)
                {
                    SubMeshDescriptor d = mesh.GetSubMesh(s);
                    int from = d.indexStart / 3;
                    int to = (d.indexStart + d.indexCount) / 3;
                    byte slot = (byte)Mathf.Min(s, 255);
                    if (s > 255) submeshOverflow++;
                    for (int t = from; t < to && t < nTri; t++) data.TriSubmesh[baseTri + t] = slot;
                }
            }

            // 3) 인스턴스 머티리얼 슬롯 테이블 — BLAS 를 복제하지 않고 인스턴스별 머티리얼 차이를 표현.
            var slots = new List<int>(instCount * 2);
            bool anyCutout = false;
            for (int i = 0; i < instCount; i++)
            {
                int mesh = instMesh[i];
                data.InstMatBase[i] = slots.Count;

                int subCount = (uniqueMeshes[mesh] != null) ? Mathf.Max(1, uniqueMeshes[mesh].subMeshCount) : 1;
                Material[] mats = (instMaterials != null && i < instMaterials.Count) ? instMaterials[i] : null;

                for (int s = 0; s < subCount; s++)
                {
                    Material mat = (mats != null && s < mats.Length) ? mats[s] : null;
                    // UV 가 없는 메시는 알파 판정이 불가능 → 불투명 고정(오탐 방지).
                    int matId = meshHasUv[mesh] ? GetOrCreateMask(mat) : -1;
                    slots.Add(matId);
                    if (matId >= 0)
                    {
                        anyCutout = true;
                        data.MeshHasCutout[mesh] = 1;   // 보수적: 이 메시를 쓰는 인스턴스 중 하나라도 컷아웃이면 켠다
                    }
                }
            }
            data.MatSlot = slots.ToArray();

            // 4) 마스크 테이블 확정
            data.MaskBits = _bits.ToArray();
            data.MaskWord = _word.ToArray();
            data.MaskW = _w.ToArray();
            data.MaskH = _h.ToArray();
            data.MaskST = _st.ToArray();
            data.Enabled = anyCutout;

            sb.Append($"cutout mats={_cutoutCount}, transparent(ignored)={_transparentCount}, ");
            sb.Append($"불투명 스킵={_skippedOpaque}, mask bytes={data.MaskBits.Length * 4}, tris={totalTris}, ");
            sb.Append($"non-identity ST={_nonIdentityST}");
            if (noUvMeshes > 0) sb.Append($", UV0 없는 메시={noUvMeshes}(알파 비활성)");
            if (submeshOverflow > 0) sb.Append($", 서브메시 255 초과={submeshOverflow}(clamp)");
            if (!anyCutout) sb.Append(" → alpha DISABLED(기존 경로 유지)");
            // 머티리얼별 요약 — 잎이 컷아웃으로 안 잡히는 원인을 바로 볼 수 있게 남긴다.
            foreach (var line in _perMaterial) sb.Append("\n    · ").Append(line);
            log = sb.ToString();

            return data;
        }
    }
}
