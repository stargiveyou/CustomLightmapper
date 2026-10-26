using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Track B(B1-5, 결정 D4): 템플릿 로컬 정점 AO — 인스턴싱과 호환되는 유일한 정점 데이터(인스턴스 무관 → 저장 = 템플릿 수 × 정점 수).
    ///
    /// 씬: 템플릿 메시 단독(항등 행렬 1인스턴스) → 2단 BVH → <see cref="BurstAO.Compute"/>(정점 위치·노멀 = 점 배열, 코어 무수정).
    ///   수관 안쪽·가지 사이처럼 '자기 자신'이 가리는 음영만 담는다(다른 인스턴스·지형의 차폐는 프로브 볼륨 몫).
    /// 저장: 메시 사본 uv3(TEXCOORD3) = (앞면 AO, 뒷면 AO). 뒷면 = 노멀 반대 반구(양면 잎 카드 — 셰이더가 백페이스에서 y 사용),
    ///   단면 메시는 뒷면 = 앞면. 셰이더(_HUSKY_VERTEX_AO)는 간접광(볼륨/SH)에만 곱함 — 직사광 차폐는 실시간 그림자.
    /// 레이 길이 = maxDistanceRelative × 템플릿 bounds 대각선(0 = 무제한), 시작점 = 정점 + n·(surfaceBiasRelative × 대각선)
    ///   (+ BurstAO 내부 1e-3). 노멀이 없는(0) 정점은 AO 1.
    /// </summary>
    public static class VertexAOBake
    {
        /// <summary>AO 를 담는 UV 채널(TEXCOORD3): x = 앞면, y = 뒷면.</summary>
        public const int UVChannel = 3;
        const uint BackSeedSalt = 0x9E3779B9u;

        [Serializable]
        public struct Settings
        {
            [Tooltip("정점당 코사인 반구 레이 수.")]
            [Min(1)] public int samples;
            [Tooltip("레이 길이 = 이 값 × 템플릿 bounds 대각선(0 = 무제한). 작게 하면 가까운 자기차폐만.")]
            [Min(0f)] public float maxDistanceRelative;
            [Tooltip("시작점 노멀 오프셋 = 이 값 × 대각선(BurstAO 내부 1e-3 에 추가). 곡면 acne 가 보이면 키움.")]
            [Min(0f)] public float surfaceBiasRelative;
            public uint seed;

            public static Settings Default => new Settings { samples = 128, maxDistanceRelative = 0.5f, surfaceBiasRelative = 1e-4f, seed = 12345u };
        }

        /// <summary>
        /// 템플릿 메시의 정점 AO. front[i] = 노멀 반구, back[i] = 반대 반구(twoSided 일 때만 — 아니면 front 사본). Read/Write 필요.
        /// </summary>
        public static void Compute(Mesh mesh, in Settings s, bool twoSided, out float[] front, out float[] back)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            var tris = TemplateInstanceSource.MeshToLocalTris(mesh);   // Read/Write 아니면 예외
            var v = mesh.vertices;
            var n = VertexNormals(mesh, v);
            float diag = mesh.bounds.size.magnitude;
            float maxDist = s.maxDistanceRelative > 0f ? s.maxDistanceRelative * diag : float.MaxValue;
            float bias = s.surfaceBiasRelative * diag;

            using var bvh = new TwoLevelBVH(new[] { tris }, new[] { new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = Matrix4x4.identity } },
                                            Allocator.TempJob, BVH.BuildQuality.SAH);
            using var scene = BurstScene.Create(bvh, null, Allocator.TempJob);
            front = Run(scene, v, n, 1f, bias, s.samples, s.seed, maxDist);
            back = twoSided ? Run(scene, v, n, -1f, bias, s.samples, s.seed ^ BackSeedSalt, maxDist) : (float[])front.Clone();
        }

        /// <summary>한 반구(sign = +1 노멀 / -1 반대) — 점 = v + n'·bias, BurstAO 시드 = seed + i·2654435761u. 노멀 0 정점은 1.</summary>
        internal static float[] Run(in BurstScene scene, Vector3[] v, Vector3[] n, float sign, float bias, int samples, uint seed, float maxDist)
        {
            int count = v.Length;
            var pts = new NativeArray<Vector3>(count, Allocator.TempJob);
            var nrm = new NativeArray<Vector3>(count, Allocator.TempJob);
            var valid = new NativeArray<bool>(count, Allocator.TempJob);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Vector3 nn = n[i] * sign;
                    nrm[i] = nn;
                    pts[i] = v[i] + nn * bias;
                    valid[i] = nn.sqrMagnitude > 0.5f;
                }
                var ao = BurstAO.Compute(scene, pts, nrm, valid, samples, seed, maxDist, Allocator.TempJob);
                var outp = ao.ToArray();
                ao.Dispose();
                for (int i = 0; i < count; i++) if (!valid[i]) outp[i] = 1f;
                return outp;
            }
            finally { pts.Dispose(); nrm.Dispose(); valid.Dispose(); }
        }

        /// <summary>원본 메시 사본 + uv3 = (front, back). 원본은 수정하지 않는다. 이름 = 원본 + "_AO".</summary>
        public static Mesh CreateAOMesh(Mesh source, float[] front, float[] back)
        {
            int n = source.vertexCount;
            if (front.Length != n || back.Length != n) throw new ArgumentException($"AO 길이({front.Length}/{back.Length}) ≠ 정점 수({n})");
            var m = UnityEngine.Object.Instantiate(source);
            m.name = source.name + "_AO";
            var uv = new List<Vector2>(n);
            for (int i = 0; i < n; i++) uv.Add(new Vector2(front[i], back[i]));
            m.SetUVs(UVChannel, uv);
            return m;
        }

        /// <summary>Compute → CreateAOMesh 한 번에.</summary>
        public static Mesh Bake(Mesh source, in Settings s, bool twoSided)
        {
            Compute(source, s, twoSided, out var front, out var back);
            return CreateAOMesh(source, front, back);
        }

        // 메시 노멀(정점 수와 같을 때) 또는 면적가중 면 노멀 누적(원본 메시는 건드리지 않음)
        static Vector3[] VertexNormals(Mesh mesh, Vector3[] v)
        {
            var mn = mesh.normals;
            if (mn != null && mn.Length == v.Length) return mn;
            var t = mesh.triangles;
            var n = new Vector3[v.Length];
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                Vector3 fn = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                n[t[i]] += fn; n[t[i + 1]] += fn; n[t[i + 2]] += fn;
            }
            for (int i = 0; i < n.Length; i++) n[i] = n[i].sqrMagnitude > 1e-20f ? n[i].normalized : Vector3.zero;
            return n;
        }
    }
}
