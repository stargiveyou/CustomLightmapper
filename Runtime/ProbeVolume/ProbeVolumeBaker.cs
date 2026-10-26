using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Track B(B1-3) 실사용 컴포넌트 — 공간 프로브 볼륨 베이크 · 저장 · 인스턴스 적용(테스트 코드 없이 동작).
    ///
    /// 볼륨: 이 Transform 위치를 중심으로 하는 월드 축 정렬 박스(<see cref="volumeSize"/>) + 간격(<see cref="probeSpacing"/>)
    ///   → <see cref="ProbeGrid.FromBounds"/>.
    /// 씬 입력: ① 템플릿·인스턴스 행렬(<see cref="templates"/> — 인스펙터 / <see cref="SetInstances"/> / 자식 캡처, 식생·프롭)
    ///   ② 정적 루트(<see cref="staticRoots"/> 하위 MeshFilter — 지형·건물, 메시별 dedupe·머티리얼 색 알베도).
    ///   템플릿별 <see cref="OccluderMode"/>(결정 D5 1차: Opaque/Excluded) · twoSided(유효성에서 뒷면 판정 제외).
    /// 베이크: 격자 위치 → Burst/GPU SH 베이커(1바운스/다바운스, 신규 커널 없음) → 유효성(뒷면 비율) → dilation → <see cref="ProbeVolumeAsset"/>.
    /// 표시(결정 D2 1차): 템플릿 인스턴스의 상/하 대표점(<see cref="TemplateInstanceSource"/>.ComputeProbePoints 템플릿 단위 오버로드 — SH 트랙과 같은 규칙)
    ///   에서 볼륨 CPU 샘플 → 2-프로브 SH 버퍼 → <see cref="InstancedSHRenderer"/>(InstancedSH_URP 무수정).
    ///   (B1-4, 템플릿별 <see cref="ShadingMode.PerPixelVolume"/>) 픽셀마다 볼륨 Texture3D 샘플 — InstancedVegetation_URP +
    ///   <see cref="ProbeVolumeShaderBinding"/>(렌더러 MPB, 선택적으로 전역). 풀·큰 나무용.
    ///   배치가 바뀌면 "Rebuild Render" 만 — 재베이크 불필요. 플레이/빌드에선 에셋만 로드(OnEnable).
    /// 정점 AO(B1-5, 결정 D4): 템플릿 메시별 로컬 AO(<see cref="VertexAOBake"/>) → AO 메시 사본(uv3)을 에셋에 함께 저장,
    ///   표시 때 원본 대신 AO 메시 + _HUSKY_VERTEX_AO 머티리얼(간접광 × AO). 키워드 켠 머티리얼은 AO 메시 템플릿에만 쓴다(uv3 없으면 GI 0).
    /// </summary>
    [ExecuteAlways]
    public class ProbeVolumeBaker : MonoBehaviour
    {
        public enum Backend { Burst, Gpu }

        /// <summary>베이크 씬에서의 역할(결정 D5 1차 — 알파 커버리지(B1-6) 전까지).</summary>
        public enum OccluderMode
        {
            Opaque,     // 불투명 삼각형으로 트레이스(잎 카드면 수관 아래가 과하게 어두움)
            Excluded,   // 베이크 씬에서 제외(차폐·반사 없음 — 과하게 밝음). 표시·샘플은 그대로
        }

        /// <summary>표시 방식(결정 D2).</summary>
        public enum ShadingMode
        {
            InstanceProbes,   // B1-3: 인스턴스 상/하 대표점 2-프로브 → InstancedSH_URP (프롭·작은 나무)
            PerPixelVolume,   // B1-4: 픽셀마다 볼륨 샘플 → InstancedVegetation_URP (풀·큰 나무·알파 잎)
        }

        [Serializable]
        public class TemplateInput
        {
            public Mesh mesh;
            [Tooltip("표면 알베도(sRGB 색 — 베이크·셰이딩엔 Linear).")]
            public Color albedo = new Color(0.5f, 0.6f, 0.4f, 1f);
            [Tooltip("임시 입력: 인스턴스 월드 행렬(L2W). 인스펙터 / SetInstances / 'Capture Matrices From Sources'.")]
            public List<Matrix4x4> matrices = new List<Matrix4x4>();
            [Tooltip("'Capture Matrices From Sources' 가 이 Transform 의 직계 자식 행렬로 matrices 를 덮어씀.")]
            public Transform captureSource;
            [Tooltip("베이크 씬에서의 역할. Excluded = 차폐·반사에서 빼고 표시만.")]
            public OccluderMode occluder = OccluderMode.Opaque;
            [Tooltip("양면 얇은 메시(잎 카드·단면 플레인) — 유효성 판정에서 뒷면 히트를 세지 않음.")]
            public bool twoSided;
            [Tooltip("이 컴포넌트가 볼륨 조명으로 그림.")]
            public bool render = true;
            [Tooltip("InstanceProbes = 인스턴스 상/하 대표점 2-프로브(InstancedSH_URP) / PerPixelVolume = 픽셀마다 볼륨 샘플(InstancedVegetation_URP — 풀·큰 나무).")]
            public ShadingMode shading = ShadingMode.InstanceProbes;
            [Tooltip("이 템플릿 전용 머티리얼(예: 잎 텍스처·알파 클립). 지정하면 알베도 MPB 주입 안 함(머티리얼 색 사용). 비우면 셰이딩 모드별 공용 머티리얼.")]
            public Material material;
            [Tooltip("이 템플릿 메시의 정점 AO(템플릿 로컬 자기차폐) 를 굽고 표시에 적용(B1-5). Read/Write 필요.")]
            public bool vertexAO = true;
        }

        [Header("볼륨 (이 Transform 위치 중심, 월드 축 정렬)")]
        public Vector3 volumeSize = new Vector3(32f, 8f, 32f);
        [Tooltip("축별 프로브 간격(수평/수직 분리 가능).")]
        public Vector3 probeSpacing = new Vector3(2f, 1f, 2f);
        [Tooltip("이보다 많으면 베이크 거부(메모리·시간 보호).")]
        [Min(1)] public int maxProbeCount = 1 << 21;

        [Header("씬 입력 — 템플릿·인스턴스 (임시)")]
        public TemplateInput[] templates = new TemplateInput[0];

        [Header("씬 입력 — 정적 메시 (지형·건물)")]
        [Tooltip("하위 MeshFilter 전부를 차폐·반사로 넣음(Read/Write 필요). 알베도는 머티리얼 _BaseColor/_Color.")]
        public Transform[] staticRoots = new Transform[0];
        public Color staticDefaultAlbedo = new Color(0.5f, 0.5f, 0.5f, 1f);
        [Tooltip("정적 메시를 양면으로 취급(유효성 뒷면 판정 제외).")]
        public bool staticTwoSided;

        [Header("광원 / 하늘")]
        [Tooltip("씬 Directional Light. 비우면 아래 대체값.")]
        public Light sun;
        public Vector3 sunDirection = new Vector3(-0.3f, -1f, -0.2f);
        public Color sunColor = Color.white;
        [Min(0f)] public float sunIntensity = 1.3f;
        [Tooltip("하늘 위쪽 복사휘도(Linear RGB).")]
        public Vector3 skyTop = new Vector3(0.5f, 0.7f, 1.0f);
        [Tooltip("하늘 아래쪽 복사휘도(Linear RGB).")]
        public Vector3 skyBottom = new Vector3(0.1f, 0.1f, 0.12f);

        [Header("베이크")]
        public Backend backend = Backend.Burst;
        [Tooltip("1 = 1바운스+앰비언트 근사, ≥2 = 경로추적 다바운스. 둘 다 물리 규약(ρ/π·E).")]
        [Range(1, 8)] public int bounces = 1;
        [Min(16)] public int dirCount = 256;
        public uint seed = 12345;
        [Tooltip("Gpu 백엔드용 PathTrace.compute. 비우면 Resources → (에디터) 프로젝트 경로.")]
        public ComputeShader pathTraceShader;

        [Header("유효성 · dilation (B1-2)")]
        public bool computeValidity = true;
        [Min(4)] public int validityDirCount = BurstProbeValidity.DefaultDirCount;
        [Range(0f, 1f)] public float validityThreshold = BurstProbeValidity.DefaultThreshold;
        [Tooltip("뒷면 판정 레이 길이(0 = 무제한). 먼 단면 지오메트리를 무시하려면 간격의 몇 배로.")]
        [Min(0f)] public float validityMaxDistance = 0f;
        [Min(0)] public int dilationIterations = 64;

        [Header("결과")]
        [Tooltip("베이크 결과(런타임은 이것만 읽음). 비우면 저장 시 outputFolder 에 새로 만든다.")]
        public ProbeVolumeAsset volumeAsset;
        public string outputFolder = "Assets/CustomLightmapper/BakeOutput";   // 소비 프로젝트 Assets/ 하위(패키지 폴더 밖)
        [Tooltip("계수 EXR 사본도 저장(32-bit float, z 슬라이스 타일).")]
        public bool exportExr = true;

        [Header("표시 (인스턴스 대표점 샘플 → InstancedSH_URP)")]
        public AnchorMode anchorMode = AnchorMode.SurfaceNormalOffset;
        public float surfaceLift = 0.15f;
        [Tooltip("켜면 리프트 = surfaceLift × 템플릿 높이.")]
        public bool liftRelativeToBounds = true;
        [Tooltip("CPU 샘플에서 무효 프로브 가중치 0(dilation 과 이중 방어).")]
        public bool useValidityWeights = true;
        [Tooltip("비우면 HuskyLibs/InstancedSH_URP 로 런타임 머티리얼 생성(이때만 exposure·shStrength 적용).")]
        public Material material;
        [Range(0f, 4f)] public float exposure = 1f;
        [Range(0f, 4f)] public float shStrength = 1f;
        public bool renderOnEnable = true;

        [Header("표시 — 픽셀 볼륨 샘플 (B1-4, PerPixelVolume 템플릿)")]
        [Tooltip("PerPixelVolume 공용 머티리얼. 비우면 HuskyLibs/InstancedVegetation_URP 로 런타임 생성(exposure·shStrength·translucency 적용).")]
        public Material vegetationMaterial;
        [Tooltip("잎 투과(런타임 생성 머티리얼만): 간접 E(n) + t·E(-n), 직사 뒷면 t 배.")]
        [Range(0f, 1f)] public float translucency = 0.3f;
        [Tooltip("샘플 위치를 노멀 방향으로 미는 거리 = 이 값 × 최소 프로브 간격(얇은 벽·지면 누수 완화).")]
        [Range(0f, 1f)] public float normalBiasCells = 0.25f;
        [Tooltip("볼륨 밖 페이드 폭(셀) — 경계에서 이 거리 동안 하늘 폴백 SH 로 전환. 0 = 경계 클램프만.")]
        [Min(0f)] public float edgeFadeCells = 1f;
        [Tooltip("전역 셰이더 변수로도 바인딩 — 이 컴포넌트 밖의 렌더러(HuskyProbeVolume.hlsl 사용)도 이 볼륨을 봄(볼륨 하나만).")]
        public bool bindGlobal;

        [Header("정점 AO (B1-5, 템플릿 로컬)")]
        [Tooltip("베이크 때 템플릿 메시별 정점 AO 메시(uv3)를 함께 만들어 에셋에 저장.")]
        public bool bakeVertexAO = true;
        public VertexAOBake.Settings vertexAOSettings = VertexAOBake.Settings.Default;
        [Tooltip("표시 때 AO 메시 + _HUSKY_VERTEX_AO 사용(에셋에 AO 가 있는 템플릿만).")]
        public bool applyVertexAO = true;
        [Tooltip("런타임 생성 머티리얼의 _VertexAOStrength(0 = AO 무시, 1 = 그대로).")]
        [Range(0f, 1f)] public float vertexAOStrength = 1f;

        [Header("기즈모")]
        public bool drawProbes = true;
        [Min(1)] public int maxGizmoProbes = 4096;
        [Min(0.001f)] public float gizmoProbeSize = 0.1f;

        readonly List<InstancedSHRenderer> _renderers = new List<InstancedSHRenderer>();
        readonly Material[] _runtimeMats = new Material[4];   // [perPixel·2 + ao] — AO 키워드 켠 머티리얼은 AO 메시에만
        bool _boundGlobal;

        internal int RendererCount => _renderers.Count;
        internal IReadOnlyList<InstancedSHRenderer> Renderers => _renderers;

        public ProbeGrid Grid => ProbeGrid.FromBounds(new Bounds(transform.position, volumeSize), probeSpacing);

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
            Debug.Log($"[ProbeVolumeBaker] 행렬 캡처: 소스 {sources}개 → 인스턴스 {captured}개.", this);
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        // ───────────────────────── 베이크 ─────────────────────────

        /// <summary>베이크 → 에셋(에디터면 디스크 저장 + EXR) → 표시 재구성.</summary>
        [ContextMenu("Bake")]
        public void Bake()
        {
            var built = BakeToAsset(out string usedBackend);
            if (built == null) return;
#if UNITY_EDITOR
            SaveAsset(built);
#else
            volumeAsset = built;
#endif
            var a = volumeAsset;
            Debug.Log($"[ProbeVolumeBaker] 베이크 완료: {a.grid}, 무효 {a.invalidCount} → dilation {a.dilatedCount}, " +
                      $"bounces={a.bounces}, dirs={a.dirCount}, backend={usedBackend}", this);
            if (renderOnEnable && isActiveAndEnabled) RebuildRender();
        }

        /// <summary>디스크 저장 없이 베이크해 새 에셋 객체(텍스처 7장 포함, 런타임 객체)를 만든다. 실패 시 null. 파괴는 호출측.</summary>
        internal ProbeVolumeAsset BakeToAsset(out string usedBackend)
        {
            usedBackend = null;
            var grid = Grid;
            if (!grid.IsValid) { Debug.LogError($"[ProbeVolumeBaker] 격자 무효(간격 > 0 필요): {grid}", this); return null; }
            if (grid.Count > maxProbeCount)
            { Debug.LogError($"[ProbeVolumeBaker] 프로브 {grid.Count}개 > maxProbeCount {maxProbeCount} — 간격을 키우거나 볼륨을 줄이세요.", this); return null; }
            Vector3Int d = grid.dims;
            if (d.x > ProbeVolumeAsset.MaxTextureDim || d.y > ProbeVolumeAsset.MaxTextureDim || d.z > ProbeVolumeAsset.MaxTextureDim)
            { Debug.LogError($"[ProbeVolumeBaker] 격자 {d} 가 Texture3D 축 한도 {ProbeVolumeAsset.MaxTextureDim} 초과.", this); return null; }
            if (!BuildSceneInput(out var tris, out var albedo, out var insts, out var countBackface)) return null;

            using var bvh = new TwoLevelBVH(tris, insts, Allocator.TempJob, BVH.BuildQuality.SAH);
            using var bScene = BurstScene.Create(bvh, albedo, Allocator.TempJob);
            var sunDL = SunLight();
            var sky = BurstSky.Gradient(skyTop, skyBottom);
            var q = new BakeQualitySettings { MaxBounces = bounces, RRStartDepth = 3, RayBias = 1e-4f };

            SH9[] sh = null;
            if (backend == Backend.Gpu)
            {
                var cs = SystemInfo.supportsComputeShaders ? PathTraceShader.Resolve(pathTraceShader) : null;
                string kName = ProbeGrid.GpuKernelName(q);
                if (cs != null && cs.HasKernel(kName) && grid.Count <= ProbeGrid.MaxGpuProbes)
                {
                    using var gpu = new GpuScene(bScene);
                    sh = grid.BakeGpu(gpu, cs, cs.FindKernel(kName), sunDL, sky, q, dirCount, seed);
                    usedBackend = "Gpu";
                }
                else Debug.LogWarning("[ProbeVolumeBaker] GPU 사용 불가(compute 미지원/PathTrace 로드/커널/프로브 수) → Burst 폴백.", this);
            }
            if (sh == null)
            {
                sh = grid.BakeBurst(bScene, sky, sunDL, q, dirCount, seed);
                usedBackend = "Burst";
            }

            bool[] valid;
            int invalid = 0, dilated = 0;
            if (computeValidity)
            {
                float maxDist = validityMaxDistance > 0f ? validityMaxDistance : float.MaxValue;
                valid = BurstProbeValidity.ComputeGrid(bScene, grid, out _, validityDirCount, validityThreshold, countBackface, maxDist);
                foreach (bool v in valid) if (!v) invalid++;
                if (invalid > 0 && dilationIterations > 0) dilated = BurstProbeValidity.Dilate(grid, sh, valid, dilationIterations);
            }
            else
            {
                valid = new bool[grid.Count];
                for (int i = 0; i < valid.Length; i++) valid[i] = true;
            }

            var asset = ScriptableObject.CreateInstance<ProbeVolumeAsset>();
            asset.grid = grid;
            asset.shPacked = SHInstancedBuffer.Flatten(sh);
            asset.valid = valid;
            asset.textures = ProbeVolumeAsset.CreateTextures(grid, asset.shPacked);
            asset.fallbackSH = SHInstancedBuffer.Flatten(new[] { SkySH(sky, dirCount) });
            asset.vertexAO = bakeVertexAO ? BakeVertexAO() : Array.Empty<ProbeVolumeAsset.VertexAOEntry>();
            asset.vertexAOSettings = vertexAOSettings;
            asset.dirCount = dirCount;
            asset.bounces = bounces;
            asset.seed = seed;
            asset.backend = usedBackend;
            asset.bakedAtUtc = DateTime.UtcNow.ToString("o");
            asset.validityComputed = computeValidity;
            asset.validityThreshold = validityThreshold;
            asset.invalidCount = invalid;
            asset.dilatedCount = dilated;
            return asset;
        }

        /// <summary>
        /// 템플릿 메시별 정점 AO(같은 메시를 쓰는 템플릿은 1개로 — twoSided 는 OR). vertexAO 꺼진 템플릿·Read/Write 꺼진 메시는 건너뜀(경고).
        /// </summary>
        internal ProbeVolumeAsset.VertexAOEntry[] BakeVertexAO()
        {
            var list = new List<ProbeVolumeAsset.VertexAOEntry>();
            var byMesh = new Dictionary<Mesh, ProbeVolumeAsset.VertexAOEntry>();
            if (templates == null) return list.ToArray();
            foreach (var tp in templates)
            {
                if (tp == null || tp.mesh == null || !tp.vertexAO) continue;
                if (byMesh.TryGetValue(tp.mesh, out var e)) { e.twoSided |= tp.twoSided; continue; }
                if (!tp.mesh.isReadable)
                { Debug.LogWarning($"[ProbeVolumeBaker] 정점 AO 생략: 메시 '{tp.mesh.name}' Read/Write 꺼짐.", this); continue; }
                e = new ProbeVolumeAsset.VertexAOEntry { source = tp.mesh, twoSided = tp.twoSided };
                byMesh[tp.mesh] = e;
                list.Add(e);
            }
            foreach (var e in list) e.aoMesh = VertexAOBake.Bake(e.source, vertexAOSettings, e.twoSided);
            return list.ToArray();
        }

        /// <summary>하늘만의 SH(빈 씬 베이크 — 같은 방향셋·규약). 픽셀 셰이더의 볼륨 밖 폴백.</summary>
        internal static SH9 SkySH(BurstSky sky, int dirCount)
        {
            using var bvh = new TwoLevelBVH(new Tri[0][], new TwoLevelBVH.Instance[0], Allocator.TempJob);
            using var scene = BurstScene.Create(bvh, new Vector3[0], Allocator.TempJob);
            var pts = new NativeArray<Vector3>(1, Allocator.TempJob);
            var sh = BurstSHBaker.Bake(scene, sky, default, pts, dirCount, Allocator.TempJob);
            SH9 r = sh[0];
            sh.Dispose(); pts.Dispose();
            return r;
        }

        /// <summary>
        /// 베이크 씬 입력: 템플릿(Opaque 만, 순서대로) → 정적 메시(유니크 메시별). 반환 배열은 메시(BLAS) 인덱스 순.
        /// 메시가 하나도 없으면 빈 씬(하늘만)으로 진행. Read/Write 꺼진 메시는 오류.
        /// </summary>
        internal bool BuildSceneInput(out Tri[][] tris, out Vector3[] albedo, out TwoLevelBVH.Instance[] insts, out bool[] countBackface)
        {
            var triList = new List<Tri[]>();
            var albList = new List<Vector3>();
            var cbList = new List<bool>();
            var instList = new List<TwoLevelBVH.Instance>();
            tris = null; albedo = null; insts = null; countBackface = null;

            if (templates != null)
            {
                for (int t = 0; t < templates.Length; t++)
                {
                    var tp = templates[t];
                    if (tp == null || tp.mesh == null || tp.occluder == OccluderMode.Excluded) continue;
                    if (tp.matrices == null || tp.matrices.Count == 0) continue;
                    if (!tp.mesh.isReadable)
                    { Debug.LogError($"[ProbeVolumeBaker] 템플릿 {t} 메시 '{tp.mesh.name}' Read/Write 꺼짐 — 임포트 설정에서 켜거나 Excluded 로.", this); return false; }
                    int mi = triList.Count;
                    triList.Add(TemplateInstanceSource.MeshToLocalTris(tp.mesh));
                    var lin = tp.albedo.linear;
                    albList.Add(new Vector3(lin.r, lin.g, lin.b));
                    cbList.Add(!tp.twoSided);
                    foreach (var m in tp.matrices) instList.Add(new TwoLevelBVH.Instance { MeshIndex = mi, LocalToWorld = m });
                }
            }

            if (staticRoots != null)
            {
                var meshToIdx = new Dictionary<Mesh, int>();
                var seen = new HashSet<MeshFilter>();
                foreach (var root in staticRoots)
                {
                    if (root == null) continue;
                    foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
                    {
                        var mesh = mf.sharedMesh;
                        if (mesh == null || !seen.Add(mf)) continue;
                        if (!mesh.isReadable)
                        { Debug.LogWarning($"[ProbeVolumeBaker] 정적 메시 '{mesh.name}'({mf.name}) Read/Write 꺼짐 — 베이크 씬에서 제외.", this); continue; }
                        if (!meshToIdx.TryGetValue(mesh, out int mi))
                        {
                            mi = triList.Count;
                            meshToIdx[mesh] = mi;
                            triList.Add(TemplateInstanceSource.MeshToLocalTris(mesh));
                            albList.Add(ReadAlbedo(mf.GetComponent<MeshRenderer>(), staticDefaultAlbedo));
                            cbList.Add(!staticTwoSided);
                        }
                        instList.Add(new TwoLevelBVH.Instance { MeshIndex = mi, LocalToWorld = mf.transform.localToWorldMatrix });
                    }
                }
            }

            tris = triList.ToArray();
            albedo = albList.ToArray();
            insts = instList.ToArray();
            countBackface = cbList.ToArray();
            return true;
        }

        // 머티리얼 baseColor → Linear 알베도(≤1). URP _BaseColor / Built-in _Color, 없으면 기본값.
        static Vector3 ReadAlbedo(MeshRenderer r, Color fallback)
        {
            var m = r != null ? r.sharedMaterial : null;
            Color col = fallback;
            if (m != null && m.HasProperty("_BaseColor")) col = m.GetColor("_BaseColor");
            else if (m != null && m.HasProperty("_Color")) col = m.GetColor("_Color");
            Color lin = col.linear;
            return new Vector3(Mathf.Clamp01(lin.r), Mathf.Clamp01(lin.g), Mathf.Clamp01(lin.b));
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

        // ───────────────────────── 저장 ─────────────────────────

#if UNITY_EDITOR
        /// <summary>
        /// 새로 만든 에셋을 디스크에 저장. 기존 volumeAsset 파일이 있으면 같은 파일(GUID 유지 → 씬 참조 유지)에
        /// 서브에셋(Texture3D×7)을 갈아 끼운다. + EXR 사본.
        /// </summary>
        void SaveAsset(ProbeVolumeAsset built)
        {
            string path = volumeAsset != null ? UnityEditor.AssetDatabase.GetAssetPath(volumeAsset) : null;
            ProbeVolumeAsset target;
            if (!string.IsNullOrEmpty(path))
            {
                target = volumeAsset;
                foreach (var sub in UnityEditor.AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
                {
                    if (sub == null) continue;
                    UnityEditor.AssetDatabase.RemoveObjectFromAsset(sub);
                    DestroyImmediate(sub, true);
                }
                string keepName = target.name;   // CopySerialized 는 이름까지 복사 → 파일명과 메인 객체명 불일치 방지
                UnityEditor.EditorUtility.CopySerialized(built, target);
                target.name = keepName;
                DestroyImmediate(built);
            }
            else
            {
                if (!BakeOutputUtil.EnsureFolder(outputFolder))
                {
                    Debug.LogError($"[ProbeVolumeBaker] outputFolder '{outputFolder}' 는 Assets/ 하위여야 함 — 메모리 에셋만 사용.", this);
                    volumeAsset = built;
                    return;
                }
                string baseName = BakeOutputUtil.SanitizeFileName($"{gameObject.scene.name}_{name}_ProbeVolume");
                path = UnityEditor.AssetDatabase.GenerateUniqueAssetPath($"{outputFolder}/{baseName}.asset");
                UnityEditor.AssetDatabase.CreateAsset(built, path);
                target = built;
            }

            foreach (var tex in target.textures)
                if (tex != null) UnityEditor.AssetDatabase.AddObjectToAsset(tex, target);
            if (target.vertexAO != null)
                foreach (var e in target.vertexAO)
                    if (e?.aoMesh != null) UnityEditor.AssetDatabase.AddObjectToAsset(e.aoMesh, target);

            target.coefficientsExr = null;
            if (exportExr)
            {
                var exr = ProbeVolumeAsset.EncodeExr(target.grid, target.shPacked);
                if (exr != null)
                    target.coefficientsExr = BakeOutputUtil.WriteExrAsset(System.IO.Path.ChangeExtension(path, ".exr"), exr, FilterMode.Point);
                else
                    Debug.LogWarning("[ProbeVolumeBaker] EXR 사본 높이가 한도를 넘어 생략(SO 는 정상 저장).", this);
            }

            UnityEditor.EditorUtility.SetDirty(target);
            UnityEditor.AssetDatabase.SaveAssets();
            volumeAsset = target;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

        // ───────────────────────── 표시 ─────────────────────────

        void OnEnable() { if (renderOnEnable) RebuildRender(); }
        void OnDisable() => ClearRender();

        void Update()
        {
            for (int i = 0; i < _renderers.Count; i++) _renderers[i].Draw();
        }

        /// <summary>
        /// 템플릿 t 인스턴스들의 2-프로브 SH(인스턴스 i → [2i]=상, [2i+1]=하 — InstancedSH_URP iid*14 순서)를 볼륨에서 샘플.
        /// 배치가 바뀌어도 베이크 없이 이것만 다시. 에셋 없거나 무효면 null.
        /// </summary>
        public SH9[] SampleInstances(int templateIndex)
        {
            if (volumeAsset == null || !volumeAsset.Validate(out _)) return null;
            var tp = templates[templateIndex];
            if (tp == null || tp.mesh == null || tp.matrices == null || tp.matrices.Count == 0) return null;
            InstanceProbePoints(tp, out var top, out var bottom);
            var pts = new Vector3[top.Length * 2];
            for (int i = 0; i < top.Length; i++) { pts[2 * i] = top[i]; pts[2 * i + 1] = bottom[i]; }
            return volumeAsset.Sample(pts, useValidityWeights);
        }

        /// <summary>
        /// 인스턴스 상/하 대표점(SH 트랙과 같은 규칙). SurfaceNormalOffset 은 삼각형이 필요 — Read/Write 꺼진 메시는 LocalTopLift 로 대체.
        /// </summary>
        internal void InstanceProbePoints(TemplateInput tp, out Vector3[] top, out Vector3[] bottom)
        {
            var mode = anchorMode;
            Tri[] tris = null;
            if (mode == AnchorMode.SurfaceNormalOffset)
            {
                if (tp.mesh.isReadable) tris = TemplateInstanceSource.MeshToLocalTris(tp.mesh);
                else mode = AnchorMode.LocalTopLift;
            }
            TemplateInstanceSource.ComputeProbePoints(tp.mesh, tris, tp.matrices.ToArray(), mode, surfaceLift, liftRelativeToBounds, out top, out bottom);
        }

        /// <summary>
        /// 에셋 → 템플릿별 렌더러(베이크 없음). InstanceProbes = 볼륨 CPU 샘플 2-프로브 SH 버퍼(InstancedSH_URP),
        /// PerPixelVolume = SH 버퍼 없이 볼륨 텍스처를 렌더러 MPB 에 바인딩(InstancedVegetation_URP). bindGlobal 이면 전역도.
        /// 정점 AO(applyVertexAO + 에셋에 AO 메시) → 원본 대신 AO 메시를 그리고, 런타임 머티리얼은 _HUSKY_VERTEX_AO 판.
        /// </summary>
        [ContextMenu("Rebuild Render")]
        public void RebuildRender()
        {
            ClearRender();
            if (volumeAsset == null || templates == null) return;
            if (!volumeAsset.Validate(out string err))
            { Debug.LogWarning($"[ProbeVolumeBaker] 에셋 '{volumeAsset.name}' 사용 불가: {err}", this); return; }
            if (bindGlobal)
            {
                ProbeVolumeShaderBinding.SetGlobal(volumeAsset, normalBiasCells, edgeFadeCells);
                _boundGlobal = true;
            }

            for (int t = 0; t < templates.Length; t++)
            {
                var tp = templates[t];
                if (tp == null || !tp.render || tp.mesh == null || tp.matrices == null || tp.matrices.Count == 0) continue;
                bool perPixel = tp.shading == ShadingMode.PerPixelVolume;
                var aoMesh = applyVertexAO && tp.vertexAO ? volumeAsset.FindAOMesh(tp.mesh) : null;
                var drawMesh = aoMesh != null ? aoMesh : tp.mesh;
                var mat = tp.material != null ? tp.material : SharedMaterial(perPixel, aoMesh != null);
                if (mat == null) continue;
                if (aoMesh == null && mat.IsKeywordEnabled(VertexAOKeyword))
                    Debug.LogWarning($"[ProbeVolumeBaker] 템플릿 {t}: 머티리얼 '{mat.name}' 이 {VertexAOKeyword} 인데 AO 메시가 없음 — uv3 가 없어 간접광이 0 이 됨.", this);
                var lin = tp.albedo.linear;
                Vector4? albedo = tp.material != null ? (Vector4?)null : new Vector4(lin.r, lin.g, lin.b, 1f);
                var mats = tp.matrices.ToArray();
                var bounds = InstanceSceneBaker.WorldBounds(tp.mesh, mats);

                if (perPixel)
                {
                    var r = new InstancedSHRenderer(drawMesh, mat, mats, null, bounds, albedo: albedo);
                    ProbeVolumeShaderBinding.Bind(r.Properties, volumeAsset, normalBiasCells, edgeFadeCells);
                    _renderers.Add(r);
                }
                else
                {
                    var sh = SampleInstances(t);
                    var buf = SHInstancedBuffer.CreateFromPacked(SHInstancedBuffer.Flatten(sh), InstanceSHBakeAsset.ProbesPerInstance);
                    _renderers.Add(new InstancedSHRenderer(drawMesh, mat, mats, buf, bounds, ownSh: true, albedo: albedo));
                }
            }
        }

        [ContextMenu("Clear Render")]
        public void ClearRender()
        {
            foreach (var r in _renderers) r?.Dispose();
            _renderers.Clear();
            for (int i = 0; i < _runtimeMats.Length; i++)
            {
                if (_runtimeMats[i] == null) continue;
                if (Application.isPlaying) Destroy(_runtimeMats[i]); else DestroyImmediate(_runtimeMats[i]);
                _runtimeMats[i] = null;
            }
            if (_boundGlobal) { ProbeVolumeShaderBinding.ClearGlobal(); _boundGlobal = false; }
        }

        public const string VertexAOKeyword = "_HUSKY_VERTEX_AO";

        /// <summary>
        /// 모드별 공용 머티리얼: 지정(material / vegetationMaterial)이 있으면 그대로(AO 키워드는 사용자 책임),
        /// 없으면 런타임 생성 — AO 판(키워드 + _VertexAOStrength)과 비-AO 판을 따로 둔다.
        /// </summary>
        Material SharedMaterial(bool perPixel, bool ao)
        {
            var assigned = perPixel ? vegetationMaterial : material;
            if (assigned != null) return assigned;
            int slot = (perPixel ? 2 : 0) + (ao ? 1 : 0);
            if (_runtimeMats[slot] != null) return _runtimeMats[slot];
            string shaderName = perPixel ? ProbeVolumeShaderBinding.VegetationShaderName : "HuskyLibs/InstancedSH_URP";
            var shader = Shader.Find(shaderName);
            if (shader == null) { Debug.LogError($"[ProbeVolumeBaker] 셰이더 '{shaderName}' 없음 — {(perPixel ? "vegetationMaterial" : "material")} 을 지정하세요.", this); return null; }
            var m = new Material(shader) { enableInstancing = true, hideFlags = HideFlags.DontSave };
            m.SetFloat("_Exposure", exposure);
            m.SetFloat("_SHStrength", shStrength);
            if (perPixel) m.SetFloat("_Translucency", translucency);
            if (ao)
            {
                m.EnableKeyword(VertexAOKeyword);
                m.SetFloat("_UseVertexAO", 1f);   // [Toggle] 프로퍼티와 키워드 일치(인스펙터 표시)
                m.SetFloat("_VertexAOStrength", vertexAOStrength);
            }
            _runtimeMats[slot] = m;
            return m;
        }

        // ───────────────────────── 기즈모 ─────────────────────────

#if UNITY_EDITOR
        // 볼륨 박스 + 프로브(유효 = 위쪽 조도 E(up) 색, 무효 = 빨강 와이어). 많으면 stride 로 솎음.
        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.8f);
            Gizmos.DrawWireCube(transform.position, volumeSize);
            if (!drawProbes || volumeAsset == null || !volumeAsset.Validate(out _)) return;

            var g = volumeAsset.grid;
            Gizmos.color = new Color(1f, 1f, 1f, 0.3f);
            Gizmos.DrawWireCube((g.origin + g.MaxCorner) * 0.5f, g.MaxCorner - g.origin);
            int n = g.Count;
            int stride = Mathf.Max(1, Mathf.CeilToInt((float)n / Mathf.Max(1, maxGizmoProbes)));
            bool hasValid = volumeAsset.valid != null && volumeAsset.valid.Length == n;
            for (int i = 0; i < n; i += stride)
            {
                Vector3 p = g.Position(i);
                if (hasValid && !volumeAsset.valid[i])
                {
                    Gizmos.color = Color.red;
                    Gizmos.DrawWireCube(p, Vector3.one * gizmoProbeSize * 1.5f);
                    continue;
                }
                Vector3 e = volumeAsset.GetProbe(i).Evaluate(Vector3.up) * (exposure / Mathf.PI);
                Gizmos.color = new Color(Gamma(e.x), Gamma(e.y), Gamma(e.z), 1f);
                Gizmos.DrawSphere(p, gizmoProbeSize);
            }
        }

        static float Gamma(float v) => Mathf.Clamp01(Mathf.Pow(Mathf.Max(0f, v), 1f / 2.2f));
#endif
    }
}
