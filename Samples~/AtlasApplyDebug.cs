using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// A4(per-instance ST) + TexelMapper 통합 검증 컴포넌트.
    /// 여러 인스턴스를 모아 ① 월드면적→ST/페이지 할당, ② 각 인스턴스의 LumelMap 을
    /// 그 ST 영역에 그대로 구워 Texture2DArray 아틀라스를 만들고, ③ 조립된 uv2 메시 + ST 를
    /// 오브젝트에 입혀 LightmapDebug.shader 로 렌더한다.
    ///
    /// 화면에 베이크 데이터가 표면에 정확히 정렬돼 보이면 ST 매핑·텍셀 복원이 일관된 것.
    /// ST 가 틀리면 옆 인스턴스 영역/깨진 색이 보인다. (라이팅 없음 — 순수 매핑 검증)
    ///
    /// 인스펙터 우클릭 → "Bake & Apply" 실행, "Restore Originals" 로 원복.
    /// </summary>
    [ExecuteAlways]
    public class AtlasApplyDebug : MonoBehaviour
    {
        public enum BakeMode { PerInstanceColor, WorldNormal, Checker, Radiance, RadianceGI }
        public enum OccluderKind { BruteForce, BVH }

        [Header("Input")]
        [Tooltip("비우면 자기 자신+자식의 MeshFilter 를 모두 수집.")]
        [SerializeField] MeshFilter[] targets;

        [Tooltip("차폐 전용(occluder-only) — 차폐 씬에만 참여, 라이트맵 베이크/메시·머티리얼 스왑 대상 아님. 지형 기획: halo·프롭·건물.")]
        public MeshFilter[] occluders;
        public SegmentationSettings segmentation = SegmentationSettings.Default;

        [Header("Allocation (LightmapAllocator)")]
        public int atlasResolution = 1024;
        public float texelsPerWorldUnit = 16f;
        public int gutterTexels = 2;
        public int maxPages = 8;

        [Header("Bake")]
        [Tooltip("PerInstanceColor/WorldNormal/Checker: 매핑 검증(라이팅 없음) / Radiance: 텍셀마다 EvaluateRadiance(AO+Direct+그림자) 베이크 — 실제 라이트맵")]
        public BakeMode mode = BakeMode.PerInstanceColor;
        [Range(0f, 0.05f)] public float chartGutter = 0.01f;
        [Tooltip("차트가 덮지 않은 빈 텍셀의 배경색(거터/여백 가시화).")]
        public Color background = new Color(0.04f, 0.04f, 0.06f, 1f);
        [Min(1)] public int checkerSize = 8;

        [Header("Post Process (Dilation)")]
        [Tooltip("거터/배경 텍셀을 인접 valid 평균으로 링 단위 확장 → 차트 경계의 검은 시임 제거(LightmapPostProcess.Dilate).")]
        public bool dilate = true;
        [Tooltip("Dilation 패스 수(=바깥으로 확장되는 텍셀 링 수). gutterTexels 근처 값 권장.")]
        [Min(0)] public int dilateIterations = 4;
        [Tooltip("Burst Job 병렬판(LightmapPostProcessBurstJob) 사용. 끄면 순수 C# 직렬판(LightmapPostProcess) — 결과 비교용.")]
        public bool dilateBurst = true;
        [Tooltip("아틀라스 샘플링 필터. Point=텍셀 그대로(블록) / Bilinear=텍셀 보간(GI 노이즈·계단 시각적 완화).")]
        public FilterMode atlasFilter = FilterMode.Point;

        [Header("Denoise (À-trous Joint Bilateral — Radiance/RadianceGI 전용, Seam Stitch 직전)")]
        [Tooltip("몬테카를로 노이즈(그레인) 제거. 텍셀별 월드 노멀·위치·색 가이드의 에지 보존 필터 — 하드 엣지·차트 경계·그림자 경계는 보존하고 평탄면 노이즈만 평활(LightmapDenoise).")]
        public bool denoise = true;
        [Tooltip("À-trous 반복 수(step=1,2,4…). 3이면 유효 반경 ≈17텍셀. 클수록 매끈하지만 비용 증가.")]
        [Range(1, 5)] public int denoiseIterations = 3;
        [Tooltip("노멀 가중 지수 pow(max(0,n·n'),p). 클수록 각진 면 분리 강함(큐브 모서리 등 하드 엣지 보존). 16~64 권장.")]
        [Range(1f, 128f)] public float denoiseNormalPower = 32f;
        [Tooltip("월드 위치 가우시안 σ(텍셀 단위 — texelsPerWorldUnit 로 월드 변환). 아틀라스에서 인접해도 월드에서 먼 차트끼리 섞이지 않게 차단. 1~4 권장.")]
        [Range(0.5f, 8f)] public float denoisePositionSigmaTexels = 2f;
        [Tooltip("색 range σ(Linear RGB L2 거리). 작을수록 그림자 경계 같은 라이팅 엣지 보존 강함 — 너무 작으면 노이즈도 엣지로 인식해 평활이 약해짐. 0.15~0.35 권장.")]
        [Range(0.01f, 1f)] public float denoiseColorSigma = 0.25f;
        [Tooltip("Burst Job 병렬판(LightmapDenoiseBurstJob) 사용. 끄면 순수 C# 직렬판 — 결과 비교용.")]
        public bool denoiseBurst = true;

        [Header("Seam Stitch (시임 스티칭, Dilation 직전)")]
        [Tooltip("Tier1(정점): 같은 원본 정점에서 갈라진 차트 경계 텍셀들을 그룹 평균 → 정점 불연속 제거.")]
        public bool seamStitchTier1 = true;
        [Tooltip("Tier2(모서리): 차트 경계 모서리를 공유 t로 DDA 순회하며 양쪽 텍셀 평균 → 모서리 중간 불연속 제거.")]
        public bool seamStitchTier2 = true;
        [Tooltip("Tier2 Jacobi 반복 수(>1이면 재완화).")]
        [Min(1)] public int seamStitchIterations = 1;
        [Tooltip("시임 양쪽 노멀 각도가 이 값 이하일 때만 스티칭(부드러운 시임). 하드 엣지(문틈·필러·큐브 모서리)는 제외해 밝은 테두리(rim) 방지. 180°면 사실상 게이팅 해제.")]
        [Range(1f, 180f)] public float seamMaxAngleDeg = 45f;

        [Header("Radiance (mode=Radiance 일 때만)")]
        [Tooltip("빛 진행 방향(예: (0,-1,0)=머리 위에서 내리쬠).")]
        public Vector3 lightDirection = new Vector3(-0.3f, -1f, -0.2f);
        public Color lightColor = Color.white;
        [Min(0f)] public float lightIntensity = 1f;
        [Tooltip("환경광(AO 로 변조). Linear RGB.")]
        public Color ambient = new Color(0.2f, 0.25f, 0.35f, 1f);
        [Tooltip("AO 반구 샘플 수. 브루트포스라 비싸다 — 작게 시작(16~32), atlasResolution 도 낮춰라(128~256).")]
        [Min(1)] public int aoSamples = 32;
        public uint seed = 12345;
        [Tooltip("표면에서 노멀로 띄우는 추가 바이어스(self-occlusion 방지).")]
        [Min(0f)] public float surfaceBias = 0f;
        [Tooltip("차폐 질의 백엔드. BVH=가속(기본, brute와 비트단위 일치 검증 완료) / BruteForce=정답(느림, 교차검증용). Radiance Diff Test 로 둘 일치 확인됨.")]
        public OccluderKind occluderKind = OccluderKind.BVH;
        [Tooltip("BVH 분할 품질(occluderKind=BVH 일 때).")]
        public BVH.BuildQuality bvhQuality = BVH.BuildQuality.Median;

        [Header("Radiance GI (mode=RadianceGI) — 경로추적 간접광")]
        [Tooltip("텍셀당 간접 경로 수(spp). 매우 비쌈 — 16~32로 시작.")]
        [Min(1)] public int indirectSamples = 32;
        [Tooltip("표면 바운스 상한.")]
        [Min(1)] public int maxBounces = 2;
        [Tooltip("하늘(미스 레이) 복사휘도. Linear RGB. 옛 ambient 대체.")]
        public Color skyColor = new Color(0.3f, 0.35f, 0.45f);
        [Tooltip("머티리얼 색을 못 읽을 때 per-mesh 기본 알베도(Linear).")]
        public Color defaultAlbedo = new Color(0.6f, 0.6f, 0.6f);

        [Header("Direct Shadow — 태양 원반 샘플링(소프트 그림자)")]
        [Tooltip("텍셀당 그림자 레이 수. 1=이진 판정(기존, 잎 경계에 점묘 노이즈). 8~32 권장.")]
        [Min(1)] public int directSamples = 1;
        [Tooltip("태양 각지름(도). 0=하드 그림자. 실제 태양 0.53°, 부드럽게 하려면 1~3°.")]
        [Range(0f, 10f)] public float sunAngularDiameterDeg = 0f;

        [Header("Alpha Cutout (α) — 잎·펜스 등 컷아웃 차폐")]
        [Tooltip("컷아웃 머티리얼의 알파를 차폐 판정에 반영한다. 끄면 쿼드 전체가 불투명 판(기존 거동).")]
        public bool alphaCutoutShadows = true;
        [Tooltip("머티리얼당 알파 마스크 해상도 상한. 그림자 실루엣 디테일의 상한을 결정한다.")]
        [Min(8)] public int alphaMaskResolution = 256;
        [Tooltip("알파 블렌딩(Transparent) 머티리얼 처리. Ignore=차폐 제외(유리·물 통짜 그림자 해소).")]
        public AlphaMaskBuilder.TransparentPolicy alphaTransparentPolicy = AlphaMaskBuilder.TransparentPolicy.Ignore;
        [Tooltip("자동 판별이 실패하는 머티리얼을 강제로 컷아웃 취급(에셋 참조 지정).")]
        public Material[] alphaForceCutout;
        [Tooltip("머티리얼/셰이더 이름 부분일치로 강제 컷아웃(대소문자 무시). 예: \"Tree_N_\", \"Leaf\". " +
                 "임포터가 만든 임베드 머티리얼처럼 에셋 참조 지정이 안 통할 때 사용.")]
        public string[] alphaForceCutoutNames;
        [Tooltip("셰이더가 컷오프를 프로퍼티로 노출하지 않을 때 쓸 기본 임계값(SpeedTree 는 0.3333 자동).")]
        [Range(0f, 1f)] public float alphaDefaultCutoff = 0.5f;

        // α 런타임 상태(BuildGiScene 에서 구성 → CPU/Burst/GPU 세 백엔드가 공유)
        AlphaSceneData _alphaData;
        string _lastAlphaLog;   // 마지막 마스크 빌드 요약(AlphaDiagnose 가 함께 출력)

        // Radiance 모드 런타임 상태(BlitRegion 이 참조)
        public enum RadianceBackend { CPU, Burst, Gpu }
        [Tooltip("RadianceGI 베이크 경로. Gpu=컴퓨트셰이더(대형 씬 최고속), Burst=병렬(권장), CPU=기존.")]
        public RadianceBackend radianceBackend = RadianceBackend.CPU;



        IOccluder _occluder;
        // 라이팅 베이크 상태(BlitRegion에서 사용)

        DirectionalLight _sun;
        [ContextMenu("From DirectionalLight Component")]
        public void GetDirectionLightFromLightComponent()
        {
            // FindObjectsOfType: 2021.3~Unity6 공통 동작(U6 에선 deprecated 경고만). FindObjectsByType 는 2022.2+ 전용.
            var lights = GameObject.FindObjectsOfType<Light>();
            foreach (var light in lights)
            {
                if (light.type == LightType.Directional)
                {
                    _sun.Color = new Vector3(light.color.r, light.color.g, light.color.b);
                    lightColor = light.color;
                    lightDirection = _sun.Direction = light.transform.forward;
                    lightIntensity = _sun.Intensity = light.intensity;
                    return;
                }
            }
        }
        Vector3 _ambientLin;

        // RadianceGI 모드 런타임 상태(2단 인스턴싱 경로추적)
        IRadianceScene _giScene;
        private TwoLevelBVH _giBvh;
        private Vector3[] _giMeshAlbedo;
        ISky _sky;
        BakeQualitySettings _giQ;

        // RadianceGI Burst 백엔드 런타임 상태(radianceBackend==Burst/Gpu 일 때 구성). BakeAndApply 끝에서 Dispose.
        BurstScene _burstScene;
        BurstSky _burstSky;
        bool _burstReady;

        // RadianceGI GPU 백엔드 런타임 상태(radianceBackend==Gpu 이고 compute 지원 시). BakeAndApply 끝에서 Dispose.
        GpuScene _gpuScene;
        ComputeShader _pathCS;
        int _kRadiance = -1;
        bool _gpuReady;
        GpuIoBuffers _gpuIo;   // 재사용 GPU I/O 버퍼(_gpuScene 과 동일 수명). per-instance Dispatch 의 5×N alloc/dispose 제거.

        [Header("Result (read-only)")]
        [SerializeField] int instanceCount;
        [SerializeField] int pageCount;
        [SerializeField, Range(0f, 1f)] float utilization;
        [SerializeField] bool overflow;
        [SerializeField] Texture2DArray atlas;
        [SerializeField] Material sharedMat;

        // 원복용 — 적용한 렌더러와 그 원본 메시/머티리얼
        [SerializeField] MeshFilter[] _appliedFilters;
        [SerializeField] Mesh[] _originalMeshes;
        [SerializeField] Material[] _originalMats;

        AllocationSettings BuildSettings() => new AllocationSettings
        {
            AtlasResolution = Mathf.Max(4, atlasResolution),
            TexelsPerWorldUnit = Mathf.Max(0.001f, texelsPerWorldUnit),
            GutterTexels = Mathf.Max(0, gutterTexels),
            MaxPages = Mathf.Max(1, maxPages),
        };

        // 머티리얼 차등 테스트: 같은 텍셀 점에서 BruteForce vs BVH 로 구운 radiance 픽셀 차이 측정.
        // 같은 seed → AO 샘플 방향 동일 → BVH≡brute 면 차이=0 이어야 한다(베이크에 쓰이는 실제 레이로 검증).
        // 0 이 아니면 = 그 텍셀/AO 샘플에서 BVH 차폐가 brute 와 어긋남(랜덤 퍼즈가 놓친 엣지 포함).
        [ContextMenu("Radiance Diff Test (BruteForce vs BVH)")]
        public void RadianceDiffTest()
        {
            var filters = ResolveTargets();
            if (filters.Length == 0) { Debug.LogWarning("[AtlasApply] DiffTest: 대상 MeshFilter 없음.", this); return; }

            var tris = BuildWorldTris(filters);
            var brute = new BruteForceOccluder(tris);
            using var bvh = new BVH(tris, Unity.Collections.Allocator.Persistent, bvhQuality);

            var sun = new DirectionalLight
            {
                Direction = lightDirection.sqrMagnitude > 1e-8f ? lightDirection.normalized : Vector3.down,
                Color = LinColor(lightColor),
                Intensity = lightIntensity,
                AngularDiameterDeg = sunAngularDiameterDeg,
            };
            Vector3 amb = LinColor(ambient);
            int res = Mathf.Clamp(atlasResolution, 4, 512); // 텍셀 해상도(인스턴스별). 비교는 점-단위라 작게 충분.

            const float thresh = 1f / 255f;                 // 8-bit 1 LSB
            double maxDiff = 0, sumDiff = 0; long nTexel = 0, over = 0;

            foreach (var mf in filters)
            {
                var srcMesh = mf.sharedMesh;
                if (srcMesh == null || !srcMesh.isReadable) continue;

                Mesh uv2mesh; LumelMap lumel;
                try
                {
                    var pr = ParameterizationPipeline.Run(srcMesh, segmentation);
                    if (pr.Charts == null || pr.Charts.Length == 0) continue;
                    DensityNormalizer.Normalize(pr.Charts);
                    ShelfPacker.Pack(pr.Charts, chartGutter);
                    (uv2mesh, _) = UVAssembly.Assemble(pr.Charts, srcMesh);
                    lumel = TexelMapper.Map(uv2mesh, res, mf.transform.localToWorldMatrix);
                }
                catch (System.Exception e) { Debug.LogError($"[DiffTest] '{mf.name}' 실패: {e.Message}", mf); continue; }

                for (int li = 0; li < lumel.Valid.Length; li++)
                {
                    if (!lumel.Valid[li]) continue;
                    Vector3 wn = lumel.WorldNormal[li];
                    Vector3 o = lumel.WorldPos[li] + wn * surfaceBias;
                    uint s = seed + (uint)li * 2654435761u;  // BlitRegion 과 동일 시드 규약
                    Vector3 rb = RadianceCore.EvaluateRadiance(brute, o, wn, sun, amb, aoSamples, s);
                    Vector3 rv = RadianceCore.EvaluateRadiance(bvh, o, wn, sun, amb, aoSamples, s);
                    float d = Mathf.Max(Mathf.Abs(rb.x - rv.x), Mathf.Max(Mathf.Abs(rb.y - rv.y), Mathf.Abs(rb.z - rv.z)));
                    if (d > maxDiff) maxDiff = d;
                    sumDiff += d; nTexel++;
                    if (d > thresh) over++;
                }
            }

            if (nTexel == 0) { Debug.LogWarning("[AtlasApply] DiffTest: 유효 텍셀 0(R/W·차트 확인).", this); return; }
            bool ok = maxDiff <= thresh;
            string msg = $"[RadianceDiff] BruteForce vs BVH({bvhQuality}): texels={nTexel}, " +
                         $"maxDiff={maxDiff:F6}, meanDiff={sumDiff / nTexel:F7}, over(1/255)={over} ({100.0 * over / nTexel:F3}%) → {(ok ? "MATCH ✅" : "DIFF ❌")}";
            if (ok) Debug.Log(msg, this); else Debug.LogWarning(msg, this);
        }

        // RadianceGI 백엔드 교차검증: 같은 BVH·per-mesh 알베도·시드(seed+li*const)로 CPU(EvaluateRadiance) 와
        //   Burst(BurstRadianceBaker.Bake=Direct+Indirect) 를 텍셀별 비교. 동일 RNG/BVH 재사용이라 ε 내 일치해야 함
        //   (FMA 경계/히트 반전만 드물게 1/255 초과). 실제 베이크에 쓰이는 경로로 백엔드 동등성 검증.
        [ContextMenu("RadianceGI Backend Diff (CPU vs Burst)")]
        public void RadianceGiBackendDiffTest()
        {
            var filters = ResolveTargets();
            if (filters.Length == 0) { Debug.LogWarning("[AtlasApply] GI DiffTest: 대상 MeshFilter 없음.", this); return; }

            // 유니크 메시(로컬) + per-mesh 알베도 + 인스턴스 → 공유 BVH 로 managed/Burst 동시 구성
            var meshToIdx = new System.Collections.Generic.Dictionary<Mesh, int>();
            var uniqueLocal = new System.Collections.Generic.List<Tri[]>();
            var uniqueMeshes = new System.Collections.Generic.List<Mesh>();
            var meshAlbedo = new System.Collections.Generic.List<Vector3>();
            var giInsts = new System.Collections.Generic.List<TwoLevelBVH.Instance>();
            var instMats = new System.Collections.Generic.List<Material[]>();
            foreach (var mf in filters)
            {
                var mesh = mf.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                if (!meshToIdx.TryGetValue(mesh, out int mi))
                {
                    mi = uniqueLocal.Count; meshToIdx[mesh] = mi;
                    uniqueLocal.Add(LocalTris(mesh)); uniqueMeshes.Add(mesh); meshAlbedo.Add(ReadAlbedo(mf));
                }
                giInsts.Add(new TwoLevelBVH.Instance { MeshIndex = mi, LocalToWorld = mf.transform.localToWorldMatrix });
                var rndA = mf.GetComponent<MeshRenderer>();
                instMats.Add(rndA != null ? rndA.sharedMaterials : null);
            }
            if (giInsts.Count == 0) { Debug.LogWarning("[AtlasApply] GI DiffTest: R/W 가능한 메시 없음.", this); return; }

            var albedoArr = meshAlbedo.ToArray();
            var alphaArr = BuildAlphaData(uniqueLocal, uniqueMeshes, giInsts, instMats);
            using var bvh = new TwoLevelBVH(uniqueLocal.ToArray(), giInsts.ToArray());
            bvh.SetAlpha(alphaArr);
            using var cpuScene = new InstancedRadianceScene(uniqueLocal.ToArray(), albedoArr, giInsts.ToArray(), bvh); // 공유 BVH(모드 A)
            var burstScene = BurstScene.Create(bvh, albedoArr, alphaArr, Allocator.Persistent);

            var sun = new DirectionalLight
            {
                Direction = lightDirection.sqrMagnitude > 1e-8f ? lightDirection.normalized : Vector3.down,
                Color = LinColor(lightColor),
                Intensity = lightIntensity,
                AngularDiameterDeg = sunAngularDiameterDeg,
            };
            ISky sky = new UniformSky(LinColor(skyColor));
            var burstSky = BurstSky.FromSky(sky);
            var q = new BakeQualitySettings { AoSamples = aoSamples, IndirectSamples = indirectSamples, MaxBounces = maxBounces, RRStartDepth = 3, RayBias = Mathf.Max(1e-4f, surfaceBias), DirectSamples = directSamples };

            int res = Mathf.Clamp(atlasResolution, 4, 256); // 비교는 점-단위라 작게 충분(GI 비쌈)
            const float thresh = 1f / 255f;
            double maxDiff = 0, sumDiff = 0; long nTexel = 0, over = 0;

            foreach (var mf in filters)
            {
                var srcMesh = mf.sharedMesh;
                if (srcMesh == null || !srcMesh.isReadable) continue;

                Mesh uv2mesh; LumelMap lumel;
                try
                {
                    var pr = ParameterizationPipeline.Run(srcMesh, segmentation);
                    if (pr.Charts == null || pr.Charts.Length == 0) continue;
                    DensityNormalizer.Normalize(pr.Charts);
                    ShelfPacker.Pack(pr.Charts, chartGutter);
                    (uv2mesh, _) = UVAssembly.Assemble(pr.Charts, srcMesh);
                    lumel = TexelMapper.Map(uv2mesh, res, mf.transform.localToWorldMatrix);
                }
                catch (System.Exception e) { Debug.LogError($"[GI DiffTest] '{mf.name}' 실패: {e.Message}", mf); continue; }

                var idx = new System.Collections.Generic.List<int>(lumel.Valid.Length);
                for (int li = 0; li < lumel.Valid.Length; li++) if (lumel.Valid[li]) idx.Add(li);
                int n = idx.Count; if (n == 0) continue;

                var pts = new NativeArray<Vector3>(n, Allocator.TempJob);
                var nrm = new NativeArray<Vector3>(n, Allocator.TempJob);
                var val = new NativeArray<bool>(n, Allocator.TempJob);
                var sds = new NativeArray<uint>(n, Allocator.TempJob);
                var cpu = new Vector3[n];
                for (int k = 0; k < n; k++)
                {
                    int li = idx[k];
                    Vector3 wn = lumel.WorldNormal[li];
                    Vector3 o = lumel.WorldPos[li] + wn * surfaceBias;
                    uint s = seed + (uint)li * 2654435761u;
                    pts[k] = o; nrm[k] = wn; val[k] = true; sds[k] = s;
                    cpu[k] = RadianceCore.EvaluateRadiance(cpuScene, o, wn, sun, sky, q, s); // Direct + Indirect
                }
                var burst = BurstRadianceBaker.Bake(burstScene, burstSky, sun, q, pts, nrm, val, sds, Allocator.TempJob);
                for (int k = 0; k < n; k++)
                {
                    Vector3 a = cpu[k], b = burst[k];
                    float d = Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Max(Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z)));
                    if (d > maxDiff) maxDiff = d; sumDiff += d; nTexel++;
                    if (d > thresh) over++;
                }
                pts.Dispose(); nrm.Dispose(); val.Dispose(); sds.Dispose(); burst.Dispose();
            }
            burstScene.Dispose();

            if (nTexel == 0) { Debug.LogWarning("[AtlasApply] GI DiffTest: 유효 텍셀 0(R/W·차트 확인).", this); return; }
            double overPct = 100.0 * over / nTexel;
            bool ok = overPct < 1.0; // FMA 경계 제외 → 1/255 초과 1% 미만이면 동등
            string msg = $"[RadianceGI Backend Diff] CPU vs Burst: texels={nTexel}, spp={indirectSamples}, bnc={maxBounces}, " +
                         $"maxDiff={maxDiff:F6}, meanDiff={sumDiff / nTexel:F7}, over(1/255)={over} ({overPct:F3}%) → {(ok ? "MATCH ✅" : "DIFF ❌")}";
            if (ok) Debug.Log(msg, this); else Debug.LogWarning(msg, this);
        }

        [ContextMenu("Bake & Apply")]
        public void BakeAndApply()
        {
            var filters = ResolveTargets();
            if (filters.Length == 0)
            {
                Debug.LogWarning("[AtlasApply] 대상 MeshFilter 가 없습니다. targets 지정 또는 자식에 MeshFilter 배치.", this);
                return;
            }

            // 베이크 시간 계측 — 단계별로 나눠야 α(알파 컷아웃)/원반 샘플링의 비용을 분리해서 볼 수 있다.
            //   scene = 씬 구성(BVH 빌드 + 알파 마스크 굽기) / bake = 레이트레이싱 루프 / post = 디노이즈·스티치·디레이트
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            var swScene = new System.Diagnostics.Stopwatch();
            var swBake = new System.Diagnostics.Stopwatch();
            var swPost = new System.Diagnostics.Stopwatch();

            // 1) 인스턴스별 월드 표면적 → 할당(ST + 페이지)
            var insts = new LightmapInstance[filters.Length];
            for (int i = 0; i < filters.Length; i++)
                insts[i] = new LightmapInstance
                {
                    InstanceId = i,
                    WorldArea = LightmapAllocator.WorldArea(filters[i].sharedMesh, filters[i].transform.localToWorldMatrix),
                };

            var settings = BuildSettings();
            var alloc = LightmapAllocator.Allocate(insts, settings);
            int res = settings.AtlasResolution;
            int pages = Mathf.Max(1, alloc.PageCount);

            // 2) 페이지별 픽셀 버퍼를 배경색으로 초기화 (Linear 아틀라스라 표시색을 linear 로 저장)
            //    validMask: blit 로 실제 칠해진 텍셀 표시 → dilation 의 소스/보존 기준.
            var slices = new Color[pages][];
            var validMask = new bool[pages][];
            Color bgLin = background.linear;
            for (int p = 0; p < pages; p++)
            {
                slices[p] = new Color[res * res];
                validMask[p] = new bool[res * res]; // 기본 false(=배경)
                for (int k = 0; k < slices[p].Length; k++) slices[p][k] = bgLin;
            }

            // Denoise 가이드(텍셀별 월드 노멀·위치) — 라이팅 모드에서만 수집(BlitRegion 이 채움).
            bool doDenoise = denoise && (mode == BakeMode.Radiance || mode == BakeMode.RadianceGI);
            Vector3[][] guideN = null, guideP = null;
            if (doDenoise)
            {
                guideN = new Vector3[pages][];
                guideP = new Vector3[pages][];
                for (int p = 0; p < pages; p++)
                {
                    guideN[p] = new Vector3[res * res];
                    guideP[p] = new Vector3[res * res];
                }
            }

            EnsureMaterial();

            // Radiance 모드: 씬 전체(모든 인스턴스) 차폐자 + 광원 1회 구성 → 인스턴스 간 그림자 반영.
            // 메시 스왑 전에 원본 sharedMesh 로 빌드(지오메트리는 uv2 메시와 동일 좌표).
            swScene.Start();
            if (mode == BakeMode.Radiance)
            {
                var occTris = BuildWorldTris(ResolveOccluderUnion(filters));
                _occluder = occluderKind == OccluderKind.BVH
                    ? (IOccluder)new BVH(occTris, Unity.Collections.Allocator.Persistent, bvhQuality)
                    : new BruteForceOccluder(occTris);
                _sun = new DirectionalLight
                {
                    Direction = lightDirection.sqrMagnitude > 1e-8f ? lightDirection.normalized : Vector3.down,
                    Color = LinColor(lightColor),
                    Intensity = lightIntensity,
                    AngularDiameterDeg = sunAngularDiameterDeg,
                };
                _ambientLin = LinColor(ambient);
                Debug.LogWarning($"[AtlasApply] Radiance 베이크({occluderKind}, {occTris.Length} tris) — 느리면 atlasResolution/aoSamples 를 낮추세요.", this);
            }
            else if (mode == BakeMode.RadianceGI)
            {
                // 2단 인스턴싱 경로추적 씬(InstancedRadianceScene) + 광원 + 하늘 1회 구성.
                BuildGiScene(ResolveOccluderUnion(filters));
                Debug.LogWarning("[AtlasApply] RadianceGI 경로추적 베이크 — 매우 느림. atlasResolution↓(128~256)·indirectSamples↓(16~32)·maxBounces 1~2 권장.", this);
            }
            swScene.Stop();

            var appliedF = new MeshFilter[filters.Length];
            var origMesh = new Mesh[filters.Length];
            var origMat = new Material[filters.Length];

            // 시임 스티칭 입력(페이지별 누적): Tier1=텍셀 인덱스 그룹, Tier2=텍셀좌표 segment 그룹.
            // 인스턴스마다 자기 ST 영역(ox,oy,sidePx) 기준으로 아틀라스 전역 인덱스/좌표를 만들어 페이지에 모은다.
            var seamTexelGroups = new System.Collections.Generic.List<int[]>[pages];
            var seamSegGroups = new System.Collections.Generic.List<LightmapSeamStitch.Seg[]>[pages];
            for (int p = 0; p < pages; p++)
            {
                seamTexelGroups[p] = new System.Collections.Generic.List<int[]>();
                seamSegGroups[p] = new System.Collections.Generic.List<LightmapSeamStitch.Seg[]>();
            }

            // 3) 인스턴스마다 파라미터화→조립→텍셀복원→영역 blit→메시/ST 적용
            swBake.Start();
            for (int i = 0; i < filters.Length; i++)
            {
                var mf = filters[i];
                var src = mf.sharedMesh;
                Vector4 st = alloc.Instances[i].ScaleOffset;
                int page = Mathf.Clamp(alloc.Instances[i].LightmapIndex, 0, pages - 1);

                // ST → 아틀라스 픽셀 영역 (Allocate 가 sd/r, rx/r 형태라 정수 복원 정확)
                int sidePx = Mathf.Max(1, Mathf.RoundToInt(st.x * res));
                int ox = Mathf.RoundToInt(st.z * res);
                int oy = Mathf.RoundToInt(st.w * res);

                Mesh uv2mesh;
                LumelMap lumel;
                SeamTable seams = null;
                try
                {
                    var pr = ParameterizationPipeline.Run(src, segmentation);
                    if (pr.Charts == null || pr.Charts.Length == 0) { Debug.LogWarning($"[AtlasApply] '{mf.name}' 차트 0개 — 건너뜀.", mf); continue; }
                    DensityNormalizer.Normalize(pr.Charts);
                    ShelfPacker.Pack(pr.Charts, chartGutter);
                    (uv2mesh, seams) = UVAssembly.Assemble(pr.Charts, src);
                    uv2mesh.hideFlags = HideFlags.DontSave;
                    // 영역 크기에 맞춰 래스터 → 아틀라스에 1:1 blit
                    lumel = TexelMapper.Map(uv2mesh, sidePx, mf.transform.localToWorldMatrix);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[AtlasApply] '{mf.name}' 처리 실패: {e.Message}", mf);
                    continue;
                }

                Color tint = Color.HSVToRGB((i * 0.6180339887f) % 1f, 0.65f, 1f); // 황금비 분산 → 인접 인스턴스 색 분리

                // RadianceGI + Gpu/Burst 백엔드: 이 인스턴스의 valid lumel 들을 한 번에 베이크(li 인덱스로 산란).
                //   CPU/Burst/GPU 모두 동일 시드(seed + li*const)·동일 pts(worldPos+wn*surfaceBias) → 교차검증 가능.
                //   null 이면 BlitRegion 이 인라인 CPU 로 폴백.
                Vector3[] giRad = (mode == BakeMode.RadianceGI)
                    ? (_gpuReady ? BakeGiLumelsGpu(lumel) : (_burstReady ? BakeGiLumelsBurst(lumel) : null))
                    : null;
                BlitRegion(slices[page], validMask[page], res, ox, oy, sidePx, lumel, tint, giRad,
                           doDenoise ? guideN[page] : null, doDenoise ? guideP[page] : null);

                // 시임 스티칭 입력 누적 — 블릿과 동일한 (ox,oy,sidePx,res) 매핑이라 텍셀이 정확히 정렬.
                // 노멀 게이팅: 시임 양쪽 노멀이 seamMaxAngleDeg 이내(부드러운 시임)일 때만 스티칭 →
                //   하드 엣지(문틈·필러·큐브 모서리)에서 서로 다른 면을 평균해 생기는 밝은 테두리(rim) 방지.
                if ((seamStitchTier1 || seamStitchTier2) && seams != null && seams.Groups.Count > 0)
                {
                    var uv2s = uv2mesh.uv2; // 패킹 라이트맵 UV(채널1) — TexelMapper 와 동일 소스
                    var nrm = uv2mesh.normals; // 게이팅용 노멀(TexelMapper.Map 이 보장). 없으면 게이팅 비활성.
                    if (nrm == null || nrm.Length != uv2mesh.vertexCount) nrm = null;
                    float cosThresh = Mathf.Cos(seamMaxAngleDeg * Mathf.Deg2Rad);

                    if (uv2s != null && uv2s.Length == uv2mesh.vertexCount)
                    {
                        if (seamStitchTier1)
                        {
                            foreach (var grp in seams.Groups)
                            {
                                if (grp == null || grp.Length < 2) continue;
                                if (nrm != null && !NormalsAgree(grp, nrm, cosThresh)) continue; // 하드 엣지 → 스킵
                                var texels = new int[grp.Length];
                                int n = 0;
                                for (int k = 0; k < grp.Length; k++)
                                {
                                    int v = grp[k];
                                    if (v < 0 || v >= uv2s.Length) continue;
                                    int ti = LightmapSeamStitch.Uv2ToTexelIndex(uv2s[v], ox, oy, sidePx, res);
                                    if (ti >= 0) texels[n++] = ti;
                                }
                                if (n >= 2)
                                {
                                    if (n != texels.Length) System.Array.Resize(ref texels, n);
                                    seamTexelGroups[page].Add(texels);
                                }
                            }
                        }
                        if (seamStitchTier2)
                        {
                            var egs = SeamEdgeBuilder.Build(uv2mesh.triangles, uv2mesh.vertexCount, seams.Groups);
                            if (nrm != null) egs = SeamEdgeBuilder.FilterByNormal(egs, nrm, seamMaxAngleDeg); // 하드 엣지 제외
                            var segGroups = SeamEdgeBuilder.BuildSegments(egs, uv2s, ox, oy, sidePx);
                            if (segGroups.Count > 0) seamSegGroups[page].AddRange(segGroups);
                        }
                    }
                }

                // 렌더러에 조립 메시 + ST 적용 (원본 보관)
                var rnd = mf.GetComponent<MeshRenderer>();
                appliedF[i] = mf;
                origMesh[i] = src;
                origMat[i] = rnd != null ? rnd.sharedMaterial : null;

                mf.sharedMesh = uv2mesh;
                if (rnd != null)
                {
                    rnd.sharedMaterial = sharedMat;
                    var mpb = new MaterialPropertyBlock();
                    rnd.GetPropertyBlock(mpb);
                    mpb.SetVector("_LightmapST", st);
                    mpb.SetFloat("_LightmapIndex", page);
                    rnd.SetPropertyBlock(mpb);
                }
            }




            swBake.Stop();
            swPost.Start();

            // 3.3) Denoise — MC 노이즈(그레인)를 노멀·위치·색 가이드 에지 보존 필터로 평활.
            //      Seam Stitch 전: 경계 텍셀 값을 먼저 안정화한 뒤 스티칭·확장해야 시임이 매끈하다.
            //      가이드는 valid 텍셀에서만 유효 — 필터도 valid 만 읽고 씀(배경/거터 불변, dilation 소스 마스크 불변).
            if (doDenoise)
            {
                var dq = new DenoiseSettings
                {
                    Iterations = denoiseIterations,
                    NormalPower = denoiseNormalPower,
                    PositionSigma = denoisePositionSigmaTexels / Mathf.Max(0.001f, texelsPerWorldUnit),
                    ColorSigma = denoiseColorSigma,
                };
                for (int p = 0; p < pages; p++)
                {
                    if (denoiseBurst)
                        LightmapDenoiseBurstJob.Denoise(slices[p], validMask[p], guideN[p], guideP[p], res, res, dq);
                    else
                        LightmapDenoise.Denoise(slices[p], validMask[p], guideN[p], guideP[p], res, res, dq);
                }
            }

            // 3.4) Seam Stitch — 차트 경계 정점(Tier1)/모서리(Tier2)의 불연속을 valid 텍셀 평균으로 제거.
            //      Dilation 보다 먼저: 경계값을 먼저 일치시킨 뒤 그 값을 거터로 확장해야 한다.
            int stitchT1 = 0, stitchT2 = 0;
            if (seamStitchTier1 || seamStitchTier2)
            {
                for (int p = 0; p < pages; p++)
                {
                    if (seamStitchTier1 && seamTexelGroups[p].Count > 0)
                    {
                        LightmapSeamStitch.Stitch(slices[p], validMask[p], seamTexelGroups[p]);
                        stitchT1 += seamTexelGroups[p].Count;
                    }
                    if (seamStitchTier2 && seamSegGroups[p].Count > 0)
                    {
                        LightmapSeamStitch.StitchEdges(slices[p], validMask[p], res, seamSegGroups[p], Mathf.Max(1, seamStitchIterations));
                        stitchT2 += seamSegGroups[p].Count;
                    }
                }
            }

            // 3.5) Push-Pull Dilation — 거터/배경 텍셀을 인접 valid 평균으로 확장해 검은 시임 제거.
            //      페이지별 독립 처리(같은 페이지에 여러 인스턴스가 들어가도 차트 간 거터를 함께 메움).
            if (dilate && dilateIterations > 0)
            {
                for (int p = 0; p < pages; p++)
                {
                    if (dilateBurst)
                        LightmapPostProcessBurstJob.Dilate(slices[p], validMask[p], res, res, dilateIterations);
                    else
                        LightmapPostProcess.Dilate(slices[p], validMask[p], res, res, dilateIterations);
                }
            }

            swPost.Stop();

            // 4) 아틀라스 텍스처 생성/갱신
            if (atlas == null || atlas.width != res || atlas.depth != pages)
            {
                // linear:true — 아틀라스에 선형값 저장(샘플 시 sRGB 디코드 없음). 셰이더 출력 시 프레임버퍼가 한 번만 sRGB 인코딩.
                atlas = new Texture2DArray(res, res, pages, TextureFormat.RGBA32, false, true)
                {
                    name = "LightmapDebug_Atlas",
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.DontSave,
                };
            }
            atlas.filterMode = atlasFilter; // 생성 여부와 무관하게 매 베이크 반영(기존 atlas 재사용 시에도)
            for (int p = 0; p < pages; p++) atlas.SetPixels(slices[p], p);
            atlas.Apply(false);
            sharedMat.SetTexture("_Lightmaps", atlas);

            _appliedFilters = appliedF;
            _originalMeshes = origMesh;
            _originalMats = origMat;

            instanceCount = filters.Length;
            pageCount = pages;
            utilization = alloc.Utilization;
            overflow = alloc.Overflow;

            string dilateInfo = (dilate && dilateIterations > 0)
                ? $", dilate={dilateIterations}×({(dilateBurst ? "Burst" : "C#")})"
                : ", dilate=off";
            string stitchInfo = (seamStitchTier1 || seamStitchTier2)
                ? $", stitch(T1={stitchT1},T2={stitchT2})"
                : ", stitch=off";
            string denoiseInfo = doDenoise
                ? $", denoise={denoiseIterations}×({(denoiseBurst ? "Burst" : "C#")})"
                : ", denoise=off";
            string occInfo = (occluders != null && occluders.Length > 0) ? $", occluders={occluders.Length}" : "";
            // α 상태를 완료 로그에 붙인다 — 알파가 실제로 켜졌는지 한 줄로 확인 가능해야 한다.
            string alphaInfo = !alphaCutoutShadows ? ", alpha=OFF(토글)"
                             : (_alphaData == null || !_alphaData.Enabled) ? ", alpha=DISABLED(컷아웃 머티리얼 0)"
                             : $", alpha=ON(masks={_alphaData.MaskW.Length}, {_alphaData.MaskBits.Length * 4 / 1024}KB)";
            // 직사광 샘플링 상태(원반 샘플링은 directSamples·각지름 둘 다 켜야 동작)
            string directInfo = (directSamples > 1 && sunAngularDiameterDeg > 0f)
                ? $", direct={directSamples}×(sun {sunAngularDiameterDeg:F2}°)"
                : ", direct=1×(hard)";

            swTotal.Stop();
            // 시간: 총계 + 단계별. bake 가 레이트레이싱이므로 알파/원반 샘플링 비용은 여기서 본다.
            string timeInfo =
                $"\n  time: total={swTotal.Elapsed.TotalSeconds:F2}s" +
                $" | scene(BVH+알파마스크)={swScene.Elapsed.TotalSeconds:F2}s" +
                $" | bake(레이트레이싱)={swBake.Elapsed.TotalSeconds:F2}s" +
                $" | post(denoise+stitch+dilate)={swPost.Elapsed.TotalSeconds:F2}s";

            Debug.Log($"[AtlasApply] {filters.Length} insts → atlas {res}×{res}×{pages}, util={alloc.Utilization:P1}, mode={mode}{occInfo}{alphaInfo}{directInfo}{denoiseInfo}{stitchInfo}{dilateInfo}" +
                      (alloc.Overflow ? "  ⚠ overflow(클램프됨)" : "") + timeInfo, this);

            // NativeArray 보유 차폐자/씬 해제(BlitRegion 은 위 루프에서 끝났으므로 안전)
            if (_occluder is System.IDisposable od) od.Dispose();
            _occluder = null;
            if (_giScene is System.IDisposable gd) gd.Dispose();
            _giScene = null;
            if (_gpuReady) { _gpuScene.Dispose(); _gpuScene = null; _gpuIo?.Dispose(); _gpuIo = null; _gpuReady = false; _pathCS = null; _kRadiance = -1; }
            if (_burstReady) { _burstScene.Dispose(); _burstReady = false; }
        }

        // LumelMap 을 아틀라스 페이지 버퍼의 (ox,oy) 영역에 blit. valid 텍셀만, 모드별 색.
        // valid: 실제 칠한 텍셀을 true 로 표시 → 이후 dilation 의 소스/보존 마스크로 사용.
        void BlitRegion(Color[] slice, bool[] valid, int res, int ox, int oy, int sidePx, LumelMap lm, Color tint, Vector3[] giRadiance = null,
                        Vector3[] guideNormal = null, Vector3[] guidePos = null)
        {
            for (int y = 0; y < sidePx; y++)
            {
                int ay = oy + y;
                if (ay < 0 || ay >= res) continue;
                for (int x = 0; x < sidePx; x++)
                {
                    int ax = ox + x;
                    if (ax < 0 || ax >= res) continue;

                    int li = y * sidePx + x;
                    if (lm.Valid == null || li >= lm.Valid.Length || !lm.Valid[li]) continue; // 빈 텍셀=배경 유지

                    Color c;
                    switch (mode)
                    {
                        case BakeMode.WorldNormal:
                            Vector3 n = lm.WorldNormal[li];
                            c = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
                            break;
                        case BakeMode.Checker:
                            bool even = (((x / checkerSize) + (y / checkerSize)) & 1) == 0;
                            c = even ? tint : tint * 0.35f; c.a = 1f;
                            break;
                        case BakeMode.Radiance:
                            {
                                Vector3 wp = lm.WorldPos[li];
                                Vector3 wn = lm.WorldNormal[li];
                                // 텍셀별 결정적 시드 → 노이즈 재현 가능. 평가 원점은 표면에서 bias 만큼 띄움.
                                uint s = seed + (uint)li * 2654435761u;
                                Vector3 lin = RadianceCore.EvaluateRadiance(_occluder, wp + wn * surfaceBias, wn, _sun, _ambientLin, aoSamples, s);
                                c = ToColor(lin);
                                break;
                            }
                        case BakeMode.RadianceGI:
                            {
                                // Burst 백엔드면 미리 베이크된 giRadiance[li] 사용, 아니면 인라인 CPU(EvaluateRadiance).
                                Vector3 lin;
                                if (giRadiance != null)
                                {
                                    lin = giRadiance[li];
                                }
                                else
                                {
                                    Vector3 wp = lm.WorldPos[li];
                                    Vector3 wn = lm.WorldNormal[li];
                                    uint s = seed + (uint)li * 2654435761u;
                                    // Direct + 경로추적 Indirect (2단 인스턴싱 씬, 하늘=sky). 알베도는 런타임 적용이라 여기선 조도.
                                    lin = RadianceCore.EvaluateRadiance(_giScene, wp + wn * surfaceBias, wn, _sun, _sky, _giQ, s);
                                }
                                c = ToColor(lin);
                                break;
                            }
                        default: // PerInstanceColor
                            c = tint;
                            break;
                    }
                    // 아틀라스가 Linear 텍스처. Radiance/GI 의 c 는 이미 선형, 디버그 표시색(sRGB)만 linear 로 변환해 화면 색 라운드트립.
                    int ai = ay * res + ax;
                    slice[ai] = (mode == BakeMode.Radiance || mode == BakeMode.RadianceGI) ? c : c.linear;
                    valid[ai] = true; // 칠한 텍셀 → dilation 소스

                    // Denoise 가이드 — blit 과 동일 (li→ai) 매핑으로 텍셀별 월드 노멀·위치 수집.
                    if (guideNormal != null) guideNormal[ai] = lm.WorldNormal[li];
                    if (guidePos != null) guidePos[ai] = lm.WorldPos[li];
                }
            }
        }

        // RadianceGI Burst 베이크: LumelMap 의 valid lumel 을 NativeArray 로 모아 BurstRadianceBaker.Bake(Direct+Indirect)로
        //   한 번에 병렬 처리 → li 인덱스로 산란한 Vector3[](조도) 반환. 시드 = seed + li*const(= BlitRegion CPU 규약과 동일).
        Vector3[] BakeGiLumelsBurst(LumelMap lm)
        {
            int total = (lm.Valid != null) ? lm.Valid.Length : 0;
            var result = new Vector3[total]; // invalid lumel = zero(BlitRegion 이 valid 만 칠함)
            if (total == 0) return result;

            var idx = new System.Collections.Generic.List<int>(total);
            for (int li = 0; li < total; li++) if (lm.Valid[li]) idx.Add(li);
            int n = idx.Count;
            if (n == 0) return result;

            var pts = new NativeArray<Vector3>(n, Allocator.TempJob);
            var nrm = new NativeArray<Vector3>(n, Allocator.TempJob);
            var val = new NativeArray<bool>(n, Allocator.TempJob);
            var sds = new NativeArray<uint>(n, Allocator.TempJob);
            for (int k = 0; k < n; k++)
            {
                int li = idx[k];
                Vector3 wn = lm.WorldNormal[li];
                pts[k] = lm.WorldPos[li] + wn * surfaceBias;   // BlitRegion 인라인 CPU 와 동일 원점
                nrm[k] = wn;
                val[k] = true;
                sds[k] = seed + (uint)li * 2654435761u;        // CPU 와 동일 시드 → 백엔드 교차검증 가능
            }

            var rad = BurstRadianceBaker.Bake(_burstScene, _burstSky, _sun, _giQ, pts, nrm, val, sds, Allocator.TempJob);
            for (int k = 0; k < n; k++) result[idx[k]] = rad[k];

            pts.Dispose(); nrm.Dispose(); val.Dispose(); sds.Dispose(); rad.Dispose();
            return result;
        }

        // RadianceGI GPU 베이크: BakeGiLumelsBurst 미러. valid lumel 을 모아 CSRadiance(Direct+Indirect) 한 디스패치 →
        //   Async 없이 GetData(1회 readback)로 li 인덱스 산란. pts=worldPos+wn*surfaceBias, seed=seed+li*const
        //   (Burst/CPU 와 정확히 동일 — 교차검증 성립). null 대신 zero-filled(BlitRegion 이 valid 만 칠함).
        Vector3[] BakeGiLumelsGpu(LumelMap lm)
        {
            int total = (lm.Valid != null) ? lm.Valid.Length : 0;
            var result = new Vector3[total];
            if (total == 0) return result;

            var idx = new System.Collections.Generic.List<int>(total);
            for (int li = 0; li < total; li++) if (lm.Valid[li]) idx.Add(li);
            int n = idx.Count;
            if (n == 0) return result;

            var pts = new Vector3[n];
            var nrm = new Vector3[n];
            var seeds = new uint[n];
            for (int k = 0; k < n; k++)
            {
                int li = idx[k];
                Vector3 wn = lm.WorldNormal[li];
                pts[k] = lm.WorldPos[li] + wn * surfaceBias;   // BakeGiLumelsBurst 와 동일 원점
                nrm[k] = wn;
                seeds[k] = seed + (uint)li * 2654435761u;       // Burst/CPU 와 동일 시드
            }

            var rad = DispatchRadianceGpu(_gpuScene, _pathCS, _kRadiance, _sun, _burstSky, _giQ, pts, nrm, seeds, n, _gpuIo);
            for (int k = 0; k < n; k++) result[idx[k]] = rad[k];
            return result;
        }

        // 재사용 GPU I/O 버퍼 홀더(grow-on-demand). per-instance 반복 DispatchRadianceGpu 에서
        //   ComputeBuffer 5개(pts/nrm/valid/seed/radiance) 를 매번 생성/해제하던 것을 제거 — 요청 n 이
        //   현재 capacity 를 초과할 때만 다음 2^k 로 (재)할당, 그 이하는 앞 n개만 SetData/readback 재사용.
        //   수명: BakeGiLumelsGpu 경로는 인스턴스 필드(_gpuIo, _gpuScene 과 동일 수명), Backend Diff 메뉴는 로컬(finally 해제).
        sealed class GpuIoBuffers : System.IDisposable
        {
            public ComputeBuffer Points, Normals, Valid, Seeds, Radiance;
            public uint[] ValidScratch;   // 항상 1u — 재할당 시 1회만 채움(매 호출 new uint[n] GC 제거)
            int _capacity;

            // 요청 n 을 담도록 보장. capacity < n 일 때만 재할당(그 외에는 no-op → 재할당 0회).
            public void Ensure(int n)
            {
                if (Points != null && n <= _capacity) return;
                Dispose();
                int cap = Mathf.NextPowerOfTwo(Mathf.Max(1, n));
                _capacity = cap;
                Points   = new ComputeBuffer(cap, 12, ComputeBufferType.Structured);
                Normals  = new ComputeBuffer(cap, 12, ComputeBufferType.Structured);
                Valid    = new ComputeBuffer(cap, sizeof(uint), ComputeBufferType.Structured);
                Seeds    = new ComputeBuffer(cap, sizeof(uint), ComputeBufferType.Structured);
                Radiance = new ComputeBuffer(cap, 12, ComputeBufferType.Structured);
                ValidScratch = new uint[cap];
                for (int i = 0; i < cap; i++) ValidScratch[i] = 1u;   // 전 구간 1 → 앞 n개도 항상 1
            }

            public void Dispose()
            {
                Points?.Dispose(); Normals?.Dispose(); Valid?.Dispose(); Seeds?.Dispose(); Radiance?.Dispose();
                Points = Normals = Valid = Seeds = Radiance = null;
                ValidScratch = null;
                _capacity = 0;
            }
        }

        // CSRadiance 디스패치 공통(BakeGiLumelsGpu·Backend Diff 재사용). 입력 pts/nrm/seeds 는 길이 n.
        //   버퍼는 재사용 홀더 io 로 grow-on-demand(초과 시에만 재할당). uniform 규약은 BurstRadianceBaker 정합.
        //   ⚠ 데이터 경로 불변: 앞 n개만 업로드/읽기 + Dispatch (n+63)/64 + _Count=n 가드 → 이전 GetData 판과 바이트 동일.
        static Vector3[] DispatchRadianceGpu(GpuScene gpuScene, ComputeShader cs, int kernel,
            DirectionalLight sun, BurstSky sky, BakeQualitySettings q,
            Vector3[] pts, Vector3[] nrm, uint[] seeds, int n, GpuIoBuffers io)
        {
            var result = new Vector3[n];
            if (n == 0) return result;

            io.Ensure(n);   // grow-on-demand: capacity < n 일 때만 (재)할당. 이후 per-instance 반복은 재할당 0회.

            // 앞 n개만 업로드 — 버퍼 count(=capacity) 가 n 보다 클 수 있으므로 count 지정 오버로드 사용.
            //   (managedStartIndex:0, computeStartIndex:0, count:n). 초과 스레드는 커널의 _Count=n 가드로 무시.
            io.Points.SetData(pts, 0, 0, n);
            io.Normals.SetData(nrm, 0, 0, n);
            io.Valid.SetData(io.ValidScratch, 0, 0, n);   // ValidScratch 는 전 구간 1u → 앞 n개 = 이전 판의 all-1 과 동일
            io.Seeds.SetData(seeds, 0, 0, n);

            gpuScene.Bind(cs, kernel);          // 순회 SRV + _TlasCount
            gpuScene.BindLighting(cs, kernel);  // _InstNormals, _MeshAlbedo
            gpuScene.BindAlpha(cs, kernel);     // α: 알파 컷아웃(꺼진 씬은 _AlphaEnabled=0 → 무영향)
            cs.SetBuffer(kernel, "_Points", io.Points);
            cs.SetBuffer(kernel, "_Normals", io.Normals);
            cs.SetBuffer(kernel, "_Valid", io.Valid);
            cs.SetBuffer(kernel, "_Seeds", io.Seeds);
            cs.SetInt("_Count", n);
            cs.SetInt("_IndirectSamples", q.IndirectSamples);
            cs.SetInt("_MaxBounces", q.MaxBounces);
            cs.SetInt("_RRStartDepth", q.RRStartDepth);
            cs.SetFloat("_RayBias", q.RayBias);
            cs.SetVector("_SunDir", sun.Direction);
            cs.SetVector("_SunColor", sun.Color);
            cs.SetFloat("_SunIntensity", sun.Intensity);
            // 태양 원반 샘플링(1이면 셰이더가 기존 단발 경로를 탄다)
            cs.SetInt("_DirectSamples", q.DirectSamples);
            cs.SetFloat("_SunHalfAngle", sun.AngularDiameterDeg * 0.5f * Mathf.Deg2Rad);
            cs.SetInt("_SkyType", sky.Type);
            cs.SetVector("_SkyTop", sky.A);
            cs.SetVector("_SkyBottom", sky.B);
            cs.SetBuffer(kernel, "_RadianceOut", io.Radiance);

            int groups = (n + 63) / 64;
            cs.Dispatch(kernel, groups, 1, 1);

            // AsyncGPUReadback: Radiance 앞 n*12 바이트(=앞 n개 Vector3)만 요청 → capacity>n 이어도 정확히 n개.
            //   커널은 [0,n) 만 기록(초과 스레드 가드)하므로 재사용 버퍼의 잔여 [n,capacity) 는 읽지 않아 오염 무관.
            //   ⚠ GetData 대비 실측 비교 필요: AsyncGPUReadback+WaitForCompletion 이 동기 GetData 보다 느릴 여지 있음(사용자 에디터 실측).
            var req = UnityEngine.Rendering.AsyncGPUReadback.Request(io.Radiance, n * 12, 0);
            req.WaitForCompletion();
            if (req.hasError)
            {
                // 드문 readback 실패 시 동기 GetData 폴백(앞 n개만).
                Debug.LogWarning("[AtlasApply] AsyncGPUReadback 실패 — GetData 폴백.");
                io.Radiance.GetData(result, 0, 0, n);
            }
            else
            {
                // GetData<Vector3>() 길이 = (n*12)/12 = n → result(길이 n) 로 정확히 복사.
                req.GetData<Vector3>().CopyTo(result);
            }
            return result;
        }

        // RadianceGI 백엔드 교차검증(Burst vs GPU): 같은 씬·같은 시드(seed+li*const)·같은 pts 로
        //   Burst BurstRadianceBaker.Bake vs GPU CSRadiance 텍셀 대조 → mean/max/over 로그(mean≈1e-9 기대, G5 수준).
        //   + Stopwatch 로 Burst vs GPU 베이크 시간 로그(대형 씬 이득 확인). GetData/.Complete 동기라 벽시계 비교 공정.
        [ContextMenu("RadianceGI Backend Diff (Burst vs GPU)")]
        public void RadianceGiBackendDiffTestGpu()
        {
            if (!SystemInfo.supportsComputeShaders)
            { Debug.LogWarning("[AtlasApply] Burst vs GPU Diff: compute shader 미지원 플랫폼 — SKIP.", this); return; }

            var filters = ResolveTargets();
            if (filters.Length == 0) { Debug.LogWarning("[AtlasApply] Burst vs GPU Diff: 대상 MeshFilter 없음.", this); return; }

            // 유니크 메시(로컬) + per-mesh 알베도 + 인스턴스 → 공유 BVH.
            var meshToIdx = new System.Collections.Generic.Dictionary<Mesh, int>();
            var uniqueLocal = new System.Collections.Generic.List<Tri[]>();
            var uniqueMeshes = new System.Collections.Generic.List<Mesh>();
            var meshAlbedo = new System.Collections.Generic.List<Vector3>();
            var giInsts = new System.Collections.Generic.List<TwoLevelBVH.Instance>();
            var instMats = new System.Collections.Generic.List<Material[]>();
            foreach (var mf in filters)
            {
                var mesh = mf.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                if (!meshToIdx.TryGetValue(mesh, out int mi))
                {
                    mi = uniqueLocal.Count; meshToIdx[mesh] = mi;
                    uniqueLocal.Add(LocalTris(mesh)); uniqueMeshes.Add(mesh); meshAlbedo.Add(ReadAlbedo(mf));
                }
                giInsts.Add(new TwoLevelBVH.Instance { MeshIndex = mi, LocalToWorld = mf.transform.localToWorldMatrix });
                var rndG = mf.GetComponent<MeshRenderer>();
                instMats.Add(rndG != null ? rndG.sharedMaterials : null);
            }
            if (giInsts.Count == 0) { Debug.LogWarning("[AtlasApply] Burst vs GPU Diff: R/W 가능한 메시 없음.", this); return; }

            var albedoArr = meshAlbedo.ToArray();
            var alphaArr = BuildAlphaData(uniqueLocal, uniqueMeshes, giInsts, instMats);
            using var bvh = new TwoLevelBVH(uniqueLocal.ToArray(), giInsts.ToArray());
            bvh.SetAlpha(alphaArr);
            var burstScene = BurstScene.Create(bvh, albedoArr, alphaArr, Allocator.Persistent);

            var pathCS = LoadPathCompute();
            if (pathCS == null) { Debug.LogWarning("[AtlasApply] Burst vs GPU Diff: PathTrace.compute 로드 실패.", this); burstScene.Dispose(); return; }
            int kRad = pathCS.FindKernel("CSRadiance");
            if (kRad < 0) { Debug.LogWarning("[AtlasApply] Burst vs GPU Diff: CSRadiance 커널 미발견.", this); burstScene.Dispose(); return; }
            var gpuScene = new GpuScene(burstScene);
            var gpuIo = new GpuIoBuffers();   // 메뉴 로컬 재사용 홀더(루프 내내 재할당 0회, 아래서 해제)

            var sun = new DirectionalLight
            {
                Direction = lightDirection.sqrMagnitude > 1e-8f ? lightDirection.normalized : Vector3.down,
                Color = LinColor(lightColor),
                Intensity = lightIntensity,
                AngularDiameterDeg = sunAngularDiameterDeg,
            };
            ISky sky = new UniformSky(LinColor(skyColor));
            var burstSky = BurstSky.FromSky(sky);
            var q = new BakeQualitySettings { AoSamples = aoSamples, IndirectSamples = indirectSamples, MaxBounces = maxBounces, RRStartDepth = 3, RayBias = Mathf.Max(1e-4f, surfaceBias), DirectSamples = directSamples };

            int res = Mathf.Clamp(atlasResolution, 4, 256);
            const float thresh = 1f / 255f;
            double maxDiff = 0, sumDiff = 0; long nTexel = 0, over = 0;
            var swBurst = new System.Diagnostics.Stopwatch();
            var swGpu = new System.Diagnostics.Stopwatch();

            foreach (var mf in filters)
            {
                var srcMesh = mf.sharedMesh;
                if (srcMesh == null || !srcMesh.isReadable) continue;

                Mesh uv2mesh; LumelMap lumel;
                try
                {
                    var pr = ParameterizationPipeline.Run(srcMesh, segmentation);
                    if (pr.Charts == null || pr.Charts.Length == 0) continue;
                    DensityNormalizer.Normalize(pr.Charts);
                    ShelfPacker.Pack(pr.Charts, chartGutter);
                    (uv2mesh, _) = UVAssembly.Assemble(pr.Charts, srcMesh);
                    lumel = TexelMapper.Map(uv2mesh, res, mf.transform.localToWorldMatrix);
                }
                catch (System.Exception e) { Debug.LogError($"[Burst vs GPU Diff] '{mf.name}' 실패: {e.Message}", mf); continue; }

                var idx = new System.Collections.Generic.List<int>(lumel.Valid.Length);
                for (int li = 0; li < lumel.Valid.Length; li++) if (lumel.Valid[li]) idx.Add(li);
                int n = idx.Count; if (n == 0) continue;

                // 동일 pts/nrm/seed (Burst=NativeArray, GPU=managed).
                var pts = new NativeArray<Vector3>(n, Allocator.TempJob);
                var nrm = new NativeArray<Vector3>(n, Allocator.TempJob);
                var val = new NativeArray<bool>(n, Allocator.TempJob);
                var sds = new NativeArray<uint>(n, Allocator.TempJob);
                var mPts = new Vector3[n];
                var mNrm = new Vector3[n];
                var mSds = new uint[n];
                for (int k = 0; k < n; k++)
                {
                    int li = idx[k];
                    Vector3 wn = lumel.WorldNormal[li];
                    Vector3 o = lumel.WorldPos[li] + wn * surfaceBias;
                    uint s = seed + (uint)li * 2654435761u;
                    pts[k] = o; nrm[k] = wn; val[k] = true; sds[k] = s;
                    mPts[k] = o; mNrm[k] = wn; mSds[k] = s;
                }

                swBurst.Start();
                var burst = BurstRadianceBaker.Bake(burstScene, burstSky, sun, q, pts, nrm, val, sds, Allocator.TempJob);
                swBurst.Stop();

                swGpu.Start();
                var gpu = DispatchRadianceGpu(gpuScene, pathCS, kRad, sun, burstSky, q, mPts, mNrm, mSds, n, gpuIo);
                swGpu.Stop();

                for (int k = 0; k < n; k++)
                {
                    Vector3 a = burst[k], b = gpu[k];
                    float d = Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Max(Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z)));
                    if (d > maxDiff) maxDiff = d; sumDiff += d; nTexel++;
                    if (d > thresh) over++;
                }
                pts.Dispose(); nrm.Dispose(); val.Dispose(); sds.Dispose(); burst.Dispose();
            }

            gpuScene.Dispose();
            gpuIo.Dispose();       // 메뉴 로컬 재사용 홀더 해제(ComputeBuffer 5개) — 영구 필드 _gpuIo 와 분리, 일회성.
            burstScene.Dispose();

            if (nTexel == 0) { Debug.LogWarning("[AtlasApply] Burst vs GPU Diff: 유효 텍셀 0(R/W·차트 확인).", this); return; }
            double overPct = 100.0 * over / nTexel;
            bool ok = overPct < 1.0; // 초월함수(sqrt/sin/cos) 발산 → MC 잡음. 1/255 초과 1% 미만이면 동등(G5 수준)
            string msg = $"[RadianceGI Backend Diff] Burst vs GPU: texels={nTexel}, spp={indirectSamples}, bnc={maxBounces}, " +
                         $"maxDiff={maxDiff:F6}, meanDiff={sumDiff / nTexel:F7}, over(1/255)={over} ({overPct:F3}%) → {(ok ? "MATCH ✅" : "DIFF ❌")}";
            if (ok) Debug.Log(msg, this); else Debug.LogWarning(msg, this);
            Debug.Log($"[RadianceGI Backend Diff] 시간: Burst={swBurst.Elapsed.TotalMilliseconds:F1}ms, GPU={swGpu.Elapsed.TotalMilliseconds:F1}ms " +
                      $"(GPU/Burst={(swBurst.Elapsed.TotalMilliseconds > 0 ? swGpu.Elapsed.TotalMilliseconds / swBurst.Elapsed.TotalMilliseconds : 0):F2}×) " +
                      $"— GPU 는 버퍼 생성/GetData 동기 포함. 대형 씬·고spp 에서 이득.", this);
        }

        void EnsureMaterial()
        {
            if (sharedMat != null) return;
            var sh = Shader.Find("CustomLightmapper/LightmapDebug");
            if (sh == null) { Debug.LogError("[AtlasApply] 셰이더 'CustomLightmapper/LightmapDebug' 를 찾을 수 없습니다.", this); return; }
            sharedMat = new Material(sh) { name = "LightmapDebug_Mat", hideFlags = HideFlags.DontSave };
        }

        [ContextMenu("Restore Originals")]
        public void RestoreOriginals()
        {
            if (_appliedFilters == null) { Debug.Log("[AtlasApply] 복원할 항목 없음.", this); return; }
            for (int i = 0; i < _appliedFilters.Length; i++)
            {
                var mf = _appliedFilters[i];
                if (mf == null) continue;
                if (_originalMeshes != null && i < _originalMeshes.Length) mf.sharedMesh = _originalMeshes[i];
                var rnd = mf.GetComponent<MeshRenderer>();
                if (rnd != null)
                {
                    rnd.SetPropertyBlock(null);
                    if (_originalMats != null && i < _originalMats.Length) rnd.sharedMaterial = _originalMats[i];
                }
            }
            _appliedFilters = null; _originalMeshes = null; _originalMats = null;
            Debug.Log("[AtlasApply] 원본 메시/머티리얼 복원 완료.", this);
        }

        // RadianceGI: 유니크 메시(로컬) + per-mesh 알베도(Linear) + 인스턴스 → InstancedRadianceScene 구성.
        // 메시 스왑 전에 호출(원본 sharedMesh·머티리얼 기준).
        void BuildGiScene(MeshFilter[] filters)
        {
            var meshToIdx = new System.Collections.Generic.Dictionary<Mesh, int>();
            var uniqueLocal = new System.Collections.Generic.List<Tri[]>();
            var uniqueMeshes = new System.Collections.Generic.List<Mesh>();
            var meshAlbedo = new System.Collections.Generic.List<Vector3>();
            var giInsts = new System.Collections.Generic.List<TwoLevelBVH.Instance>();
            var instMats = new System.Collections.Generic.List<Material[]>();
            foreach (var mf in filters)
            {
                var mesh = mf.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                if (!meshToIdx.TryGetValue(mesh, out int mi))
                {
                    mi = uniqueLocal.Count;
                    meshToIdx[mesh] = mi;
                    uniqueLocal.Add(LocalTris(mesh));
                    uniqueMeshes.Add(mesh);
                    meshAlbedo.Add(ReadAlbedo(mf));
                }
                giInsts.Add(new TwoLevelBVH.Instance { MeshIndex = mi, LocalToWorld = mf.transform.localToWorldMatrix });
                var rnd = mf.GetComponent<MeshRenderer>();
                instMats.Add(rnd != null ? rnd.sharedMaterials : null);
            }

            _giScene = new InstancedRadianceScene(uniqueLocal.ToArray(), meshAlbedo.ToArray(), giInsts.ToArray()); // change cpu vs burst

            _giBvh = ((InstancedRadianceScene)_giScene).Bvh;
            _giMeshAlbedo = meshAlbedo.ToArray();

            // α: 알파 컷아웃 씬 구성 → CPU(TwoLevelBVH) / Burst(BurstScene) / GPU(GpuScene) 가 공유.
            _alphaData = BuildAlphaData(uniqueLocal, uniqueMeshes, giInsts, instMats);
            _giBvh.SetAlpha(_alphaData);

            _sun = new DirectionalLight
            {
                Direction = lightDirection.sqrMagnitude > 1e-8f ? lightDirection.normalized : Vector3.down,
                Color = LinColor(lightColor),
                Intensity = lightIntensity,
                AngularDiameterDeg = sunAngularDiameterDeg,
            };
            _sky = new UniformSky(LinColor(skyColor));
            _giQ = new BakeQualitySettings
            {
                AoSamples = aoSamples,
                IndirectSamples = indirectSamples,
                MaxBounces = maxBounces,
                RRStartDepth = 3,
                RayBias = Mathf.Max(1e-4f, surfaceBias),
                DirectSamples = directSamples,
            };

            // Burst/Gpu 백엔드: 동일 BVH(_giBvh) + per-mesh 알베도 → POD 평탄화(BurstScene). 모든 인스턴스 blit 에서 재사용.
            //   Gpu 도 BurstScene 이 필요(GpuScene 이 생성자에서 이 SoA 를 ComputeBuffer 로 업로드). _burstSky 는 uniform 값 공급.
            if (radianceBackend == RadianceBackend.Burst || radianceBackend == RadianceBackend.Gpu)
            {
                _burstScene = BurstScene.Create(_giBvh, _giMeshAlbedo, _alphaData, Allocator.Persistent);
                _burstSky = BurstSky.FromSky(_sky);
                _burstReady = true;
            }

            // Gpu 백엔드: BurstScene → GpuScene(ComputeBuffer) + PathTrace.compute 로드. 미지원/로드실패 시 Burst 로 폴백(_burstReady 유지).
            if (radianceBackend == RadianceBackend.Gpu)
            {
                if (!SystemInfo.supportsComputeShaders)
                {
                    Debug.LogWarning("[AtlasApply] Compute shader 미지원 플랫폼 — RadianceGI GPU 백엔드 → Burst 폴백.", this);
                }
                else
                {
                    _pathCS = LoadPathCompute();
                    if (_pathCS == null)
                    {
                        Debug.LogWarning("[AtlasApply] PathTrace.compute 로드 실패 — RadianceGI GPU 백엔드 → Burst 폴백.", this);
                    }
                    else
                    {
                        _kRadiance = _pathCS.FindKernel("CSRadiance");
                        if (_kRadiance < 0)
                        {
                            Debug.LogWarning("[AtlasApply] CSRadiance 커널 미발견 — RadianceGI GPU 백엔드 → Burst 폴백.", this);
                            _pathCS = null;
                        }
                        else
                        {
                            _gpuScene = new GpuScene(_burstScene);   // 생성자에서 SoA → ComputeBuffer 업로드
                            _gpuIo = new GpuIoBuffers();             // 재사용 I/O 버퍼 홀더(첫 Dispatch 에서 grow-on-demand 할당)
                            _gpuReady = true;
                        }
                    }
                }
            }
        }

        // PathTrace.compute 로드: Shaders/Resources 배치 → 에디터·빌드·패키지 어디서든 Resources.Load 로 동작.
        static ComputeShader LoadPathCompute()
        {
            return Resources.Load<ComputeShader>("PathTrace");
        }

        /// <summary>
        /// α: 유니크 메시·인스턴스로부터 알파 컷아웃 씬 데이터를 만든다.
        /// 컷아웃 머티리얼이 없거나 토글이 꺼져 있으면 Disabled 를 돌려 세 백엔드가 기존 경로를 탄다.
        /// </summary>
        AlphaSceneData BuildAlphaData(System.Collections.Generic.List<Tri[]> uniqueLocal,
                                      System.Collections.Generic.List<Mesh> uniqueMeshes,
                                      System.Collections.Generic.List<TwoLevelBVH.Instance> insts,
                                      System.Collections.Generic.List<Material[]> instMats)
        {
            if (!alphaCutoutShadows) return AlphaSceneData.Disabled;

            var triCount = new int[uniqueLocal.Count];
            for (int m = 0; m < uniqueLocal.Count; m++) triCount[m] = uniqueLocal[m] != null ? uniqueLocal[m].Length : 0;

            var instMesh = new int[insts.Count];
            for (int i = 0; i < insts.Count; i++) instMesh[i] = insts[i].MeshIndex;

            var builder = new AlphaMaskBuilder
            {
                MaskResolution = alphaMaskResolution,
                Transparent = alphaTransparentPolicy,
                DefaultCutoff = alphaDefaultCutoff,
                ForceCutout = (alphaForceCutout != null && alphaForceCutout.Length > 0)
                    ? new System.Collections.Generic.HashSet<Material>(alphaForceCutout)
                    : null,
                ForceCutoutNames = alphaForceCutoutNames,
            };
            var data = builder.BuildScene(uniqueMeshes, triCount, instMesh, instMats, out string log);
            _lastAlphaLog = log;                       // AlphaDiagnose 가 한 덩어리로 함께 출력
            Debug.Log($"[AtlasApply] alpha: {log}", this);
            return data;
        }

        /// <summary>
        /// α 진단 — 알파 컷아웃이 **실제로 차폐 판정을 바꾸고 있는지** 수치로 확인한다.
        /// 같은 레이 집합을 알파 ON/OFF 로 두 번 쏴서 결과가 달라진 개수를 센다.
        /// 0 이면 알파가 아무 일도 안 하는 것 → 머티리얼 판별/UV/마스크 중 하나가 문제.
        /// </summary>
        [ContextMenu("Alpha Diagnose (알파 효과 측정)")]
        public void AlphaDiagnose()
        {
            // 가장 흔한 함정부터 먼저 걸러낸다 — 토글이 꺼져 있으면 마스크 빌더가 아예 안 돈다.
            if (!alphaCutoutShadows)
            {
                Debug.LogWarning("[AlphaDiag] ⚠ 인스펙터의 alphaCutoutShadows 가 **꺼져 있습니다**. " +
                                 "이 상태로는 마스크를 굽지 않으므로 알파가 전혀 적용되지 않습니다. " +
                                 "체크박스를 켜고 다시 실행하세요.", this);
                return;
            }

            var filters = ResolveTargets();
            if (filters.Length == 0) { Debug.LogWarning("[AlphaDiag] 대상 MeshFilter 없음.", this); return; }
            var union = ResolveOccluderUnion(filters);

            var meshToIdx = new System.Collections.Generic.Dictionary<Mesh, int>();
            var uniqueLocal = new System.Collections.Generic.List<Tri[]>();
            var uniqueMeshes = new System.Collections.Generic.List<Mesh>();
            var insts = new System.Collections.Generic.List<TwoLevelBVH.Instance>();
            var instMats = new System.Collections.Generic.List<Material[]>();
            foreach (var mf in union)
            {
                var mesh = mf.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                if (!meshToIdx.TryGetValue(mesh, out int mi))
                {
                    mi = uniqueLocal.Count; meshToIdx[mesh] = mi;
                    uniqueLocal.Add(LocalTris(mesh)); uniqueMeshes.Add(mesh);
                }
                insts.Add(new TwoLevelBVH.Instance { MeshIndex = mi, LocalToWorld = mf.transform.localToWorldMatrix });
                var r = mf.GetComponent<MeshRenderer>();
                instMats.Add(r != null ? r.sharedMaterials : null);
            }
            if (insts.Count == 0) { Debug.LogWarning("[AlphaDiag] R/W 가능한 메시 없음.", this); return; }

            var alpha = BuildAlphaData(uniqueLocal, uniqueMeshes, insts, instMats);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== α 진단 ===");
            // 셰이더 이름 등 머티리얼별 사유가 여기에 있다. 비어 있으면 빌더가 안 돈 것.
            sb.AppendLine($"[마스크 빌드] {(string.IsNullOrEmpty(_lastAlphaLog) ? "(빌드 안 됨 — alphaCutoutShadows 확인)" : _lastAlphaLog)}");
            sb.AppendLine($"인스턴스 {insts.Count}, 유니크 메시 {uniqueMeshes.Count}, alpha.Enabled={alpha.Enabled}");
            for (int m = 0; m < uniqueMeshes.Count; m++)
            {
                var msh = uniqueMeshes[m];
                bool cut = alpha.Enabled && alpha.MeshCutout(m);
                bool hasUv = msh != null && msh.uv != null && msh.uv.Length > 0;
                sb.AppendLine($"  mesh[{m}] '{(msh != null ? msh.name : "null")}' tris={uniqueLocal[m].Length}, submesh={(msh != null ? msh.subMeshCount : 0)}, uv0={(hasUv ? "있음" : "**없음**")}, 컷아웃={(cut ? "예" : "아니오")}");
            }

            if (!alpha.Enabled)
            {
                sb.AppendLine("→ alpha DISABLED. 위 머티리얼 로그에서 '불투명 취급' 줄의 shader 이름을 확인하고,");
                sb.AppendLine("   필요하면 인스펙터 alphaForceCutout 에 잎 머티리얼을 직접 지정하세요.");
                Debug.Log(sb.ToString(), this);
                return;
            }

            // 리시버 상단에 격자를 깔고 태양 방향으로 그림자 레이를 쏜다(= EvaluateDirect 와 같은 질의).
            Bounds b = new Bounds();
            bool first = true;
            foreach (var mf in filters)
            {
                var r = mf.GetComponent<MeshRenderer>();
                if (r == null) continue;
                if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds);
            }
            if (first) { Debug.LogWarning("[AlphaDiag] 리시버 Renderer 없음.", this); return; }

            using var bvh = new TwoLevelBVH(uniqueLocal.ToArray(), insts.ToArray());
            Vector3 L = -(lightDirection.sqrMagnitude > 1e-8f ? lightDirection.normalized : Vector3.down);
            Vector3 n = Vector3.up;

            const int G = 200;
            int rays = 0, occOn = 0, occOff = 0, changed = 0;
            float y = b.max.y + 1e-3f;
            for (int iy = 0; iy < G; iy++)
            {
                for (int ix = 0; ix < G; ix++)
                {
                    Vector3 p = new Vector3(
                        Mathf.Lerp(b.min.x, b.max.x, (ix + 0.5f) / G), y,
                        Mathf.Lerp(b.min.z, b.max.z, (iy + 0.5f) / G));
                    Vector3 o = p + n * 1e-3f;
                    rays++;

                    bvh.SetAlpha(null);
                    bool off = bvh.Occluded(o, L, 1e30f);
                    bvh.SetAlpha(alpha);
                    bool on = bvh.Occluded(o, L, 1e30f);

                    if (off) occOff++;
                    if (on) occOn++;
                    if (off != on) changed++;
                }
            }

            sb.AppendLine($"그림자 레이 {rays}발 (리시버 상단 {G}×{G} 격자, 태양 방향)");
            sb.AppendLine($"  알파 OFF 차폐 = {occOff} ({100f * occOff / rays:F1}%)");
            sb.AppendLine($"  알파 ON  차폐 = {occOn} ({100f * occOn / rays:F1}%)");
            sb.AppendLine($"  → 알파로 뚫린 레이 = **{changed}** ({100f * changed / rays:F1}%)");
            sb.AppendLine(changed == 0
                ? "  ⚠ 0 이면 알파가 차폐 판정을 전혀 바꾸지 못하고 있다."
                : "  ✔ 알파가 차폐를 실제로 통과시키고 있다. 그림자가 여전히 진하면 캐노피가 겹겹이라 물리적으로 맞는 결과일 수 있다.");
            Debug.Log(sb.ToString(), this);
        }

        // 머티리얼 baseColor → Linear 알베도(≤1). URP _BaseColor / Built-in _Color, 없으면 기본값.
        Vector3 ReadAlbedo(MeshFilter mf)
        {
            var r = mf.GetComponent<MeshRenderer>();
            var m = r?.sharedMaterial;
            Color col = defaultAlbedo;
            if (m != null && m.HasProperty("_BaseColor")) col = m.GetColor("_BaseColor");
            else if (m != null && m.HasProperty("_Color")) col = m.GetColor("_Color");
            Color lin = col.linear;
            return new Vector3(Mathf.Clamp01(lin.r), Mathf.Clamp01(lin.g), Mathf.Clamp01(lin.b));
        }

        // 변환 없는 로컬 Tri[] (BLAS 입력용).
        static Tri[] LocalTris(Mesh mesh)
        {
            var v = mesh.vertices;
            var t = mesh.triangles;
            var tris = new Tri[t.Length / 3];
            for (int i = 0; i < tris.Length; i++)
                tris[i] = new Tri { V0 = v[t[i * 3]], V1 = v[t[i * 3 + 1]], V2 = v[t[i * 3 + 2]] };
            return tris;
        }

        // 모든 타깃 메시의 삼각형을 월드 공간 Tri[] 로 평탄화(차폐자 입력).
        static Tri[] BuildWorldTris(MeshFilter[] filters)
        {
            var tris = new System.Collections.Generic.List<Tri>();
            foreach (var mf in filters)
            {
                var mesh = mf.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                var v = mesh.vertices;
                var t = mesh.triangles;
                var m = mf.transform.localToWorldMatrix;
                for (int i = 0; i + 2 < t.Length; i += 3)
                    tris.Add(new Tri
                    {
                        V0 = m.MultiplyPoint3x4(v[t[i]]),
                        V1 = m.MultiplyPoint3x4(v[t[i + 1]]),
                        V2 = m.MultiplyPoint3x4(v[t[i + 2]]),
                    });
            }
            return tris.ToArray();
        }

        // 시임 그룹 정점 노멀이 모두 기준 노멀과 임계각(cosThresh) 이내인지 = 부드러운 시임 판정.
        // 하드 엣지(노멀 불연속)면 false → Tier1 스티칭 제외. 메시 노멀은 단위벡터라 Dot=cos(각).
        static bool NormalsAgree(int[] group, Vector3[] normals, float cosThresh)
        {
            Vector3 refN = Vector3.zero; bool haveRef = false;
            for (int i = 0; i < group.Length; i++)
            {
                int v = group[i];
                if (v < 0 || v >= normals.Length) continue;
                Vector3 nv = normals[v];
                if (!haveRef) { refN = nv; haveRef = true; }
                else if (Vector3.Dot(refN, nv) < cosThresh) return false;
            }
            return true;
        }

        // 인스펙터 광원색(sRGB/감마) → Linear Vector3. 베이크는 Linear 전제라 .linear 필수.
        static Vector3 LinColor(Color c) { var l = c.linear; return new Vector3(l.r, l.g, l.b); }

        // 선형 RGB → 아틀라스 저장값. 아틀라스가 Linear 텍스처라 인코딩 없이 클램프만(LDR).
        // 셰이더 출력 시 프레임버퍼가 linear→sRGB 인코딩을 정확히 1회 수행.
        static Color ToColor(Vector3 lin) => new Color(
            Mathf.Clamp01(lin.x), Mathf.Clamp01(lin.y), Mathf.Clamp01(lin.z), 1f);

        MeshFilter[] ResolveTargets()
        {
            var list = new System.Collections.Generic.List<MeshFilter>();
            if (targets != null && targets.Length > 0)
            {
                foreach (var mf in targets) if (mf != null && mf.sharedMesh != null) list.Add(mf);
            }
            else
            {
                foreach (var mf in GetComponentsInChildren<MeshFilter>())
                    if (mf.sharedMesh != null) list.Add(mf);
            }

            // occluder-only 차집합: occluders 에 포함된 MeshFilter 는 receiver 집합에서 제외.
            // (targets 를 비워 자식 폴백일 때 occluder 가 자식으로 잡혀 receiver 로 베이크되는 사고 방지.)
            // occluders 가 null/비어있으면 no-op → 기존 거동과 비트동일.
            if (occluders != null && occluders.Length > 0)
            {
                var excl = new System.Collections.Generic.HashSet<MeshFilter>();
                foreach (var mf in occluders) if (mf != null) excl.Add(mf);
                if (excl.Count > 0) list.RemoveAll(mf => excl.Contains(mf));
            }
            return list.ToArray();
        }

        // receivers ∪ occluders — 차폐 씬 구성용(중복 제거, null/sharedMesh null 스킵).
        // occluders 가 null/비어있으면 receivers 를 그대로 반환(합집합=filters).
        MeshFilter[] ResolveOccluderUnion(MeshFilter[] receivers)
        {
            var list = new System.Collections.Generic.List<MeshFilter>();
            var seen = new System.Collections.Generic.HashSet<MeshFilter>();
            if (receivers != null)
                foreach (var mf in receivers)
                    if (mf != null && mf.sharedMesh != null && seen.Add(mf)) list.Add(mf);
            if (occluders != null)
                foreach (var mf in occluders)
                    if (mf != null && mf.sharedMesh != null && seen.Add(mf)) list.Add(mf);
            return list.ToArray();
        }
    }
}
