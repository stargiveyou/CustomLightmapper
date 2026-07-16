using System.Collections.Generic;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// C2 라이팅 코어(RadianceCore + BruteForceOccluder) 씬 시각화 디버거.
    /// 씬의 메시들을 월드 삼각형(Tri[])으로 모아 차폐자로 쓰고, receiver 메시의
    /// 정점(샘플점)마다 AO/Direct/Radiance 를 평가해 기즈모 색으로 그린다.
    ///
    /// BVH 없이 BruteForceOccluder 만으로 동작 → C2 를 C1 과 독립으로 눈으로 검증.
    /// 인스펙터 우클릭 → "Evaluate", "Run Self Tests".
    /// </summary>
    [ExecuteAlways]
    public class LightmapEvaluateDebugger : MonoBehaviour
    {
        public enum Channel { Radiance, AO, Direct }

        [Header("Geometry")]
        [Tooltip("차폐자로 쓸 메시들. 비우면 자기 자신+자식의 MeshFilter 전부 수집.")]
        [SerializeField] MeshFilter[] occluders;
        [Tooltip("샘플점을 뜰 표면(정점 사용). 비우면 occluders 의 첫 메시.")]
        [SerializeField] MeshFilter receiver;
        [Tooltip("정점 N개당 1개만 샘플(과밀 방지).")]
        [Min(1)] public int sampleStride = 1;

        [Header("Light (Directional)")]
        [Tooltip("빛이 진행하는 방향(예: (0,-1,0)=머리 위에서 내리쬠).")]
        public Vector3 lightDirection = new Vector3(-0.3f, -1f, -0.2f);
        public Color lightColor = Color.white;
        [Min(0f)] public float lightIntensity = 1f;
        [Tooltip("환경광(AO 로 변조). Linear RGB.")]
        public Color ambient = new Color(0.2f, 0.25f, 0.35f, 1f);

        [Header("AO / Sampling")]
        [Min(1)] public int aoSamples = 64;
        public uint seed = 12345;
        [Tooltip("샘플점을 표면에서 노멀 방향으로 띄우는 추가 거리(self-occlusion acne 방지). RadianceCore 내부 기본 바이어스(1e-3)에 더해진다. 곡면 메시에서 AO가 과하게 어두우면 키운다.")]
        [Min(0f)] public float surfaceBias = 0f;

        [Header("View")]
        public Channel channel = Channel.Radiance;
        [Min(0.001f)] public float gizmoSize = 0.03f;
        [Tooltip("노멀 기즈모(방향 확인).")]
        public bool drawNormals = false;
        public float normalLength = 0.1f;

        [Header("Result (read-only)")]
        [SerializeField] int sampleCount;
        [SerializeField] int occluderTriCount;

        // 평가 결과 캐시(기즈모용)
        Vector3[] _pts, _nrm;
        Color[] _col;
        bool _has;

        // 전체 회귀: C2 토대(RayTri·BruteForce·RadianceCore) → C1(BVH) → C2 Indirect. 한 버튼.
        [ContextMenu("Run All Tests (C1+C2)")]
        public void RunAllTests()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(LightmapEvaluateTests.RunAll());     // C2 토대(RayTri/BruteForce/AO/Direct)
            sb.AppendLine(BVHTests.RunAll());                   // C1 가속구조(BruteForce 정답 대조)
            sb.AppendLine(RadianceIndirectTests.RunAll());      // C2 Indirect(경로추적)
            sb.AppendLine(InstancedSceneModeBTests.RunAll());   // 2단 인스턴싱 모드B(submesh 알베도)
            sb.AppendLine(BurstSceneTests.RunAll());            // G0 Burst POD 경로 ≡ 관리형 TwoLevelBVH
            sb.AppendLine(BurstRadianceCompareTests.RunAll());  // G2/G3 Burst Direct/Indirect ≡ RadianceCore
            sb.AppendLine(TemplateInstanceSourceTests.RunAll()); // P1 Mesh+Matrix 어댑터 ≡ MeshFilter 경로(BuildGiScene)
            sb.AppendLine(SH9BakeTextureTest.RunAll());          // SH-1 SH9 프로젝션/재구성/조도 항등식(균등·선형성·방향성)
            sb.AppendLine(BurstSHBakerTests.RunAll());           // SH-2 BurstSHBaker per-instance SH9(빈씬·그라디언트·hit 반사)
            sb.AppendLine(SHPackedTests.RunAll());               // SH-3 SHPacked 정렬 패킹(7×float4) Pack↔Unpack 왕복
            sb.AppendLine(SHEvalProbeGpuTests.RunAll());         // SH-5 셰이더 SH 디코드 ≡ CPU SH9.Evaluate(실 StructuredBuffer 왕복·렌더 컨텍스트 필요)
            sb.AppendLine(GpuBvhCompareTests.RunAll());          // G4 GPU 2단 BVH 순회 ≡ BurstTwoLevelBVH(실 ComputeBuffer 디스패치·compute 지원 필요)
            sb.AppendLine(GpuRadianceCompareTests.RunAll());     // G5 GPU 경로추적(AO/Direct/Indirect) ≡ Burst(실 ComputeBuffer 디스패치·compute 지원 필요)
            sb.AppendLine(GpuSHBakeCompareTests.RunAll());        // SH-G GPU CSSHBake per-probe SH9 ≡ BurstSHBaker(동일 방향셋·compute 지원 필요)
            Debug.Log(sb.ToString(), this);
        }

        [ContextMenu("Run Self Tests")]
        public void RunSelfTests() => Debug.Log(LightmapEvaluateTests.RunAll(), this);

        [ContextMenu("Run Indirect Tests")]
        public void RunIndirectTests() => Debug.Log(RadianceIndirectTests.RunAll(), this);

        [ContextMenu("Run Mode-B Albedo Tests")]
        public void RunModeBTests() => Debug.Log(InstancedSceneModeBTests.RunAll(), this);

        [ContextMenu("Run BVH Cross Tests")]
        public void RunBVHCrossTests() => Debug.Log(BVHTests.RunAll(), this);

        [ContextMenu("Run Burst Scene Tests (G0)")]
        public void RunBurstSceneTests() => Debug.Log(BurstSceneTests.RunAll(), this);

        [ContextMenu("Run Burst Radiance Compare (G2 _ G3)")]
        public void RunBurstRadianceCompare() => Debug.Log(BurstRadianceCompareTests.RunAll(), this);

        [ContextMenu("Run SH GPU Decode Test (SH-5)")]
        public void RunSHGpuDecodeTest() => Debug.Log(SHEvalProbeGpuTests.RunAll(), this);

        [ContextMenu("Run GPU BVH Compare (G4)")]
        public void RunGpuBvhCompare() => Debug.Log(GpuBvhCompareTests.RunAll(), this);

        [ContextMenu("Run GPU Radiance Compare (G5)")]
        public void RunGpuRadianceCompare() => Debug.Log(GpuRadianceCompareTests.RunAll(), this);

        [ContextMenu("Run GPU SH Bake Compare (SH-G)")]
        public void RunGpuSHBakeCompare() => Debug.Log(GpuSHBakeCompareTests.RunAll(), this);

        [ContextMenu("Run BVH Mesh Test")]
        public void RunBVHMeshTest()
        {
            var mf = receiver != null ? receiver : GetComponentInChildren<MeshFilter>();
            if (mf == null || mf.sharedMesh == null)
            {
                Debug.LogWarning("[LMEval] BVH Mesh Test: receiver 또는 자식 MeshFilter 가 없습니다.", this);
                return;
            }
            Debug.Log(BVHTests.RunMeshTest(mf.sharedMesh, mf.transform.localToWorldMatrix), this);
        }

        [ContextMenu("Run BVH Scene Test (multi-mesh)")]
        public void RunBVHSceneTest()
        {
            var filters = ResolveOccluders().ToArray();   // occluders 지정분 또는 자식 전부
            if (filters.Length == 0)
            {
                Debug.LogWarning("[LMEval] BVH Scene Test: occluders 또는 자식 MeshFilter 가 없습니다.", this);
                return;
            }

            Debug.Log("필터 개수 : " + filters.Length);

            Debug.Log(BVHTests.RunSceneTest(filters), this);
        }

        [ContextMenu("Clear")]
        public void Clear() { _has = false; _pts = _nrm = null; _col = null; sampleCount = 0; }

        [ContextMenu("Evaluate")]
        public void Evaluate()
        {
            var occMF = ResolveOccluders();
            if (occMF.Count == 0) { Debug.LogWarning("[LMEval] 차폐 메시가 없습니다.", this); return; }

            var tris = BuildWorldTris(occMF);
            var occluder = new BruteForceOccluder(tris);
            occluderTriCount = tris.Length;

            var recv = receiver != null ? receiver : occMF[0];
            var mesh = recv.sharedMesh;
            if (mesh == null) { Debug.LogWarning("[LMEval] receiver 메시가 없습니다.", this); return; }
            if (!mesh.isReadable) { Debug.LogWarning($"[LMEval] receiver '{mesh.name}' 가 Read/Write 비활성이라 정점 접근 불가. 임포트 설정에서 Read/Write Enabled 체크.", this); return; }

            var l2w = recv.transform.localToWorldMatrix;
            var verts = mesh.vertices;
            // 노멀이 없거나 불일치면 sharedMesh 를 변형(RecalculateNormals)하지 않고 로컬 배열로 계산.
            var meshNorms = mesh.normals;
            var norms = (meshNorms != null && meshNorms.Length == verts.Length)
                ? meshNorms
                : ComputeNormals(verts, mesh.triangles);

            var sun = new DirectionalLight
            {
                Direction = lightDirection.sqrMagnitude > 1e-8f ? lightDirection.normalized : Vector3.down,
                Color = new Vector3(lightColor.r, lightColor.g, lightColor.b),
                Intensity = lightIntensity,
            };
            Vector3 amb = new Vector3(ambient.r, ambient.g, ambient.b);

            int stride = Mathf.Max(1, sampleStride);
            var pts = new List<Vector3>();
            var nrm = new List<Vector3>();
            var col = new List<Color>();

            for (int i = 0; i < verts.Length; i += stride)
            {
                Vector3 wp = l2w.MultiplyPoint3x4(verts[i]);
                Vector3 wn = l2w.MultiplyVector(norms[i]).normalized;
                // #3 self-occlusion 방지: 평가 원점을 표면에서 노멀로 띄움(표시는 원래 wp 유지).
                Vector3 origin = wp + wn * surfaceBias;
                // 결정적 시드: 전역 seed + 정점 인덱스(점마다 다른 시퀀스, 재현 가능)
                uint s = seed + (uint)i * 2654435761u;

                Color c;
                switch (channel)
                {
                    case Channel.AO:
                        float ao = RadianceCore.EvaluateAO(occluder, origin, wn, aoSamples, s);
                        c = new Color(ao, ao, ao, 1f);
                        break;
                    case Channel.Direct:
                        c = ToColor(RadianceCore.EvaluateDirect(occluder, origin, wn, sun));
                        break;
                    default:
                        c = ToColor(RadianceCore.EvaluateRadiance(occluder, origin, wn, sun, amb, aoSamples, s));
                        break;
                }
                pts.Add(wp);
                nrm.Add(wn);
                col.Add(c);
            }

            _pts = pts.ToArray(); _nrm = nrm.ToArray(); _col = col.ToArray();
            sampleCount = _pts.Length;
            _has = true;
            Debug.Log($"[LMEval] {channel}: {sampleCount} samples on '{recv.name}', occluder tris={occluderTriCount}, aoSamples={aoSamples}", this);
        }

        // 선형 RGB → 표시용(감마 근사 + 클램프). 검증 목적이라 단순 처리.
        static Color ToColor(Vector3 lin)
        {
            return new Color(
                Mathf.Clamp01(Mathf.Pow(Mathf.Max(0f, lin.x), 1f / 2.2f)),
                Mathf.Clamp01(Mathf.Pow(Mathf.Max(0f, lin.y), 1f / 2.2f)),
                Mathf.Clamp01(Mathf.Pow(Mathf.Max(0f, lin.z), 1f / 2.2f)), 1f);
        }

        List<MeshFilter> ResolveOccluders()
        {
            var list = new List<MeshFilter>();
            if (occluders != null && occluders.Length > 0)
            {
                foreach (var mf in occluders) if (mf != null && mf.sharedMesh != null) list.Add(mf);
            }
            else
            {
                foreach (var mf in GetComponentsInChildren<MeshFilter>())
                    if (mf.sharedMesh != null) list.Add(mf);
            }
            return list;
        }

        // 여러 MeshFilter 의 삼각형을 월드 공간 Tri[] 로 평탄화
        static Tri[] BuildWorldTris(List<MeshFilter> filters)
        {
            var tris = new List<Tri>();
            foreach (var mf in filters)
            {
                var mesh = mf.sharedMesh;
                if (mesh == null || !mesh.isReadable)
                {
                    if (mesh != null) Debug.LogWarning($"[LMEval] occluder '{mesh.name}' 가 Read/Write 비활성 — 차폐에서 제외.");
                    continue;
                }
                var v = mesh.vertices;
                var t = mesh.triangles;
                var m = mf.transform.localToWorldMatrix;
                for (int i = 0; i < t.Length; i += 3)
                {
                    tris.Add(new Tri
                    {
                        V0 = m.MultiplyPoint3x4(v[t[i]]),
                        V1 = m.MultiplyPoint3x4(v[t[i + 1]]),
                        V2 = m.MultiplyPoint3x4(v[t[i + 2]]),
                    });
                }
            }
            return tris.ToArray();
        }

        // sharedMesh 변형 없이 정점 노멀 계산(면 노멀 면적가중 누적 → 정규화).
        static Vector3[] ComputeNormals(Vector3[] v, int[] t)
        {
            var n = new Vector3[v.Length];
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                Vector3 fn = Vector3.Cross(v[b] - v[a], v[c] - v[a]); // 미정규화 = 면적가중
                n[a] += fn; n[b] += fn; n[c] += fn;
            }
            for (int i = 0; i < n.Length; i++)
                n[i] = n[i].sqrMagnitude > 1e-12f ? n[i].normalized : Vector3.up;
            return n;
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            if (!_has || _pts == null) return;
            for (int i = 0; i < _pts.Length; i++)
            {
                Gizmos.color = _col[i];
                Gizmos.DrawSphere(_pts[i], gizmoSize);
                if (drawNormals)
                {
                    Gizmos.color = new Color(_nrm[i].x * 0.5f + 0.5f, _nrm[i].y * 0.5f + 0.5f, _nrm[i].z * 0.5f + 0.5f);
                    Gizmos.DrawLine(_pts[i], _pts[i] + _nrm[i] * normalLength);
                }
            }
        }
#endif
    }
}
