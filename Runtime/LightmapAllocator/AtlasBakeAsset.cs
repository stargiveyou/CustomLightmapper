using System;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 텍스처 라이트맵 베이크 결과 — 런타임 로드 원본(<see cref="AtlasApplyDebug"/> 가 저장·적용).
    /// 서브에셋: 아틀라스 <see cref="atlas"/>(Texture2DArray, 라이팅 모드는 RGBAHalf 선형) · 머티리얼 <see cref="material"/>
    /// (LightmapDebug, _Lightmaps=atlas) · 원본 메시별 uv2 메시(같은 원본은 같은 uv2 — 파라미터화가 결정적).
    /// 인스턴스 기록: 대상 MeshFilter 경로 · 원본 메시/머티리얼(복원용) · uv2 메시 · ST · 페이지.
    /// EXR 사본(<see cref="pagesExr"/>, 페이지별 half)은 교환·검사용 — 런타임은 서브에셋 아틀라스만 읽는다.
    /// ST/페이지는 MaterialPropertyBlock(씬 미저장)이라 로드마다 다시 적용해야 한다(AtlasApplyDebug.applyOnEnable).
    /// </summary>
    [PreferBinarySerialization]
    public sealed class AtlasBakeAsset : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            [Tooltip("대상 MeshFilter 경로: 베이커 하위면 상대 경로, 아니면 '/' 로 시작하는 씬 절대 경로(이름 기반 — 같은 이름 형제는 구분 불가).")]
            public string path;
            public Mesh sourceMesh;
            public Material sourceMaterial;
            public Mesh uv2Mesh;
            public Vector4 scaleOffset;
            public int page;
        }

        public Entry[] entries = Array.Empty<Entry>();
        public Texture2DArray atlas;
        public Material material;

        [Header("베이크 정보 (읽기 전용)")]
        public string mode;
        public int resolution;
        public int pageCount;
        public string bakedAtUtc;
        [Tooltip("페이지별 EXR 사본(half float, 선형). 교환·검사용.")]
        public Texture2D[] pagesExr = Array.Empty<Texture2D>();

        /// <summary>적용 가능 여부: 아틀라스·머티리얼 존재, 엔트리마다 uv2 메시·페이지 범위.</summary>
        public bool Validate(out string error)
        {
            if (atlas == null) { error = "atlas 없음"; return false; }
            if (material == null) { error = "material 없음"; return false; }
            if (entries == null) { error = "entries 가 null"; return false; }
            for (int i = 0; i < entries.Length; i++)
            {
                var e = entries[i];
                if (e == null || e.uv2Mesh == null) { error = $"엔트리 {i}: uv2 메시 없음"; return false; }
                if (e.page < 0 || e.page >= atlas.depth) { error = $"엔트리 {i}({e.path}): 페이지 {e.page} 가 범위 밖(0..{atlas.depth - 1})"; return false; }
            }
            error = null;
            return true;
        }
    }
}
