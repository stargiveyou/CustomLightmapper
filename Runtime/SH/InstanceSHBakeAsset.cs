using System;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 인스턴싱 SH 베이크 결과 — 런타임 로드 원본. <see cref="InstanceSceneBaker"/> 가 생성·갱신하고 표시도 이것만 읽는다.
    /// 템플릿별: 메시 · 알베도(Linear) · 인스턴스 월드 행렬 · SH 패킹(인스턴스당 프로브 2개(상/하) × float4 7개 — GPU 버퍼 레이아웃 그대로).
    /// EXR 사본(<see cref="coefficientsExr"/>)은 같은 계수의 교환·검사용(SO 와 이중 저장, 런타임 미사용).
    /// 대량 인스턴스(수만 × 14 float4)라 텍스트 YAML 대신 바이너리 직렬화.
    /// </summary>
    [PreferBinarySerialization]
    public sealed class InstanceSHBakeAsset : ScriptableObject
    {
        /// <summary>인스턴스당 프로브 수(상/하 2-프로브 — InstancedSH_URP 의 iid*14 인덱싱).</summary>
        public const int ProbesPerInstance = 2;
        /// <summary>인스턴스당 float4 수(2 × 7).</summary>
        public const int Float4PerInstance = ProbesPerInstance * SHPacked.Float4Count;

        [Serializable]
        public class Template
        {
            public Mesh mesh;
            public Vector3 albedoLinear = new Vector3(0.5f, 0.5f, 0.5f);
            public Matrix4x4[] matrices = Array.Empty<Matrix4x4>();
            [Tooltip("인스턴스 i 의 프로브 p(0=상,1=하): [(i*2+p)*7, +7) — SHInstancedBuffer.CreateFromPacked 에 그대로 업로드.")]
            public Vector4[] shPacked = Array.Empty<Vector4>();
        }

        public Template[] templates = Array.Empty<Template>();

        [Header("베이크 정보 (읽기 전용)")]
        public int dirCount;
        public int bounces;
        public uint seed;
        public string backend;
        public string bakedAtUtc;
        [Tooltip("L2: 베이크에 쓴 로컬 광원 요약(종류별 개수·간접만·그림자 없음·출처).")]
        public string lightLog;
        [Tooltip("같은 계수의 EXR 사본. 레이아웃은 InstanceSceneBaker.ExrInstancesPerRow 참조.")]
        public Texture2D coefficientsExr;

        public int InstanceCount
        {
            get
            {
                int n = 0;
                if (templates != null) foreach (var t in templates) n += t?.matrices?.Length ?? 0;
                return n;
            }
        }

        /// <summary>표시 가능 여부: 템플릿마다 메시 존재 + 패킹 길이 = 행렬 수 × 14.</summary>
        public bool Validate(out string error)
        {
            if (templates == null) { error = "templates 가 null"; return false; }
            for (int t = 0; t < templates.Length; t++)
            {
                var tp = templates[t];
                if (tp == null || tp.mesh == null) { error = $"템플릿 {t}: 메시 없음"; return false; }
                int expect = (tp.matrices?.Length ?? 0) * Float4PerInstance;
                int got = tp.shPacked?.Length ?? 0;
                if (got != expect) { error = $"템플릿 {t}({tp.mesh.name}): SH 패킹 {got} ≠ 기대 {expect}(행렬 {tp.matrices?.Length ?? 0} × {Float4PerInstance})"; return false; }
            }
            error = null;
            return true;
        }
    }
}
