using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// AtlasApplyDebug 백엔드 교차검증 3종(구 AtlasApplyDebug 내장 ContextMenu 이전 — 베이커/테스트 분리).
    /// 베이커 컴포넌트의 설정(타깃·광원·품질·시드)을 그대로 읽어, 실제 베이크와 같은 텍셀 점·시드 규약으로 두 경로를 비교한다.
    /// 인스펙터 우클릭 메뉴는 Editor/AtlasApplyDiffTestsMenu 가 "CONTEXT/AtlasApplyDebug/…" 로 붙인다(기존과 같은 메뉴명).
    /// 베이커의 공유 헬퍼(ResolveTargets·BuildWorldTris·LocalTris·ReadAlbedo·LinColor·LoadPathCompute·DispatchRadianceGpu)는
    /// internal + InternalsVisibleTo 로 접근 — 베이커 쪽은 이 클래스를 모른다(단방향).
    /// </summary>
    public static class AtlasApplyDiffTests
    {
        // 머티리얼 차등 테스트: 같은 텍셀 점에서 BruteForce vs BVH 로 구운 radiance 픽셀 차이 측정.
        // 같은 seed → AO 샘플 방향 동일 → BVH≡brute 면 차이=0 이어야 한다(베이크에 쓰이는 실제 레이로 검증).
        // 0 이 아니면 = 그 텍셀/AO 샘플에서 BVH 차폐가 brute 와 어긋남(랜덤 퍼즈가 놓친 엣지 포함).
        public static void RadianceDiff(AtlasApplyDebug t)
        {
            var filters = t.ResolveTargets();
            if (filters.Length == 0) { Debug.LogWarning("[AtlasApply] DiffTest: 대상 MeshFilter 없음.", t); return; }

            var tris = AtlasApplyDebug.BuildWorldTris(filters);
            var brute = new BruteForceOccluder(tris);
            using var bvh = new BVH(tris, Allocator.Persistent, t.bvhQuality);

            var sun = new DirectionalLight
            {
                Direction = t.lightDirection.sqrMagnitude > 1e-8f ? t.lightDirection.normalized : Vector3.down,
                Color = AtlasApplyDebug.LinColor(t.lightColor),
                Intensity = t.lightIntensity,
                AngularDiameterDeg = t.sunAngularDiameterDeg,
            };
            Vector3 amb = AtlasApplyDebug.LinColor(t.ambient);
            int res = Mathf.Clamp(t.atlasResolution, 4, 512); // 텍셀 해상도(인스턴스별). 비교는 점-단위라 작게 충분.

            const float thresh = 1f / 255f;                 // 8-bit 1 LSB
            double maxDiff = 0, sumDiff = 0; long nTexel = 0, over = 0;

            foreach (var mf in filters)
            {
                var srcMesh = mf.sharedMesh;
                if (srcMesh == null || !srcMesh.isReadable) continue;

                Mesh uv2mesh; LumelMap lumel;
                try
                {
                    var pr = ParameterizationPipeline.Run(srcMesh, t.segmentation, t.flatten);
                    if (pr.Charts == null || pr.Charts.Length == 0) continue;
                    DensityNormalizer.Normalize(pr.Charts);
                    ChartPacker.Pack(pr.Charts, t.chartGutter, t.chartPacking);
                    (uv2mesh, _) = UVAssembly.Assemble(pr.Charts, srcMesh);
                    lumel = TexelMapper.Map(uv2mesh, res, mf.transform.localToWorldMatrix);
                }
                catch (System.Exception e) { Debug.LogError($"[DiffTest] '{mf.name}' 실패: {e.Message}", mf); continue; }

                for (int li = 0; li < lumel.Valid.Length; li++)
                {
                    if (!lumel.Valid[li]) continue;
                    Vector3 wn = lumel.WorldNormal[li];
                    Vector3 o = lumel.WorldPos[li] + wn * t.surfaceBias;
                    uint s = t.seed + (uint)li * 2654435761u;  // BlitRegion 과 동일 시드 규약
                    Vector3 rb = RadianceCore.EvaluateRadiance(brute, o, wn, sun, amb, t.aoSamples, s);
                    Vector3 rv = RadianceCore.EvaluateRadiance(bvh, o, wn, sun, amb, t.aoSamples, s);
                    float d = Mathf.Max(Mathf.Abs(rb.x - rv.x), Mathf.Max(Mathf.Abs(rb.y - rv.y), Mathf.Abs(rb.z - rv.z)));
                    if (d > maxDiff) maxDiff = d;
                    sumDiff += d; nTexel++;
                    if (d > thresh) over++;
                }
            }

            if (nTexel == 0) { Debug.LogWarning("[AtlasApply] DiffTest: 유효 텍셀 0(R/W·차트 확인).", t); return; }
            bool ok = maxDiff <= thresh;
            string msg = $"[RadianceDiff] BruteForce vs BVH({t.bvhQuality}): texels={nTexel}, " +
                         $"maxDiff={maxDiff:F6}, meanDiff={sumDiff / nTexel:F7}, over(1/255)={over} ({100.0 * over / nTexel:F3}%) → {(ok ? "MATCH ✅" : "DIFF ❌")}";
            if (ok) Debug.Log(msg, t); else Debug.LogWarning(msg, t);
        }

        // RadianceGI 백엔드 교차검증: 같은 BVH·per-mesh 알베도·시드(seed+li*const)로 CPU(EvaluateRadiance) 와
        //   Burst(BurstRadianceBaker.Bake=Direct+Indirect) 를 텍셀별 비교. 동일 RNG/BVH 재사용이라 ε 내 일치해야 함
        //   (FMA 경계/히트 반전만 드물게 1/255 초과). 실제 베이크에 쓰이는 경로로 백엔드 동등성 검증.
        public static void RadianceGiBackendDiff(AtlasApplyDebug t)
        {
            var filters = t.ResolveTargets();
            if (filters.Length == 0) { Debug.LogWarning("[AtlasApply] GI DiffTest: 대상 MeshFilter 없음.", t); return; }

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
                    uniqueLocal.Add(AtlasApplyDebug.LocalTris(mesh)); uniqueMeshes.Add(mesh); meshAlbedo.Add(t.ReadAlbedo(mf));
                }
                giInsts.Add(new TwoLevelBVH.Instance { MeshIndex = mi, LocalToWorld = mf.transform.localToWorldMatrix });
                var rndA = mf.GetComponent<MeshRenderer>();
                instMats.Add(rndA != null ? rndA.sharedMaterials : null);
            }
            if (giInsts.Count == 0) { Debug.LogWarning("[AtlasApply] GI DiffTest: R/W 가능한 메시 없음.", t); return; }

            var albedoArr = meshAlbedo.ToArray();
            var alphaArr = t.BuildAlphaData(uniqueLocal, uniqueMeshes, giInsts, instMats);   // α 컷아웃(패키지) — CPU·Burst 같은 데이터
            using var bvh = new TwoLevelBVH(uniqueLocal.ToArray(), giInsts.ToArray());
            bvh.SetAlpha(alphaArr);
            using var cpuScene = new InstancedRadianceScene(uniqueLocal.ToArray(), albedoArr, giInsts.ToArray(), bvh); // 공유 BVH(모드 A)
            var burstScene = BurstScene.Create(bvh, albedoArr, alphaArr, Allocator.Persistent);

            var sun = new DirectionalLight
            {
                Direction = t.lightDirection.sqrMagnitude > 1e-8f ? t.lightDirection.normalized : Vector3.down,
                Color = AtlasApplyDebug.LinColor(t.lightColor),
                Intensity = t.lightIntensity,
                AngularDiameterDeg = t.sunAngularDiameterDeg,
            };
            ISky sky = new UniformSky(AtlasApplyDebug.LinColor(t.skyColor));
            var burstSky = BurstSky.FromSky(sky);
            var q = new BakeQualitySettings { AoSamples = t.aoSamples, IndirectSamples = t.indirectSamples, MaxBounces = t.maxBounces, RRStartDepth = 3, RayBias = Mathf.Max(1e-4f, t.surfaceBias), DirectSamples = t.directSamples };

            int res = Mathf.Clamp(t.atlasResolution, 4, 256); // 비교는 점-단위라 작게 충분(GI 비쌈)
            const float thresh = 1f / 255f;
            double maxDiff = 0, sumDiff = 0; long nTexel = 0, over = 0;

            foreach (var mf in filters)
            {
                var srcMesh = mf.sharedMesh;
                if (srcMesh == null || !srcMesh.isReadable) continue;

                Mesh uv2mesh; LumelMap lumel;
                try
                {
                    var pr = ParameterizationPipeline.Run(srcMesh, t.segmentation, t.flatten);
                    if (pr.Charts == null || pr.Charts.Length == 0) continue;
                    DensityNormalizer.Normalize(pr.Charts);
                    ChartPacker.Pack(pr.Charts, t.chartGutter, t.chartPacking);
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
                    Vector3 o = lumel.WorldPos[li] + wn * t.surfaceBias;
                    uint s = t.seed + (uint)li * 2654435761u;
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

            if (nTexel == 0) { Debug.LogWarning("[AtlasApply] GI DiffTest: 유효 텍셀 0(R/W·차트 확인).", t); return; }
            double overPct = 100.0 * over / nTexel;
            bool ok = overPct < 1.0; // FMA 경계 제외 → 1/255 초과 1% 미만이면 동등
            string msg = $"[RadianceGI Backend Diff] CPU vs Burst: texels={nTexel}, spp={t.indirectSamples}, bnc={t.maxBounces}, " +
                         $"maxDiff={maxDiff:F6}, meanDiff={sumDiff / nTexel:F7}, over(1/255)={over} ({overPct:F3}%) → {(ok ? "MATCH ✅" : "DIFF ❌")}";
            if (ok) Debug.Log(msg, t); else Debug.LogWarning(msg, t);
        }

        // RadianceGI 백엔드 교차검증(Burst vs GPU): 같은 씬·같은 시드(seed+li*const)·같은 pts 로
        //   Burst BurstRadianceBaker.Bake vs GPU CSRadiance 텍셀 대조 → mean/max/over 로그(mean≈1e-9 기대, G5 수준).
        //   + Stopwatch 로 Burst vs GPU 베이크 시간 로그(대형 씬 이득 확인). GetData/.Complete 동기라 벽시계 비교 공정.
        public static void RadianceGiBackendDiffGpu(AtlasApplyDebug t)
        {
            if (!SystemInfo.supportsComputeShaders)
            { Debug.LogWarning("[AtlasApply] Burst vs GPU Diff: compute shader 미지원 플랫폼 — SKIP.", t); return; }

            var filters = t.ResolveTargets();
            if (filters.Length == 0) { Debug.LogWarning("[AtlasApply] Burst vs GPU Diff: 대상 MeshFilter 없음.", t); return; }

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
                    uniqueLocal.Add(AtlasApplyDebug.LocalTris(mesh)); uniqueMeshes.Add(mesh); meshAlbedo.Add(t.ReadAlbedo(mf));
                }
                giInsts.Add(new TwoLevelBVH.Instance { MeshIndex = mi, LocalToWorld = mf.transform.localToWorldMatrix });
                var rndG = mf.GetComponent<MeshRenderer>();
                instMats.Add(rndG != null ? rndG.sharedMaterials : null);
            }
            if (giInsts.Count == 0) { Debug.LogWarning("[AtlasApply] Burst vs GPU Diff: R/W 가능한 메시 없음.", t); return; }

            var albedoArr = meshAlbedo.ToArray();
            var alphaArr = t.BuildAlphaData(uniqueLocal, uniqueMeshes, giInsts, instMats);   // α 컷아웃(패키지) — Burst·GPU 같은 데이터
            using var bvh = new TwoLevelBVH(uniqueLocal.ToArray(), giInsts.ToArray());
            bvh.SetAlpha(alphaArr);
            var burstScene = BurstScene.Create(bvh, albedoArr, alphaArr, Allocator.Persistent);

            var pathCS = AtlasApplyDebug.LoadPathCompute();
            if (pathCS == null) { Debug.LogWarning("[AtlasApply] Burst vs GPU Diff: PathTrace.compute 로드 실패.", t); burstScene.Dispose(); return; }
            int kRad = pathCS.FindKernel("CSRadiance");
            if (kRad < 0) { Debug.LogWarning("[AtlasApply] Burst vs GPU Diff: CSRadiance 커널 미발견.", t); burstScene.Dispose(); return; }
            var gpuScene = new GpuScene(burstScene);
            var gpuIo = new AtlasApplyDebug.GpuIoBuffers();   // 메뉴 로컬 재사용 홀더(루프 내내 재할당 0회, 아래서 해제)

            var sun = new DirectionalLight
            {
                Direction = t.lightDirection.sqrMagnitude > 1e-8f ? t.lightDirection.normalized : Vector3.down,
                Color = AtlasApplyDebug.LinColor(t.lightColor),
                Intensity = t.lightIntensity,
                AngularDiameterDeg = t.sunAngularDiameterDeg,
            };
            ISky sky = new UniformSky(AtlasApplyDebug.LinColor(t.skyColor));
            var burstSky = BurstSky.FromSky(sky);
            var q = new BakeQualitySettings { AoSamples = t.aoSamples, IndirectSamples = t.indirectSamples, MaxBounces = t.maxBounces, RRStartDepth = 3, RayBias = Mathf.Max(1e-4f, t.surfaceBias), DirectSamples = t.directSamples };

            int res = Mathf.Clamp(t.atlasResolution, 4, 256);
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
                    var pr = ParameterizationPipeline.Run(srcMesh, t.segmentation, t.flatten);
                    if (pr.Charts == null || pr.Charts.Length == 0) continue;
                    DensityNormalizer.Normalize(pr.Charts);
                    ChartPacker.Pack(pr.Charts, t.chartGutter, t.chartPacking);
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
                    Vector3 o = lumel.WorldPos[li] + wn * t.surfaceBias;
                    uint s = t.seed + (uint)li * 2654435761u;
                    pts[k] = o; nrm[k] = wn; val[k] = true; sds[k] = s;
                    mPts[k] = o; mNrm[k] = wn; mSds[k] = s;
                }

                swBurst.Start();
                var burst = BurstRadianceBaker.Bake(burstScene, burstSky, sun, q, pts, nrm, val, sds, Allocator.TempJob);
                swBurst.Stop();

                swGpu.Start();
                var gpu = AtlasApplyDebug.DispatchRadianceGpu(gpuScene, pathCS, kRad, sun, burstSky, q, mPts, mNrm, mSds, n, gpuIo);
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
            gpuIo.Dispose();       // 메뉴 로컬 재사용 홀더 해제(ComputeBuffer 5개) — 베이커 영구 필드 _gpuIo 와 분리, 일회성.
            burstScene.Dispose();

            if (nTexel == 0) { Debug.LogWarning("[AtlasApply] Burst vs GPU Diff: 유효 텍셀 0(R/W·차트 확인).", t); return; }
            double overPct = 100.0 * over / nTexel;
            bool ok = overPct < 1.0; // 초월함수(sqrt/sin/cos) 발산 → MC 잡음. 1/255 초과 1% 미만이면 동등(G5 수준)
            string msg = $"[RadianceGI Backend Diff] Burst vs GPU: texels={nTexel}, spp={t.indirectSamples}, bnc={t.maxBounces}, " +
                         $"maxDiff={maxDiff:F6}, meanDiff={sumDiff / nTexel:F7}, over(1/255)={over} ({overPct:F3}%) → {(ok ? "MATCH ✅" : "DIFF ❌")}";
            if (ok) Debug.Log(msg, t); else Debug.LogWarning(msg, t);
            Debug.Log($"[RadianceGI Backend Diff] 시간: Burst={swBurst.Elapsed.TotalMilliseconds:F1}ms, GPU={swGpu.Elapsed.TotalMilliseconds:F1}ms " +
                      $"(GPU/Burst={(swBurst.Elapsed.TotalMilliseconds > 0 ? swGpu.Elapsed.TotalMilliseconds / swBurst.Elapsed.TotalMilliseconds : 0):F2}×) " +
                      $"— GPU 는 버퍼 생성/GetData 동기 포함. 대형 씬·고spp 에서 이득.", t);
        }
    }
}
