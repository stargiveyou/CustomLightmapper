using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 인스턴싱 SH 베이크 실사용 컴포넌트 — 테스트·데모 코드 없이 동작(베이크 코어 어셈블리).
    ///
    /// 입력(임시): 템플릿별 Mesh + 알베도 + 인스턴스 월드 행렬. 실 인스턴스 데이터 소스가 확정되기 전까지
    ///   ① 인스펙터 목록(<see cref="TemplateInput.matrices"/>), ② 코드 <see cref="SetInstances"/>,
    ///   ③ "Capture Matrices From Sources"(자식 Transform 행렬 캡처) 중 하나로 채운다.
    /// 베이크: <see cref="TemplateInstanceSource"/>(2단 씬 + 2-프로브 대표점) → <see cref="BurstSHBaker"/> 또는 <see cref="GpuSHBaker"/>
    ///   (bounces 1 = 1바운스+앰비언트 근사, ≥2 = 경로추적 다바운스. 둘 다 물리 규약 ρ/π·E).
    /// 저장: <see cref="InstanceSHBakeAsset"/>(ScriptableObject, 런타임 원본) + EXR 사본(에디터 전용 쓰기).
    /// 표시: 에셋 → 템플릿별 <see cref="InstancedSHRenderer"/>(DrawMeshInstancedIndirect, InstancedSH_URP).
    ///   플레이/빌드에선 베이크 없이 에셋만 로드(OnEnable).
    /// </summary>
    [ExecuteAlways]
    public class InstanceSceneBaker : MonoBehaviour
    {
        public enum Backend { Burst, Gpu }

        [Serializable]
        public class TemplateInput
        {
            public Mesh mesh;
            [Tooltip("표면 알베도(sRGB 색 — 베이크·셰이딩엔 Linear 로 변환).")]
            public Color albedo = new Color(0.6f, 0.6f, 0.6f, 1f);
            [Tooltip("임시 입력: 인스턴스 월드 행렬(L2W). 인스펙터 / SetInstances / 'Capture Matrices From Sources' 로 채움.")]
            public List<Matrix4x4> matrices = new List<Matrix4x4>();
            [Tooltip("'Capture Matrices From Sources' 가 이 Transform 의 직계 자식 localToWorldMatrix 로 matrices 를 덮어씀(임시 저작 편의).")]
            public Transform captureSource;
        }

        /// <summary>EXR 사본 레이아웃: 한 행에 인스턴스 64개(텍스처 높이 한도 16384 회피). 인스턴스 i → 행 i/64, 열 (i%64)·14 부터 float4 14개.</summary>
        public const int ExrInstancesPerRow = 64;

        [Header("입력 (임시 — 실 인스턴스 데이터 소스 확정 전)")]
        public TemplateInput[] templates = new TemplateInput[0];

        [Header("프로브 배치 (상/하 2-프로브)")]
        public AnchorMode anchorMode = AnchorMode.SurfaceNormalOffset;
        public float surfaceLift = 0.15f;
        [Tooltip("켜면 리프트 = surfaceLift × 템플릿 높이(실 메시 크기 비례).")]
        public bool liftRelativeToBounds = true;

        [Header("광원 / 하늘")]
        [Tooltip("씬 Directional Light. 비우면 아래 대체값(방향·색·세기).")]
        public Light sun;
        public Vector3 sunDirection = new Vector3(-0.3f, -1f, -0.2f);
        public Color sunColor = Color.white;
        [Min(0f)] public float sunIntensity = 1.3f;
        [Tooltip("하늘 위쪽 복사휘도(Linear RGB).")]
        public Vector3 skyTop = new Vector3(0.5f, 0.7f, 1.0f);
        [Tooltip("하늘 아래쪽 복사휘도(Linear RGB).")]
        public Vector3 skyBottom = new Vector3(0.1f, 0.1f, 0.12f);

        [Header("로컬 광원 (Point · Spot · Area — L2)")]
        [Tooltip("프로브 SH 에 로컬 광원을 굽는다: 이웃 표면이 받은 빛(바운스) + Direct 플래그 광원이 프로브에 직접 주는 빛(셰이더는 태양만 실시간이라). " +
                 "기본 Scene = 로드된 씬의 켜진 Point/Spot/Area 라이트. None = 구 거동(태양 + 하늘만).")]
        public LocalLightSettings localLights = new LocalLightSettings();

        [Header("베이크")]
        public Backend backend = Backend.Burst;
        [Tooltip("1 = 1바운스+앰비언트 근사, ≥2 = 경로추적 다바운스. 둘 다 물리 규약(ρ/π·E).")]
        [Range(1, 8)] public int bounces = 1;
        [Min(16)] public int dirCount = 512;
        public uint seed = 12345;
        [Tooltip("Gpu 백엔드용 PathTrace.compute. 비우면 Resources → (에디터) 프로젝트 경로. 빌드에서 GPU 베이크 시 지정.")]
        public ComputeShader pathTraceShader;

        [Header("결과")]
        [Tooltip("베이크 결과(런타임은 이것만 읽음). 비우면 베이크 시 outputFolder 에 새로 만든다.")]
        public InstanceSHBakeAsset bakeAsset;
        [Tooltip("에셋·EXR 저장 폴더(Assets/ 하위).")]
        public string outputFolder = "Assets/CustomLightmapper/BakeOutput";   // 소비 프로젝트 Assets/ 하위(패키지 폴더 밖)
        [Tooltip("SH 계수 EXR 사본도 저장(32-bit float).")]
        public bool exportExr = true;

        [Header("표시")]
        [Tooltip("비우면 HuskyLibs/InstancedSH_URP 로 런타임 머티리얼 생성(이때만 exposure·shStrength 적용). 빌드에선 지정 권장(셰이더 포함 보장).")]
        public Material material;
        [Range(0f, 4f)] public float exposure = 1f;
        [Range(0f, 4f)] public float shStrength = 1f;
        [Tooltip("OnEnable 에 에셋으로 렌더러 구성.")]
        public bool renderOnEnable = true;

        readonly List<InstancedSHRenderer> _renderers = new List<InstancedSHRenderer>();
        Material _runtimeMat;

        internal int RendererCount => _renderers.Count;

        // ───────────────────────── 입력 ─────────────────────────

        /// <summary>코드 입력(임시 소스 → 실 데이터 소스 교체 지점). 기존 templates 를 덮어쓴다.</summary>
        public void SetInstances(Mesh[] meshes, Matrix4x4[][] matrices, Color[] albedo = null)
        {
            if (meshes == null || matrices == null || meshes.Length != matrices.Length)
                throw new ArgumentException("meshes 와 matrices 길이가 같아야 함");
            templates = new TemplateInput[meshes.Length];
            for (int t = 0; t < meshes.Length; t++)
            {
                templates[t] = new TemplateInput
                {
                    mesh = meshes[t],
                    matrices = new List<Matrix4x4>(matrices[t] ?? Array.Empty<Matrix4x4>()),
                };
                if (albedo != null && t < albedo.Length) templates[t].albedo = albedo[t];
            }
        }

        [ContextMenu("Capture Matrices From Sources")]
        public void CaptureMatricesFromSources()
        {
            int captured = 0, sources = 0;
            foreach (var t in templates)
            {
                if (t == null || t.captureSource == null) continue;
                sources++;
                t.matrices.Clear();
                foreach (Transform c in t.captureSource) { t.matrices.Add(c.localToWorldMatrix); captured++; }
            }
            Debug.Log($"[InstanceSceneBaker] 행렬 캡처: 소스 {sources}개 → 인스턴스 {captured}개.", this);
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        // ───────────────────────── 베이크 ─────────────────────────

        /// <summary>베이크 → 에셋 갱신(에디터면 디스크 저장 + EXR) → 렌더러 재구성.</summary>
        [ContextMenu("Bake")]
        public void Bake()
        {
            var asset = BakeToAsset(bakeAsset, out string usedBackend);
            if (asset == null) return;
#if UNITY_EDITOR
            SaveAssetAndExr(asset);
#endif
            bakeAsset = asset;
            Debug.Log($"[InstanceSceneBaker] 베이크 완료: 템플릿 {asset.templates.Length}, 인스턴스 {asset.InstanceCount}, " +
                      $"bounces={asset.bounces}, dirs={asset.dirCount}, backend={usedBackend}, lights={asset.lightLog}", this);
            if (renderOnEnable && isActiveAndEnabled) RebuildRender();
        }

        /// <summary>디스크 저장 없이 베이크해 에셋 객체를 채운다(target 있으면 덮어씀, 없으면 새로 생성). 실패 시 null.</summary>
        internal InstanceSHBakeAsset BakeToAsset(InstanceSHBakeAsset target, out string usedBackend)
        {
            usedBackend = null;
            if (!CollectInput(out var meshes, out var albedoLin, out var mats)) return null;

            var input = new MatrixInstanceInput { templates = meshes, templateAlbedo = albedoLin, instanceMatrices = mats };
            using var s = TemplateInstanceSource.BuildScene(input, Allocator.TempJob, BVH.BuildQuality.SAH, surfaceLift, anchorMode);
            var bakeLights = localLights != null ? localLights.Collect() : Array.Empty<BakeLight>();   // L2: 로컬 광원(바운스 + 직접광 투영)
            using var bScene = s.ToBurstScene(Allocator.TempJob, bakeLights);
            TemplateInstanceSource.ComputeProbePoints(s, meshes, anchorMode, surfaceLift, liftRelativeToBounds, out var top, out var bot);

            var sunDL = SunLight();
            var sky = BurstSky.Gradient(skyTop, skyBottom);
            var q = new BakeQualitySettings { MaxBounces = bounces, RRStartDepth = 3, RayBias = 1e-4f };

            // GPU 준비(불가하면 Burst 폴백)
            GpuScene gpu = null; ComputeShader cs = null; int kernel = -1;
            if (backend == Backend.Gpu)
            {
                cs = SystemInfo.supportsComputeShaders ? PathTraceShader.Resolve(pathTraceShader) : null;
                string kName = bounces > 1 ? "CSSHBakeMulti" : "CSSHBake";
                if (cs != null && cs.HasKernel(kName)) kernel = cs.FindKernel(kName);
                if (kernel >= 0) gpu = new GpuScene(bScene);
                else Debug.LogWarning("[InstanceSceneBaker] GPU 사용 불가(compute 미지원/PathTrace 로드/커널 미발견) → Burst 폴백.", this);
                if (gpu != null && !gpu.TraversalFits)
                {
                    Debug.LogWarning($"[InstanceSceneBaker] BVH 깊이(스택 {gpu.RequiredStack}칸 필요) > GPU 순회 스택 {GpuBakeLimits.TraversalStack} → Burst 폴백.", this);
                    gpu.Dispose();
                    gpu = null;
                }
            }
            usedBackend = gpu != null ? "Gpu" : "Burst";

            SH9[] shTop, shBot;
            try
            {
                shTop = BakeProbes(bScene, gpu, cs, kernel, sunDL, sky, q, top, seed);
                shBot = BakeProbes(bScene, gpu, cs, kernel, sunDL, sky, q, bot, seed ^ 0x5bd1e995u); // 상/하 노이즈 비상관
            }
            finally { gpu?.Dispose(); }

            // 템플릿별 묶기: s.instances 순서(BuildScene 은 템플릿 순서대로 평탄화)에서 MeshIndex 로 분류
            var asset = target != null ? target : ScriptableObject.CreateInstance<InstanceSHBakeAsset>();
            asset.templates = new InstanceSHBakeAsset.Template[meshes.Length];
            for (int t = 0; t < meshes.Length; t++)
            {
                var tm = new List<Matrix4x4>();
                var probes = new List<SH9>();
                for (int i = 0; i < s.instances.Length; i++)
                {
                    if (s.instances[i].MeshIndex != t) continue;
                    tm.Add(s.instances[i].LocalToWorld);
                    probes.Add(shTop[i]); probes.Add(shBot[i]);   // top=probe0, bottom=probe1 (셰이더 iid*14 정합)
                }
                asset.templates[t] = new InstanceSHBakeAsset.Template
                {
                    mesh = meshes[t],
                    albedoLinear = albedoLin[t],
                    matrices = tm.ToArray(),
                    shPacked = SHInstancedBuffer.Flatten(probes.ToArray()),
                };
            }
            asset.dirCount = dirCount;
            asset.bounces = bounces;
            asset.seed = seed;
            asset.backend = usedBackend;
            asset.bakedAtUtc = DateTime.UtcNow.ToString("o");
            asset.lightLog = $"{BakeLightUtil.Summary(bakeLights)}({(localLights != null ? localLights.source : LocalLightSource.None)})";
            return asset;
        }

        bool CollectInput(out Mesh[] meshes, out Vector3[] albedoLin, out Matrix4x4[][] mats)
        {
            meshes = null; albedoLin = null; mats = null;
            if (templates == null || templates.Length == 0)
            { Debug.LogWarning("[InstanceSceneBaker] templates 가 비어 있음.", this); return false; }

            int total = 0;
            meshes = new Mesh[templates.Length];
            albedoLin = new Vector3[templates.Length];
            mats = new Matrix4x4[templates.Length][];
            for (int t = 0; t < templates.Length; t++)
            {
                var tp = templates[t];
                if (tp == null || tp.mesh == null)
                { Debug.LogError($"[InstanceSceneBaker] 템플릿 {t}: 메시 없음.", this); return false; }
                if (!tp.mesh.isReadable)
                { Debug.LogError($"[InstanceSceneBaker] 메시 '{tp.mesh.name}' Read/Write 꺼짐 — 임포트 설정에서 켜야 베이크 가능.", this); return false; }
                var lin = tp.albedo.linear;
                meshes[t] = tp.mesh;
                albedoLin[t] = new Vector3(lin.r, lin.g, lin.b);
                mats[t] = tp.matrices != null ? tp.matrices.ToArray() : Array.Empty<Matrix4x4>();
                total += mats[t].Length;
            }
            if (total == 0)
            { Debug.LogWarning("[InstanceSceneBaker] 인스턴스 행렬이 0개 — matrices 를 채우세요(인스펙터/SetInstances/Capture).", this); return false; }
            return true;
        }

        DirectionalLight SunLight()
        {
            if (sun != null)
            {
                var c = sun.color.linear;
                return new DirectionalLight { Direction = sun.transform.forward, Color = new Vector3(c.r, c.g, c.b), Intensity = sun.intensity };
            }
            var lc = sunColor.linear;
            return new DirectionalLight
            {
                Direction = sunDirection.sqrMagnitude > 1e-8f ? sunDirection.normalized : Vector3.down,
                Color = new Vector3(lc.r, lc.g, lc.b),
                Intensity = sunIntensity,
            };
        }

        SH9[] BakeProbes(in BurstScene bScene, GpuScene gpu, ComputeShader cs, int kernel,
                         DirectionalLight sunDL, BurstSky sky, BakeQualitySettings q, Vector3[] points, uint probeSeed)
        {
            if (gpu != null)
            {
                // GPU 도 Burst 와 같은 피보나치 방향셋을 CPU 에서 계산해 업로드(교차검증 계약)
                var dirsNa = BurstSHBaker.FibonacchiSphere(dirCount, Allocator.Temp);
                var dirs = dirsNa.ToArray(); dirsNa.Dispose();
                float w = 4f * Mathf.PI / dirCount;
                return bounces > 1
                    ? GpuSHBaker.BakeMultiBounce(gpu, cs, kernel, sunDL, sky, q, probeSeed, points, dirs, w)
                    : GpuSHBaker.Bake(gpu, cs, kernel, sunDL, sky, points, dirs, w);
            }

            var pts = new NativeArray<Vector3>(points, Allocator.TempJob);
            try
            {
                var outp = bounces > 1
                    ? BurstSHBaker.BakeMultiBounce(bScene, sky, sunDL, q, pts, dirCount, probeSeed, Allocator.TempJob)
                    : BurstSHBaker.Bake(bScene, sky, sunDL, pts, dirCount, Allocator.TempJob);
                var arr = outp.ToArray();
                outp.Dispose();
                return arr;
            }
            finally { pts.Dispose(); }
        }

        // ───────────────────────── 저장 ─────────────────────────

        /// <summary>
        /// SH 계수 EXR(32-bit float RGBA) 인코딩. 폭 = 64 × 14 texel, 높이 = ⌈N/64⌉. 인스턴스 i(템플릿 순서대로 전역 번호)는
        /// 행 i/64 · 열 (i%64)·14 부터 float4 14개(상 프로브 7 + 하 프로브 7). 빈 칸은 0.
        /// </summary>
        internal static byte[] EncodeCoefficientsExr(InstanceSHBakeAsset asset)
        {
            int per = InstanceSHBakeAsset.Float4PerInstance;
            int n = asset.InstanceCount;
            int w = ExrInstancesPerRow * per;
            int h = Mathf.Max(1, (n + ExrInstancesPerRow - 1) / ExrInstancesPerRow);
            var px = new Color[w * h];
            int gi = 0;
            foreach (var t in asset.templates)
            {
                for (int i = 0; i < t.matrices.Length; i++, gi++)
                {
                    int row = gi / ExrInstancesPerRow, col = (gi % ExrInstancesPerRow) * per;
                    for (int k = 0; k < per; k++)
                    {
                        var v = t.shPacked[i * per + k];
                        px[row * w + col + k] = new Color(v.x, v.y, v.z, v.w);
                    }
                }
            }
            return BakeOutputUtil.EncodeExr(px, w, h, fullFloat: true);
        }

#if UNITY_EDITOR
        void SaveAssetAndExr(InstanceSHBakeAsset asset)
        {
            if (!UnityEditor.AssetDatabase.Contains(asset))
            {
                if (!BakeOutputUtil.EnsureFolder(outputFolder))
                { Debug.LogError($"[InstanceSceneBaker] outputFolder '{outputFolder}' 는 Assets/ 하위여야 함 — 저장 생략(메모리 에셋만).", this); return; }
                string baseName = BakeOutputUtil.SanitizeFileName($"{gameObject.scene.name}_{name}_InstanceSH");
                string path = UnityEditor.AssetDatabase.GenerateUniqueAssetPath($"{outputFolder}/{baseName}.asset");
                UnityEditor.AssetDatabase.CreateAsset(asset, path);
            }

            if (exportExr)
            {
                string exrPath = System.IO.Path.ChangeExtension(UnityEditor.AssetDatabase.GetAssetPath(asset), ".exr");
                // 데이터 텍스처: 선형·무압축·밉 없음·포인트 샘플(계수 보간 금지)
                asset.coefficientsExr = BakeOutputUtil.WriteExrAsset(exrPath, EncodeCoefficientsExr(asset), FilterMode.Point);
            }

            UnityEditor.EditorUtility.SetDirty(asset);
            UnityEditor.EditorUtility.SetDirty(this);
            UnityEditor.AssetDatabase.SaveAssets();
        }
#endif

        // ───────────────────────── 표시 ─────────────────────────

        void OnEnable() { if (renderOnEnable) RebuildRender(); }
        void OnDisable() => ClearRender();

        void Update()
        {
            for (int i = 0; i < _renderers.Count; i++) _renderers[i].Draw();
        }

        /// <summary>에셋 → 템플릿별 InstancedSHRenderer 재구성(베이크 없음).</summary>
        [ContextMenu("Rebuild Render From Asset")]
        public void RebuildRender()
        {
            ClearRender();
            if (bakeAsset == null) return;
            if (!bakeAsset.Validate(out string err))
            { Debug.LogWarning($"[InstanceSceneBaker] 에셋 '{bakeAsset.name}' 표시 불가: {err}", this); return; }
            var mat = ResolveMaterial();
            if (mat == null) return;

            foreach (var t in bakeAsset.templates)
            {
                if (t.matrices.Length == 0) continue;
                var sh = SHInstancedBuffer.CreateFromPacked(t.shPacked, InstanceSHBakeAsset.ProbesPerInstance);
                var alb = new Vector4(t.albedoLinear.x, t.albedoLinear.y, t.albedoLinear.z, 1f);
                _renderers.Add(new InstancedSHRenderer(t.mesh, mat, t.matrices, sh, WorldBounds(t.mesh, t.matrices), ownSh: true, albedo: alb));
            }
        }

        [ContextMenu("Clear Render")]
        public void ClearRender()
        {
            foreach (var r in _renderers) r?.Dispose();   // ownSh=true → SH 버퍼도 해제
            _renderers.Clear();
            if (_runtimeMat != null)
            {
                if (Application.isPlaying) Destroy(_runtimeMat); else DestroyImmediate(_runtimeMat);
                _runtimeMat = null;
            }
        }

        Material ResolveMaterial()
        {
            if (material != null) return material;     // 지정 머티리얼은 속성을 건드리지 않음(에셋 변경 방지)
            var shader = Shader.Find("HuskyLibs/InstancedSH_URP");
            if (shader == null) { Debug.LogError("[InstanceSceneBaker] 셰이더 'HuskyLibs/InstancedSH_URP' 없음 — material 을 지정하세요.", this); return null; }
            _runtimeMat = new Material(shader) { enableInstancing = true, hideFlags = HideFlags.DontSave };
            _runtimeMat.SetFloat("_Exposure", exposure);
            _runtimeMat.SetFloat("_SHStrength", shStrength);
            return _runtimeMat;
        }

        // 메시 로컬 AABB 를 각 인스턴스 행렬로 변환한 AABB 들의 합(|M|·extents — 회전·비균등 스케일 대응)
        internal static Bounds WorldBounds(Mesh mesh, Matrix4x4[] mats)
        {
            var lb = mesh.bounds;
            Vector3 e = lb.extents;
            var wb = new Bounds();
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                var c = m.MultiplyPoint3x4(lb.center);
                var we = new Vector3(
                    Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                    Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                    Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
                var b = new Bounds(c, we * 2f);
                if (i == 0) wb = b; else wb.Encapsulate(b);
            }
            return wb;
        }
    }
}
