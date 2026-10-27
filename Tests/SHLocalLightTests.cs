using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// L2 — SH·프로브 로컬 광원(에디터: Burst/GPU/베이커). 투영 식 자체는 헤드리스 <see cref="SHLightProjectionTests"/>.
    ///  A) 광원 0개 BurstScene(빈 배열) ≡ 구 Create — SHJob·SHPathJob 비트 동일.
    ///  B) Burst SHJob(광원 4종) ≈ 관리형 레퍼런스(같은 규약: 히트 = ρ⊙((태양 + 로컬 바운스)/π + 하늘(n)), 미스 = 하늘, + AddDirectSH) —
    ///     프로브 6축 평가 조도 비교(초월함수 ULP 로 그림자 경계 반전 ≤ 3%) · 광원 쪽 조도 증가 프로브 수.
    ///  C) GPU(compute 필요): CSSHBake(+CSSHDirect) ≈ Burst · CSSHBakeMulti(+CSSHDirect) ≈ Burst SHPathJob — 계수 평균 상대차.
    ///  D) ProbeVolumeBaker: Listed 점광원 → 광원 근처 프로브 조도 증가 · lightLog · None ≡ Listed(빈 목록)(비트 동일).
    ///  E) InstanceSceneBaker: Listed 점광원 → 인스턴스 SH 변화 · lightLog.
    /// </summary>
    public static class SHLocalLightTests
    {
        const int DirCount = 128;

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== L2 SH·프로브 로컬 광원 (Burst · GPU · 베이커) ===");
            int pass = 0, total = 0;
            BackendTests(sb, ref pass, ref total);
            ProbeVolumeTest(sb, ref pass, ref total);
            InstanceTest(sb, ref pass, ref total);
            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        static readonly Vector3[] Axes = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };

        static bool SameSH(SH9 a, SH9 b)
        {
            Vector3[] x = { a.c0, a.c1, a.c2, a.c3, a.c4, a.c5, a.c6, a.c7, a.c8 }, y = { b.c0, b.c1, b.c2, b.c3, b.c4, b.c5, b.c6, b.c7, b.c8 };
            for (int i = 0; i < 9; i++) if (x[i].x != y[i].x || x[i].y != y[i].y || x[i].z != y[i].z) return false;
            return true;
        }

        static double CoeffRel(SH9[] a, SH9[] b)
        {
            double d = 0, m = 0;
            for (int i = 0; i < a.Length; i++)
            {
                Vector3[] x = { a[i].c0, a[i].c1, a[i].c2, a[i].c3, a[i].c4, a[i].c5, a[i].c6, a[i].c7, a[i].c8 };
                Vector3[] y = { b[i].c0, b[i].c1, b[i].c2, b[i].c3, b[i].c4, b[i].c5, b[i].c6, b[i].c7, b[i].c8 };
                for (int k = 0; k < 9; k++) { d += (x[k] - y[k]).magnitude; m += y[k].magnitude; }
            }
            return d / System.Math.Max(1e-12, m);
        }

        // BurstSHBaker.SHJob 의 관리형 미러(레퍼런스)
        static SH9 RefSH(InstancedRadianceScene sc, BakeLight[] lights, BurstSky sky, DirectionalLight sun, Vector3 p, Vector3[] dirs, float w, uint index)
        {
            var sh = new SH9();
            var q = new OccluderQuery(sc.Occluder);
            foreach (var d in dirs)
            {
                Vector3 L;
                if (sc.ClosestHit(p, d, 1e-4f, float.MaxValue, out var hp, out var hn, out var alb))
                {
                    Vector3 eD = RadianceCore.EvaluateDirect(sc.Occluder, hp, hn, sun);
                    eD = LightSampling.AddBounce(eD, lights, ref q, hp, hn);
                    Vector3 irr = eD / Mathf.PI + sky.Radiance(hn);
                    L = new Vector3(alb.x * irr.x, alb.y * irr.y, alb.z * irr.z);
                }
                else L = sky.Radiance(d);
                sh.Accumulate(d, L, w);
            }
            LightSampling.AddDirectSH(ref sh, lights, ref q, p, index);
            return sh;
        }

        static SH9[] ToArr(NativeArray<SH9> a) { var r = a.ToArray(); a.Dispose(); return r; }

        // ── A·B·C ──
        static void BackendTests(StringBuilder sb, ref int pass, ref int total)
        {
            GpuRadianceCompareTests.BuildScene(out var meshes, out var albedo, out var insts);
            using var bvh = new TwoLevelBVH(meshes, insts);
            using var managed = new InstancedRadianceScene(meshes, albedo, insts, bvh);
            var lights = LocalLightBakeTests.SceneLights();
            var bsOld = BurstScene.Create(bvh, albedo, null, Allocator.Persistent);
            var bsEmpty = BurstScene.Create(bvh, albedo, null, new BakeLight[0], Allocator.Persistent);
            var bs = BurstScene.Create(bvh, albedo, null, lights, Allocator.Persistent);
            try
            {
                GpuRadianceCompareTests.CollectSurfaceSamples(meshes, insts, 120, out var sp, out var sn);
                int n = sp.Length;
                var pts = new Vector3[n];
                for (int i = 0; i < n; i++) pts[i] = sp[i] + sn[i] * 0.25f;   // 표면에서 띄운 프로브
                var naPts = new NativeArray<Vector3>(pts, Allocator.TempJob);
                var sun = GpuRadianceCompareTests.Sun();
                var sky = BurstSky.Gradient(new Vector3(0.5f, 0.6f, 0.8f), new Vector3(0.15f, 0.12f, 0.1f));
                var q = new BakeQualitySettings { MaxBounces = 3, RRStartDepth = 2, RayBias = 1e-4f };

                // A) 광원 0개 ≡ 구 Create
                var o1 = ToArr(BurstSHBaker.Bake(bsOld, sky, sun, naPts, DirCount, Allocator.TempJob));
                var e1 = ToArr(BurstSHBaker.Bake(bsEmpty, sky, sun, naPts, DirCount, Allocator.TempJob));
                var o2 = ToArr(BurstSHBaker.BakeMultiBounce(bsOld, sky, sun, q, naPts, DirCount, 77u, Allocator.TempJob));
                var e2 = ToArr(BurstSHBaker.BakeMultiBounce(bsEmpty, sky, sun, q, naPts, DirCount, 77u, Allocator.TempJob));
                bool same = true;
                for (int i = 0; i < n; i++) same &= SameSH(o1[i], e1[i]) && SameSH(o2[i], e2[i]);
                Check(sb, ref pass, ref total, $"A: 광원 0개 ≡ 구 Create — SHJob·SHPathJob 비트 동일 (프로브 {n})", same);

                // B) Burst SHJob ≈ 관리형 레퍼런스
                var bL = ToArr(BurstSHBaker.Bake(bs, sky, sun, naPts, DirCount, Allocator.TempJob));
                var dirsNa = BurstSHBaker.FibonacchiSphere(DirCount, Allocator.Temp);
                var dirs = dirsNa.ToArray(); dirsNa.Dispose();
                float w = 4f * Mathf.PI / DirCount;
                int mism = 0, brighter = 0; double meanE = 0;
                for (int i = 0; i < n; i++)
                {
                    var r = RefSH(managed, lights, sky, sun, pts[i], dirs, w, (uint)i);
                    double rel = 0; bool up = false;
                    foreach (var ax in Axes)
                    {
                        Vector3 eb = bL[i].Evaluate(ax), er = r.Evaluate(ax), e0 = o1[i].Evaluate(ax);
                        rel = System.Math.Max(rel, (eb - er).magnitude / Mathf.Max(1e-3f, er.magnitude));
                        if (eb.magnitude > e0.magnitude * 1.01f + 1e-4f) up = true;
                    }
                    meanE += rel;
                    if (rel > 1e-3) mism++;
                    if (up) brighter++;
                }
                meanE /= n;
                int bud = Mathf.Max(2, Mathf.RoundToInt(n * 0.03f));
                Check(sb, ref pass, ref total, $"B: Burst SHJob(광원 4종) ≈ 관리형 레퍼런스 — mean {meanE:E2} mism {mism}/≤{bud} · 광원으로 밝아진 프로브 {brighter}/{n}",
                      meanE < 5e-3 && mism <= bud && brighter > n / 4);

                // C) GPU
                var cs = SystemInfo.supportsComputeShaders ? AtlasApplyDebug.LoadPathCompute() : null;
                if (cs == null || !cs.HasKernel("CSSHDirect"))
                {
                    sb.AppendLine("  [SKIP] C: compute 미지원 또는 PathTrace.compute(CSSHDirect) 로드 실패");
                }
                else
                {
                    using var gs = new GpuScene(bs);
                    var gL = GpuSHBaker.Bake(gs, cs, cs.FindKernel("CSSHBake"), sun, sky, pts, dirs, w);
                    var bM = ToArr(BurstSHBaker.BakeMultiBounce(bs, sky, sun, q, naPts, DirCount, 77u, Allocator.TempJob));
                    var gM = GpuSHBaker.BakeMultiBounce(gs, cs, cs.FindKernel("CSSHBakeMulti"), sun, sky, q, 77u, pts, dirs, w);
                    double r1 = CoeffRel(gL, bL), r2 = CoeffRel(gM, bM);
                    Check(sb, ref pass, ref total, $"C: GPU(광원 {gs.LightCount}) — CSSHBake+CSSHDirect ≈ Burst(상대 {r1:E2}) · CSSHBakeMulti+CSSHDirect ≈ Burst(상대 {r2:E2})",
                          gs.LightCount == lights.Length && r1 < 2e-3 && r2 < 1e-2);
                }
                naPts.Dispose();
            }
            finally { bsOld.Dispose(); bsEmpty.Dispose(); bs.Dispose(); }
        }

        static Light MakeLamp(GameObject root, Vector3 pos, float intensity)
        {
            var go = new GameObject("Lamp") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(root.transform, false);
            go.transform.position = pos;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.intensity = intensity;
            l.range = 6f;
            l.color = Color.white;
            l.shadows = LightShadows.Hard;
            return l;
        }

        // ── D) ProbeVolumeBaker ──
        static void ProbeVolumeTest(StringBuilder sb, ref int pass, ref int total)
        {
            var go = new GameObject("SHLocalLightTests_PV") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.position = new Vector3(0f, 2.1f, 0f);
            var box = InstanceSceneBakerTests.MakeBoxMesh();
            var made = new List<ProbeVolumeAsset>();
            try
            {
                var baker = go.AddComponent<ProbeVolumeBaker>();
                baker.renderOnEnable = false;
                baker.computeValidity = false;
                baker.bakeVertexAO = false;
                baker.volumeSize = new Vector3(8f, 4f, 8f);
                baker.probeSpacing = Vector3.one;
                baker.dirCount = 64;
                baker.bounces = 1;
                ProbeVolumeBakerTests.SetupTemplates(baker, box);
                var lamp = MakeLamp(go, new Vector3(1.6f, 2.6f, 1.4f), 5f);

                baker.localLights.source = LocalLightSource.None;
                var aNone = ProbeVolumeBakerTests.Bake(baker, made);
                baker.localLights.source = LocalLightSource.Listed;
                baker.localLights.lights = new Light[0];
                var aEmpty = ProbeVolumeBakerTests.Bake(baker, made);
                baker.localLights.lights = new[] { lamp };
                var aLamp = ProbeVolumeBakerTests.Bake(baker, made);

                bool ok = aNone != null && aEmpty != null && aLamp != null;
                if (ok)
                {
                    var sNone = aNone.UnpackProbes(); var sEmpty = aEmpty.UnpackProbes(); var sLamp = aLamp.UnpackProbes();
                    bool same = true;
                    for (int i = 0; i < sNone.Length; i++) same &= SameSH(sNone[i], sEmpty[i]);
                    // 램프에서 가장 가까운 프로브의 램프 쪽 조도
                    var grid = baker.Grid; var pos = grid.Positions();
                    int best = 0; float bd = float.MaxValue;
                    for (int i = 0; i < pos.Length; i++) { float d = (pos[i] - lamp.transform.position).sqrMagnitude; if (d < bd) { bd = d; best = i; } }
                    Vector3 toLamp = (lamp.transform.position - pos[best]).normalized;
                    float e0 = sNone[best].Evaluate(toLamp).magnitude, e1 = sLamp[best].Evaluate(toLamp).magnitude;
                    ok = same && e1 > e0 * 1.5f + 0.05f && aLamp.lightLog != null && aLamp.lightLog.Contains("P1") && aNone.lightLog.StartsWith("0");
                    Check(sb, ref pass, ref total, $"D: ProbeVolumeBaker — 램프 근처 프로브 조도 {e0:F3} → {e1:F3} · lightLog '{aLamp.lightLog}' · None ≡ Listed(빈 목록)", ok);
                }
                else Check(sb, ref pass, ref total, "D: ProbeVolumeBaker 베이크 실패", false);
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(box);
                foreach (var a in made) ProbeVolumeBakerTests.DestroyAsset(a);
            }
        }

        // ── E) InstanceSceneBaker ──
        static void InstanceTest(StringBuilder sb, ref int pass, ref int total)
        {
            var go = new GameObject("SHLocalLightTests_IS") { hideFlags = HideFlags.HideAndDontSave };
            var box = InstanceSceneBakerTests.MakeBoxMesh();
            InstanceSHBakeAsset a0 = null, a1 = null;
            try
            {
                var baker = go.AddComponent<InstanceSceneBaker>();
                baker.renderOnEnable = false;
                baker.dirCount = 64;
                baker.bounces = 1;
                var meshes = new[] { box, box };
                var mats = new[]
                {
                    new[] { Matrix4x4.TRS(new Vector3(0f, 0.5f, 0f), Quaternion.identity, Vector3.one) },
                    new[] { Matrix4x4.TRS(new Vector3(0f, -0.05f, 0f), Quaternion.identity, new Vector3(10f, 0.1f, 10f)) },
                };
                baker.SetInstances(meshes, mats, new[] { new Color(0.7f, 0.7f, 0.7f), new Color(0.6f, 0.6f, 0.6f) });
                var lamp = MakeLamp(go, new Vector3(1.2f, 1.5f, 0.3f), 5f);

                baker.localLights.source = LocalLightSource.None;
                a0 = baker.BakeToAsset(null, out _);
                baker.localLights.source = LocalLightSource.Listed;
                baker.localLights.lights = new[] { lamp };
                a1 = baker.BakeToAsset(null, out _);
                bool ok = a0 != null && a1 != null;
                if (ok)
                {
                    var p0 = a0.templates[0].shPacked; var p1 = a1.templates[0].shPacked;
                    double diff = 0;
                    for (int i = 0; i < p0.Length; i++) diff += (p1[i] - p0[i]).magnitude;
                    ok = diff > 1e-3 && a1.lightLog != null && a1.lightLog.Contains("P1");
                    Check(sb, ref pass, ref total, $"E: InstanceSceneBaker — 램프로 인스턴스 SH 변화(Σ|Δ| {diff:F3}) · lightLog '{a1.lightLog}'", ok);
                }
                else Check(sb, ref pass, ref total, "E: InstanceSceneBaker 베이크 실패", false);
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(box);
                if (a0 != null) Object.DestroyImmediate(a0);
                if (a1 != null) Object.DestroyImmediate(a1);
            }
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
