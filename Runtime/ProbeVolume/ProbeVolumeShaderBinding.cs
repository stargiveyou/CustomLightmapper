using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Track B(B1-4): <see cref="ProbeVolumeAsset"/> → 셰이더(HuskyProbeVolume.hlsl) 바인딩 + 그 셰이더의 CPU 미러.
    ///
    /// 셰이더 계약(HuskyProbeVolume.hlsl 과 1:1 — 한쪽을 바꾸면 양쪽 수정):
    ///   _HuskyProbeSH0..6   Texture3D(asset.textures, 인라인 linear·clamp 샘플러)
    ///   _HuskyPV_Origin     xyz = grid.origin
    ///   _HuskyPV_InvSpacing xyz = 1 / grid.spacing
    ///   _HuskyPV_Dims       xyz = grid.dims
    ///   _HuskyPV_Params     x = 활성(1/0), y = normalBias(월드), z = 가장자리 페이드(셀), w = B2 지형 추종(1) / 균일(0)
    ///   _HuskyPV_FallbackSH float4[7] = 폴백 SH(하늘만) 패킹
    ///   _HuskyPV_Height     Texture2D(asset.heightTexture, dims.x × dims.z 열 높이 — w = 1 일 때만 읽음, 균일이면 검정 더미)
    ///   지형 추종이면 _HuskyPV_Origin.y = 지면 위 첫 층 높이(grid.origin.y) — 셰이더가 열 높이 bilinear 를 더해 g.y 를 지면 기준으로.
    /// 바인딩 대상: MaterialPropertyBlock(렌더러별 — <see cref="InstancedSHRenderer.Properties"/>) · Material · 전역(SetGlobal*).
    /// 폴백 SH 가 없는 에셋(v15.8 이전 베이크)은 페이드를 0(경계 클램프만)으로 강제한다.
    /// </summary>
    public static class ProbeVolumeShaderBinding
    {
        public const string VegetationShaderName = "HuskyLibs/InstancedVegetation_URP";

        static readonly int[] ID_SH =
        {
            Shader.PropertyToID("_HuskyProbeSH0"), Shader.PropertyToID("_HuskyProbeSH1"), Shader.PropertyToID("_HuskyProbeSH2"),
            Shader.PropertyToID("_HuskyProbeSH3"), Shader.PropertyToID("_HuskyProbeSH4"), Shader.PropertyToID("_HuskyProbeSH5"),
            Shader.PropertyToID("_HuskyProbeSH6"),
        };
        static readonly int ID_Origin = Shader.PropertyToID("_HuskyPV_Origin");
        static readonly int ID_InvSpacing = Shader.PropertyToID("_HuskyPV_InvSpacing");
        static readonly int ID_Dims = Shader.PropertyToID("_HuskyPV_Dims");
        static readonly int ID_Params = Shader.PropertyToID("_HuskyPV_Params");
        static readonly int ID_Fallback = Shader.PropertyToID("_HuskyPV_FallbackSH");
        static readonly int ID_Height = Shader.PropertyToID("_HuskyPV_Height");

        /// <summary>바인딩 값 묶음(텍스처 제외). 셰이더가 읽는 그대로.</summary>
        public struct Values
        {
            public Vector4 origin, invSpacing, dims, parameters;
            public Vector4[] fallback;   // 7
        }

        /// <summary>
        /// 에셋 → 바인딩 값. <paramref name="normalBiasCells"/> 는 최소 간격 대비 비율(월드 bias = 비율 × min(spacing)),
        /// <paramref name="edgeFadeCells"/> 는 볼륨 밖 페이드 폭(셀). asset 이 null/무효면 비활성(폴백만 — 이때 폴백 0).
        /// 지형 추종(B2) 에셋은 높이 텍스처가 없으면 비활성(셰이더가 열 높이를 못 읽음).
        /// </summary>
        public static Values Compute(ProbeVolumeAsset asset, float normalBiasCells, float edgeFadeCells)
        {
            var v = new Values { fallback = new Vector4[SHPacked.Float4Count] };
            if (asset == null || !asset.Validate(out _) || asset.textures == null || asset.textures.Length != ProbeVolumeAsset.TextureCount
                || (asset.IsTerrainLayers && asset.heightTexture == null))
            {
                v.dims = Vector4.one;
                v.invSpacing = Vector4.one;
                return v;   // parameters.x = 0 → 비활성
            }
            var g = asset.grid;
            bool hasFallback = asset.fallbackSH != null && asset.fallbackSH.Length == SHPacked.Float4Count;
            if (hasFallback) System.Array.Copy(asset.fallbackSH, v.fallback, SHPacked.Float4Count);
            float minSpacing = Mathf.Min(g.spacing.x, Mathf.Min(g.spacing.y, g.spacing.z));
            v.origin = new Vector4(g.origin.x, g.origin.y, g.origin.z, 0f);
            v.invSpacing = new Vector4(1f / g.spacing.x, 1f / g.spacing.y, 1f / g.spacing.z, 0f);
            v.dims = new Vector4(g.dims.x, g.dims.y, g.dims.z, 0f);
            v.parameters = new Vector4(1f, Mathf.Max(0f, normalBiasCells) * minSpacing, hasFallback ? Mathf.Max(0f, edgeFadeCells) : 0f,
                                       asset.IsTerrainLayers ? 1f : 0f);
            return v;
        }

        public static void Bind(MaterialPropertyBlock mpb, ProbeVolumeAsset asset, float normalBiasCells, float edgeFadeCells)
        {
            var v = Compute(asset, normalBiasCells, edgeFadeCells);
            if (v.parameters.x > 0f) for (int t = 0; t < ID_SH.Length; t++) mpb.SetTexture(ID_SH[t], asset.textures[t]);
            mpb.SetTexture(ID_Height, HeightTexture(asset, v));
            mpb.SetVector(ID_Origin, v.origin);
            mpb.SetVector(ID_InvSpacing, v.invSpacing);
            mpb.SetVector(ID_Dims, v.dims);
            mpb.SetVector(ID_Params, v.parameters);
            mpb.SetVectorArray(ID_Fallback, v.fallback);
        }

        public static void Bind(Material mat, ProbeVolumeAsset asset, float normalBiasCells, float edgeFadeCells)
        {
            var v = Compute(asset, normalBiasCells, edgeFadeCells);
            if (v.parameters.x > 0f) for (int t = 0; t < ID_SH.Length; t++) mat.SetTexture(ID_SH[t], asset.textures[t]);
            mat.SetTexture(ID_Height, HeightTexture(asset, v));
            mat.SetVector(ID_Origin, v.origin);
            mat.SetVector(ID_InvSpacing, v.invSpacing);
            mat.SetVector(ID_Dims, v.dims);
            mat.SetVector(ID_Params, v.parameters);
            mat.SetVectorArray(ID_Fallback, v.fallback);
        }

        /// <summary>전역 바인딩 — HuskyProbeVolume.hlsl 을 쓰는 모든 머티리얼(렌더러 MPB 미지정분)이 이 볼륨을 봄. 볼륨은 한 번에 하나.</summary>
        public static void SetGlobal(ProbeVolumeAsset asset, float normalBiasCells, float edgeFadeCells)
        {
            var v = Compute(asset, normalBiasCells, edgeFadeCells);
            if (v.parameters.x > 0f) for (int t = 0; t < ID_SH.Length; t++) Shader.SetGlobalTexture(ID_SH[t], asset.textures[t]);
            Shader.SetGlobalTexture(ID_Height, HeightTexture(asset, v));
            Shader.SetGlobalVector(ID_Origin, v.origin);
            Shader.SetGlobalVector(ID_InvSpacing, v.invSpacing);
            Shader.SetGlobalVector(ID_Dims, v.dims);
            Shader.SetGlobalVector(ID_Params, v.parameters);
            Shader.SetGlobalVectorArray(ID_Fallback, v.fallback);
        }

        /// <summary>전역 비활성화(폴백 0 — 셰이더는 간접광 0).</summary>
        public static void ClearGlobal() => SetGlobal(null, 0f, 0f);

        // 지형 추종이면 에셋 높이 텍스처, 아니면 검정 더미(셰이더는 w = 0 이면 읽지 않음 — 이전 바인딩이 남지 않게 항상 덮어씀)
        static Texture HeightTexture(ProbeVolumeAsset asset, in Values v) =>
            v.parameters.x > 0f && v.parameters.w > 0f ? asset.heightTexture : Texture2D.blackTexture;

        // ───────────────────────── CPU 미러 ─────────────────────────

        /// <summary>HuskyPV_VolumeWeight 미러(지형 추종 — 열 격자 기준 g.y). heights 가 비면 아래 균일판과 같다.</summary>
        public static float VolumeWeight(in ProbeGrid grid, float[] heights, Vector3 p, float fadeCells) =>
            VolumeWeight(ProbeTerrainLayers.ColumnGrid(grid, heights, p), p, fadeCells);

        /// <summary>HuskyPV_VolumeWeight 미러: 안 = 1, 경계 밖 d 셀 → saturate(1 - d/fade). fade ≤ 0 → 1.</summary>
        public static float VolumeWeight(in ProbeGrid grid, Vector3 p, float fadeCells)
        {
            if (fadeCells <= 0f) return 1f;
            Vector3 g = grid.GridCoord(p);
            float ox = Mathf.Max(-g.x, g.x - (grid.dims.x - 1));
            float oy = Mathf.Max(-g.y, g.y - (grid.dims.y - 1));
            float oz = Mathf.Max(-g.z, g.z - (grid.dims.z - 1));
            return Mathf.Clamp01(1f - Mathf.Max(ox, Mathf.Max(oy, oz)) / fadeCells);
        }

        /// <summary>
        /// HuskyPV_Irradiance / HuskyPV_IrradianceTranslucent(t &gt; 0) 미러. probes = 볼륨 프로브(텍스처와 같은 값 — half 저장이면
        /// 텍스처에서 읽은 값을 넘겨야 GPU 와 비교 가능), fallback = 폴백 SH, v = <see cref="Compute"/> 결과.
        /// HW trilinear = 무가중 <see cref="ProbeGrid.Sample(SH9[], Vector3)"/>.
        /// </summary>
        public static Vector3 EvaluateIrradiance(in ProbeGrid grid, SH9[] probes, in SH9 fallback, in Values v, Vector3 p, Vector3 n, float translucency = 0f)
            => EvaluateIrradiance(grid, null, probes, fallback, v, p, n, translucency);

        /// <summary>
        /// 지형 추종(B2) 포함판: 샘플 위치 ps 의 열 격자(<see cref="ProbeTerrainLayers.ColumnGrid"/>)로 페이드·trilinear —
        /// 셰이더 HuskyPV_GridCoord(w = 1) 와 같은 식. heights 가 비면 균일판과 비트 동일.
        /// </summary>
        public static Vector3 EvaluateIrradiance(in ProbeGrid grid, float[] heights, SH9[] probes, in SH9 fallback, in Values v, Vector3 p, Vector3 n, float translucency = 0f)
        {
            Vector3 eFb = fallback.Evaluate(n) + translucency * fallback.Evaluate(-n);
            if (v.parameters.x < 0.5f) return eFb;
            Vector3 ps = p + n * v.parameters.y;
            var col = ProbeTerrainLayers.ColumnGrid(grid, heights, ps);
            float w = VolumeWeight(col, ps, v.parameters.z);
            SH9 s = col.Sample(probes, ps);
            Vector3 eVol = s.Evaluate(n) + translucency * s.Evaluate(-n);
            return eFb + (eVol - eFb) * w;   // HLSL lerp(a, b, w) = a + (b - a)·w
        }

        public static SH9 UnpackFallback(in Values v) => SHInstancedBuffer.UnflattenAt(v.fallback, 0);
    }
}
