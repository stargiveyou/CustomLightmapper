using System.Text;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// InstanceSceneBaker(SH 실사용 컴포넌트) 검증 — 디스크 저장 없이 BakeToAsset 으로 메모리 에셋 생성.
    ///  A) 에셋 구조: 템플릿 수·인스턴스 수·패킹 길이(행렬 × 14)·Validate·값 유한/비자명.
    ///  B) 정합: 에셋의 프로브 SH ≡ 같은 입력으로 코어 API(BuildScene→ComputeProbePoints→BurstSHBaker.Bake)를 직접 돌린 값(비트 동일).
    ///  C) 다바운스(bounces=3): 결정적(같은 seed 두 번 비트 동일) + 1바운스와 다른 값.
    ///  D) EXR 사본: 인코딩 바이트가 EXR 매직(76 2F 31 01)으로 시작.
    ///  E) 표시: 에셋 → 템플릿별 렌더러 생성(URP 셰이더 있을 때).
    /// 임시 GameObject·메시·에셋은 끝에 파괴.
    /// </summary>
    public static class InstanceSceneBakerTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== InstanceSceneBaker (SH 실사용 컴포넌트) ===");
            int pass = 0, total = 0;

            var go = new GameObject("InstanceSceneBakerTests") { hideFlags = HideFlags.HideAndDontSave };
            var box = MakeBoxMesh();
            InstanceSHBakeAsset a1 = null, a3 = null, a3b = null;
            try
            {
                var baker = go.AddComponent<InstanceSceneBaker>();
                baker.renderOnEnable = false;
                baker.dirCount = 128;
                baker.anchorMode = AnchorMode.SurfaceNormalOffset;
                baker.surfaceLift = 0.1f;
                baker.liftRelativeToBounds = true;
                baker.sunDirection = new Vector3(-0.4f, -1f, -0.3f);
                baker.sunIntensity = 1.5f;

                // 템플릿 0: 박스 3개 / 템플릿 1: 같은 메시를 납작한 바닥으로 1개
                var meshes = new[] { box, box };
                var mats = new[]
                {
                    new[]
                    {
                        Matrix4x4.TRS(new Vector3(-2f, 0.5f, 0f), Quaternion.Euler(0, 20, 0), Vector3.one),
                        Matrix4x4.TRS(new Vector3( 0f, 0.5f, 0f), Quaternion.Euler(10, 45, 0), new Vector3(1f, 1.5f, 0.8f)),
                        Matrix4x4.TRS(new Vector3( 2f, 0.5f, 0f), Quaternion.identity, Vector3.one * 0.7f),
                    },
                    new[] { Matrix4x4.TRS(new Vector3(0f, -0.05f, 0f), Quaternion.identity, new Vector3(12f, 0.1f, 12f)) },
                };
                var albedo = new[] { new Color(0.8f, 0.4f, 0.3f), new Color(0.6f, 0.6f, 0.6f) };
                baker.SetInstances(meshes, mats, albedo);

                // ── A) 1바운스 에셋 구조 ──
                baker.bounces = 1;
                a1 = baker.BakeToAsset(null, out string be1);
                bool okA = a1 != null && a1.Validate(out string errA);
                Check(sb, ref pass, ref total, $"A: 에셋 생성·Validate (backend={be1})", okA);
                if (a1 != null)
                {
                    Check(sb, ref pass, ref total, $"A: 템플릿 {a1.templates.Length}=2, 인스턴스 {a1.InstanceCount}=4", a1.templates.Length == 2 && a1.InstanceCount == 4);
                    Check(sb, ref pass, ref total, $"A: 패킹 길이 {a1.templates[0].shPacked.Length}=3×14, {a1.templates[1].shPacked.Length}=1×14",
                        a1.templates[0].shPacked.Length == 3 * InstanceSHBakeAsset.Float4PerInstance && a1.templates[1].shPacked.Length == InstanceSHBakeAsset.Float4PerInstance);
                    float c0 = InstanceSHBakeAsset_C0(a1, 0, 0);
                    Check(sb, ref pass, ref total, $"A: 값 유한·비자명 (inst0 상 |c0|={c0:0.000})", Finite(a1) && c0 > 1e-3f);
                }

                // ── B) 코어 API 직접 호출과 비트 동일 ──
                if (a1 != null)
                {
                    var linAlb = new[] { Lin(albedo[0]), Lin(albedo[1]) };
                    var input = new MatrixInstanceInput { templates = meshes, templateAlbedo = linAlb, instanceMatrices = mats };
                    using var s = TemplateInstanceSource.BuildScene(input, Allocator.TempJob, BVH.BuildQuality.SAH, baker.surfaceLift, baker.anchorMode);
                    using var bs = s.ToBurstScene(Allocator.TempJob);
                    TemplateInstanceSource.ComputeProbePoints(s, meshes, baker.anchorMode, baker.surfaceLift, baker.liftRelativeToBounds, out var top, out _);
                    var sunL = baker.sunColor.linear;
                    var sun = new DirectionalLight { Direction = baker.sunDirection.normalized, Color = new Vector3(sunL.r, sunL.g, sunL.b), Intensity = baker.sunIntensity };
                    var pts = new NativeArray<Vector3>(top, Allocator.TempJob);
                    var direct = BurstSHBaker.Bake(bs, BurstSky.Gradient(baker.skyTop, baker.skyBottom), sun, pts, baker.dirCount, Allocator.TempJob);
                    // 전역 인스턴스 1 = 템플릿0 의 두 번째 인스턴스 → 에셋 프로브 인덱스 (1*2+0)
                    var fromAsset = SHInstancedBuffer.UnflattenAt(a1.templates[0].shPacked, 1 * 2 + 0);
                    float d = MaxCoeffDiff(direct[1], fromAsset);
                    Check(sb, ref pass, ref total, $"B: 에셋 SH ≡ 코어 API 직접 베이크 (Δ={d:0.0e0})", d == 0f);
                    direct.Dispose(); pts.Dispose();
                }

                // ── C) 다바운스 결정성 ──
                baker.bounces = 3;
                a3 = baker.BakeToAsset(null, out _);
                a3b = baker.BakeToAsset(null, out _);
                if (a3 != null && a3b != null && a1 != null)
                {
                    bool same = SamePacked(a3, a3b);
                    Check(sb, ref pass, ref total, "C: 다바운스 같은 seed 두 번 → 비트 동일", same);
                    Check(sb, ref pass, ref total, "C: 다바운스 ≠ 1바운스(경로추적 반영)", !SamePacked(a1, a3));
                    Check(sb, ref pass, ref total, "C: 다바운스 값 유한", Finite(a3));
                }
                else Check(sb, ref pass, ref total, "C: 다바운스 베이크 실패", false);

                // ── D) EXR 사본 ──
                if (a1 != null)
                {
                    var exr = InstanceSceneBaker.EncodeCoefficientsExr(a1);
                    bool magic = exr != null && exr.Length > 4 && exr[0] == 0x76 && exr[1] == 0x2F && exr[2] == 0x31 && exr[3] == 0x01;
                    Check(sb, ref pass, ref total, $"D: EXR 인코딩 {exr?.Length ?? 0} bytes, 매직 76 2F 31 01", magic);
                }

                // ── E) 표시 ──
                if (Shader.Find("HuskyLibs/InstancedSH_URP") == null)
                    sb.AppendLine("  [SKIP] E: 셰이더 'HuskyLibs/InstancedSH_URP' 없음");
                else if (a1 != null)
                {
                    baker.bakeAsset = a1;
                    baker.RebuildRender();
                    Check(sb, ref pass, ref total, $"E: 에셋 → 렌더러 {baker.RendererCount}개(템플릿 2)", baker.RendererCount == 2);
                    baker.ClearRender();
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(box);
                if (a1) Object.DestroyImmediate(a1);
                if (a3) Object.DestroyImmediate(a3);
                if (a3b) Object.DestroyImmediate(a3b);
            }

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        static float InstanceSHBakeAsset_C0(InstanceSHBakeAsset a, int template, int probe)
            => SHInstancedBuffer.UnflattenAt(a.templates[template].shPacked, probe).c0.magnitude;

        static bool Finite(InstanceSHBakeAsset a)
        {
            foreach (var t in a.templates)
                foreach (var v in t.shPacked)
                    if (float.IsNaN(v.x + v.y + v.z + v.w) || float.IsInfinity(v.x + v.y + v.z + v.w)) return false;
            return true;
        }

        static bool SamePacked(InstanceSHBakeAsset a, InstanceSHBakeAsset b)
        {
            if (a.templates.Length != b.templates.Length) return false;
            for (int t = 0; t < a.templates.Length; t++)
            {
                var x = a.templates[t].shPacked; var y = b.templates[t].shPacked;
                if (x.Length != y.Length) return false;
                for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
            }
            return true;
        }

        static float MaxCoeffDiff(SH9 a, SH9 b)
        {
            float m = 0f;
            m = Mathf.Max(m, (a.c0 - b.c0).magnitude); m = Mathf.Max(m, (a.c1 - b.c1).magnitude); m = Mathf.Max(m, (a.c2 - b.c2).magnitude);
            m = Mathf.Max(m, (a.c3 - b.c3).magnitude); m = Mathf.Max(m, (a.c4 - b.c4).magnitude); m = Mathf.Max(m, (a.c5 - b.c5).magnitude);
            m = Mathf.Max(m, (a.c6 - b.c6).magnitude); m = Mathf.Max(m, (a.c7 - b.c7).magnitude); m = Mathf.Max(m, (a.c8 - b.c8).magnitude);
            return m;
        }

        static Vector3 Lin(Color c) { var l = c.linear; return new Vector3(l.r, l.g, l.b); }

        // 단위 박스(±0.5) — 면별 정점 분리(노멀 평탄), Read/Write 가능(런타임 생성 메시는 기본 readable)
        internal static Mesh MakeBoxMesh()
        {
            var m = new Mesh { name = "InstanceSceneBakerTests_Box", hideFlags = HideFlags.HideAndDontSave };
            var p = new[]
            {
                new Vector3(-.5f,-.5f,-.5f), new Vector3(.5f,-.5f,-.5f), new Vector3(.5f,.5f,-.5f), new Vector3(-.5f,.5f,-.5f),
                new Vector3(-.5f,-.5f, .5f), new Vector3(.5f,-.5f, .5f), new Vector3(.5f,.5f, .5f), new Vector3(-.5f,.5f, .5f),
            };
            int[][] faces = { new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 3, 7, 6, 2 }, new[] { 0, 4, 7, 3 }, new[] { 1, 2, 6, 5 } };
            var v = new Vector3[24]; var idx = new int[36];
            for (int f = 0; f < 6; f++)
            {
                for (int k = 0; k < 4; k++) v[f * 4 + k] = p[faces[f][k]];
                int b = f * 4, o = f * 6;
                idx[o] = b; idx[o + 1] = b + 1; idx[o + 2] = b + 2; idx[o + 3] = b; idx[o + 4] = b + 2; idx[o + 5] = b + 3;
            }
            m.vertices = v; m.triangles = idx;
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
