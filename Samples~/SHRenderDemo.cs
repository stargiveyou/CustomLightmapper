
using UnityEngine;
using System.Collections.Generic;
using System;
using Unity.Collections;


#if UNITY_EDITOR
using UnityEditor;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// SH-5 통합 렌더 하니스 (실 D3D11 렌더 + 기즈모 대조 + 스크린샷).
    ///   어댑터 → BurstSHBaker → SHInstanceBuffer → InstancedSHRenderer(DrawMeshInstancedIndirect)
    ///   같은 데이터를 SHDebugView 기즈모에도 주입 → "기즈모 SH 색 ↔ 실제 표면색" 육안 대조.
    ///
    /// 어댑터는 여러 템플릿을 한 씬으로 만들지만 DrawMeshInstancedIndirect 는 메시(템플릿) 하나당
    /// 그리므로, 템플릿별로 인스턴스·SH 를 분리해 렌더러를 템플릿 수만큼 둔다.
    /// 셰이더: "HuskyLibs/InstancedSH_BuiltIn" (HLSL, D3D11 StructuredBuffer). 씬은 Built-In RP.
    /// 주의: 실런타임(Play/에디터 렌더)에서만 화면 확인 — 헤드리스 불가. SH 경로 전달·미등록 의존.
    /// </summary>
    /// <summary>
    /*
Mesh[30] ─추출→ Tri[][] localTris (+albedo[])
Matrix4x4[50000] ──────→ TwoLevelBVH.Instance[]  (templateId + L2W)
                          InstancedRadianceScene (수정 없음)
per-instance 대표점 p_i = M_i · anchor_local
   └─ 인스턴스마다 반구 샘플 N개로 EvaluateRadiance(scene, p_i, dir) → SH9 프로젝션
        → StructuredBuffer<SH9>  (InstanceID 인덱싱)
indirect 셰이더: SH9를 표면 노멀로 평가 + 직사광(실시간) → 최종
  */
    /// </summary> 
    [RequireComponent(typeof(SHDebugView))]
    [ExecuteInEditMode]
    public class SHRenderDemo : MonoBehaviour
    {
        [Header("데모 씬")]
        public int perTemplate = 300;
        public int shDir = 512;
        // 인스턴스 기울기(pitch/roll) 최대 각. 0=기존 Y회전만(하위호환), >0=임의 3축 회전으로
        // 셰이더의 로컬-up 축 2-프로브 보간(EvaluateInstanceSH2Axis) 검증.
        [Range(0f, 90f)] public float tiltMaxDeg = 0f;
        // 프로브 대표점 배치. 데모 박스는 볼록·비회전이라 LocalTopLift 로 충분(윗면 위로 여백만큼 띄움).
        // 실 템플릿(임의 메시·회전)으로 교체 시 SurfaceNormalOffset 권장.
        public AnchorMode anchorMode = AnchorMode.LocalTopLift;
        public float surfaceLift = 0.15f; // anchor 여백(LocalTopLift/SurfaceNormalOffset=로컬, WorldUpLift=월드)
        // 실 템플릿용: 리프트를 템플릿 높이에 비례 적용(유효 리프트 = surfaceLift × bounds.size.y).
        // 고정 0.15 는 소형 데모 박스 기준이라 실 메시 스케일에서 과소/과대 — 켜면 크기 무관 일관.
        public bool liftRelativeToBounds = false;

        [Header("광원/하늘")]
        public Light sun;                 // 씬 Directional (없으면 파라미터로)
        public Vector3 sunDirFallback = new Vector3(-0.3f, -1f, -0.2f);
        public float sunIntensity = 1.3f;
        public Vector3 skyTop = new Vector3(0.5f, 0.7f, 1.0f);
        public Vector3 skyBottom = new Vector3(0.1f, 0.1f, 0.12f);

        [Header("표시")]
        public bool drawGizmoCompare = true;
        [Range(0f, 4f)] public float exposure = 0.5f; // 2-프로브 면별음영 기준 기본값(0.2는 muddy/평평)

        [SerializeField, Header("머티리얼")]
        Material _mat;
        readonly List<InstancedSHRenderer> _renderers = new List<InstancedSHRenderer>();
        readonly List<SHInstancedBuffer> _shBuffers = new List<SHInstancedBuffer>();

        public Mesh[] TestMeshes;

        public enum ShBakeBackend { Burst, Gpu }
        [Header("SH 베이크 백엔드")]
        public ShBakeBackend shBakeBackend = ShBakeBackend.Burst;


        [ContextMenu("Bake + Render SH")]
        public void BakeAndRender()
        {
            Cleanup();

            // 머티리얼(HLSL 셰이더)
            var shader = Shader.Find("HuskyLibs/InstancedSH_BuiltIn");
            if (shader == null) { Debug.LogError("[SHRenderDemo] 셰이더 'HuskyLibs/InstancedSH_BuiltIn' 없음"); return; }
            _mat = new Material(shader) { enableInstancing = true };
            _mat.SetFloat("_Exposure", exposure);

            // 템플릿: 인스펙터에 TestMeshes 를 주입하면 그걸 쓰고, 없으면 데모 박스 2종 생성.
            // 소유 구분이 핵심 — 데모가 만든 메시만 Cleanup 대상(_demoMeshes)에 넣는다.
            // 인스펙터 주입분(프로젝트 에셋)을 파괴 대상에 넣으면 DestroyImmediate 가 에셋 자체를 삭제한다.
            Mesh[] meshes;
            bool meshesOwned; // true = 데모가 생성 → Cleanup 에서 파괴해도 안전
            if (TestMeshes != null && TestMeshes.Length > 0)
            {
                foreach (var m in TestMeshes)
                {
                    if (m == null) { Debug.LogError("[SHRenderDemo] TestMeshes 에 null 엔트리 있음"); return; }
                    if (!m.isReadable) { Debug.LogError($"[SHRenderDemo] 메시 '{m.name}' 가 Read/Write 불가 — 임포트 설정에서 Read/Write Enabled 켜세요(BuildScene 이 정점 접근 필요)."); return; }
                }
                meshes = TestMeshes;      // 인스펙터 에셋 — 파괴 금지
                meshesOwned = false;
            }
            else
            {
                meshes = new[] { MakeBox(0.3f), MakeBox(0.5f) };
                meshesOwned = true;
            }
            _demoMeshes = meshesOwned ? meshes : null; // 소유한 것만 등록

            // 알베도·인스턴스 행렬을 실제 템플릿 수에 맞춰 생성(길이 정합 — 불일치 시 BuildScene throw).
            int nTemplate = meshes.Length;
            // base albedo 6개 컬러를 랜덤으로 출력
            var baseAlbedo = new[] { new Vector3(0.7f, 0.4f, 0.3f), new Vector3(0.35f, 0.6f, 0.4f) };
            var albedo = new Vector3[nTemplate];
            for (int t = 0; t < nTemplate; t++)
                albedo[t] = baseAlbedo[t % baseAlbedo.Length];
            // 겹침 없는 지터드 그리드 배치(공유 그리드 라운드로빈 → 교차 겹침도 방지).
            // 랜덤 배치는 작은 박스 프로브가 겹친 큰 박스 내부에 갇혀 SH=0(검정) 아티팩트 유발.
            var matrices = MakePackedGrid(meshes, perTemplate);
            var input = new MatrixInstanceInput
            {
                templates = meshes,
                templateAlbedo = albedo,
                instanceMatrices = matrices,
            };

            using var s = TemplateInstanceSource.BuildScene(input, Unity.Collections.Allocator.TempJob, BVH.BuildQuality.SAH, surfaceLift, anchorMode);
            using var bScene = s.ToBurstScene(Unity.Collections.Allocator.TempJob);
            var sunDL = new DirectionalLight
            {
                Direction = sun ? sun.transform.forward : sunDirFallback,
                Intensity = sun ? sun.intensity : this.sunIntensity,
                Color = sun ? new Vector3(sun.color.r, sun.color.g, sun.color.b) : Vector3.one
            };

            var sky = BurstSky.Gradient(skyTop, skyBottom);

            // SH 베이크 백엔드 셋업. 기본 Burst(무변경). Gpu 는 검증된 GpuSHBaker(≡BurstSHBaker) 사용.
            // 불가 시(compute 미지원/셰이더 로드 실패/커널 미발견) Burst 폴백.
            GpuScene gpuScene = null; ComputeShader shCs = null; int kSH = -1; bool useGpu = false;
            if (shBakeBackend == ShBakeBackend.Gpu && SystemInfo.supportsComputeShaders)
            {
                shCs = Resources.Load<ComputeShader>("PathTrace"); // Shaders/Resources 배치 → 에디터·빌드 공통 로드
                if (shCs != null) { kSH = shCs.FindKernel("CSSHBake"); if (kSH >= 0) { gpuScene = new GpuScene(bScene); useGpu = true; } }
            }
            if (shBakeBackend == ShBakeBackend.Gpu && !useGpu)
                Debug.LogWarning("[SHRenderDemo] GPU SH 백엔드 사용 불가(compute 미지원/셰이더 로드/커널 미발견) → Burst 폴백.", this);

            var nInst = s.instances.Length;

            //전 인스턴스 Bake — 상단/하단 2프로브(면별 수직음영). 상·하단 모두 anchorMode 대칭 적용
            // (기존: 하단이 AABB min.y 고정이라 SurfaceNormalOffset 등에서 상·하 규칙 불일치 → 실 메시에서
            //  하단 프로브가 지오메트리 안/지면 아래로 들어갈 수 있었음). 템플릿별 로컬 앵커를 1회 산출 후
            //  인스턴스 M 으로 변환. 리프트는 liftRelativeToBounds 면 템플릿 높이 비례.
            var topLocal = new Vector3[nTemplate]; var topWL = new float[nTemplate];
            var botLocal = new Vector3[nTemplate]; var botWL = new float[nTemplate];
            for (int t = 0; t < nTemplate; t++)
            {
                var b = meshes[t].bounds;
                float lift = liftRelativeToBounds ? surfaceLift * b.size.y : surfaceLift;
                TemplateInstanceSource.ComputeLocalAnchor(anchorMode, b, s.uniqueMeshes[t], lift, top: true, out topLocal[t], out topWL[t]);
                TemplateInstanceSource.ComputeLocalAnchor(anchorMode, b, s.uniqueMeshes[t], lift, top: false, out botLocal[t], out botWL[t]);
            }
            var topPts = new NativeArray<Vector3>(nInst, Allocator.TempJob);
            var botPts = new NativeArray<Vector3>(nInst, Allocator.TempJob);
            for (int i = 0; i < nInst; i++)
            {
                int t = s.instances[i].MeshIndex;
                topPts[i] = s.instances[i].LocalToWorld.MultiplyPoint3x4(topLocal[t]) + Vector3.up * topWL[t];
                botPts[i] = s.instances[i].LocalToWorld.MultiplyPoint3x4(botLocal[t]) + Vector3.up * botWL[t];
            }
            // 양 백엔드 모두 SH9[] 산출 → 다운스트림 shTop[i]/shBot[i] 접근 무변경.
            SH9[] shTop, shBot;
            try
            {
                if (useGpu)
                {
                    // Gpu 경로: Burst 와 동일 피보나치 방향셋·가중치를 CPU 에서 산출해 업로드(교차검증 성립).
                    var naDirs = BurstSHBaker.FibonacchiSphere(shDir, Allocator.TempJob);
                    Vector3[] dirs = naDirs.ToArray(); naDirs.Dispose();
                    float shWeight = 4f * Mathf.PI / shDir;
                    Vector3[] topArr = topPts.ToArray();
                    Vector3[] botArr = botPts.ToArray();
                    shTop = GpuSHBaker.Bake(gpuScene, shCs, kSH, sunDL, sky, topArr, dirs, shWeight);
                    shBot = GpuSHBaker.Bake(gpuScene, shCs, kSH, sunDL, sky, botArr, dirs, shWeight);
                }
                else
                {
                    // Burst 경로: NativeArray<SH9> → SH9[] 복사 후 원본 Dispose(다운스트림 통일).
                    shTop = ToArray(BurstSHBaker.Bake(bScene, sky, sunDL, topPts, shDir, Allocator.TempJob));
                    shBot = ToArray(BurstSHBaker.Bake(bScene, sky, sunDL, botPts, shDir, Allocator.TempJob));
                }

            //템플릿별로 분리(렌더러는 메시 하나당)
            var gizmoPts = new List<Vector3>();
            var gizmoSH = new List<SH9>();

            // 드로우 컬링 바운즈: 상/하 프로브 전체를 감싸도록(하단 프로브가 박스 아래로 내려가므로 포함).
            var world = new Bounds(nInst > 0 ? topPts[0] : Vector3.zero, Vector3.zero);
            for (int i = 0; i < nInst; i++) { world.Encapsulate(topPts[i]); world.Encapsulate(botPts[i]); }
            world.Expand(4f); // 박스 절반+프로브 리프트 여유
            for (int t = 0; t < meshes.Length; t++)
            {
                var mats = new List<Matrix4x4>();
                var shList = new List<SH9>();   // 인스턴스당 2개(top, bottom) 연속 — 셰이더 iid*14 정합

                for (int i = 0; i < nInst; i++)
                {
                    if (s.instances[i].MeshIndex != t) continue;
                    mats.Add(s.instances[i].LocalToWorld);
                    shList.Add(shTop[i]); shList.Add(shBot[i]);            // top=probe0, bottom=probe1
                    gizmoPts.Add(topPts[i]); gizmoSH.Add(shTop[i]);
                    gizmoPts.Add(botPts[i]); gizmoSH.Add(shBot[i]);
                }
                if (mats.Count == 0) continue;

                var shNative = new NativeArray<SH9>(shList.ToArray(), Allocator.Temp);
                var shBuf = SHInstancedBuffer.Create(shNative, probesPerInstance: 2);
                shNative.Dispose();

                // 템플릿 알베도를 셰이더 _Color 로 전달(베이크엔 이미 반영, 셰이딩엔 미반영이던 버그 수정).
                var alb = new Vector4(albedo[t].x, albedo[t].y, albedo[t].z, 1f);
                var renderer = new InstancedSHRenderer(meshes[t], _mat, mats.ToArray(), shBuf, world, ownSh: true, albedo: alb);
                _renderers.Add(renderer);
                _shBuffers.Add(shBuf);
            }
            //기즈모 대조 데이터 주입
            var gv = GetComponent<SHDebugView>();
            if (gv)
            {
                gv.SetData(gizmoPts.ToArray(), gizmoSH.ToArray());
            }
            }
            finally
            {
                // TempJob 입력 해제(예외 시에도 누수 없음). shTop/shBot 은 관리 배열이라 Dispose 불필요.
                if (topPts.IsCreated) topPts.Dispose();
                if (botPts.IsCreated) botPts.Dispose();
                gpuScene?.Dispose(); // GPU 백엔드 씬 버퍼 해제 (Burst 경로/폴백 시 null → no-op)
            }
        }

        /// <summary>NativeArray&lt;SH9&gt; → SH9[] 복사 후 원본 Dispose(다운스트림 백엔드 통일용).</summary>
        static SH9[] ToArray(NativeArray<SH9> na)
        {
            var arr = na.ToArray();
            na.Dispose();
            return arr;
        }

        /// <summary>
        /// 실 템플릿(Adonis_Snackbar A/B) 원클릭 셋업: Resources 에서 메시 로드 → TestMeshes 주입 +
        /// 임의 메시 권장 설정(SurfaceNormalOffset·bounds 비례 리프트·perTemplate 상한). 이후 "Bake + Render SH".
        /// </summary>
        [ContextMenu("Load Real Templates (Adonis_Snackbar)")]
        public void LoadRealTemplates()
        {
            var found = new List<Mesh>();
            found.AddRange(Resources.LoadAll<Mesh>("Adonis_Snackbar_A"));
            found.AddRange(Resources.LoadAll<Mesh>("Adonis_Snackbar_B"));
            if (found.Count == 0) { Debug.LogError("[SHRenderDemo] Resources 에서 Adonis_Snackbar_A/B 메시를 못 찾음(경로/임포트 확인)"); return; }
            foreach (var m in found)
                if (!m.isReadable) { Debug.LogError($"[SHRenderDemo] '{m.name}' Read/Write 꺼짐 — 임포트 설정에서 켜야 베이크 가능"); return; }

            TestMeshes = found.ToArray();
            anchorMode = AnchorMode.SurfaceNormalOffset; // 임의 메시·회전에 견고한 권장 모드
            liftRelativeToBounds = true;                 // 실 메시 크기 비례 리프트
            perTemplate = Mathf.Min(perTemplate, 100);   // 실 메시는 삼각형 수 커서 베이크 부담 — 상한
            EditorUtility.SetDirty(this);
            Debug.Log($"[SHRenderDemo] 실 템플릿 {found.Count}개 로드 → TestMeshes. anchorMode=SurfaceNormalOffset, liftRelativeToBounds=on, perTemplate={perTemplate}. 'Bake + Render SH' 실행하세요.", this);
        }

        Mesh[] _demoMeshes;

        private void Update()
        {
            for (int i = 0; i < _renderers.Count; i++)
            {
                _renderers[i].Draw();
            }
        }

        [ContextMenu("Capture Screenshot")]
        public void Capture()
        {
            string path = System.IO.Path.Combine(Application.dataPath, $"SHRender_{System.DateTime.Now:HHmmss}.png");
            ScreenCapture.CaptureScreenshot(path);
            Debug.Log($"[SHRenderDemo] 스크린샷 저장(다음 프레임): {path}");
        }

        void OnDisable() => Cleanup();
        void OnDestroy() => Cleanup();

        void Cleanup()
        {
            foreach (var r in _renderers) r?.Dispose();   // ownsSh=true → SH 버퍼도 해제
            _renderers.Clear();
            _shBuffers.Clear();
            if (_mat) { DestroyImmediate(_mat); _mat = null; }
            if (_demoMeshes != null) { foreach (var m in _demoMeshes) if (m) DestroyImmediate(m); _demoMeshes = null; }
        }

        static float R(System.Random rng) => (float)(rng.NextDouble() * 2 - 1);

        // 로컬 AABB 8코너를 rot 로 회전시켜 월드 최소 y 산출(기울인 인스턴스 바닥 착지 보정용, 성능 무관).
        static float RotatedMinY(Bounds b, Quaternion rot)
        {
            Vector3 mn = b.min, mx = b.max;
            float minY = float.MaxValue;
            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3((c & 1) == 0 ? mn.x : mx.x,
                                         (c & 2) == 0 ? mn.y : mx.y,
                                         (c & 4) == 0 ? mn.z : mx.z);
                float wy = (rot * corner).y;
                if (wy < minY) minY = wy;
            }
            return minY;
        }

        /// <summary>
        /// 겹침 없는 지터드 그리드 배치. 전체 인스턴스(nTemplate·perTemplate)를 한 그리드에 놓고
        /// 라운드로빈으로 템플릿 분배 → 템플릿 간 겹침까지 방지. 셀 간격은 최대 박스의 회전 대각선
        /// 풋프린트 이상으로 잡아, 지터·Y회전에도 인접 박스가 안 겹치게 한다(→ 프로브 갇힘=SH검정 제거).
        /// </summary>
        private Matrix4x4[][] MakePackedGrid(Mesh[] meshes, int perTemplate)
        {
            int nTemplate = meshes.Length;
            int total = nTemplate * perTemplate;
            bool tilt = tiltMaxDeg > 0f;

            // 셀 크기 기준: tilt=0 은 XZ 풋프린트 회전 대각선(×√2), tilt>0 은 3D 대각선
            // (bounds.size.magnitude)로 잡아 임의 3축 회전에도 인접 박스가 안 겹치게 한다.
            float maxFoot = 0f;
            foreach (var m in meshes)
            {
                var sz = m.bounds.size;
                maxFoot = Mathf.Max(maxFoot, tilt ? sz.magnitude : Mathf.Max(sz.x, sz.z));
            }
            float cell = tilt ? maxFoot + 0.2f : maxFoot * 1.41421f + 0.2f;      // 대각선 + 여백
            float jitter = tilt ? 0.1f : (cell - maxFoot * 1.41421f) * 0.5f;     // 여백 절반까지만 흔들어 겹침 방지 보장

            int cols = Mathf.CeilToInt(Mathf.Sqrt(total));
            float extent = cols * cell;
            float origin = -extent * 0.5f + cell * 0.5f; // 원점 중심 정렬

            var lists = new List<Matrix4x4>[nTemplate];
            for (int t = 0; t < nTemplate; t++) lists[t] = new List<Matrix4x4>(perTemplate);

            var rng = new System.Random(12345);
            for (int k = 0; k < total; k++)
            {
                int gx = k % cols, gz = k / cols;
                int t = k % nTemplate;
                float x = origin + gx * cell + R(rng) * jitter;
                float z = origin + gz * cell + R(rng) * jitter;
                Quaternion rot;
                float y;
                if (tilt)
                {
                    // 임의 3축: pitch/roll 은 ±tiltMaxDeg, yaw 는 360° 전역.
                    rot = Quaternion.Euler(R(rng) * tiltMaxDeg, (float)rng.NextDouble() * 360f, R(rng) * tiltMaxDeg);
                    // 기울이면 축정렬 AABB 전제가 깨져 min.y 로는 바닥 관통 → 로컬 8코너를 회전시켜 월드 min.y 보정.
                    y = -RotatedMinY(meshes[t].bounds, rot) + 0.01f;
                }
                else
                {
                    // 바닥 착지: 피벗 보정(-bounds.min.y) → 메시 크기·피벗 무관하게 밑면이 y≈0 에 정렬
                    // (기존 y=0.3 고정은 소형 박스 전제 — 실 메시는 공중부양/관통).
                    y = -meshes[t].bounds.min.y + 0.01f;
                    rot = Quaternion.Euler(0, (float)rng.NextDouble() * 360f, 0);
                }
                lists[t].Add(Matrix4x4.TRS(new Vector3(x, y, z), rot, Vector3.one));
            }

            var outArr = new Matrix4x4[nTemplate][];
            for (int t = 0; t < nTemplate; t++) outArr[t] = lists[t].ToArray();
            return outArr;
        }

        private Mesh MakeBox(float h)
        {
            var v = new[]
         {
                new Vector3(-h,-h,-h), new Vector3(h,-h,-h), new Vector3(h,h,-h), new Vector3(-h,h,-h),
                new Vector3(-h,-h,h),  new Vector3(h,-h,h),  new Vector3(h,h,h),  new Vector3(-h,h,h),
            };
            var t = new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5, 0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2 };
            var m = new Mesh { name = "propBox" };
            m.vertices = v; m.triangles = t; m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }
    }
}

#endif