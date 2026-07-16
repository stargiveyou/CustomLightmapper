using UnityEngine;
using HuskyLibs.CustomLightmapper.Bake;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// A1~A3(+옵션 A4) → UVAssembly → TexelMapper.Map 까지 구동해
    /// 루멜 맵(worldPos/worldNormal/valid)을 프리뷰 텍스처·기즈모로 점검하는 디버그 컴포넌트.
    /// 컴포넌트를 붙이고 인스펙터 우클릭 → "Run Texel Map" 으로 실행한다.
    ///
    /// TexelMapper 는 레이/BVH 없이 조립 메시(uv2+normals)만으로 동작하므로
    /// 공유 코어(C1/C2) 없이 여기서 단독 검증 가능하다.
    /// </summary>
    [ExecuteAlways]
    public class TexelMapperDebug : MonoBehaviour
    {
        public enum PreviewMode { WorldNormal, WorldPos, ValidMask }

        [Header("Input")]
        [Tooltip("비우면 같은 오브젝트의 MeshFilter.sharedMesh 를 사용.")]
        [SerializeField] Mesh targetMesh;
        public SegmentationSettings settings = SegmentationSettings.Default;
        [Min(1)] public int resolution = 64;

        [Header("A4 (검증용)")]
        [Tooltip("켜면 DensityNormalizer→ShelfPacker 적용. 끄면 차트가 UV 에서 겹쳐 텍셀 충돌.")]
        public bool applyPacking = true;
        [Range(0f, 0.1f)] public float gutter = 0.01f;

        [Header("Preview")]
        public PreviewMode preview = PreviewMode.WorldNormal;
        [Tooltip("생성된 프리뷰 텍스처(읽기 전용). 클릭하면 확대 미리보기.")]
        [SerializeField] Texture2D previewTex;

        [Header("Gizmos")]
        public bool drawTexels = false;
        public bool drawNormals = false;
        public float normalLength = 0.05f;
        [Tooltip("텍셀 N개당 1개만 기즈모로 그림(과밀 방지).")]
        [Min(1)] public int gizmoStride = 1;

        [Header("Result (read-only)")]
        [SerializeField] int totalTexels;
        [SerializeField] int validTexels;
        [SerializeField, Range(0f, 1f)] float coverage;
        [SerializeField] Vector3 boundsMin, boundsMax;

        LumelMap _lm;
        bool _hasResult;

        Mesh ResolveMesh()
        {
            if (targetMesh != null) return targetMesh;
            var mf = GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        [ContextMenu("Run Texel Map")]
        public void Run()
        {
            var src = ResolveMesh();
            if (src == null)
            {
                Debug.LogWarning("[TexelMap] 대상 Mesh 가 없습니다. targetMesh 를 지정하거나 MeshFilter 를 붙이세요.", this);
                return;
            }

            // A1~A3: 차트별 평탄화 UV
            var pr = ParameterizationPipeline.Run(src, settings);
            if (pr.Charts == null || pr.Charts.Length == 0)
            {
                Debug.LogWarning("[TexelMap] 차트가 0개입니다.", this);
                return;
            }

            // A4(옵션): 밀도 정규화 + 셸프 패킹 → uv2 가 [0,1] 아틀라스에 비겹침 배치
            if (applyPacking)
            {
                DensityNormalizer.Normalize(pr.Charts);
                ShelfPacker.Pack(pr.Charts, gutter);
            }

            // 조립(uv2 + normals 메시) → A5b 래스터화
            var (uv2mesh, _) = UVAssembly.Assemble(pr.Charts, src);
            _lm = TexelMapper.Map(uv2mesh, resolution, transform.localToWorldMatrix);
            _hasResult = true;

            BuildPreview();
            Report(src);
        }

        void Report(Mesh src)
        {
            totalTexels = _lm.Resolution * _lm.Resolution;
            int valid = 0;
            if (_lm.Valid != null)
                for (int i = 0; i < _lm.Valid.Length; i++) if (_lm.Valid[i]) valid++; // 커버된 텍셀만 카운트
            validTexels = valid;
            coverage = totalTexels > 0 ? (float)validTexels / totalTexels : 0f;
            boundsMin = _lm.BoundsMin;
            boundsMax = _lm.BoundsMax;

            Debug.Log(
                $"[TexelMap] '{src.name}' res={_lm.Resolution} → valid={validTexels}/{totalTexels} ({coverage:P1}), " +
                $"bounds=({boundsMin})~({boundsMax}), packing={(applyPacking ? "on" : "off")}", this);

            if (!applyPacking)
                Debug.LogWarning("[TexelMap] packing=off: 차트가 UV 에서 겹쳐 텍셀이 충돌할 수 있습니다(검증 한정).", this);
        }

        // 루멜 맵을 res × res 텍스처로 시각화 (모드별 채널 인코딩)
        void BuildPreview()
        {
            int res = _lm.Resolution;
            if (res <= 0)
            {
                previewTex = null;
                return;
            }

            if (previewTex == null || previewTex.width != res || previewTex.height != res)
            {
                previewTex = new Texture2D(res, res, TextureFormat.RGBA32, false)
                {
                    name = "TexelMap_Preview",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.DontSave,
                };
            }

            Vector3 ext = _lm.BoundsMax - _lm.BoundsMin;
            ext = new Vector3(Mathf.Max(ext.x, 1e-6f), Mathf.Max(ext.y, 1e-6f), Mathf.Max(ext.z, 1e-6f));

            var px = new Color32[res * res];
            for (int i = 0; i < px.Length; i++)
            {
                if (!_lm.Valid[i]) { px[i] = new Color32(0, 0, 0, 255); continue; } // 빈 텍셀=검정

                Color c;
                switch (preview)
                {
                    case PreviewMode.WorldNormal: // 노멀 [-1,1] → [0,1]
                        Vector3 n = _lm.WorldNormal[i];
                        c = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
                        break;
                    case PreviewMode.WorldPos:    // 위치 → 바운드 정규화
                        Vector3 p = _lm.WorldPos[i] - _lm.BoundsMin;
                        c = new Color(p.x / ext.x, p.y / ext.y, p.z / ext.z, 1f);
                        break;
                    default:                      // ValidMask
                        c = Color.white;
                        break;
                }
                px[i] = c;
            }
            previewTex.SetPixels32(px);
            previewTex.Apply(false);
        }

#if UNITY_EDITOR
        [ContextMenu("Save Preview PNG")]
        public void SavePreviewPNG()
        {
            if (previewTex == null) { Debug.LogWarning("[TexelMap] 프리뷰가 없습니다. 먼저 Run.", this); return; }
            var bytes = previewTex.EncodeToPNG();
            string path = $"Assets/TexelMap_{preview}_{_lm.Resolution}.png";
            System.IO.File.WriteAllBytes(path, bytes);
            UnityEditor.AssetDatabase.ImportAsset(path);
            Debug.Log($"[TexelMap] 저장: {path}", this);
        }
#endif

        [ContextMenu("Clear Result")]
        public void ClearResult()
        {
            _hasResult = false;
            _lm = default;
            previewTex = null;
            totalTexels = validTexels = 0;
            coverage = 0f;
            boundsMin = boundsMax = Vector3.zero;
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            if (!_hasResult || (!drawTexels && !drawNormals) || _lm.Valid == null) return;

            int res = _lm.Resolution;
            float dot = Mathf.Max(normalLength * 0.15f, 1e-4f);
            for (int i = 0; i < _lm.Valid.Length; i += gizmoStride)
            {
                if (!_lm.Valid[i]) continue;
                Vector3 p = _lm.WorldPos[i];   // 이미 월드 공간(Map 에서 l2w 적용됨)
                Vector3 n = _lm.WorldNormal[i];

                // 노멀을 색으로 → 텍셀 점/노멀선 색 일치
                Gizmos.color = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f);
                if (drawTexels) Gizmos.DrawCube(p, Vector3.one * dot);
                if (drawNormals) Gizmos.DrawLine(p, p + n * normalLength);
            }
        }
#endif
    }
}