using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 로컬 광원 — Unity Light 변환 · Burst/GPU 미러 · AtlasApplyDebug 실베이크 검증(에디터). 해석해·CPU 식은 헤드리스 <see cref="BakeLightTests"/>.
    ///  A) BakeLightUtil.TryConvert: Point/Spot/Rect/Disc 필드(위치·축·범위·반각 cos·면 크기·그림자 반경) · Shadows None → 그림자 플래그 없음 ·
    ///     FollowBakeType 에서 Mixed → 간접만 · Realtime 제외 · Directional·꺼진 라이트·강도 0 제외.
    ///  B) Burst ≈ CPU(4종 광원 + 태양): 1차 직사(BurstDirect ↔ EvaluateDirectPrimary) · 간접(BurstIndirect ↔ EvaluateIndirect) —
    ///     BurstRadianceCompareTests 와 같은 허용(초월함수 ULP 로 그림자 경계 반전 ≤ 1~3%, 평균 오차 작음). 광원 0개 BurstScene ≡ 구 Create(비트 동일).
    ///  C) GPU(compute 필요): CSRadiance(DirectAll) ≈ Burst · CSRadianceAccum 누적 ≈ Burst 누적 — 평균 상대차.
    ///  D) 실베이크(Burst): None &lt; Listed(Mixed = 간접만) &lt; Listed(Baked) 평균 휘도 · Listed 로그 광원 수 · GPU ≈ Burst(평균 상대차, compute 필요).
    /// </summary>
    public static class LocalLightBakeTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 로컬 광원 (Unity 변환 · Burst/GPU 미러 · 실베이크) ===");
            int pass = 0, total = 0;
            TestConvert(sb, ref pass, ref total);
            TestBackends(sb, ref pass, ref total);
            TestAtlas(sb, ref pass, ref total);
            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        static Light MakeLight(GameObject root, LightType type, Vector3 pos, Quaternion rot, float intensity = 2f)
        {
            var go = new GameObject("TestLight") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            var l = go.AddComponent<Light>();
            l.type = type;
            l.intensity = intensity;
            l.color = Color.white;
            l.shadows = LightShadows.Hard;
            return l;
        }

        // ── A) 변환 ──
        static void TestConvert(StringBuilder sb, ref int pass, ref int total)
        {
            var root = new GameObject("LocalLightConvert") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var opt = new BakeLightUtil.Options { AreaSamples = 12, SoftSamples = 6, FollowBakeType = false };
                var pl = MakeLight(root, LightType.Point, new Vector3(1, 2, 3), Quaternion.identity);
                pl.range = 7f;
                var sp = MakeLight(root, LightType.Spot, new Vector3(0, 3, 0), Quaternion.Euler(90, 0, 0));
                sp.range = 9f; sp.spotAngle = 50f; sp.innerSpotAngle = 20f;
                var rc = MakeLight(root, (LightType)3, new Vector3(0, 2, 0), Quaternion.Euler(90, 0, 0));
                var dc = MakeLight(root, (LightType)4, new Vector3(0, 2, 1), Quaternion.Euler(90, 0, 0));
                var dir = MakeLight(root, LightType.Directional, Vector3.zero, Quaternion.identity);
                var off = MakeLight(root, LightType.Point, Vector3.zero, Quaternion.identity); off.enabled = false;
                var zero = MakeLight(root, LightType.Point, Vector3.zero, Quaternion.identity, 0f);
#if UNITY_EDITOR
                rc.areaSize = new Vector2(1.5f, 0.5f);
                dc.areaSize = new Vector2(0.4f, 0.4f);
                pl.shadowRadius = 0.25f;
#endif
                bool c1 = BakeLightUtil.TryConvert(pl, opt, out var P);
                bool c2 = BakeLightUtil.TryConvert(sp, opt, out var S);
                bool c3 = BakeLightUtil.TryConvert(rc, opt, out var R);
                bool c4 = BakeLightUtil.TryConvert(dc, opt, out var D);
                bool ok = c1 && c2 && c3 && c4;
                ok &= !BakeLightUtil.TryConvert(dir, opt, out _) && !BakeLightUtil.TryConvert(off, opt, out _) && !BakeLightUtil.TryConvert(zero, opt, out _);
                if (ok)
                {
                    ok &= P.Kind == BakeLightType.Point && P.Position == new Vector3(1, 2, 3) && P.Range == 7f && P.Intensity == 2f
                       && (P.Flags & BakeLight.FlagDirect) != 0 && (P.Flags & BakeLight.FlagShadows) != 0;
                    ok &= S.Kind == BakeLightType.Spot && Vector3.Dot(S.Direction, Vector3.down) > 0.999f
                       && Mathf.Abs(S.CosOuter - Mathf.Cos(25f * Mathf.Deg2Rad)) < 1e-5f && Mathf.Abs(S.CosInner - Mathf.Cos(10f * Mathf.Deg2Rad)) < 1e-5f;
                    ok &= R.Kind == BakeLightType.Rect && R.Samples == 12 && Vector3.Dot(R.Direction, Vector3.down) > 0.999f;
                    ok &= D.Kind == BakeLightType.Disc && D.Samples == 12;
#if UNITY_EDITOR
                    ok &= Mathf.Abs(R.Width - 1.5f) < 1e-6f && Mathf.Abs(R.Height - 0.5f) < 1e-6f && Mathf.Abs(D.Radius - 0.4f) < 1e-6f
                       && Mathf.Abs(P.Radius - 0.25f) < 1e-6f && P.Samples == 6;
#endif
                }
                pl.shadows = LightShadows.None;
                ok &= BakeLightUtil.TryConvert(pl, opt, out var P2) && (P2.Flags & BakeLight.FlagShadows) == 0;
#if UNITY_EDITOR
                var follow = opt; follow.FollowBakeType = true;
                sp.lightmapBakeType = LightmapBakeType.Mixed;
                rc.lightmapBakeType = LightmapBakeType.Realtime;
                dc.lightmapBakeType = LightmapBakeType.Baked;
                ok &= BakeLightUtil.TryConvert(sp, follow, out var Sm) && (Sm.Flags & BakeLight.FlagDirect) == 0
                   && !BakeLightUtil.TryConvert(rc, follow, out _)
                   && BakeLightUtil.TryConvert(dc, follow, out var Db) && (Db.Flags & BakeLight.FlagDirect) != 0
                   && BakeLightUtil.TryConvert(rc, opt, out _);   // FollowBakeType 끄면 Realtime 도 포함
#endif
                var conv = BakeLightUtil.Convert(new[] { pl, pl, sp, dir, null }, opt);
                ok &= conv.Length == 2;   // 중복·Directional·null 제외
                Check(sb, ref pass, ref total, "A: Unity Light 변환 — 4종 필드 · 그림자 None · Mixed → 간접만 · Realtime 제외(Follow) · Directional/꺼짐/강도 0/중복 제외", ok);
            }
            finally { Object.DestroyImmediate(root); }
        }

        internal static BakeLight[] SceneLights() => new[]
        {
            BakeLight.Point(new Vector3(-1.5f, 2.5f, 0.5f), new Vector3(1f, 0.9f, 0.7f), 6f, range: 10f, radius: 0.3f, samples: 4),
            BakeLight.Spot(new Vector3(2f, 3f, -1f), new Vector3(-0.3f, -1f, 0.2f), Vector3.one, 10f, 12f, 70f, 35f),
            BakeLight.Rect(new Vector3(0f, 4f, 2f), Vector3.down, Vector3.right, Vector3.forward, 2f, 1f, new Vector3(0.8f, 0.9f, 1f), 3f, 8),
            BakeLight.Disc(new Vector3(-2f, 1.5f, -2f), new Vector3(1f, -0.5f, 1f).normalized, new Vector3(1f, 0f, -1f).normalized,
                           Vector3.Cross(new Vector3(1f, -0.5f, 1f).normalized, new Vector3(1f, 0f, -1f).normalized), 0.5f, Vector3.one, 4f, 8),
        };

        static void Stats(Vector3[] refR, Vector3[] got, float tol, out float meanE, out int mism)
        {
            double sum = 0; mism = 0;
            for (int i = 0; i < refR.Length; i++)
            {
                float d = (refR[i] - got[i]).magnitude, m = Mathf.Max(1e-3f, refR[i].magnitude);
                sum += d / m;
                if (d / m > tol) mism++;
            }
            meanE = refR.Length > 0 ? (float)(sum / refR.Length) : 0f;
        }

        // ── B·C) 백엔드 ──
        static void TestBackends(StringBuilder sb, ref int pass, ref int total)
        {
            GpuRadianceCompareTests.BuildScene(out var meshes, out var albedo, out var insts);
            using var bvh = new TwoLevelBVH(meshes, insts);
            using var managed = new InstancedRadianceScene(meshes, albedo, insts, bvh);
            var lights = SceneLights();
            var bsNone = BurstScene.Create(bvh, albedo, null, Allocator.Persistent);
            var bsEmpty = BurstScene.Create(bvh, albedo, null, new BakeLight[0], Allocator.Persistent);
            var bs = BurstScene.Create(bvh, albedo, null, lights, Allocator.Persistent);
            try
            {
                GpuRadianceCompareTests.CollectSurfaceSamples(meshes, insts, 300, out var pts, out var nrm);
                int n = pts.Length;
                var seeds = new uint[n];
                for (int i = 0; i < n; i++) seeds[i] = 4242u + (uint)i * 2654435761u;
                var sun = GpuRadianceCompareTests.Sun();
                var skyM = new GradientSky(new Vector3(0.5f, 0.6f, 0.8f), new Vector3(0.15f, 0.12f, 0.10f));
                var skyB = BurstSky.FromSky(skyM);
                var q = new BakeQualitySettings { AoSamples = 1, IndirectSamples = 24, MaxBounces = 3, RRStartDepth = 2, RayBias = 1e-4f, DirectSamples = 1 };

                var naP = new NativeArray<Vector3>(pts, Allocator.TempJob);
                var naN = new NativeArray<Vector3>(nrm, Allocator.TempJob);
                var naV = new NativeArray<bool>(n, Allocator.TempJob);
                var naS = new NativeArray<uint>(seeds, Allocator.TempJob);
                for (int i = 0; i < n; i++) naV[i] = true;

                // 광원 0개(빈 배열) ≡ 구 Create — 비트 동일
                var r0 = BurstRadianceBaker.Bake(bsNone, skyB, sun, q, naP, naN, naV, naS, Allocator.TempJob);
                var r1 = BurstRadianceBaker.Bake(bsEmpty, skyB, sun, q, naP, naN, naV, naS, Allocator.TempJob);
                bool same0 = true;
                for (int i = 0; i < n; i++) { Vector3 a = r0[i], b = r1[i]; same0 &= a.x == b.x && a.y == b.y && a.z == b.z; }
                r0.Dispose(); r1.Dispose();

                // 1차 직사: Burst ↔ CPU
                var bDir = BurstDirect.Compute(bs, naP, naN, naV, sun, q.DirectSamples, naS, Allocator.TempJob);
                var cDir = new Vector3[n];
                for (int i = 0; i < n; i++) cDir[i] = RadianceCore.EvaluateDirectPrimary(managed.Occluder, pts[i], nrm[i], sun, q.DirectSamples, seeds[i], lights);
                Stats(cDir, bDir.ToArray(), 1e-3f, out float meanD, out int misD);
                // 간접: Burst ↔ CPU
                var bInd = BurstIndirect.Compute(bs, skyB, sun, q, naP, naN, naV, naS, Allocator.TempJob);
                var cInd = new Vector3[n];
                for (int i = 0; i < n; i++) cInd[i] = RadianceCore.EvaluateIndirect(managed, pts[i], nrm[i], sun, skyM, q, seeds[i], lights);
                Stats(cInd, bInd.ToArray(), 5e-2f, out float meanI, out int misI);
                // 광원이 실제로 기여하는지(빈 씬 대비 밝아진 점)
                int lit = 0;
                var bNo = BurstDirect.Compute(bsNone, naP, naN, naV, sun, q.DirectSamples, naS, Allocator.TempJob);
                for (int i = 0; i < n; i++) if (bDir[i].magnitude > bNo[i].magnitude + 1e-4f) lit++;
                bNo.Dispose();
                int budD = Mathf.Max(1, Mathf.RoundToInt(n * 0.01f)), budI = Mathf.Max(2, Mathf.RoundToInt(n * 0.03f));
                Check(sb, ref pass, ref total, $"B: Burst ≈ CPU — 직사 mean {meanD:E2} mism {misD}/≤{budD} · 간접 mean {meanI:E2} mism {misI}/≤{budI} · 광원 기여 {lit}/{n} · 광원 0개 ≡ 구 Create",
                      same0 && misD <= budD && meanD < 1e-2f && misI <= budI && meanI < 5e-3f && lit > n / 10);

                // C) GPU
                if (!SystemInfo.supportsComputeShaders || AtlasApplyDebug.LoadPathCompute() == null)
                {
                    sb.AppendLine("  [SKIP] C: compute 미지원 또는 PathTrace.compute 로드 실패");
                }
                else
                {
                    var cs = AtlasApplyDebug.LoadPathCompute();
                    using var gs = new GpuScene(bs);
                    var bRad = BurstRadianceBaker.Bake(bs, skyB, sun, q, naP, naN, naV, naS, Allocator.TempJob);
                    var bRadArr = bRad.ToArray(); bRad.Dispose();
                    Vector3[] gRad;
                    using (var io = new AtlasApplyDebug.GpuIoBuffers())
                        gRad = AtlasApplyDebug.DispatchRadianceGpu(gs, cs, cs.FindKernel("CSRadiance"), sun, skyB, q, pts, nrm, seeds, n, io);
                    Stats(bRadArr, gRad, 5e-2f, out float meanG, out int misG);
                    Vector3[] gAcc, bAcc;
                    var st = new AdaptiveSampling { MaxSamples = q.IndirectSamples, SamplesPerPass = 6 };
                    using (var ge = new GpuPathAccum(gs, cs, cs.FindKernel("CSRadianceAccum"), sun, skyB, q))
                    { var a = new ProgressiveAccumulator(ge, st, pts, nrm, seeds, n); a.Run(); gAcc = a.Result(); }
                    using (var be = new BurstPathAccum(bs, skyB, sun, q))
                    { var a = new ProgressiveAccumulator(be, st, pts, nrm, seeds, n); a.Run(); bAcc = a.Result(); }
                    Stats(bAcc, gAcc, 5e-2f, out float meanA, out int misA);
                    int bud = Mathf.Max(2, Mathf.RoundToInt(n * 0.03f));
                    Check(sb, ref pass, ref total, $"C: GPU ≈ Burst(광원 {gs.LightCount}개) — CSRadiance mean {meanG:E2} mism {misG}/≤{bud} · CSRadianceAccum mean {meanA:E2} mism {misA}/≤{bud}",
                          gs.LightCount == lights.Length && meanG < 5e-3f && misG <= bud && meanA < 5e-3f && misA <= bud);
                }
                bDir.Dispose(); bInd.Dispose();
                naP.Dispose(); naN.Dispose(); naV.Dispose(); naS.Dispose();
            }
            finally { bsNone.Dispose(); bsEmpty.Dispose(); bs.Dispose(); }
        }

        // ── D) 실베이크 ──
        static void TestAtlas(StringBuilder sb, ref int pass, ref int total)
        {
            if (Shader.Find("CustomLightmapper/LightmapDebug") == null)
            { sb.AppendLine("  [SKIP] D: 셰이더 'CustomLightmapper/LightmapDebug' 없음"); return; }

            var root = new GameObject("LocalLightBakeTests") { hideFlags = HideFlags.HideAndDontSave };
            var filters = new List<MeshFilter>
            {
                AtlasBatchBakeTests.Prim(root, PrimitiveType.Cube, new Vector3(0f, -0.05f, 0f), new Vector3(6f, 0.1f, 6f)),
                AtlasBatchBakeTests.Prim(root, PrimitiveType.Cube, new Vector3(0f, 0.5f, 0f), Vector3.one),
                AtlasBatchBakeTests.Prim(root, PrimitiveType.Cube, new Vector3(0f, 3f, 0f), new Vector3(6f, 0.1f, 6f)),   // 천장(태양 차단)
            };
            var lamp = MakeLight(root, LightType.Point, new Vector3(1.5f, 1.5f, 1f), Quaternion.identity, 4f);
            lamp.range = 8f;
            var baker = root.AddComponent<AtlasApplyDebug>();
            var made = new List<Object>();
            try
            {
                baker.saveBakeResult = false;
                baker.applyOnEnable = false;
                baker.denoise = false;
                baker.atlasResolution = 128;
                baker.texelsPerWorldUnit = 6f;
                baker.mode = AtlasApplyDebug.BakeMode.RadianceGI;
                baker.radianceBackend = AtlasApplyDebug.RadianceBackend.Burst;
                baker.indirectSamples = 16;
                baker.maxBounces = 2;
                baker.localLights = new[] { lamp };

                baker.localLightSource = LocalLightSource.None;
                double eNone = MeanLum(Bake(baker, filters, made, out int n0));
                baker.localLightSource = LocalLightSource.Listed;
                baker.localLightsFollowBakeType = true;
#if UNITY_EDITOR
                lamp.lightmapBakeType = LightmapBakeType.Mixed;
#endif
                double eMixed = MeanLum(Bake(baker, filters, made, out int nMixed));
#if UNITY_EDITOR
                lamp.lightmapBakeType = LightmapBakeType.Baked;
#endif
                var baked = Bake(baker, filters, made, out int nBaked);
                double eBaked = MeanLum(baked);
                bool ok = n0 == 0 && nMixed == 1 && nBaked == 1 && eNone < eMixed && eMixed < eBaked;
                string gpuInfo = "";
                if (SystemInfo.supportsComputeShaders && AtlasApplyDebug.LoadPathCompute() != null)
                {
                    baker.radianceBackend = AtlasApplyDebug.RadianceBackend.Gpu;
                    var g = Bake(baker, filters, made, out _);
                    double rel = MeanRelDiff(baked, g);
                    gpuInfo = $" · GPU ≈ Burst(평균 상대차 {rel:E2}, {baker.LastBakeBackend})";
                    ok &= baker.LastBakeBackend == "Gpu" && rel < 2e-2;
                }
                Check(sb, ref pass, ref total, $"D: 실베이크 평균 휘도 None {eNone:F4} < Mixed(간접만) {eMixed:F4} < Baked {eBaked:F4}{gpuInfo}", ok);
            }
            finally
            {
                Object.DestroyImmediate(root);
                foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
            }
        }

        static Color[][] Bake(AtlasApplyDebug baker, List<MeshFilter> filters, List<Object> made, out int lightCount)
        {
            var originals = new Mesh[filters.Count];
            for (int i = 0; i < filters.Count; i++) originals[i] = filters[i].sharedMesh;
            baker.BakeAndApply();
            lightCount = baker.LastBakeLights != null ? baker.LastBakeLights.Length : -1;
            var atlas = baker.Atlas;
            Color[][] px = null;
            if (atlas != null)
            {
                px = new Color[atlas.depth][];
                for (int p = 0; p < atlas.depth; p++) px[p] = atlas.GetPixels(p);
            }
            made.Add(atlas); made.Add(baker.SharedMaterial);
            for (int i = 0; i < filters.Count; i++)
                if (filters[i].sharedMesh != originals[i]) made.Add(filters[i].sharedMesh);
            baker.RestoreOriginals();
            return px;
        }

        static double MeanLum(Color[][] a)
        {
            if (a == null) return 0;
            double s = 0; long c = 0;
            foreach (var p in a) foreach (var x in p) { s += 0.2126 * x.r + 0.7152 * x.g + 0.0722 * x.b; c++; }
            return c > 0 ? s / c : 0;
        }

        static double MeanRelDiff(Color[][] a, Color[][] b)
        {
            if (a == null || b == null || a.Length != b.Length) return double.MaxValue;
            double s = 0; long c = 0;
            for (int p = 0; p < a.Length; p++)
                for (int i = 0; i < a[p].Length; i++)
                {
                    Color x = a[p][i], y = b[p][i];
                    double d = System.Math.Abs(x.r - y.r) + System.Math.Abs(x.g - y.g) + System.Math.Abs(x.b - y.b);
                    s += d / System.Math.Max(1e-2, x.r + x.g + x.b);
                    c++;
                }
            return c > 0 ? s / c : 0;
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
