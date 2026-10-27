using System;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Track B(B1-3): 프로브 볼륨 베이크 결과 — 런타임 로드 원본. <see cref="ProbeVolumeBaker"/> 가 생성·갱신.
    ///
    /// 저장(결정 D3): 격자 메타(<see cref="grid"/>) · SH 패킹(<see cref="shPacked"/>, 프로브 idx → [idx·7, +7), GPU 레이아웃 =
    ///   <see cref="SHInstancedBuffer.Flatten"/>) · 유효성(<see cref="valid"/>) + 서브에셋 Texture3D×7(RGBAHalf, Bilinear, Clamp —
    ///   텍스처 t 의 텍셀 (i,j,k) = shPacked[idx·7 + t], B1-4 픽셀 샘플용) + EXR 사본(데이터 교환·검사용, 런타임 미사용).
    /// 폴백 SH(<see cref="fallbackSH"/>, 하늘만): 픽셀 셰이더(B1-4)가 볼륨 밖으로 페이드할 대상.
    /// 정점 AO(<see cref="vertexAO"/>, B1-5): 템플릿 메시별 AO 메시 사본(uv3 = 앞/뒷면 AO, 서브에셋) — 볼륨과 무관하지만 같은 베이크·파일로 관리.
    /// 알파(B1-6): <see cref="alphaCutout"/> = 베이크 레이가 컷아웃 머티리얼 구멍을 통과했는지(기록용 — 런타임 무관).
    /// 인스턴스 배치는 저장하지 않는다 — 배치가 바뀌면 <see cref="Sample"/> 만 다시(재베이크 불필요, 결정 D2).
    /// 대량 프로브(수십만 × 7 float4)라 바이너리 직렬화.
    /// </summary>
    [PreferBinarySerialization]
    public sealed class ProbeVolumeAsset : ScriptableObject
    {
        /// <summary>계수 텍스처 수 = 프로브당 float4 수(SHPacked).</summary>
        public const int TextureCount = SHPacked.Float4Count;
        /// <summary>Texture3D 축 한도(D3D11/Metal 공통 최소).</summary>
        public const int MaxTextureDim = 2048;
        /// <summary>EXR 사본 폭 한도 — z 슬라이스를 가로로 타일하다 넘으면 다음 줄.</summary>
        public const int MaxExrWidth = 16384;

        public ProbeGrid grid;
        [Tooltip("프로브 idx 의 SH: [idx*7, +7) float4 — SHInstancedBuffer.UnflattenAt(shPacked, idx).")]
        public Vector4[] shPacked = Array.Empty<Vector4>();
        [Tooltip("프로브 유효성(뒷면 비율 ≤ τ). 무효 프로브 값은 dilation 으로 채워져 있음 — CPU 샘플은 무효 탭 가중치 0.")]
        public bool[] valid = Array.Empty<bool>();
        [Tooltip("계수 텍스처 7장(서브에셋). 텍스처 t = 모든 프로브의 float4 t번째.")]
        public Texture3D[] textures = Array.Empty<Texture3D>();
        [Tooltip("폴백 SH(하늘만 — 빈 씬 베이크, SHPacked 7×float4). 픽셀 셰이더가 볼륨 밖 페이드 대상으로 사용(B1-4). 비어 있으면 페이드 없이 경계 클램프.")]
        public Vector4[] fallbackSH = Array.Empty<Vector4>();

        [Header("베이크 정보 (읽기 전용)")]
        public int dirCount;
        public int bounces;
        public uint seed;
        public string backend;
        public string bakedAtUtc;
        public bool validityComputed;
        public float validityThreshold;
        public int invalidCount;
        public int dilatedCount;
        [Tooltip("B1-6: 베이크 씬에 알파 컷아웃 any-hit 가 적용됐는지(컷아웃 머티리얼 ≥ 1). false 면 잎 카드도 통짜 불투명으로 트레이스됨.")]
        public bool alphaCutout;
        [Tooltip("B1-6: 알파 마스크 빌드 요약(컷아웃 머티리얼 수·마스크 크기·스킵 사유).")]
        public string alphaLog;
        [Tooltip("L2: 베이크에 쓴 로컬 광원 요약(종류별 개수·간접만·그림자 없음·출처).")]
        public string lightLog;
        [Tooltip("같은 계수의 EXR 사본(32-bit float). 레이아웃은 ExrLayout 참조.")]
        public Texture2D coefficientsExr;

        /// <summary>템플릿 메시 1개의 정점 AO(B1-5). aoMesh = source 사본 + uv3(앞면, 뒷면 AO).</summary>
        [Serializable]
        public class VertexAOEntry
        {
            public Mesh source;
            public Mesh aoMesh;
            public bool twoSided;
            [Tooltip("B1-6: AO 레이가 이 메시 컷아웃 서브메시의 구멍을 통과했는지.")]
            public bool alphaCutout;
        }

        [Header("정점 AO (B1-5)")]
        public VertexAOEntry[] vertexAO = Array.Empty<VertexAOEntry>();
        public VertexAOBake.Settings vertexAOSettings;

        public int ProbeCount => grid.Count;

        /// <summary>원본 템플릿 메시의 AO 메시(없으면 null).</summary>
        public Mesh FindAOMesh(Mesh source)
        {
            if (source == null || vertexAO == null) return null;
            foreach (var e in vertexAO) if (e != null && e.source == source && e.aoMesh != null) return e.aoMesh;
            return null;
        }

        /// <summary>런타임 사용 가능 여부: 격자 · 패킹 길이 = 프로브 × 7 · valid 길이(0 또는 프로브 수).</summary>
        public bool Validate(out string error)
        {
            if (!grid.IsValid) { error = $"격자 무효: {grid}"; return false; }
            int n = grid.Count;
            if ((shPacked?.Length ?? 0) != n * SHPacked.Float4Count)
            { error = $"SH 패킹 {shPacked?.Length ?? 0} ≠ 기대 {n * SHPacked.Float4Count}(프로브 {n} × 7)"; return false; }
            if (valid != null && valid.Length != 0 && valid.Length != n)
            { error = $"valid 길이 {valid.Length} ≠ 프로브 {n}"; return false; }
            error = null;
            return true;
        }

        public SH9 GetProbe(int index) => SHInstancedBuffer.UnflattenAt(shPacked, index);

        public SH9[] UnpackProbes()
        {
            var sh = new SH9[grid.Count];
            for (int i = 0; i < sh.Length; i++) sh[i] = SHInstancedBuffer.UnflattenAt(shPacked, i);
            return sh;
        }

        /// <summary>
        /// 위치 배열에서 볼륨 샘플(CPU trilinear — <see cref="ProbeGrid.Sample(SH9[], bool[], Vector3)"/>). 패킹은 호출당 1회 해제.
        /// <paramref name="useValidity"/> 면 무효 탭 가중치 0(dilation 과 이중 방어).
        /// </summary>
        public SH9[] Sample(Vector3[] points, bool useValidity = true)
        {
            var sh = UnpackProbes();
            bool[] mask = useValidity && valid != null && valid.Length == sh.Length ? valid : null;
            var outp = new SH9[points.Length];
            for (int i = 0; i < points.Length; i++) outp[i] = grid.Sample(sh, mask, points[i]);
            return outp;
        }

        // ───────────────────────── 텍스처 ─────────────────────────

        /// <summary>
        /// 패킹 → Texture3D×7(RGBAHalf 미지원이면 RGBAFloat). Texture3D.SetPixels 순서(x + w·(y + h·z)) = 격자 선형 인덱스.
        /// 읽기 가능 상태로 둔다(에셋 저장·검증용). 축 &gt; <see cref="MaxTextureDim"/> 이면 예외.
        /// </summary>
        public static Texture3D[] CreateTextures(in ProbeGrid grid, Vector4[] packed, string namePrefix = "ProbeSH")
        {
            Vector3Int d = grid.dims;
            if (d.x > MaxTextureDim || d.y > MaxTextureDim || d.z > MaxTextureDim)
                throw new ArgumentOutOfRangeException(nameof(grid), $"격자 {d} 가 Texture3D 축 한도 {MaxTextureDim} 초과");
            var fmt = SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf) ? TextureFormat.RGBAHalf : TextureFormat.RGBAFloat;
            int n = grid.Count;
            var texs = new Texture3D[TextureCount];
            var px = new Color[n];
            for (int t = 0; t < TextureCount; t++)
            {
                for (int i = 0; i < n; i++)
                {
                    Vector4 v = packed[i * SHPacked.Float4Count + t];
                    px[i] = new Color(v.x, v.y, v.z, v.w);
                }
                var tex = new Texture3D(d.x, d.y, d.z, fmt, false)
                {
                    name = $"{namePrefix}{t}",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                tex.SetPixels(px);
                tex.Apply(false);
                texs[t] = tex;
            }
            return texs;
        }

        // ───────────────────────── EXR 사본 ─────────────────────────

        /// <summary>
        /// EXR 레이아웃: z 슬라이스(dims.x × dims.y)를 가로로 tilesX 개씩 타일, 계수 텍스처 t 는 세로 블록 t.
        /// 프로브 (i,j,k) · 계수 t → 픽셀 x = (k % tilesX)·dx + i, y = (t·tilesY + k / tilesX)·dy + j. 폭 ≤ <see cref="MaxExrWidth"/>.
        /// </summary>
        public static void ExrLayout(in ProbeGrid grid, out int tilesX, out int tilesY, out int width, out int height)
        {
            Vector3Int d = grid.dims;
            tilesX = Mathf.Clamp(MaxExrWidth / Mathf.Max(1, d.x), 1, Mathf.Max(1, d.z));
            tilesY = (d.z + tilesX - 1) / tilesX;
            width = tilesX * d.x;
            height = TextureCount * tilesY * d.y;
        }

        /// <summary>EXR 픽셀 배열(<see cref="ExrLayout"/>, 빈 칸 0).</summary>
        public static Color[] BuildExrPixels(in ProbeGrid grid, Vector4[] packed)
        {
            ExrLayout(grid, out int tilesX, out int tilesY, out int w, out int h);
            Vector3Int d = grid.dims;
            var px = new Color[w * h];
            for (int k = 0; k < d.z; k++)
                for (int j = 0; j < d.y; j++)
                    for (int i = 0; i < d.x; i++)
                    {
                        int idx = grid.Index(i, j, k);
                        int x = (k % tilesX) * d.x + i;
                        for (int t = 0; t < TextureCount; t++)
                        {
                            int y = (t * tilesY + k / tilesX) * d.y + j;
                            Vector4 v = packed[idx * SHPacked.Float4Count + t];
                            px[y * w + x] = new Color(v.x, v.y, v.z, v.w);
                        }
                    }
            return px;
        }

        /// <summary>계수 EXR(32-bit float) 인코딩. 높이가 텍스처 한도를 넘으면 null.</summary>
        public static byte[] EncodeExr(in ProbeGrid grid, Vector4[] packed)
        {
            ExrLayout(grid, out _, out _, out int w, out int h);
            if (h > MaxExrWidth) return null;
            return BakeOutputUtil.EncodeExr(BuildExrPixels(grid, packed), w, h, fullFloat: true);
        }
    }
}
