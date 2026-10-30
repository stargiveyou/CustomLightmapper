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

        /// <summary>SSAA(텍셀 내부 슈퍼샘플링) 범위. Off = 기존 경로와 비트 동일.</summary>
        public enum SsaaMode
        {
            Off,        // 텍셀당 1샘플(중앙) — 기존 거동
            Adaptive,   // 1패스 후 라이팅 급변 텍셀만 S×S 재샘플(권장)
            Full,       // 모든 valid 텍셀을 S×S 재샘플(레퍼런스/비교용, S² 배 비용)
        }

        [Header("Input")]
        [Tooltip("비우면 자기 자신+자식의 MeshFilter 를 모두 수집.")]
        [SerializeField] MeshFilter[] targets;

        [Tooltip("차폐 전용(occluder-only) — 차폐 씬에만 참여, 라이트맵 베이크/메시·머티리얼 스왑 대상 아님. 지형 기획: halo·프롭·건물.")]
        public MeshFilter[] occluders;
        public SegmentationSettings segmentation = SegmentationSettings.Default;
        [Tooltip("차트 평탄화(A3): Planar → (겹침·L2 stretch > 임계) LSCM 등각 → (겹침) MVC 전단사. FoldoverOnly 처럼 임계를 Infinity 로 두면 겹칠 때만 LSCM.")]
        public FlattenSettings flatten = FlattenSettings.Default;

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
        [Tooltip("최종 인코딩(결정 ①): Radiance/RadianceGI 아틀라스를 RGBAHalf 선형 HDR(무클램프)로 저장 — 1 을 넘는 직사광도 보존되고 스티칭·dilation 이 클램프 전 값을 평균. 끄면 RGBA32 + 0~1 클램프(LDR, 구 동작). 매핑 검증 모드는 항상 RGBA32.")]
        public bool hdrOutput = true;

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

        [Header("Local Lights — Point · Spot · Area(Rect/Disc)")]
        [Tooltip("로컬 광원 출처. Scene = 로드된 씬의 켜진 Point/Spot/Area/Disc 라이트 전부(Directional 제외 — 태양은 위 Radiance 설정), " +
                 "Listed = localLights 배열만, None = 로컬 광원 없음(태양 + 하늘만, 구 거동). Radiance·RadianceGI 모드에서 1차 직사 + 바운스(GI) 에 들어간다.")]
        public LocalLightSource localLightSource = LocalLightSource.Scene;
        [Tooltip("localLightSource = Listed 일 때 쓰는 라이트 목록.")]
        public Light[] localLights;
        [Tooltip("켜면 Unity 라이트 Mode 를 따름: Baked = 직사+간접, Mixed = 간접만(직사는 실시간 라이트가 그림), Realtime = 제외. 끄면 전부 직사+간접.")]
        public bool localLightsFollowBakeType = false;
        [Tooltip("면광원(Rect/Disc) 1차 직사 샘플 수. 면 위 저불일치 점마다 그림자 레이 1발 — 반그림자·근거리 정확도. 8~32 권장.")]
        [Min(1)] public int areaLightSamples = 16;
        [Tooltip("그림자 반경(Unity Light 'Baked Shadow Radius')이 있는 Point/Spot 의 1차 직사 샘플 수(반경 0 이면 1발 — 하드 그림자).")]
        [Min(1)] public int softLightSamples = 8;

        [Header("SSAA — 텍셀 내부 슈퍼샘플링(그림자 경계 계단 제거)")]
        [Tooltip("Off=텍셀당 1샘플(기존, 비트 동일) / Adaptive=라이팅 급변 텍셀만 S×S 재샘플(권장) / Full=전 텍셀 S×S(S²배 비용, 레퍼런스). " +
                 "Radiance·RadianceGI 모드에서만 동작한다.")]
        public SsaaMode ssaaMode = SsaaMode.Off;
        [Tooltip("한 축 서브샘플 수 S. 텍셀당 S×S 개를 RGSS(회전격자) 위치에서 샘플해 평균. 2면 4단계, 3이면 9단계 계조.")]
        [Range(2, 4)] public int ssaaFactor = 2;
        [Tooltip("Adaptive 엣지 판정: 이웃 텍셀과의 상대 휘도차 임계. 낮을수록 더 많은 텍셀이 재샘플 대상(품질↑ 비용↑). 0.05~0.2 권장.")]
        [Range(0.01f, 1f)] public float ssaaEdgeThreshold = 0.10f;
        [Tooltip("Adaptive 엣지 판정에서 '같은 표면 이웃'으로 인정할 노멀 각도 상한. 이보다 각지면 정상 불연속으로 보고 비교하지 않는다.")]
        [Range(1f, 180f)] public float ssaaEdgeNormalAngleDeg = 45f;
        [Tooltip("검출된 엣지 마스크를 확장할 링 수. 1이면 계단 양쪽을 모두 재샘플(권장). 0이면 한쪽만 매끈해져 오히려 티난다.")]
        [Range(0, 3)] public int ssaaEdgeDilate = 1;
        [Tooltip("재샘플된 텍셀을 마젠타로 칠해 마스크를 눈으로 확인(라이팅 값 대신). 튜닝용 — 켜면 결과 아틀라스는 못 쓴다.")]
        public bool ssaaDebugMask = false;

        [Header("Bake 배치 · GPU 분할 (G6-3b · G6-4)")]
        [Tooltip("인스턴스들의 valid lumel 을 이 수까지 모아 1패스·SSAA 를 한 번에 굽는다(GPU = 1 업로드·1 readback, Burst = 1 잡). " +
                 "0 = 인스턴스마다(구 거동). 결과는 배치 크기와 무관하게 비트 동일 — 메모리(점당 ~28B + lumel 맵)와 진행 표시 간격만 달라진다.")]
        [Min(0)] public int bakeBatchLumels = 1 << 20;
        [Tooltip("GPU 디스패치 1회당 점 수. 작을수록 긴 단일 디스패치의 TDR(GPU 타임아웃) 위험↓·디스패치 수↑. 결과는 분할과 무관하게 비트 동일. " +
                 "한 디스패치 한도(65535 그룹 × 64 ≈ 4.19M 점)를 넘지 않게 자동 제한.")]
        [Min(64)] public int gpuDispatchChunk = GpuBakeLimits.DefaultChunk;

        [Header("적응 샘플링 · 진행형 (RadianceGI)")]
        [Tooltip("간접광을 패스(samplesPerPass spp)로 나눠 누적하고, 매 패스 끝에 텍셀별 표준오차가 adaptiveThreshold × 텍셀 휘도 이하로 " +
                 "수렴한 텍셀은 더 쏘지 않는다(indirectSamples = 상한). 밝고 트인 면은 일찍 멈추고 그림자 경계·구석만 상한까지 — 같은 상한에서 레이 절감, " +
                 "같은 시간이면 상한을 올려 노이즈↓. 끄면 전 텍셀 indirectSamples(구 거동과 비트 동일). CPU/Burst/GPU 공통(판정은 관리 코드라 백엔드 무관).")]
        public bool adaptiveSampling = false;
        [Tooltip("패스당 간접 경로 수. 적응 판정·진행형 미리보기의 간격. 작을수록 판정이 촘촘(조기 종료↑)하지만 패스마다 디스패치·readback·판정 " +
                 "오버헤드가 붙는다 — 8 이면 256 상한에서 같은 평균 spp 의 균등 베이크보다 1.7배 느렸고 32 에서 같아졌다(2026-10-28 실측).")]
        [Min(1)] public int samplesPerPass = 32;
        [Tooltip("이 spp 이전에는 수렴 판정을 하지 않는다(분산 추정이 믿을 만해지는 최소 표본). 16~32 권장. " +
                 "경로가 전부 0 인 텍셀은 이 값 × 4 까지 더 쏜 뒤 판정(접촉 그림자가 0 으로 굳는 것 방지).")]
        [Min(2)] public int adaptiveMinSamples = 16;
        [Tooltip("수렴 기준: 평균 휘도의 표준오차 ≤ 이 값 × (직사+간접) 텍셀 휘도. 0.02~0.05 권장 — 작을수록 품질↑ 비용↑.")]
        [Range(0.001f, 0.5f)] public float adaptiveThreshold = 0.03f;
        [Tooltip("진행형 미리보기(우클릭 'Bake Progressive (Preview)')에서 이 패스 수마다 아틀라스를 다시 합성(denoise·스티칭·dilation 포함)해 화면 갱신.")]
        [Min(1)] public int progressivePreviewEveryPasses = 1;

        [Header("GPU 후처리 (Denoise · 시임 스티칭 · Dilation)")]
        [Tooltip("시임 스티칭(Tier1·Tier2) + dilation 을 컴퓨트 셰이더로(LightmapPost.compute). 결과 = CPU 판(나눗셈 ULP 차이만). " +
                 "compute 미지원·셰이더 로드 실패 시 CPU 판으로 폴백. gpuDenoise 가 켜져 있으면 denoise 도 같은 업로드 안에서 GPU 로.")]
        public bool gpuPostProcess = false;
        [Tooltip("gpuPostProcess 가 켜져 있을 때 denoise 도 GPU 로(페이지 업로드 1회 안에서 denoise → 스티칭 → dilation → readback 1회). " +
                 "식·탭 순서는 CPU 판과 같고 pow/exp 가 GPU 근사라 CPU 판과 ε(상대 ~1e-6) 차이. 끄면 denoise 만 CPU(denoiseBurst 에 따라 Burst/C#).")]
        public bool gpuDenoise = true;
        [Tooltip("GPU 후처리용 LightmapPost.compute. 비우면 Resources → (에디터) 패키지 경로에서 찾음.")]
        public ComputeShader postShader;

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
        [Tooltip("Gpu 백엔드용 PathTrace.compute. 비우면 Resources → (에디터) 프로젝트 경로에서 찾음. 플레이어 빌드에서 GPU 베이크 시 지정.")]
        public ComputeShader pathTraceShader;



        IOccluder _occluder;
        // 라이팅 베이크 상태(BlitRegion에서 사용)

        DirectionalLight _sun;
        BakeLight[] _bakeLights = System.Array.Empty<BakeLight>();   // 이번 베이크의 로컬 광원(BeginRun 에서 수집 — CPU/Burst/GPU 공유)

        /// <summary>마지막 베이크에 쓴 로컬 광원(검증용).</summary>
        internal BakeLight[] LastBakeLights => _bakeLights;

        /// <summary>localLightSource 에 따라 로컬 광원을 BakeLight 로 변환(Directional·꺼진 라이트·강도 0 제외).</summary>
        internal BakeLight[] CollectBakeLights()
        {
            var opt = new BakeLightUtil.Options
            {
                AreaSamples = Mathf.Max(1, areaLightSamples),
                SoftSamples = Mathf.Max(1, softLightSamples),
                FollowBakeType = localLightsFollowBakeType,
            };
            return BakeLightUtil.Collect(localLightSource, localLights, opt);
        }

        [ContextMenu("From DirectionalLight Component")]
        public void GetDirectionLightFromLightComponent()
        {
            // 씬 라이트 탐색 공용 경로(Unity 6.5 API 분기 포함). 순서 = 객체 ID 오름차순 → Directional 이 여럿이면 결정적으로 첫 번째.
            foreach (var light in BakeLightUtil.FindSceneLights())
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
        int _kAccum = -1;          // CSRadianceAccum(적응·진행형 이어 굽기)
        GpuPathAccum _gpuAccum;    // 이어 굽기 상주 버퍼(_gpuScene 과 동일 수명, 배치마다 다시 Bind)

        // 적응 샘플링 · 진행형 통계(완료 로그용): 실제 경로 수 / 상한(점 × indirectSamples) / 누적기에 넣은 점 수
        long _accumSamples, _accumBudget, _accumPoints;
        bool _accumUsed;

        [Header("Bake Result (저장 · 런타임 로드)")]
        [Tooltip("베이크 결과 에셋(SO). applyOnEnable 이면 OnEnable 에 이걸로 적용 — 플레이/빌드에서 베이크 없이 동작.")]
        public AtlasBakeAsset bakeAsset;
        [Tooltip("라이팅 모드(Radiance/RadianceGI) 베이크 후 자동 저장(SO + EXR 페이지). 에디터 전용. 매핑 검증 모드는 저장 안 함.")]
        public bool saveBakeResult = true;
        [Tooltip("에셋·EXR 저장 폴더(Assets/ 하위).")]
        public string outputFolder = "Assets/CustomLightmapper/BakeOutput";   // 소비 프로젝트 Assets/ 하위(패키지 폴더 밖)
        [Tooltip("페이지별 EXR 사본도 저장(half float, 선형).")]
        public bool exportExr = true;
        [Tooltip("OnEnable 에 bakeAsset 적용(메시·머티리얼 + ST/페이지 MPB). MPB 는 씬에 저장되지 않아 로드마다 다시 적용해야 함.")]
        public bool applyOnEnable = true;

        [Header("Result (read-only)")]
        [SerializeField] int instanceCount;
        [SerializeField] int pageCount;
        [SerializeField, Range(0f, 1f)] float utilization;
        [SerializeField] bool overflow;
        [SerializeField] Texture2DArray atlas;
        [SerializeField] Material sharedMat;
        [SerializeField] string progressiveStatus;

        // 이번 베이크의 아틀라스 인코딩(BakeAndApply 에서 1회 결정 → BlitRegion·아틀라스 생성이 공유)
        bool _hdrAtlas;

        // 마지막 베이크의 인스턴스 기록(저장용 — 세션 런타임 상태, 직렬화 안 함)
        struct BakeRecord { public MeshFilter filter; public Mesh source; public Material sourceMat; public Mesh uv2; public Vector4 st; public int page; }
        readonly System.Collections.Generic.List<BakeRecord> _lastBake = new System.Collections.Generic.List<BakeRecord>();

        // 검증(Tests 어셈블리)용 결과 접근 — 베이크 결과 아틀라스·적용 머티리얼(둘 다 DontSave 런타임 객체)
        internal Texture2DArray Atlas => atlas;
        internal Material SharedMaterial => sharedMat;
        /// <summary>마지막 베이크가 실제로 쓴 조도 백엔드("Gpu"/"Burst"/"CPU", 매핑 모드면 null) — 폴백 여부 확인용.</summary>
        internal string LastBakeBackend { get; private set; }
        /// <summary>마지막 베이크의 적응·진행형 누적 통계: 실제 간접 경로 수 / 상한(점 × indirectSamples). 누적기를 안 썼으면 0.</summary>
        internal long LastAccumSamples => _accumSamples;
        internal long LastAccumBudget => _accumBudget;
        /// <summary>마지막 합성의 스티칭·dilation 실행 위치("GPU"/"CPU").</summary>
        internal string LastPostBackend { get; private set; }
        /// <summary>마지막 합성의 denoise 실행 위치("GPU"/"Burst"/"C#", 안 했으면 "off").</summary>
        internal string LastDenoiseBackend { get; private set; }

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

        // ───────────────────── 베이크 실행(동기 BakeAndApply · 진행형 미리보기 공용 단계) ─────────────────────
        //   BeginRun(할당·버퍼·씬) → PrepareInstance(파라미터화·텍셀 복원) → 조도(배치 FlushBatch / 진행형 누적)
        //   → FinishInstance(blit + 시임 입력 + 메시 적용) → Compose(denoise·stitch·dilate·아틀라스) → EndRun(기록·로그·해제·저장).
        //   진행형은 같은 단계를 쓰되 조도를 패스마다 누적하고 ResetCanvas + BlitInstance + Compose 로 미리보기를 다시 만든다.
        sealed class BakeRun
        {
            public MeshFilter[] filters;
            public AllocationResult alloc;
            public int res, pages;
            public Color[][] slices;
            public bool[][] validMask;
            public bool doDenoise;
            public Vector3[][] guideN, guideP;
            public MeshFilter[] appliedF;
            public Mesh[] origMesh;
            public Material[] origMat;
            // 시임 스티칭 입력(페이지별 누적): Tier1=텍셀 인덱스 그룹, Tier2=텍셀좌표 segment 그룹
            public System.Collections.Generic.List<int[]>[] seamTexelGroups;
            public System.Collections.Generic.List<LightmapSeamStitch.Seg[]>[] seamSegGroups;
            // SSAA 통계(완료 로그용): 재샘플된 텍셀 수 / 그 텍셀에서 쏜 서브샘플 수 / 전체 valid 텍셀 수
            public int ssaaTexels;
            public long ssaaSubSamples, ssaaBaseTexels;
            public int batches, prepared;
            public int stitchT1, stitchT2;
            public bool postOnGpu;
            public bool denoiseOnGpu;
            public readonly System.Diagnostics.Stopwatch swTotal = new System.Diagnostics.Stopwatch();
            public readonly System.Diagnostics.Stopwatch swScene = new System.Diagnostics.Stopwatch();
            public readonly System.Diagnostics.Stopwatch swBake = new System.Diagnostics.Stopwatch();
            public readonly System.Diagnostics.Stopwatch swPost = new System.Diagnostics.Stopwatch();
            // 진행형 미리보기
            public System.Collections.Generic.List<PendingBake> all;
            public ProgressiveAccumulator acc;
            public IPathAccumBackend accBackend;
            public bool accOwned;
            public int passesSinceCompose, previews;
        }

        [ContextMenu("Bake & Apply")]
        public void BakeAndApply()
        {
            CancelProgressive(finalize: false);
            var filters = ResolveTargets();
            if (filters.Length == 0)
            {
                Debug.LogWarning("[AtlasApply] 대상 MeshFilter 가 없습니다. targets 지정 또는 자식에 MeshFilter 배치.", this);
                return;
            }

            var r = BeginRun(filters);

            // 3) 인스턴스마다 파라미터화→조립→텍셀복원 → (배치) 조도 베이크 → 영역 blit→메시/ST 적용
            //   G6-3b: 준비된 인스턴스를 bakeBatchLumels(valid lumel 수)까지 모아 1패스·SSAA 2패스를 한 번에 굽는다
            //   (GPU = 1 업로드·분할 디스패치·1 readback, Burst = 1 잡). 텍셀 조도는 점별 독립(점·노멀·시드 — 시드는 인스턴스 안
            //   lumel 인덱스 기준)이라 배치 크기와 무관하게 비트 동일. blit·시임·메시 적용은 배치 안에서도 인스턴스 순서 그대로.
            var pending = new System.Collections.Generic.List<PendingBake>();
            long pendingLumels = 0;
            r.swBake.Start();
            try
            {
                for (int i = 0; i < filters.Length; i++)
                {
                    var pb = PrepareInstance(r, i);
                    if (pb == null) continue;
                    pending.Add(pb);
                    pendingLumels += CountValid(pb.lumel);
                    if (pendingLumels >= bakeBatchLumels) { FlushBatch(r, pending, pendingLumels); pendingLumels = 0; }   // 0 = 인스턴스마다(구 거동)
                }
                FlushBatch(r, pending, pendingLumels);
            }
            finally
            {
#if UNITY_EDITOR
                UnityEditor.EditorUtility.ClearProgressBar();
#endif
            }
            r.swBake.Stop();

            Compose(r);
            EndRun(r, saveBakeResult);
        }

        // 1) 할당 → 2) 페이지 버퍼·가이드 → 씬(차폐자/GI 씬·광원) 1회 구성. 메시 스왑 전에 원본 sharedMesh 로 빌드.
        BakeRun BeginRun(MeshFilter[] filters)
        {
            // 베이크 시간 계측 — 단계별로 나눠야 α(알파 컷아웃)/원반 샘플링의 비용을 분리해서 볼 수 있다.
            //   scene = 씬 구성(BVH 빌드 + 알파 마스크 굽기) / bake = 레이트레이싱 루프 / post = 디노이즈·스티치·디레이트
            var r = new BakeRun { filters = filters };
            r.swTotal.Start();

            _lastBake.Clear();
            _accumSamples = 0; _accumBudget = 0; _accumPoints = 0; _accumUsed = false;
            // 1) 인스턴스별 월드 표면적 → 할당(ST + 페이지)
            var insts = new LightmapInstance[filters.Length];
            for (int i = 0; i < filters.Length; i++)
                insts[i] = new LightmapInstance
                {
                    InstanceId = i,
                    WorldArea = LightmapAllocator.WorldArea(filters[i].sharedMesh, filters[i].transform.localToWorldMatrix),
                };

            var settings = BuildSettings();
            r.alloc = LightmapAllocator.Allocate(insts, settings);
            r.res = settings.AtlasResolution;
            r.pages = Mathf.Max(1, r.alloc.PageCount);
            int res = r.res, pages = r.pages;

            // 최종 인코딩(1회 결정): 라이팅 모드 + hdrOutput → RGBAHalf 선형 HDR. 미지원 플랫폼은 RGBA32 폴백.
            _hdrAtlas = hdrOutput && (mode == BakeMode.Radiance || mode == BakeMode.RadianceGI);
            LastBakeBackend = null;
            if (_hdrAtlas && !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf))
            {
                Debug.LogWarning("[AtlasApply] RGBAHalf 미지원 플랫폼 — RGBA32(0~1 클램프)로 폴백.", this);
                _hdrAtlas = false;
            }

            // 2) 페이지별 픽셀 버퍼를 배경색으로 초기화 (Linear 아틀라스라 표시색을 linear 로 저장)
            //    validMask: blit 로 실제 칠해진 텍셀 표시 → dilation 의 소스/보존 기준.
            r.slices = new Color[pages][];
            r.validMask = new bool[pages][];
            for (int p = 0; p < pages; p++)
            {
                r.slices[p] = new Color[res * res];
                r.validMask[p] = new bool[res * res]; // 기본 false(=배경)
            }
            ResetCanvas(r);

            // Denoise 가이드(텍셀별 월드 노멀·위치) — 라이팅 모드에서만 수집(BlitRegion 이 채움).
            r.doDenoise = denoise && (mode == BakeMode.Radiance || mode == BakeMode.RadianceGI);
            if (r.doDenoise)
            {
                r.guideN = new Vector3[pages][];
                r.guideP = new Vector3[pages][];
                for (int p = 0; p < pages; p++)
                {
                    r.guideN[p] = new Vector3[res * res];
                    r.guideP[p] = new Vector3[res * res];
                }
            }

            EnsureMaterial();

            // Radiance 모드: 씬 전체(모든 인스턴스) 차폐자 + 광원 1회 구성 → 인스턴스 간 그림자 반영.
            // 메시 스왑 전에 원본 sharedMesh 로 빌드(지오메트리는 uv2 메시와 동일 좌표).
            r.swScene.Start();
            // 로컬 광원(Point/Spot/Area) — 라이팅 모드만. 씬 구성(Burst/GPU 업로드) 전에 확정.
            _bakeLights = (mode == BakeMode.Radiance || mode == BakeMode.RadianceGI) ? CollectBakeLights() : System.Array.Empty<BakeLight>();
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
            r.swScene.Stop();
            LastBakeBackend = mode == BakeMode.Radiance ? "CPU"
                            : mode == BakeMode.RadianceGI ? (_gpuReady ? "Gpu" : _burstReady ? "Burst" : "CPU")
                            : null;

            r.appliedF = new MeshFilter[filters.Length];
            r.origMesh = new Mesh[filters.Length];
            r.origMat = new Material[filters.Length];

            // 인스턴스마다 자기 ST 영역(ox,oy,sidePx) 기준으로 아틀라스 전역 인덱스/좌표를 만들어 페이지에 모은다.
            r.seamTexelGroups = new System.Collections.Generic.List<int[]>[pages];
            r.seamSegGroups = new System.Collections.Generic.List<LightmapSeamStitch.Seg[]>[pages];
            for (int p = 0; p < pages; p++)
            {
                r.seamTexelGroups[p] = new System.Collections.Generic.List<int[]>();
                r.seamSegGroups[p] = new System.Collections.Generic.List<LightmapSeamStitch.Seg[]>();
            }
            return r;
        }

        // 페이지 버퍼 = 배경색, valid = false (진행형은 미리보기마다 다시 칠하려고 반복 호출).
        void ResetCanvas(BakeRun r)
        {
            Color bgLin = background.linear;
            for (int p = 0; p < r.pages; p++)
            {
                var s = r.slices[p];
                for (int k = 0; k < s.Length; k++) s[k] = bgLin;
                System.Array.Clear(r.validMask[p], 0, r.validMask[p].Length);
            }
        }

        // 인스턴스 i 파라미터화 → 패킹 → uv2 조립 → 텍셀 복원. 건너뛰면 null.
        PendingBake PrepareInstance(BakeRun r, int i)
        {
            var mf = r.filters[i];
            // 이전 베이크/에셋 적용으로 uv2 메시·라이트맵 머티리얼이 들어가 있어도 진짜 원본 기준으로 처리(재베이크·복원·저장 정합)
            OriginalOf(mf, out var src, out var srcMat);
            Vector4 st = r.alloc.Instances[i].ScaleOffset;
            int page = Mathf.Clamp(r.alloc.Instances[i].LightmapIndex, 0, r.pages - 1);

            // ST → 아틀라스 픽셀 영역 (Allocate 가 sd/r, rx/r 형태라 정수 복원 정확)
            int sidePx = Mathf.Max(1, Mathf.RoundToInt(st.x * r.res));
            int ox = Mathf.RoundToInt(st.z * r.res);
            int oy = Mathf.RoundToInt(st.w * r.res);

            Mesh uv2mesh;
            LumelMap lumel;
            SeamTable seams = null;
            try
            {
                var pr = ParameterizationPipeline.Run(src, segmentation, flatten);
                if (pr.Charts == null || pr.Charts.Length == 0) { Debug.LogWarning($"[AtlasApply] '{mf.name}' 차트 0개 — 건너뜀.", mf); return null; }
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
                return null;
            }

            r.prepared++;
            return new PendingBake
            {
                index = i, mf = mf, src = src, srcMat = srcMat, st = st,
                page = page, sidePx = sidePx, ox = ox, oy = oy,
                uv2mesh = uv2mesh, lumel = lumel, seams = seams,
            };
        }

        // 배치 1개: 조도(1패스 + SSAA) → 인스턴스 순서대로 FinishInstance. pending 은 비운다.
        void FlushBatch(BakeRun r, System.Collections.Generic.List<PendingBake> pending, long pendingLumels)
        {
            if (pending.Count == 0) return;
            r.batches++;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.DisplayProgressBar("AtlasApply 베이크",
                $"배치 {r.batches} — 인스턴스 {r.prepared}/{r.filters.Length} (lumel {pendingLumels})", (float)r.prepared / r.filters.Length);
#endif
            var rads = BakeLumelsBatch(pending);
            if (rads != null)
            {
                for (int k = 0; k < pending.Count; k++) r.ssaaBaseTexels += CountValid(pending[k].lumel);
                ApplySsaaBatch(pending, rads, ref r.ssaaTexels, ref r.ssaaSubSamples);
            }
            for (int k = 0; k < pending.Count; k++) FinishInstance(r, pending[k], rads?[k]);
            pending.Clear();
        }

        // 구운 조도(rad)를 아틀라스에 blit + 시임 입력 누적 + 렌더러에 uv2 메시·ST 적용
        void FinishInstance(BakeRun r, PendingBake pb, Vector3[] rad)
        {
            BlitInstance(r, pb, rad);
            RegisterInstance(r, pb);
        }

        void BlitInstance(BakeRun r, PendingBake pb, Vector3[] rad)
        {
            int page = pb.page;
            Color tint = Color.HSVToRGB((pb.index * 0.6180339887f) % 1f, 0.65f, 1f); // 황금비 분산 → 인접 인스턴스 색 분리
            BlitRegion(r.slices[page], r.validMask[page], r.res, pb.ox, pb.oy, pb.sidePx, pb.lumel, tint, rad,
                       r.doDenoise ? r.guideN[page] : null, r.doDenoise ? r.guideP[page] : null);
        }

        // 시임 스티칭 입력 누적(지오메트리만 — 조도와 무관해 인스턴스당 1회) + 메시·머티리얼·ST 적용
        void RegisterInstance(BakeRun r, PendingBake pb)
        {
            int i = pb.index;
            var mf = pb.mf;
            var uv2mesh = pb.uv2mesh;
            var seams = pb.seams;
            int page = pb.page, ox = pb.ox, oy = pb.oy, sidePx = pb.sidePx, res = r.res;

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
                                r.seamTexelGroups[page].Add(texels);
                            }
                        }
                    }
                    if (seamStitchTier2)
                    {
                        var egs = SeamEdgeBuilder.Build(uv2mesh.triangles, uv2mesh.vertexCount, seams.Groups);
                        if (nrm != null) egs = SeamEdgeBuilder.FilterByNormal(egs, nrm, seamMaxAngleDeg); // 하드 엣지 제외
                        var segGroups = SeamEdgeBuilder.BuildSegments(egs, uv2s, ox, oy, sidePx);
                        if (segGroups.Count > 0) r.seamSegGroups[page].AddRange(segGroups);
                    }
                }
            }

            // 렌더러에 조립 메시 + ST 적용 (원본 보관)
            var rnd = mf.GetComponent<MeshRenderer>();
            r.appliedF[i] = mf;
            r.origMesh[i] = pb.src;
            r.origMat[i] = pb.srcMat;
            _lastBake.Add(new BakeRecord { filter = mf, source = pb.src, sourceMat = pb.srcMat, uv2 = uv2mesh, st = pb.st, page = page });

            mf.sharedMesh = uv2mesh;
            if (rnd != null)
            {
                rnd.sharedMaterial = sharedMat;
                var mpb = new MaterialPropertyBlock();
                rnd.GetPropertyBlock(mpb);
                mpb.SetVector("_LightmapST", pb.st);
                mpb.SetFloat("_LightmapIndex", page);
                rnd.SetPropertyBlock(mpb);
            }
        }

        // 후처리(denoise → 시임 스티칭 → dilation) + 아틀라스 텍스처 생성/갱신. 페이지 버퍼를 제자리 변경한다.
        void Compose(BakeRun r)
        {
            int res = r.res, pages = r.pages;
            var slices = r.slices;
            var validMask = r.validMask;
            r.swPost.Start();

            // 3.3) Denoise — MC 노이즈(그레인)를 노멀·위치·색 가이드 에지 보존 필터로 평활.
            //      Seam Stitch 전: 경계 텍셀 값을 먼저 안정화한 뒤 스티칭·확장해야 시임이 매끈하다.
            //      가이드는 valid 텍셀에서만 유효 — 필터도 valid 만 읽고 씀(배경/거터 불변, dilation 소스 마스크 불변).
            //      gpuPostProcess + gpuDenoise 면 GPU 후처리 안에서(업로드 1회) — GPU 가 실패하면 원본이 그대로라 CPU 판으로 처음부터.
            var dq = new DenoiseSettings
            {
                Iterations = denoiseIterations,
                NormalPower = denoiseNormalPower,
                PositionSigma = denoisePositionSigmaTexels / Mathf.Max(0.001f, texelsPerWorldUnit),
                ColorSigma = denoiseColorSigma,
            };
            bool wantGpuDenoise = r.doDenoise && gpuPostProcess && gpuDenoise;
            if (r.doDenoise && !wantGpuDenoise) DenoiseCpu(r, dq);

            // 3.4) Seam Stitch → 3.5) Dilation. GPU 후처리(gpuPostProcess)는 페이지마다 (denoise→)T1→T2→dilate 를 한 번에(실패 시 CPU).
            r.stitchT1 = r.stitchT2 = 0;
            r.postOnGpu = gpuPostProcess && StitchAndDilateGpu(r, wantGpuDenoise, dq);
            r.denoiseOnGpu = r.postOnGpu && wantGpuDenoise;
            if (!r.postOnGpu)
            {
                if (wantGpuDenoise) DenoiseCpu(r, dq);
                StitchAndDilateCpu(r);
            }
            LastPostBackend = r.postOnGpu ? "GPU" : "CPU";
            LastDenoiseBackend = !r.doDenoise ? "off" : r.denoiseOnGpu ? "GPU" : (denoiseBurst ? "Burst" : "C#");

            r.swPost.Stop();

            // 4) 아틀라스 텍스처 생성/갱신 — 라이팅 모드는 RGBAHalf(8B/texel, 1024² 페이지 8MB), 그 외 RGBA32
            var atlasFormat = _hdrAtlas ? TextureFormat.RGBAHalf : TextureFormat.RGBA32;
            if (atlas == null || atlas.width != res || atlas.depth != pages || atlas.format != atlasFormat)
            {
                // 교체 시 이전 런타임 아틀라스 해제(DontSave 로 만든 것만 — 에셋은 건드리지 않음)
                if (atlas != null && (atlas.hideFlags & HideFlags.DontSave) != 0)
                {
                    if (Application.isPlaying) Destroy(atlas); else DestroyImmediate(atlas);
                }
                // linear:true — 아틀라스에 선형값 저장(샘플 시 sRGB 디코드 없음). 셰이더 출력 시 프레임버퍼가 한 번만 sRGB 인코딩.
                atlas = new Texture2DArray(res, res, pages, atlasFormat, false, true)
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
        }

        void DenoiseCpu(BakeRun r, in DenoiseSettings dq)
        {
            for (int p = 0; p < r.pages; p++)
            {
                if (denoiseBurst)
                    LightmapDenoiseBurstJob.Denoise(r.slices[p], r.validMask[p], r.guideN[p], r.guideP[p], r.res, r.res, dq);
                else
                    LightmapDenoise.Denoise(r.slices[p], r.validMask[p], r.guideN[p], r.guideP[p], r.res, r.res, dq);
            }
        }

        // 3.4) Seam Stitch — 차트 경계 정점(Tier1)/모서리(Tier2)의 불연속을 valid 텍셀 평균으로 제거.
        //      Dilation 보다 먼저: 경계값을 먼저 일치시킨 뒤 그 값을 거터로 확장해야 한다.
        // 3.5) Push-Pull Dilation — 거터/배경 텍셀을 인접 valid 평균으로 확장해 검은 시임 제거.
        //      페이지별 독립 처리(같은 페이지에 여러 인스턴스가 들어가도 차트 간 거터를 함께 메움).
        void StitchAndDilateCpu(BakeRun r)
        {
            int res = r.res, pages = r.pages;
            if (seamStitchTier1 || seamStitchTier2)
            {
                for (int p = 0; p < pages; p++)
                {
                    if (seamStitchTier1 && r.seamTexelGroups[p].Count > 0)
                    {
                        LightmapSeamStitch.Stitch(r.slices[p], r.validMask[p], r.seamTexelGroups[p]);
                        r.stitchT1 += r.seamTexelGroups[p].Count;
                    }
                    if (seamStitchTier2 && r.seamSegGroups[p].Count > 0)
                    {
                        LightmapSeamStitch.StitchEdges(r.slices[p], r.validMask[p], res, r.seamSegGroups[p], Mathf.Max(1, seamStitchIterations));
                        r.stitchT2 += r.seamSegGroups[p].Count;
                    }
                }
            }

            if (dilate && dilateIterations > 0)
            {
                for (int p = 0; p < pages; p++)
                {
                    if (dilateBurst)
                        LightmapPostProcessBurstJob.Dilate(r.slices[p], r.validMask[p], res, res, dilateIterations);
                    else
                        LightmapPostProcess.Dilate(r.slices[p], r.validMask[p], res, res, dilateIterations);
                }
            }
        }

        // GPU 후처리(LightmapPost.compute): 페이지마다 업로드 1회 → (denoise 핑퐁) → T1(웨이브) → T2(샘플→수집 Jacobi) → dilation 핑퐁 → readback 1회.
        //   계획(웨이브·CSR)은 CPU 판과 같은 순서·같은 소스 집합이라 스티칭·dilation = CPU 판(나눗셈 ULP 차이만), denoise 는 ε. 실패하면 false → CPU 판.
        bool StitchAndDilateGpu(BakeRun r, bool withDenoise, in DenoiseSettings dq)
        {
            if (!SystemInfo.supportsComputeShaders) return false;
            var cs = LightmapPostGpu.Resolve(postShader);
            if (cs == null) { Debug.LogWarning("[AtlasApply] LightmapPost.compute 로드 실패 — 후처리 CPU 폴백.", this); return false; }

            var opt = new LightmapPostGpu.Options
            {
                Tier1 = seamStitchTier1,
                Tier2 = seamStitchTier2,
                Tier2Iterations = Mathf.Max(1, seamStitchIterations),
                DilateIterations = dilate ? dilateIterations : 0,
                Denoise = withDenoise,
                DenoiseSettings = dq,
            };
            // 전 페이지를 먼저 복사본으로 돌려 하나라도 실패하면 원본을 건드리지 않고 CPU 판으로 넘긴다(부분 적용 방지).
            var outPx = new Color[r.pages][];
            var outValid = new bool[r.pages][];
            int t1 = 0, t2 = 0;
            for (int p = 0; p < r.pages; p++)
            {
                outPx[p] = (Color[])r.slices[p].Clone();
                outValid[p] = (bool[])r.validMask[p].Clone();
                if (withDenoise) { opt.GuideNormal = r.guideN[p]; opt.GuidePos = r.guideP[p]; }
                if (!LightmapPostGpu.Run(cs, outPx[p], outValid[p], r.res, r.seamTexelGroups[p], r.seamSegGroups[p], opt, out string err))
                {
                    Debug.LogWarning($"[AtlasApply] GPU 후처리 실패(페이지 {p}: {err}) — CPU 폴백.", this);
                    return false;
                }
                if (seamStitchTier1) t1 += r.seamTexelGroups[p].Count;
                if (seamStitchTier2) t2 += r.seamSegGroups[p].Count;
            }
            for (int p = 0; p < r.pages; p++) { r.slices[p] = outPx[p]; r.validMask[p] = outValid[p]; }
            r.stitchT1 = t1; r.stitchT2 = t2;
            return true;
        }

        // 기록(원복·저장) · 결과 필드 · 완료 로그 · 씬 해제 · 저장
        void EndRun(BakeRun r, bool save)
        {
            var filters = r.filters;
            var alloc = r.alloc;
            int res = r.res, pages = r.pages;
            var atlasFormat = _hdrAtlas ? TextureFormat.RGBAHalf : TextureFormat.RGBA32;

            _appliedFilters = r.appliedF;
            _originalMeshes = r.origMesh;
            _originalMats = r.origMat;

            instanceCount = filters.Length;
            pageCount = pages;
            utilization = alloc.Utilization;
            overflow = alloc.Overflow;

            string postTag = r.postOnGpu ? "GPU" : null;
            string dilateInfo = (dilate && dilateIterations > 0)
                ? $", dilate={dilateIterations}×({postTag ?? (dilateBurst ? "Burst" : "C#")})"
                : ", dilate=off";
            string stitchInfo = (seamStitchTier1 || seamStitchTier2)
                ? $", stitch(T1={r.stitchT1},T2={r.stitchT2}{(postTag != null ? ",GPU" : "")})"
                : ", stitch=off";
            string denoiseInfo = r.doDenoise
                ? $", denoise={denoiseIterations}×({(r.denoiseOnGpu ? "GPU" : denoiseBurst ? "Burst" : "C#")})"
                : ", denoise=off";
            string occInfo = (occluders != null && occluders.Length > 0) ? $", occluders={occluders.Length}" : "";
            string lightInfo = (mode == BakeMode.Radiance || mode == BakeMode.RadianceGI)
                ? $", lights={BakeLightUtil.Summary(_bakeLights)}({localLightSource})" : "";
            // α 상태를 완료 로그에 붙인다 — 알파가 실제로 켜졌는지 한 줄로 확인 가능해야 한다.
            string alphaInfo = !alphaCutoutShadows ? ", alpha=OFF(토글)"
                             : (_alphaData == null || !_alphaData.Enabled) ? ", alpha=DISABLED(컷아웃 머티리얼 0)"
                             : $", alpha=ON(masks={_alphaData.MaskW.Length}, {_alphaData.MaskBits.Length * 4 / 1024}KB)";
            // 직사광 샘플링 상태(원반 샘플링은 directSamples·각지름 둘 다 켜야 동작)
            string directInfo = (directSamples > 1 && sunAngularDiameterDeg > 0f)
                ? $", direct={directSamples}×(sun {sunAngularDiameterDeg:F2}°)"
                : ", direct=1×(hard)";
            // SSAA: 재샘플 텍셀 비율과 실제 레이 배수. 배수 = (전체-엣지 + 서브샘플)/전체 — 적응형이 얼마나 싸게 먹혔는지.
            string ssaaInfo;
            if (ssaaMode == SsaaMode.Off || ssaaFactor < 2 || r.ssaaBaseTexels == 0)
            {
                ssaaInfo = ", ssaa=off";
            }
            else
            {
                double pct = 100.0 * r.ssaaTexels / r.ssaaBaseTexels;
                double mult = (double)(r.ssaaBaseTexels - r.ssaaTexels + r.ssaaSubSamples) / r.ssaaBaseTexels;
                ssaaInfo = $", ssaa={ssaaMode} {ssaaFactor}×{ssaaFactor}(텍셀 {r.ssaaTexels}/{r.ssaaBaseTexels}={pct:F1}%, rays ×{mult:F2})"
                         + (ssaaDebugMask ? " ⚠DEBUG_MASK" : "");
            }

            // G6-3b 배치·G6-4 분할 — 조도 베이크 호출 수(1패스 기준) = 배치 수
            string batchInfo = (mode == BakeMode.Radiance || mode == BakeMode.RadianceGI)
                ? $", batch={r.batches}×(≤{bakeBatchLumels} lumels){(_gpuReady ? $" gpuChunk={gpuDispatchChunk}" : "")}"
                : "";
            // 적응 샘플링 · 진행형: 실제로 쏜 간접 경로 / 상한(점 × indirectSamples)
            string accumInfo = "";
            if (_accumUsed && _accumBudget > 0 && _accumPoints > 0)
            {
                double avg = (double)_accumSamples / _accumPoints;
                accumInfo = $", {(adaptiveSampling ? "adaptive" : "accum")}(spp 평균 {avg:F1}/{_accumBudget / _accumPoints}, 경로 {100.0 * _accumSamples / _accumBudget:F1}%, pass×{Mathf.Max(1, samplesPerPass)})";
            }
            if (r.acc != null) accumInfo += $", progressive(패스 {r.acc.Passes}, 미리보기 {r.previews})";

            r.swTotal.Stop();
            // 시간: 총계 + 단계별. bake 가 레이트레이싱이므로 알파/원반 샘플링 비용은 여기서 본다.
            string timeInfo =
                $"\n  time: total={r.swTotal.Elapsed.TotalSeconds:F2}s" +
                $" | scene(BVH+알파마스크)={r.swScene.Elapsed.TotalSeconds:F2}s" +
                $" | bake(레이트레이싱)={r.swBake.Elapsed.TotalSeconds:F2}s" +
                $" | post(denoise+stitch+dilate)={r.swPost.Elapsed.TotalSeconds:F2}s";

            Debug.Log($"[AtlasApply] {filters.Length} insts → atlas {res}×{res}×{pages} {atlasFormat}, util={alloc.Utilization:P1}, mode={mode}{occInfo}{lightInfo}{alphaInfo}{directInfo}{ssaaInfo}{batchInfo}{accumInfo}{denoiseInfo}{stitchInfo}{dilateInfo}" +
                      (alloc.Overflow ? "  ⚠ overflow(클램프됨)" : "") + timeInfo, this);

            DisposeBakeScene();

#if UNITY_EDITOR
            if (save && (mode == BakeMode.Radiance || mode == BakeMode.RadianceGI)) SaveBakeResult();
#endif
        }

        // NativeArray 보유 차폐자/씬·GPU 버퍼 해제(blit 이 끝난 뒤에만)
        void DisposeBakeScene()
        {
            if (_occluder is System.IDisposable od) od.Dispose();
            _occluder = null;
            if (_giScene is System.IDisposable gd) gd.Dispose();
            _giScene = null;
            _gpuAccum?.Dispose(); _gpuAccum = null;
            if (_gpuReady) { _gpuScene.Dispose(); _gpuScene = null; _gpuIo?.Dispose(); _gpuIo = null; _gpuReady = false; _pathCS = null; _kRadiance = -1; _kAccum = -1; }
            if (_burstReady) { _burstScene.Dispose(); _burstReady = false; }
        }

        // ───────────────────── 진행형 미리보기(저 spp → 누적) ─────────────────────
        //   RadianceGI 전용. 전 인스턴스를 준비해 lumel 전체를 한 누적기에 묶고(배치 = 1, 메모리 상주), 패스 0(samplesPerPass spp)
        //   결과로 곧바로 아틀라스·메시를 적용한 뒤 패스마다 같은 RNG 스트림을 이어 누적 → progressivePreviewEveryPasses 마다 다시 합성.
        //   끝나면 SSAA → 최종 합성 → 저장. 최종 아틀라스 = 같은 설정의 BakeAndApply 와 같다
        //   (CPU/Burst 비트 동일 · adaptive 면 GPU 도 비트 동일 · 비적응 GPU 는 CSRadiance 대비 통계 일치).
        //   에디터: "Bake Progressive (Preview)" 가 EditorApplication.update 로 패스를 돌림. 런타임/테스트: BeginProgressive + ProgressiveStep 반복.
        BakeRun _progressive;

        /// <summary>진행형 베이크가 돌고 있는지.</summary>
        public bool IsProgressiveRunning => _progressive != null;

        /// <summary>
        /// 진행형 시작: 준비 + 패스 0 + 첫 미리보기 적용. 이어서 돌 패스가 있으면 true(→ <see cref="ProgressiveStep"/> 반복).
        /// RadianceGI 가 아니면 일괄 베이크로 대신하고 false.
        /// </summary>
        public bool BeginProgressive()
        {
            CancelProgressive(finalize: false);
            if (mode != BakeMode.RadianceGI)
            {
                Debug.LogWarning("[AtlasApply] 진행형 미리보기는 RadianceGI 전용 — 일괄 베이크로 실행.", this);
                BakeAndApply();
                return false;
            }
            var filters = ResolveTargets();
            if (filters.Length == 0)
            {
                Debug.LogWarning("[AtlasApply] 대상 MeshFilter 가 없습니다. targets 지정 또는 자식에 MeshFilter 배치.", this);
                return false;
            }

            var r = BeginRun(filters);
            r.all = new System.Collections.Generic.List<PendingBake>();
            r.swBake.Start();
            for (int i = 0; i < filters.Length; i++)
            {
                var pb = PrepareInstance(r, i);
                if (pb != null) r.all.Add(pb);
            }
            CollectPoints(r.all, out var pts, out var nrm, out var sds, out int n);
            r.accBackend = CreateAccumBackend(out r.accOwned);
            r.acc = new ProgressiveAccumulator(r.accBackend, AccumSettings(), pts, nrm, sds, n);
            r.acc.Step();                         // 패스 0 — 저 spp 미리보기
            r.batches = 1;
            _accumUsed = true;
            var rads = ScatterRadiance(r.all, r.acc.Result());
            for (int k = 0; k < r.all.Count; k++) FinishInstance(r, r.all[k], rads[k]);
            r.swBake.Stop();

            _progressive = r;
            Compose(r);
            r.previews = 1;
            // 진행 중에도 Restore 가 이번 적용분을 되돌리도록 원복 기록을 먼저 갱신
            _appliedFilters = r.appliedF; _originalMeshes = r.origMesh; _originalMats = r.origMat;
            UpdateProgressiveStatus(r);

            if (r.acc.Finished) { FinishProgressive(canceled: false); return false; }
            return true;
        }

        /// <summary>패스 1회(+ 주기적 미리보기 합성). 끝나면 최종 합성·저장까지 하고 false.</summary>
        public bool ProgressiveStep()
        {
            var r = _progressive;
            if (r == null) return false;
            r.swBake.Start();
            bool more = r.acc.Step();
            r.swBake.Stop();
            if (!more) { FinishProgressive(canceled: false); return false; }
            if (++r.passesSinceCompose >= Mathf.Max(1, progressivePreviewEveryPasses))
            {
                Recompose(r, ScatterRadiance(r.all, r.acc.Result()));
                r.passesSinceCompose = 0;
                r.previews++;
            }
            UpdateProgressiveStatus(r);
            return true;
        }

        // 현재 추정으로 페이지 버퍼를 처음부터 다시 칠하고 후처리·업로드(시임 입력·메시 적용은 그대로)
        void Recompose(BakeRun r, Vector3[][] rads)
        {
            ResetCanvas(r);
            for (int k = 0; k < r.all.Count; k++) BlitInstance(r, r.all[k], rads[k]);
            Compose(r);
        }

        // 마무리: canceled 면 현재 spp 로 확정(SSAA·저장 생략), 아니면 SSAA → 최종 합성 → 저장
        void FinishProgressive(bool canceled)
        {
            var r = _progressive;
            _progressive = null;
            UnhookProgressive();
            if (r == null) return;

            var rads = ScatterRadiance(r.all, r.acc.Result());
            _accumSamples += r.acc.TotalSamples;
            _accumBudget += (long)r.acc.PointCount * r.acc.MaxSamples;
            _accumPoints += r.acc.PointCount;
            if (r.accOwned) r.accBackend.Dispose();
            r.accBackend = null;

            if (!canceled)
            {
                r.swBake.Start();
                for (int k = 0; k < r.all.Count; k++) r.ssaaBaseTexels += CountValid(r.all[k].lumel);
                ApplySsaaBatch(r.all, rads, ref r.ssaaTexels, ref r.ssaaSubSamples);
                r.swBake.Stop();
            }
            Recompose(r, rads);
            r.previews++;
            progressiveStatus = canceled
                ? $"취소 — {r.acc.SamplesDone}/{r.acc.MaxSamples} spp 에서 확정"
                : $"완료 — 패스 {r.acc.Passes}";
            EndRun(r, save: !canceled && saveBakeResult);
        }

        // 진행 중 세션 정리. finalize = 현재 spp 로 확정해 적용, 아니면 자원만 해제(메시·아틀라스는 마지막 미리보기 상태로 남음).
        void CancelProgressive(bool finalize)
        {
            if (_progressive == null) return;
            if (finalize) { FinishProgressive(canceled: true); return; }
            var r = _progressive;
            _progressive = null;
            UnhookProgressive();
            if (r.accOwned) r.accBackend?.Dispose();
            r.accBackend = null;
            DisposeBakeScene();
            progressiveStatus = "중단";
        }

        void UpdateProgressiveStatus(BakeRun r)
        {
            var a = r.acc;
            progressiveStatus = $"패스 {a.Passes} · spp {a.SamplesDone}/{a.MaxSamples} · 활성 {a.ActiveCount}/{a.PointCount}";
        }

        void OnDisable() => CancelProgressive(finalize: false);

#if UNITY_EDITOR
        [ContextMenu("Bake Progressive (Preview)")]
        public void BakeProgressive()
        {
            if (!BeginProgressive()) return;
            UnityEditor.EditorApplication.update -= ProgressiveTick;
            UnityEditor.EditorApplication.update += ProgressiveTick;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            UnityEditor.SceneView.RepaintAll();
        }

        [ContextMenu("Cancel Progressive Bake")]
        public void CancelProgressiveBake() => CancelProgressive(finalize: true);

        void ProgressiveTick()
        {
            if (this == null || _progressive == null) { UnhookProgressive(); return; }
            ProgressiveStep();
            UnityEditor.SceneView.RepaintAll();
        }

        // 도메인 리로드 전 Native/GPU 자원 해제(델리게이트·세션은 리로드에서 사라짐)
        void OnBeforeAssemblyReload() => CancelProgressive(finalize: false);
#endif

        void UnhookProgressive()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= ProgressiveTick;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
#endif
        }

        // LumelMap 을 아틀라스 페이지 버퍼의 (ox,oy) 영역에 blit. valid 텍셀만, 모드별 색.
        // valid: 실제 칠한 텍셀을 true 로 표시 → 이후 dilation 의 소스/보존 마스크로 사용.
        // radiance: BakeLumels(+SSAA) 가 미리 구운 조도(li 인덱스). null 이면 인라인 CPU 폴백.
        void BlitRegion(Color[] slice, bool[] valid, int res, int ox, int oy, int sidePx, LumelMap lm, Color tint, Vector3[] radiance = null,
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
                                // BakeLumels(+SSAA) 가 구워 넘긴 값 우선. 없으면 인라인 CPU(동일 시드·동일 원점 → 같은 값).
                                Vector3 lin;
                                if (radiance != null)
                                {
                                    lin = radiance[li];
                                }
                                else
                                {
                                    Vector3 wp = lm.WorldPos[li];
                                    Vector3 wn = lm.WorldNormal[li];
                                    // 텍셀별 결정적 시드 → 노이즈 재현 가능. 평가 원점은 표면에서 bias 만큼 띄움.
                                    uint s = LightmapSSAA.TexelSeed(seed, li);
                                    lin = RadianceCore.EvaluateRadiance(_occluder, wp + wn * surfaceBias, wn, _sun, _ambientLin, aoSamples, s, _bakeLights);
                                }
                                c = _hdrAtlas ? ToHdrColor(lin) : ToColor(lin);
                                break;
                            }
                        case BakeMode.RadianceGI:
                            {
                                // 미리 베이크된 radiance[li] 사용, 아니면 인라인 CPU(EvaluateRadiance).
                                Vector3 lin;
                                if (radiance != null)
                                {
                                    lin = radiance[li];
                                }
                                else
                                {
                                    Vector3 wp = lm.WorldPos[li];
                                    Vector3 wn = lm.WorldNormal[li];
                                    uint s = LightmapSSAA.TexelSeed(seed, li);
                                    // Direct + 경로추적 Indirect (2단 인스턴싱 씬, 하늘=sky). 알베도는 런타임 적용이라 여기선 조도.
                                    lin = RadianceCore.EvaluateRadiance(_giScene, wp + wn * surfaceBias, wn, _sun, _sky, _giQ, s, _bakeLights);
                                }
                                c = _hdrAtlas ? ToHdrColor(lin) : ToColor(lin);
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

        static int CountValid(in LumelMap lm)
        {
            if (lm.Valid == null) return 0;
            int c = 0;
            for (int i = 0; i < lm.Valid.Length; i++) if (lm.Valid[i]) c++;
            return c;
        }

        // G6-3b 배치 대기열 한 칸 — 준비(파라미터화·텍셀 복원)가 끝나 조도 베이크·blit 을 기다리는 인스턴스.
        sealed class PendingBake
        {
            public int index;
            public MeshFilter mf;
            public Mesh src;
            public Material srcMat;
            public Vector4 st;
            public int page, sidePx, ox, oy;
            public Mesh uv2mesh;
            public LumelMap lumel;
            public SeamTable seams;
        }

        // 라이팅 모드 텍셀 조도를 배치 전체에서 한 번에 베이크 → 인스턴스별 li 인덱스 Vector3[](invalid = zero).
        //   라이팅 모드가 아니면 null(BlitRegion 이 디버그 색을 칠한다).
        //   시드 = LightmapSSAA.TexelSeed(seed, li) — li 는 인스턴스 안 lumel 인덱스 = 기존 seed + li*2654435761u 규약 그대로 →
        //   배치와 무관하고, 기존 백엔드 교차검증(CPU/Burst/GPU Diff 메뉴)이 그대로 성립한다.
        Vector3[][] BakeLumelsBatch(System.Collections.Generic.List<PendingBake> batch)
        {
            if (mode != BakeMode.Radiance && mode != BakeMode.RadianceGI) return null;

            CollectPoints(batch, out var pts, out var nrm, out var sds, out int n);
            if (n == 0) return ScatterRadiance(batch, null);
            return ScatterRadiance(batch, BakePointsFull(pts, nrm, sds, n));
        }

        // 배치의 valid lumel 을 인스턴스·li 순서로 압축(평가 원점 = 표면 + surfaceBias·노멀, 시드 = TexelSeed(seed, li)).
        void CollectPoints(System.Collections.Generic.List<PendingBake> batch, out Vector3[] pts, out Vector3[] nrm, out uint[] sds, out int n)
        {
            n = 0;
            for (int b = 0; b < batch.Count; b++) n += CountValid(batch[b].lumel);
            pts = new Vector3[n];
            nrm = new Vector3[n];
            sds = new uint[n];
            int k = 0;
            for (int b = 0; b < batch.Count; b++)
            {
                var lm = batch[b].lumel;
                if (lm.Valid == null) continue;
                for (int li = 0; li < lm.Valid.Length; li++)
                {
                    if (!lm.Valid[li]) continue;
                    Vector3 wn = lm.WorldNormal[li];
                    pts[k] = lm.WorldPos[li] + wn * surfaceBias;   // 세 백엔드 공통 평가 원점
                    nrm[k] = wn;
                    sds[k] = LightmapSSAA.TexelSeed(seed, li);
                    k++;
                }
            }
        }

        // 압축 조도(CollectPoints 순서) → 인스턴스별 li 인덱스 배열(invalid = zero). rad = null 이면 전부 zero.
        static Vector3[][] ScatterRadiance(System.Collections.Generic.List<PendingBake> batch, Vector3[] rad)
        {
            var result = new Vector3[batch.Count][];
            int k = 0;
            for (int b = 0; b < batch.Count; b++)
            {
                var lm = batch[b].lumel;
                result[b] = new Vector3[lm.Valid != null ? lm.Valid.Length : 0];   // invalid lumel = zero(BlitRegion 이 valid 만 칠함)
                if (rad == null || lm.Valid == null) continue;
                for (int li = 0; li < lm.Valid.Length; li++)
                    if (lm.Valid[li]) result[b][li] = rad[k++];
            }
            return result;
        }

        // 1패스·SSAA 공통 진입점: 적응 샘플링이면 누적기(패스 + 수렴 조기 종료), 아니면 일괄 BakePoints(구 경로 그대로).
        Vector3[] BakePointsFull(Vector3[] pts, Vector3[] nrm, uint[] seeds, int n)
        {
            if (!(mode == BakeMode.RadianceGI && adaptiveSampling) || n == 0) return BakePoints(pts, nrm, seeds, n);
            var backend = CreateAccumBackend(out bool owned);
            try
            {
                var acc = new ProgressiveAccumulator(backend, AccumSettings(), pts, nrm, seeds, n);
                acc.Run();
                _accumUsed = true;
                _accumSamples += acc.TotalSamples;
                _accumBudget += (long)n * acc.MaxSamples;
                _accumPoints += n;
                return acc.Result();
            }
            finally { if (owned) backend.Dispose(); }
        }

        AdaptiveSampling AccumSettings() => new AdaptiveSampling
        {
            MaxSamples = _giQ.IndirectSamples,
            SamplesPerPass = samplesPerPass,
            Adaptive = adaptiveSampling,
            MinSamples = adaptiveMinSamples,
            Threshold = adaptiveThreshold,
        };

        // 이어 굽기 백엔드: GPU(상주 버퍼 재사용 — 소유권은 베이커) → Burst → CPU. 일괄 경로와 같은 백엔드 우선순위.
        IPathAccumBackend CreateAccumBackend(out bool owned)
        {
            if (_gpuReady && _kAccum >= 0)
            {
                owned = false;
                return _gpuAccum ??= new GpuPathAccum(_gpuScene, _pathCS, _kAccum, _sun, _burstSky, _giQ, gpuDispatchChunk);
            }
            owned = true;
            if (_burstReady) return new BurstPathAccum(_burstScene, _burstSky, _sun, _giQ);
            return new CpuPathAccum(_giScene, _sun, _sky, _giQ, _bakeLights);
        }

        // 임의 점 리스트 → 조도. 1패스(텍셀 중앙)와 SSAA 2패스(서브샘플)가 같은 진입점을 쓴다 —
        //   덕분에 SSAA 가 백엔드 분기를 다시 짜지 않고 CPU/Burst/GPU 를 그대로 탄다.
        //   ⚠ pts 는 이미 surfaceBias 가 더해진 평가 원점이어야 한다.
        Vector3[] BakePoints(Vector3[] pts, Vector3[] nrm, uint[] seeds, int n)
        {
            var result = new Vector3[n];
            if (n == 0) return result;

            // Radiance: 차폐자 + ambient*AO (경로추적 없음)
            if (mode == BakeMode.Radiance)
            {
                for (int k = 0; k < n; k++)
                    result[k] = RadianceCore.EvaluateRadiance(_occluder, pts[k], nrm[k], _sun, _ambientLin, aoSamples, seeds[k], _bakeLights);
                return result;
            }

            // RadianceGI: GPU → Burst → CPU 순으로 가용 백엔드
            if (_gpuReady)
                return DispatchRadianceGpu(_gpuScene, _pathCS, _kRadiance, _sun, _burstSky, _giQ, pts, nrm, seeds, n, _gpuIo, gpuDispatchChunk);

            if (_burstReady)
            {
                var p = new NativeArray<Vector3>(n, Allocator.TempJob);
                var q = new NativeArray<Vector3>(n, Allocator.TempJob);
                var v = new NativeArray<bool>(n, Allocator.TempJob);
                var s = new NativeArray<uint>(n, Allocator.TempJob);
                for (int k = 0; k < n; k++) { p[k] = pts[k]; q[k] = nrm[k]; v[k] = true; s[k] = seeds[k]; }

                var rad = BurstRadianceBaker.Bake(_burstScene, _burstSky, _sun, _giQ, p, q, v, s, Allocator.TempJob);
                for (int k = 0; k < n; k++) result[k] = rad[k];

                p.Dispose(); q.Dispose(); v.Dispose(); s.Dispose(); rad.Dispose();
                return result;
            }

            for (int k = 0; k < n; k++)
                result[k] = RadianceCore.EvaluateRadiance(_giScene, pts[k], nrm[k], _sun, _sky, _giQ, seeds[k], _bakeLights);
            return result;
        }

        // SSAA — 1패스 조도(rad)에서 라이팅이 급변하는 텍셀을 찾아(Full 이면 전 valid 텍셀) 그 텍셀만
        //   S×S 서브샘플로 다시 굽고 평균으로 덮어쓴다.
        //   아틀라스 해상도도 valid 마스크도 안 건드리므로 이후 denoise/stitch/dilate/ST 는 전부 그대로다.
        //   1패스 값은 엣지 텍셀에서 버린다 — 짝수 S 의 서브샘플 격자에는 중앙점이 없어 재사용하면
        //   가중치가 편향되고, 엣지는 전체의 몇 % 라 버리는 비용이 무시할 수준이다.
        //   G6-3b: 배치 안 인스턴스들의 서브샘플을 모아(최대 MaxSsaaBatchPoints 점씩) 한 번에 굽는다 — 시드는 (li, s) 별이라 비트 동일.
        const int MaxSsaaBatchPoints = 1 << 22;   // 4M 점 ≈ 서브샘플 입력 112MB 상한(SSAA Full × 큰 배치의 메모리 보호)

        sealed class SsaaJob
        {
            public int batchIndex;
            public LumelSubsamples ss;
            public int spt, start, count;
        }

        void ApplySsaaBatch(System.Collections.Generic.List<PendingBake> batch, Vector3[][] rads, ref int edgeTexels, ref long subSamples)
        {
            if (ssaaMode == SsaaMode.Off || ssaaFactor < 2) return;
            if (mode != BakeMode.Radiance && mode != BakeMode.RadianceGI) return;
            if (rads == null) return;

            int S = Mathf.Clamp(ssaaFactor, 2, 4);
            var jobs = new System.Collections.Generic.List<SsaaJob>();
            var pts = new System.Collections.Generic.List<Vector3>();
            var nrm = new System.Collections.Generic.List<Vector3>();
            var sds = new System.Collections.Generic.List<uint>();
            int edgeAcc = 0; long subAcc = 0;

            void BakeAndResolve()
            {
                if (jobs.Count == 0) return;
                var sub = BakePointsFull(pts.ToArray(), nrm.ToArray(), sds.ToArray(), pts.Count);   // 적응 샘플링이면 서브샘플도 누적기
                foreach (var job in jobs)
                {
                    ResolveSsaa(job, sub, rads[job.batchIndex]);
                    edgeAcc += job.ss.SlotCount;
                    subAcc += job.count;
                }
                jobs.Clear(); pts.Clear(); nrm.Clear(); sds.Clear();
            }

            for (int b = 0; b < batch.Count; b++)
            {
                var job = BuildSsaaJob(batch[b], b, rads[b], S, pts, nrm, sds);
                if (job != null) jobs.Add(job);
                if (pts.Count >= MaxSsaaBatchPoints) BakeAndResolve();
            }
            BakeAndResolve();
            edgeTexels += edgeAcc;
            subSamples += subAcc;
        }

        // 인스턴스 1개의 SSAA 서브샘플을 배치 목록에 이어 붙인다. 재샘플할 게 없으면 null(통계도 안 셈 — 구 ApplySsaa 와 같은 조기 반환).
        SsaaJob BuildSsaaJob(PendingBake pb, int batchIndex, Vector3[] rad, int S,
                             System.Collections.Generic.List<Vector3> pts, System.Collections.Generic.List<Vector3> nrm,
                             System.Collections.Generic.List<uint> sds)
        {
            var lm = pb.lumel;
            if (rad == null || lm.Valid == null) return null;
            int spt = S * S;

            bool[] mask;
            if (ssaaMode == SsaaMode.Full)
            {
                mask = (bool[])lm.Valid.Clone();
            }
            else
            {
                // 이웃 인정 거리: 텍셀 1개 ≈ 1/texelsPerWorldUnit 월드 단위 → 4텍셀까지만 같은 표면으로 본다.
                float maxDist = 4f / Mathf.Max(0.001f, texelsPerWorldUnit);
                mask = LightmapSSAA.DetectEdges(lm, rad, pb.sidePx, ssaaEdgeThreshold,
                                                Mathf.Cos(ssaaEdgeNormalAngleDeg * Mathf.Deg2Rad),
                                                maxDist, ssaaEdgeDilate, out _);
            }

            int slots = 0;
            for (int i = 0; i < mask.Length; i++) if (mask[i]) slots++;
            if (slots == 0) return null;

            // 메모리 가드: 서브샘플 저장 ≈ slots*S²*25B. Full + 큰 sidePx 조합이 수백 MB 로 튀는 것을 막는다.
            long estBytes = (long)slots * spt * 25L;
            if (estBytes > 512L * 1024 * 1024)
            {
                Debug.LogWarning($"[AtlasApply] SSAA 스킵 — 서브샘플 버퍼 추정 {estBytes / (1024 * 1024)}MB (>512MB). " +
                                 "ssaaMode=Adaptive 로 낮추거나 ssaaFactor/atlasResolution 을 줄이세요.", this);
                return null;
            }

            var ss = TexelMapper.MapSubsamples(pb.uv2mesh, pb.sidePx, pb.mf.transform.localToWorldMatrix, S, mask);
            if (ss.SlotCount == 0) return null;

            // 유효 서브샘플만 압축해 배치 목록에 추가(1패스와 같은 백엔드·같은 원점 규약).
            int start = pts.Count;
            for (int slot = 0; slot < ss.SlotCount; slot++)
            {
                int li = ss.TexelOfSlot[slot];
                int b = slot * spt;
                for (int s = 0; s < spt; s++)
                {
                    if (!ss.Valid[b + s]) continue;
                    Vector3 wn = ss.WorldNormal[b + s];
                    pts.Add(ss.WorldPos[b + s] + wn * surfaceBias);
                    nrm.Add(wn);
                    sds.Add(LightmapSSAA.SubSeed(seed, li, s));   // 서브샘플마다 다른 MC 노이즈 → 평균이 AA+디노이즈 겸함
                }
            }
            int count = pts.Count - start;
            if (count == 0) return null;
            return new SsaaJob { batchIndex = batchIndex, ss = ss, spt = spt, start = start, count = count };
        }

        // 평균 → 텍셀 덮어쓰기. 유효 서브샘플 0인 텍셀(중앙만 아슬하게 걸린 얇은 삼각형)은 1패스 값 유지.
        void ResolveSsaa(SsaaJob job, Vector3[] sub, Vector3[] rad)
        {
            var ss = job.ss;
            int spt = job.spt;
            int cursor = job.start;
            for (int slot = 0; slot < ss.SlotCount; slot++)
            {
                int b = slot * spt;
                Vector3 sum = Vector3.zero; int cnt = 0;
                for (int s = 0; s < spt; s++)
                {
                    if (!ss.Valid[b + s]) continue;
                    sum += sub[cursor++];
                    cnt++;
                }
                int li = ss.TexelOfSlot[slot];
                if (cnt > 0) rad[li] = sum / cnt;
                if (ssaaDebugMask) rad[li] = new Vector3(1f, 0f, 1f);   // 마스크 가시화(마젠타)
            }
        }

        // 재사용 GPU I/O 버퍼 홀더(grow-on-demand). per-instance 반복 DispatchRadianceGpu 에서
        //   ComputeBuffer 5개(pts/nrm/valid/seed/radiance) 를 매번 생성/해제하던 것을 제거 — 요청 n 이
        //   현재 capacity 를 초과할 때만 다음 2^k 로 (재)할당, 그 이하는 앞 n개만 SetData/readback 재사용.
        //   수명: BakePoints(GPU) 경로는 인스턴스 필드(_gpuIo, _gpuScene 과 동일 수명), Backend Diff 메뉴는 로컬(finally 해제).
        internal sealed class GpuIoBuffers : System.IDisposable
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

        // CSRadiance 디스패치 공통(BakePoints·Backend Diff 재사용). 입력 pts/nrm/seeds 는 길이 n.
        //   버퍼는 재사용 홀더 io 로 grow-on-demand(초과 시에만 재할당). uniform 규약은 BurstRadianceBaker 정합.
        //   ⚠ 데이터 경로 불변: 앞 n개만 업로드/읽기 + _Count=n 가드 → 이전 GetData 판과 바이트 동일.
        //   G6-4 분할: 한 번 업로드 → chunk 점씩 _Base 만 바꿔 연속 Dispatch(동기화 없음) → 1회 readback.
        //     한 Dispatch 그룹 한도(65535×64 ≈ 4.19M 점 — 큰 인스턴스·SSAA Full 이 넘던 한도) 해소 + 긴 단일 디스패치 TDR 위험 완화.
        //     스레드 i = 점 _Base+i, 시드는 점별 → chunk 와 무관하게 비트 동일. _Base 는 끝나면 0 으로 복원(공유 ComputeShader 상태).
        internal static Vector3[] DispatchRadianceGpu(GpuScene gpuScene, ComputeShader cs, int kernel,
            DirectionalLight sun, BurstSky sky, BakeQualitySettings q,
            Vector3[] pts, Vector3[] nrm, uint[] seeds, int n, GpuIoBuffers io, int chunk = GpuBakeLimits.DefaultChunk)
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

            PathTraceShader.BindRadiance(cs, kernel, gpuScene, sun, sky, q);   // 씬·조명·α + 품질·태양·하늘 uniform(CSRadianceAccum 과 공유)
            cs.SetBuffer(kernel, "_Points", io.Points);
            cs.SetBuffer(kernel, "_Normals", io.Normals);
            cs.SetBuffer(kernel, "_Valid", io.Valid);
            cs.SetBuffer(kernel, "_Seeds", io.Seeds);
            cs.SetInt("_Count", n);
            cs.SetBuffer(kernel, "_RadianceOut", io.Radiance);

            try
            {
                foreach (var (start, count) in GpuBakeLimits.Tiles(n, chunk))
                {
                    cs.SetInt("_Base", start);
                    cs.Dispatch(kernel, GpuBakeLimits.Groups(count), 1, 1);
                }
            }
            finally { cs.SetInt("_Base", 0); }

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

        // ───────────────────── 원본 추적 · 결과 저장 · 에셋 적용 ─────────────────────

        // 현재 적용 상태와 무관한 '진짜 원본'(메시·머티리얼). 이전 베이크/에셋 적용으로 바뀐 렌더러면 보관해 둔 원본을 돌려준다.
        void OriginalOf(MeshFilter mf, out Mesh mesh, out Material mat)
        {
            var rnd = mf.GetComponent<MeshRenderer>();
            mesh = mf.sharedMesh;
            mat = rnd != null ? rnd.sharedMaterial : null;
            if (_appliedFilters == null) return;
            int k = System.Array.IndexOf(_appliedFilters, mf);
            if (k < 0) return;
            if (_originalMeshes != null && k < _originalMeshes.Length && _originalMeshes[k] != null) mesh = _originalMeshes[k];
            if (_originalMats != null && k < _originalMats.Length && _originalMats[k] != null) mat = _originalMats[k];
        }

        void OnEnable()
        {
            if (applyOnEnable && bakeAsset != null) ApplyFromAsset();
        }

        /// <summary>저장된 베이크 결과 적용(베이크 없음): 대상 MeshFilter 에 uv2 메시·라이트맵 머티리얼 + ST/페이지 MPB. 원본은 Restore 용으로 보관.</summary>
        [ContextMenu("Apply From Bake Asset")]
        public void ApplyFromAsset()
        {
            if (bakeAsset == null) { Debug.LogWarning("[AtlasApply] bakeAsset 없음.", this); return; }
            if (!bakeAsset.Validate(out string err)) { Debug.LogWarning($"[AtlasApply] bakeAsset '{bakeAsset.name}' 적용 불가: {err}", this); return; }

            var applied = new System.Collections.Generic.List<MeshFilter>();
            var oMesh = new System.Collections.Generic.List<Mesh>();
            var oMat = new System.Collections.Generic.List<Material>();
            int missing = 0;
            foreach (var e in bakeAsset.entries)
            {
                var mf = ResolvePath(e.path);
                if (mf == null) { missing++; continue; }
                var rnd = mf.GetComponent<MeshRenderer>();
                applied.Add(mf); oMesh.Add(e.sourceMesh); oMat.Add(e.sourceMaterial);
                mf.sharedMesh = e.uv2Mesh;
                if (rnd != null)
                {
                    rnd.sharedMaterial = bakeAsset.material;
                    var mpb = new MaterialPropertyBlock();
                    rnd.GetPropertyBlock(mpb);
                    mpb.SetVector("_LightmapST", e.scaleOffset);
                    mpb.SetFloat("_LightmapIndex", e.page);
                    rnd.SetPropertyBlock(mpb);
                }
            }
            _appliedFilters = applied.ToArray();
            _originalMeshes = oMesh.ToArray();
            _originalMats = oMat.ToArray();
            if (missing > 0)
                Debug.LogWarning($"[AtlasApply] bakeAsset 적용: {applied.Count}개 적용, {missing}개 대상 못 찾음(경로 변경/이름 중복 확인).", this);
        }

        /// <summary>
        /// 마지막 베이크(이 세션)로 결과 에셋 객체를 만든다(디스크 저장 없음). 아틀라스·머티리얼·uv2 메시는 저장 가능한 사본(DontSave 아님).
        /// uv2 메시는 원본 메시당 1개(파라미터화가 결정적이라 같은 원본 = 같은 uv2). 마지막 베이크가 없으면 null.
        /// </summary>
        internal AtlasBakeAsset BuildBakeAsset()
        {
            if (_lastBake.Count == 0 || atlas == null)
            { Debug.LogWarning("[AtlasApply] 저장할 베이크 없음 — 먼저 'Bake & Apply'.", this); return null; }
            var shader = Shader.Find("CustomLightmapper/LightmapDebug");
            if (shader == null) { Debug.LogError("[AtlasApply] 셰이더 'CustomLightmapper/LightmapDebug' 없음 — 저장 불가.", this); return null; }

            var asset = ScriptableObject.CreateInstance<AtlasBakeAsset>();
            var arr = new Texture2DArray(atlas.width, atlas.height, atlas.depth, atlas.format, false, true)
            {
                name = "Atlas",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = atlas.filterMode,
            };
            for (int p = 0; p < atlas.depth; p++) arr.SetPixels(atlas.GetPixels(p), p);
            arr.Apply(false);
            asset.atlas = arr;

            asset.material = new Material(shader) { name = "LightmapAtlas_Mat" };
            asset.material.SetTexture("_Lightmaps", arr);

            var uv2BySource = new System.Collections.Generic.Dictionary<Mesh, Mesh>();
            var entries = new AtlasBakeAsset.Entry[_lastBake.Count];
            for (int i = 0; i < _lastBake.Count; i++)
            {
                var r = _lastBake[i];
                if (!uv2BySource.TryGetValue(r.source, out var saved))
                {
                    saved = Instantiate(r.uv2);
                    saved.name = (r.source != null ? r.source.name : "Mesh") + "_uv2";
                    saved.hideFlags = HideFlags.None;
                    uv2BySource[r.source] = saved;
                }
                entries[i] = new AtlasBakeAsset.Entry
                {
                    path = PathOf(r.filter.transform),
                    sourceMesh = r.source,
                    sourceMaterial = r.sourceMat,
                    uv2Mesh = saved,
                    scaleOffset = r.st,
                    page = r.page,
                };
            }
            asset.entries = entries;
            asset.mode = mode.ToString();
            asset.resolution = atlas.width;
            asset.pageCount = atlas.depth;
            asset.bakedAtUtc = System.DateTime.UtcNow.ToString("o");
            return asset;
        }

        // 베이커 하위면 상대 경로, 아니면 '/' 로 시작하는 씬 절대 경로(이름 기반)
        string PathOf(Transform t)
        {
            var parts = new System.Collections.Generic.List<string>();
            var cur = t;
            while (cur != null && cur != transform) { parts.Add(cur.name); cur = cur.parent; }
            if (cur == transform) { parts.Reverse(); return string.Join("/", parts); }
            parts.Clear();
            for (cur = t; cur != null; cur = cur.parent) parts.Add(cur.name);
            parts.Reverse();
            return "/" + string.Join("/", parts);
        }

        MeshFilter ResolvePath(string path)
        {
            Transform t;
            if (string.IsNullOrEmpty(path)) t = transform;
            else if (path[0] == '/') { var go = GameObject.Find(path); t = go != null ? go.transform : null; }
            else t = transform.Find(path);
            return t != null ? t.GetComponent<MeshFilter>() : null;
        }

#if UNITY_EDITOR
        /// <summary>
        /// 결과 저장(SO + 페이지 EXR) → 렌더러를 저장된 uv2 메시·머티리얼로 교체(씬 저장 시 참조 유지) → bakeAsset 지정.
        /// 기존 bakeAsset 이 있으면 같은 파일(GUID 유지)에 서브에셋을 갈아 끼운다.
        /// </summary>
        [ContextMenu("Save Bake Result (SO + EXR)")]
        public void SaveBakeResult()
        {
            var built = BuildBakeAsset();
            if (built == null) return;

            string path = bakeAsset != null ? UnityEditor.AssetDatabase.GetAssetPath(bakeAsset) : null;
            AtlasBakeAsset target;
            if (!string.IsNullOrEmpty(path))
            {
                // 기존 파일 재사용: 이전 서브에셋 제거 후 내용 교체(메인 에셋 GUID → 씬 참조 유지)
                target = bakeAsset;
                foreach (var sub in UnityEditor.AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
                {
                    if (sub == null) continue;
                    UnityEditor.AssetDatabase.RemoveObjectFromAsset(sub);
                    DestroyImmediate(sub, true);
                }
                UnityEditor.EditorUtility.CopySerialized(built, target);
                DestroyImmediate(built);
            }
            else
            {
                if (!BakeOutputUtil.EnsureFolder(outputFolder))
                { Debug.LogError($"[AtlasApply] outputFolder '{outputFolder}' 는 Assets/ 하위여야 함 — 저장 생략.", this); return; }
                string baseName = BakeOutputUtil.SanitizeFileName($"{gameObject.scene.name}_{name}_Atlas");
                path = UnityEditor.AssetDatabase.GenerateUniqueAssetPath($"{outputFolder}/{baseName}.asset");
                UnityEditor.AssetDatabase.CreateAsset(built, path);
                target = built;
            }

            // 서브에셋: 아틀라스·머티리얼·uv2 메시(고유)
            UnityEditor.AssetDatabase.AddObjectToAsset(target.atlas, target);
            UnityEditor.AssetDatabase.AddObjectToAsset(target.material, target);
            var added = new System.Collections.Generic.HashSet<Mesh>();
            foreach (var e in target.entries)
                if (added.Add(e.uv2Mesh)) UnityEditor.AssetDatabase.AddObjectToAsset(e.uv2Mesh, target);

            // 페이지별 EXR 사본(half, 선형)
            if (exportExr)
            {
                target.pagesExr = new Texture2D[target.pageCount];
                for (int p = 0; p < target.pageCount; p++)
                {
                    string exrPath = path.Substring(0, path.Length - ".asset".Length) + $"_page{p}.exr";
                    var exr = BakeOutputUtil.EncodeExr(target.atlas.GetPixels(p), target.resolution, target.resolution, fullFloat: false);
                    target.pagesExr[p] = BakeOutputUtil.WriteExrAsset(exrPath, exr, atlasFilter);
                }
            }
            else target.pagesExr = new Texture2D[0];

            UnityEditor.EditorUtility.SetDirty(target);
            UnityEditor.AssetDatabase.SaveAssets();

            // 렌더러를 저장된 객체로 교체(런타임 DontSave 메시·머티리얼은 씬에 저장되지 않음)
            bakeAsset = target;
            ApplyFromAsset();
            foreach (var mf in _appliedFilters)
            {
                UnityEditor.EditorUtility.SetDirty(mf);
                var rnd = mf.GetComponent<MeshRenderer>();
                if (rnd != null) UnityEditor.EditorUtility.SetDirty(rnd);
            }
            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"[AtlasApply] 베이크 결과 저장: {path} (엔트리 {target.entries.Length}, uv2 메시 {added.Count}, 페이지 {target.pageCount}{(exportExr ? " + EXR" : "")})", this);
        }
#endif

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
            CancelProgressive(finalize: false);
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
                OriginalOf(mf, out var mesh, out _);   // 원본 메시로 dedupe(적용된 uv2 메시는 인스턴스마다 달라 dedupe 가 깨짐)
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
                _burstScene = BurstScene.Create(_giBvh, _giMeshAlbedo, _alphaData, _bakeLights, Allocator.Persistent);   // 로컬 광원 포함(GpuScene 도 여기서 업로드)
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
                    _pathCS = PathTraceShader.Resolve(pathTraceShader);
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
                            if (!_gpuScene.TraversalFits)
                            {
                                // 순회 스택(BVH_STACK 32) 초과 트리는 GPU 결과가 틀린다 → Burst(스택 64) 폴백
                                Debug.LogWarning($"[AtlasApply] BVH 깊이(스택 {_gpuScene.RequiredStack}칸 필요, TLAS {_gpuScene.TlasDepth} · BLAS 최대 {_gpuScene.MaxBlasDepth}) " +
                                                 $"> GPU 순회 스택 {GpuBakeLimits.TraversalStack} — RadianceGI GPU 백엔드 → Burst 폴백.", this);
                                _gpuScene.Dispose();
                                _gpuScene = null;
                                _pathCS = null;
                            }
                            else
                            {
                                _gpuIo = new GpuIoBuffers();             // 재사용 I/O 버퍼 홀더(첫 Dispatch 에서 grow-on-demand 할당)
                                // 이어 굽기 커널(적응·진행형). 없으면(구 셰이더 지정) 누적은 Burst 로.
                                _kAccum = _pathCS.HasKernel("CSRadianceAccum") ? _pathCS.FindKernel("CSRadianceAccum") : -1;
                                _gpuReady = true;
                            }
                        }
                    }
                }
            }
        }

        // PathTrace.compute 로드(인스턴스 필드 없이 — 교차검증 메뉴용). 셰이더가 Resources 밖이라 기존 Resources.Load 만으론
        // 항상 null → GPU 백엔드가 Burst 로 폴백되던 문제를 PathTraceShader(에디터 경로 폴백)로 해소.
        internal static ComputeShader LoadPathCompute() => PathTraceShader.Resolve();

        /// <summary>
        /// α: 유니크 메시·인스턴스로부터 알파 컷아웃 씬 데이터를 만든다.
        /// 컷아웃 머티리얼이 없거나 토글이 꺼져 있으면 Disabled 를 돌려 세 백엔드가 기존 경로를 탄다.
        /// </summary>
        internal AlphaSceneData BuildAlphaData(System.Collections.Generic.List<Tri[]> uniqueLocal,
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
        internal Vector3 ReadAlbedo(MeshFilter mf)
        {
            OriginalOf(mf, out _, out var m);   // 라이트맵 머티리얼이 적용돼 있어도 원본 알베도
            Color col = defaultAlbedo;
            if (m != null && m.HasProperty("_BaseColor")) col = m.GetColor("_BaseColor");
            else if (m != null && m.HasProperty("_Color")) col = m.GetColor("_Color");
            Color lin = col.linear;
            return new Vector3(Mathf.Clamp01(lin.r), Mathf.Clamp01(lin.g), Mathf.Clamp01(lin.b));
        }

        // 변환 없는 로컬 Tri[] (BLAS 입력용).
        internal static Tri[] LocalTris(Mesh mesh)
        {
            var v = mesh.vertices;
            var t = mesh.triangles;
            var tris = new Tri[t.Length / 3];
            for (int i = 0; i < tris.Length; i++)
                tris[i] = new Tri { V0 = v[t[i * 3]], V1 = v[t[i * 3 + 1]], V2 = v[t[i * 3 + 2]] };
            return tris;
        }

        // 모든 타깃 메시의 삼각형을 월드 공간 Tri[] 로 평탄화(차폐자 입력).
        internal static Tri[] BuildWorldTris(MeshFilter[] filters)
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
        internal static Vector3 LinColor(Color c) { var l = c.linear; return new Vector3(l.r, l.g, l.b); }

        // 선형 RGB → RGBA32 아틀라스 저장값(hdrOutput 끔/미지원 폴백). Linear 텍스처라 인코딩 없이 클램프만(LDR).
        // 셰이더 출력 시 프레임버퍼가 linear→sRGB 인코딩을 정확히 1회 수행.
        static Color ToColor(Vector3 lin) => new Color(
            Mathf.Clamp01(lin.x), Mathf.Clamp01(lin.y), Mathf.Clamp01(lin.z), 1f);

        // 선형 RGB → RGBAHalf 아틀라스 저장값(최종 인코딩). 무클램프(음수만 0) — half 최대 65504 는 조도 범위에 충분.
        static Color ToHdrColor(Vector3 lin) => new Color(
            Mathf.Max(0f, lin.x), Mathf.Max(0f, lin.y), Mathf.Max(0f, lin.z), 1f);

        /// <summary>
        /// C0 씬 수집 결과 주입(Editor <c>BakeSceneCollectMenu</c>) — targets/occluders 를 교체할 뿐 베이크 경로는 그대로(C0 결정 ①).
        /// 아래 ResolveTargets 의 occluder 차집합 · ResolveOccluderUnion 이 기존 계약대로 소비한다.
        /// </summary>
        public void SetCollected(MeshFilter[] receivers, MeshFilter[] occluderOnly)
        {
            targets = receivers ?? System.Array.Empty<MeshFilter>();
            occluders = occluderOnly ?? System.Array.Empty<MeshFilter>();
        }

        internal MeshFilter[] ResolveTargets()
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
