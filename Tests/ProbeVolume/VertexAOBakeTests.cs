using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using UnityEngine;
using static HuskyLibs.CustomLightmapper.Bake.ProbeGridTests;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Track B B1-5 검증: <see cref="VertexAOBake"/>(템플릿 로컬 정점 AO) + 베이커·에셋·표시 연동.
    ///  A) ≡ BurstAO.Compute 직접 호출(같은 단독 씬·오프셋 점·노멀·시드 — 비트 동일) · 같은 설정 두 번 비트 동일.
    ///  B) 고립 볼록 메시(박스) / 고립 평면(양면): AO = 1(자기차폐 없음, 뒷면 포함).
    ///  C) 무한 천장 아래 작은 바닥: AO ≈ 0(≤ 0.02) · 레이 길이를 천장보다 짧게 → AO = 1(maxDistance 의미).
    ///  D) 양면 잎 카드 + 뒷면 쪽 가림판: 앞면 AO = 1, 뒷면 AO ≈ 0 / twoSided=false 면 뒷면 = 앞면.
    ///  E) AO 메시 사본: 정점·삼각형·노멀 ≡ 원본, uv3 = (앞, 뒤), 원본 uv3 미기록, 이름 "_AO".
    ///  F) 베이커: 에셋 vertexAO = AO 켠 템플릿의 고유 메시 수(같은 메시 공유 → 1, twoSided OR) · uv3 ≡ VertexAOBake 직접 ·
    ///     표시 = AO 메시 + _HUSKY_VERTEX_AO 런타임 머티리얼, AO 끈 템플릿은 원본 메시 + 키워드 없음(URP 셰이더 있을 때).
    /// 임시 메시·GameObject·에셋은 끝에 파괴. 호출: Debug.Log(VertexAOBakeTests.RunAll());
    /// </summary>
    public static class VertexAOBakeTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Track B 정점 AO (B1-5, 템플릿 로컬) ===");
            int pass = 0, total = 0;
            var made = new List<Object>();
            try
            {
                TestDirect(sb, ref pass, ref total, made);
                TestIsolated(sb, ref pass, ref total, made);
                TestCeiling(sb, ref pass, ref total, made);
                TestTwoSided(sb, ref pass, ref total, made);
                TestAOMesh(sb, ref pass, ref total, made);
                TestBaker(sb, ref pass, ref total, made);
            }
            finally
            {
                foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
            }
            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        static VertexAOBake.Settings S(int samples = 64, float maxRel = 0f) =>
            new VertexAOBake.Settings { samples = samples, maxDistanceRelative = maxRel, surfaceBiasRelative = 1e-4f, seed = 777u };

        // ── A) ≡ BurstAO 직접 ──
        static void TestDirect(StringBuilder sb, ref int pass, ref int total, List<Object> made)
        {
            // 오목한 메시: 바닥 + 벽 2장(L자 골) — AO 가 0/1 이 아닌 중간값이 나오도록
            var mesh = Track(made, BuildMesh("Valley",
                Patch(new Vector3(0f, 0f, 0f), Vector3.right, Vector3.forward, 2f, 2f, 4),
                Patch(new Vector3(-1f, 1f, 0f), Vector3.up, Vector3.forward, 2f, 2f, 3, flip: true),   // 노멀 +X(골 안쪽)
                Patch(new Vector3(0f, 1f, -1f), Vector3.right, Vector3.up, 2f, 2f, 3, flip: true)));   // 노멀 +Z(골 안쪽)
            var s = S(64, 0.5f);
            VertexAOBake.Compute(mesh, s, false, out var front, out var back);

            var tris = TemplateInstanceSource.MeshToLocalTris(mesh);
            float diag = mesh.bounds.size.magnitude;
            using var bvh = new TwoLevelBVH(new[] { tris }, new[] { new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = Matrix4x4.identity } },
                                            Allocator.TempJob, BVH.BuildQuality.SAH);
            using var scene = BurstScene.Create(bvh, null, Allocator.TempJob);
            var v = mesh.vertices; var n = mesh.normals;
            var pts = new NativeArray<Vector3>(v.Length, Allocator.TempJob);
            var nrm = new NativeArray<Vector3>(v.Length, Allocator.TempJob);
            var val = new NativeArray<bool>(v.Length, Allocator.TempJob);
            for (int i = 0; i < v.Length; i++) { pts[i] = v[i] + n[i] * (s.surfaceBiasRelative * diag); nrm[i] = n[i]; val[i] = true; }
            var direct = BurstAO.Compute(scene, pts, nrm, val, s.samples, s.seed, s.maxDistanceRelative * diag, Allocator.TempJob);
            float d = 0f, mn = 1f;
            for (int i = 0; i < v.Length; i++) { d = Mathf.Max(d, Mathf.Abs(direct[i] - front[i])); mn = Mathf.Min(mn, front[i]); }
            direct.Dispose(); pts.Dispose(); nrm.Dispose(); val.Dispose();

            VertexAOBake.Compute(mesh, s, false, out var front2, out _);
            bool same = true; for (int i = 0; i < front.Length; i++) same &= front[i] == front2[i];
            bool backCopy = true; for (int i = 0; i < front.Length; i++) backCopy &= back[i] == front[i];
            Check(sb, ref pass, ref total, $"A: ≡ BurstAO.Compute 직접(단독 씬·오프셋·시드, Δ={d}) · 재현 · 단면이면 뒷면=앞면 · 골짜기 min AO={mn:0.00}(<1)",
                d == 0f && same && backCopy && mn < 0.95f);
        }

        // ── B) 고립 볼록·평면 ──
        static void TestIsolated(StringBuilder sb, ref int pass, ref int total, List<Object> made)
        {
            var box = Track(made, InstanceSceneBakerTests.MakeBoxMesh());
            VertexAOBake.Compute(box, S(), false, out var fb, out _);
            var plane = Track(made, BuildMesh("Plane", Patch(Vector3.zero, Vector3.right, Vector3.forward, 1f, 1f, 3)));
            VertexAOBake.Compute(plane, S(), true, out var fp, out var bp);
            Check(sb, ref pass, ref total, $"B: 고립 박스 AO min={Min(fb):0.000} · 고립 평면 앞 {Min(fp):0.000}/뒤 {Min(bp):0.000} (전부 1)",
                Min(fb) == 1f && Min(fp) == 1f && Min(bp) == 1f);
        }

        // ── C) 무한 천장 아래 ──
        static void TestCeiling(StringBuilder sb, ref int pass, ref int total, List<Object> made)
        {
            // 작은 바닥(±0.1, 노멀 +Y) + 큰 천장(±100, y=0.5, 노멀 -Y). 바닥 정점만 검사.
            var mesh = Track(made, BuildMesh("Ceiling",
                Patch(Vector3.zero, Vector3.right, Vector3.forward, 0.2f, 0.2f, 2),
                Patch(new Vector3(0f, 0.5f, 0f), Vector3.right, Vector3.forward, 200f, 200f, 2, flip: true)));
            int floorVerts = 4;   // Patch(.., 2) = 2×2 정점
            VertexAOBake.Compute(mesh, S(256, 0f), false, out var ao, out _);
            float maxFloor = 0f; for (int i = 0; i < floorVerts; i++) maxFloor = Mathf.Max(maxFloor, ao[i]);

            // 레이 길이 < 천장 높이 → 닿지 않음 → 1. 대각선 ≈ 283 → 0.5/283 의 절반
            float diag = mesh.bounds.size.magnitude;
            VertexAOBake.Compute(mesh, S(256, 0.25f / diag), false, out var aoShort, out _);
            float minShort = 1f; for (int i = 0; i < floorVerts; i++) minShort = Mathf.Min(minShort, aoShort[i]);
            Check(sb, ref pass, ref total, $"C: 무한 천장 아래 바닥 AO max={maxFloor:0.000}(≈0) · 레이 길이 0.25 < 천장 0.5 → AO min={minShort:0.000}(=1)",
                maxFloor <= 0.02f && minShort == 1f);
        }

        // ── D) 양면 잎 카드 ──
        static void TestTwoSided(StringBuilder sb, ref int pass, ref int total, List<Object> made)
        {
            // 잎 카드(±0.1, y=0, 노멀 +Y) + 아래쪽 큰 가림판(±50, y=-0.2, 노멀 +Y)
            var mesh = Track(made, BuildMesh("LeafCard",
                Patch(Vector3.zero, Vector3.right, Vector3.forward, 0.2f, 0.2f, 2),
                Patch(new Vector3(0f, -0.2f, 0f), Vector3.right, Vector3.forward, 100f, 100f, 2)));
            VertexAOBake.Compute(mesh, S(256, 0f), true, out var f, out var b);
            float minF = 1f, maxB = 0f; for (int i = 0; i < 4; i++) { minF = Mathf.Min(minF, f[i]); maxB = Mathf.Max(maxB, b[i]); }
            VertexAOBake.Compute(mesh, S(256, 0f), false, out var f1, out var b1);
            bool copy = true; for (int i = 0; i < f1.Length; i++) copy &= b1[i] == f1[i];
            Check(sb, ref pass, ref total, $"D: 양면 카드 — 앞면 AO min={minF:0.000}(=1) · 뒷면(가림판 쪽) max={maxB:0.000}(≈0) · twoSided=false 면 뒷면=앞면",
                minF == 1f && maxB <= 0.05f && copy);
        }

        // ── E) AO 메시 사본 ──
        static void TestAOMesh(StringBuilder sb, ref int pass, ref int total, List<Object> made)
        {
            var src = Track(made, InstanceSceneBakerTests.MakeBoxMesh());
            int n = src.vertexCount;
            var front = new float[n]; var back = new float[n];
            for (int i = 0; i < n; i++) { front[i] = i / (float)n; back[i] = 1f - i / (float)n; }
            var ao = Track(made, VertexAOBake.CreateAOMesh(src, front, back));
            var uv = new List<Vector2>(); ao.GetUVs(VertexAOBake.UVChannel, uv);
            var srcUv = new List<Vector2>(); src.GetUVs(VertexAOBake.UVChannel, srcUv);
            bool okUv = uv.Count == n;
            for (int i = 0; okUv && i < n; i++) okUv &= uv[i].x == front[i] && uv[i].y == back[i];
            bool okGeo = ao.vertexCount == n && SameArr(ao.vertices, src.vertices) && SameArr(ao.normals, src.normals) && SameArr(ao.triangles, src.triangles);
            Check(sb, ref pass, ref total, $"E: AO 메시 '{ao.name}' — 지오메트리 ≡ 원본 · uv3 = (앞, 뒤) · 원본 uv3 {srcUv.Count}개(미기록)",
                okUv && okGeo && srcUv.Count == 0 && ao.name == src.name + "_AO");
        }

        // ── F) 베이커 연동 ──
        static void TestBaker(StringBuilder sb, ref int pass, ref int total, List<Object> made)
        {
            var go = new GameObject("VertexAOBakeTests") { hideFlags = HideFlags.HideAndDontSave };
            made.Add(go);
            go.transform.position = new Vector3(0f, 2.1f, 0f);
            var box = Track(made, InstanceSceneBakerTests.MakeBoxMesh());
            var card = Track(made, BuildMesh("Card", Patch(Vector3.zero, Vector3.right, Vector3.forward, 1f, 1f, 2)));
            var baker = go.AddComponent<ProbeVolumeBaker>();
            baker.localLights.source = LocalLightSource.None;   // 열린 씬의 라이트와 무관하게(테스트 격리)
            baker.renderOnEnable = false;
            baker.volumeSize = new Vector3(6f, 4f, 6f);
            baker.probeSpacing = Vector3.one * 2f;
            baker.dirCount = 32;
            baker.computeValidity = false;
            baker.vertexAOSettings = S(32, 0.5f);
            // 템플릿 0·1 = 같은 박스 메시(1 엔트리, 1 만 twoSided → OR), 2 = 카드(AO 끔)
            baker.SetInstances(new[] { box, box, card },
                new[] { new[] { Matrix4x4.Translate(new Vector3(-1f, 0.5f, 0f)) }, new[] { Matrix4x4.Translate(new Vector3(1f, 0.5f, 0f)) },
                        new[] { Matrix4x4.Translate(new Vector3(0f, 1.5f, 1.5f)) } });
            baker.templates[1].twoSided = true;
            baker.templates[2].vertexAO = false;

            var a = baker.BakeToAsset(out _);
            if (a == null) { Check(sb, ref pass, ref total, "F: 베이크 실패", false); return; }
            made.Add(a);
            foreach (var t in a.textures) made.Add(t);
            foreach (var e in a.vertexAO) made.Add(e.aoMesh);

            var entry = a.vertexAO.Length == 1 ? a.vertexAO[0] : null;
            bool okEntries = entry != null && entry.source == box && entry.twoSided && entry.aoMesh != null && a.FindAOMesh(card) == null;
            bool okUv = false;
            if (entry != null)
            {
                VertexAOBake.Compute(box, baker.vertexAOSettings, true, out var f, out var b);
                var uv = new List<Vector2>(); entry.aoMesh.GetUVs(VertexAOBake.UVChannel, uv);
                okUv = uv.Count == f.Length;
                for (int i = 0; okUv && i < f.Length; i++) okUv &= uv[i].x == f[i] && uv[i].y == b[i];
            }
            Check(sb, ref pass, ref total, $"F: 에셋 정점 AO 엔트리 {a.vertexAO.Length}(공유 메시 1, twoSided OR, AO 끈 카드 제외) · uv3 ≡ VertexAOBake 직접",
                okEntries && okUv);

            if (Shader.Find("HuskyLibs/InstancedSH_URP") == null) { sb.AppendLine("  [SKIP] F: 셰이더 'HuskyLibs/InstancedSH_URP' 없음 — 표시 검사 생략"); return; }
            baker.volumeAsset = a;
            bool okDraw;
            try
            {
                baker.RebuildRender();
                var rs = baker.Renderers;
                okDraw = rs.Count == 3
                         && rs[0].DrawMesh == entry.aoMesh && rs[1].DrawMesh == entry.aoMesh && rs[2].DrawMesh == card
                         && rs[0].DrawMaterial.IsKeywordEnabled(ProbeVolumeBaker.VertexAOKeyword)
                         && !rs[2].DrawMaterial.IsKeywordEnabled(ProbeVolumeBaker.VertexAOKeyword)
                         && rs[0].DrawMaterial != rs[2].DrawMaterial;
                baker.applyVertexAO = false;
                baker.RebuildRender();
                okDraw &= baker.Renderers[0].DrawMesh == box && !baker.Renderers[0].DrawMaterial.IsKeywordEnabled(ProbeVolumeBaker.VertexAOKeyword);
            }
            finally { baker.ClearRender(); baker.applyVertexAO = true; }
            Check(sb, ref pass, ref total, "F: 표시 — AO 템플릿 = AO 메시 + _HUSKY_VERTEX_AO 머티리얼, AO 끈 템플릿 = 원본 + 키워드 없음 · applyVertexAO=false → 원본", okDraw);
        }

        // ───────────────────────── 헬퍼 ─────────────────────────

        static T Track<T>(List<Object> made, T o) where T : Object { made.Add(o); return o; }

        static float Min(float[] a) { float m = 1f; foreach (var x in a) m = Mathf.Min(m, x); return m; }

        static bool SameArr<T>(T[] a, T[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
            return true;
        }

        // 격자 사각형(res×res 정점) — 원점 중심, 축 u·v, 노멀 = cross(v, u)(flip 이면 반대). 정점 노멀 = 면 노멀(평면).
        struct PatchDesc { public Vector3[] v; public Vector3 n; public int[] t; }

        static PatchDesc Patch(Vector3 center, Vector3 u, Vector3 v, float sizeU, float sizeV, int res, bool flip = false)
        {
            var q = new PatchDesc { v = new Vector3[res * res], t = new int[(res - 1) * (res - 1) * 6] };
            for (int j = 0; j < res; j++)
                for (int i = 0; i < res; i++)
                    q.v[j * res + i] = center + u * ((i / (float)(res - 1) - 0.5f) * sizeU) + v * ((j / (float)(res - 1) - 0.5f) * sizeV);
            q.n = Vector3.Cross(v, u).normalized * (flip ? -1f : 1f);
            int k = 0;
            for (int j = 0; j < res - 1; j++)
                for (int i = 0; i < res - 1; i++)
                {
                    int a = j * res + i, b = a + 1, c = a + res, d = c + 1;
                    if (!flip) { q.t[k++] = a; q.t[k++] = c; q.t[k++] = b; q.t[k++] = b; q.t[k++] = c; q.t[k++] = d; }
                    else { q.t[k++] = a; q.t[k++] = b; q.t[k++] = c; q.t[k++] = b; q.t[k++] = d; q.t[k++] = c; }
                }
            return q;
        }

        // 사각형들 → 메시(정점 순서 = 인자 순서, 첫 사각형 정점이 0 부터). 노멀 명시, Read/Write 가능(런타임 생성).
        static Mesh BuildMesh(string name, params PatchDesc[] quads)
        {
            var v = new List<Vector3>(); var n = new List<Vector3>(); var t = new List<int>();
            foreach (var q in quads)
            {
                int b = v.Count;
                v.AddRange(q.v);
                for (int i = 0; i < q.v.Length; i++) n.Add(q.n);
                foreach (int x in q.t) t.Add(b + x);
            }
            var m = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            m.SetVertices(v); m.SetNormals(n); m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }
    }
}
