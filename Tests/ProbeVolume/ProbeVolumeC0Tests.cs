using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// v15.19 검증: <see cref="ProbeVolumeBaker"/> 정적 입력의 C0 합류(<see cref="ProbeVolumeBaker.StaticSource"/>).
    /// 씬 = 정적 루트(지면 + 벽) · 나무 2그루(captureSource 'Trees' → 자식 'Tree_i' → 손자 'Trunk' 에 MeshFilter, 로컬 오프셋) ·
    ///      'Dup'(템플릿 1 의 행렬과 같은 메시·월드 행렬).
    ///  A) Roots 구 거동: BuildSceneInput ≡ 구 루프 미러(비트 동일) · Collected(같은 MeshFilter 같은 순서) ≡ Roots — 입력·베이크 SH 비트 동일.
    ///  B) 템플릿 중복 제외: Collected 에 나무 줄기·Dup 를 넣어도 ① captureSource 자손 ② (메시, 행렬) 일치로 빠짐 →
    ///     정적만 넣은 것과 베이크 비트 동일 · sceneLog '템플릿 중복 −3' · Roots 는 구 거동대로 제외 안 함.
    ///  C) Excluded 템플릿도 제외 키에 포함 — 베이크 씬에서 뺀 식생이 정적 입력으로 되살아나지 않음.
    ///  D) SetCollected(source 전환 · null → 빈 배열) · null/중복/메시 없는 MeshFilter 건너뜀.
    ///  E) Collected 인데 비어 있음 → 경고 후 정적 없이 진행 = Roots + 빈 staticRoots 와 비트 동일.
    /// SceneCollector(에디터 어셈블리) 자체는 여기서 부르지 않는다 — 수집 결과 주입 이후 계약만. 호출: Debug.Log(ProbeVolumeC0Tests.RunAll());
    /// </summary>
    public static class ProbeVolumeC0Tests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Track B 정적 입력 C0 합류 (v15.19) ===");
            int pass = 0, total = 0;

            var box = InstanceSceneBakerTests.MakeBoxMesh();
            var made = new List<ProbeVolumeAsset>();
            var roots = new List<GameObject>();
            try
            {
                var go = Hidden("ProbeVolumeC0Tests", roots);
                go.transform.position = new Vector3(0f, 2.1f, 0f);
                var baker = go.AddComponent<ProbeVolumeBaker>();
                baker.renderOnEnable = false;
                baker.localLights.source = LocalLightSource.None;   // 열린 씬의 라이트와 무관하게(테스트 격리)
                baker.bakeVertexAO = false;
                baker.volumeSize = new Vector3(8f, 4f, 8f);
                baker.probeSpacing = Vector3.one;
                baker.dirCount = 64;
                baker.bounces = 1;

                var statics = Hidden("Statics", roots).transform;
                var ground = Child(statics, "Ground", box, Matrix4x4.TRS(new Vector3(0f, -0.05f, 0f), Quaternion.identity, new Vector3(12f, 0.1f, 12f)));
                var wall = Child(statics, "Wall", box, Matrix4x4.TRS(new Vector3(1.6f, 1.0f, 0.13f), Quaternion.identity, new Vector3(0.7f, 2.0f, 3.1f)));
                var trees = Hidden("Trees", roots).transform;
                var trunks = new List<MeshFilter>();
                var treePos = new[] { new Vector3(-2.3f, 0f, 1.7f), new Vector3(-1.1f, 0f, -2.2f) };
                for (int i = 0; i < treePos.Length; i++)
                {
                    var tree = new GameObject($"Tree_{i}") { hideFlags = HideFlags.HideAndDontSave }.transform;
                    tree.SetParent(trees, false);
                    tree.localPosition = treePos[i];
                    trunks.Add(Child(tree, "Trunk", box, Matrix4x4.TRS(new Vector3(0f, 0.8f, 0f), Quaternion.identity, new Vector3(0.5f, 1.6f, 0.5f)), local: true));
                }
                var dup = Child(Hidden("DupRoot", roots).transform, "Dup", box,
                                Matrix4x4.TRS(new Vector3(2.4f, 0.4f, -1.9f), Quaternion.Euler(0f, 25f, 0f), new Vector3(0.8f, 0.8f, 0.8f)));

                var staticSet = new[] { ground, wall };
                TestRootsLegacy(sb, ref pass, ref total, baker, statics, staticSet, made);
                TestTemplateOverlap(sb, ref pass, ref total, baker, box, trees, trunks, dup, staticSet, made);
                TestExcludedTemplate(sb, ref pass, ref total, baker, trunks, dup, staticSet);
                TestSetCollected(sb, ref pass, ref total, baker, ground, wall, roots);
                TestEmptyCollected(sb, ref pass, ref total, baker, made);
            }
            finally
            {
                foreach (var r in roots) if (r != null) Object.DestroyImmediate(r);
                Object.DestroyImmediate(box);
                foreach (var a in made) ProbeVolumeBakerTests.DestroyAsset(a);
            }

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ── A) Roots 구 거동 · Collected ≡ Roots ──
        static void TestRootsLegacy(StringBuilder sb, ref int pass, ref int total, ProbeVolumeBaker baker, Transform statics, MeshFilter[] staticSet, List<ProbeVolumeAsset> made)
        {
            baker.templates = new ProbeVolumeBaker.TemplateInput[0];
            baker.staticSource = ProbeVolumeBaker.StaticSource.Roots;
            baker.staticRoots = new[] { statics };
            bool okR = baker.BuildSceneInput(out var trisR, out var albR, out var instR, out var cbR);
            LegacyMirror(baker, out var trisL, out var albL, out var instL, out var cbL);
            Check(sb, ref pass, ref total, $"A: Roots — BuildSceneInput ≡ 구 루프 미러(메시 {trisR?.Length} · 인스턴스 {instR?.Length}, 비트 동일)",
                  okR && SameInput(trisR, albR, instR, cbR, trisL, albL, instL, cbL) && instR.Length == 2);
            var aR = ProbeVolumeBakerTests.Bake(baker, made);

            baker.SetCollected(statics.GetComponentsInChildren<MeshFilter>());
            bool okC = baker.BuildSceneInput(out var trisC, out var albC, out var instC, out var cbC);
            var aC = ProbeVolumeBakerTests.Bake(baker, made);
            Check(sb, ref pass, ref total, "A: Collected(같은 MeshFilter·순서) ≡ Roots — 씬 입력 · 베이크 SH 비트 동일",
                  okC && SameInput(trisC, albC, instC, cbC, trisR, albR, instR, cbR) && aR != null && aC != null && SamePacked(aR, aC));
            baker.staticSource = ProbeVolumeBaker.StaticSource.Roots;
            baker.staticRoots = new Transform[0];
        }

        // ── B) 템플릿 중복 제외 ──
        static void TestTemplateOverlap(StringBuilder sb, ref int pass, ref int total, ProbeVolumeBaker baker, Mesh box, Transform trees,
                                        List<MeshFilter> trunks, MeshFilter dup, MeshFilter[] staticSet, List<ProbeVolumeAsset> made)
        {
            baker.templates = new[]
            {
                new ProbeVolumeBaker.TemplateInput { mesh = box, captureSource = trees },
                new ProbeVolumeBaker.TemplateInput { mesh = box, matrices = new List<Matrix4x4> { dup.transform.localToWorldMatrix } },
            };
            baker.CaptureMatricesFromSources();   // 템플릿 0 ← Tree_i 행렬(줄기는 손자라 행렬이 다름 → captureSource 규칙만 잡음)
            bool captured = baker.templates[0].matrices.Count == 2
                            && !baker.templates[0].matrices.Contains(trunks[0].transform.localToWorldMatrix);

            var withDup = new List<MeshFilter>(staticSet);
            withDup.Insert(1, trunks[0]);
            withDup.Add(dup);
            withDup.Add(trunks[1]);
            baker.SetCollected(withDup.ToArray());
            var list = baker.StaticFilters(out int overlap);
            bool okIn = baker.BuildSceneInput(out _, out _, out var insts, out _);
            string log = baker.LastSceneLog;
            var aDup = ProbeVolumeBakerTests.Bake(baker, made);

            baker.SetCollected(staticSet);
            var aOnly = ProbeVolumeBakerTests.Bake(baker, made);
            Check(sb, ref pass, ref total,
                  $"B: Collected 의 줄기 2(captureSource 자손)·Dup 1(메시+행렬) 제외 → 정적 {list.Count} · 인스턴스 {insts?.Length}(2+1+2) · 정적만 넣은 것과 베이크 비트 동일",
                  captured && overlap == 3 && list.Count == 2 && list[0] == staticSet[0] && list[1] == staticSet[1]
                  && okIn && insts.Length == 5 && aDup != null && aOnly != null && SamePacked(aDup, aOnly));
            Check(sb, ref pass, ref total, $"B: sceneLog '{log}' — static(Collected) 2 · 템플릿 중복 −3 · 에셋에 기록",
                  log != null && log.Contains("static(Collected) 2") && log.Contains("템플릿 중복 −3") && aDup != null && aDup.sceneLog == log);

            // Roots 는 구 거동(중복 제외 없음 — 비트 동일 보장)
            baker.staticSource = ProbeVolumeBaker.StaticSource.Roots;
            baker.staticRoots = new[] { trees };
            var rootsList = baker.StaticFilters(out int rootsOverlap);
            baker.staticRoots = new Transform[0];
            Check(sb, ref pass, ref total, $"B: Roots 는 템플릿 중복을 빼지 않음(구 거동) — 줄기 {rootsList.Count}/2 · 제외 {rootsOverlap}",
                  rootsList.Count == 2 && rootsOverlap == 0);
        }

        // ── C) Excluded 템플릿 ──
        static void TestExcludedTemplate(StringBuilder sb, ref int pass, ref int total, ProbeVolumeBaker baker, List<MeshFilter> trunks, MeshFilter dup, MeshFilter[] staticSet)
        {
            baker.templates[0].occluder = ProbeVolumeBaker.OccluderMode.Excluded;
            var all = new List<MeshFilter>(staticSet) { trunks[0], trunks[1], dup };
            baker.SetCollected(all.ToArray());
            baker.StaticFilters(out int overlap);
            bool okIn = baker.BuildSceneInput(out var tris, out _, out var insts, out _);
            baker.templates[0].occluder = ProbeVolumeBaker.OccluderMode.Opaque;
            Check(sb, ref pass, ref total, $"C: Excluded 템플릿의 줄기도 정적 입력에서 제외(제외 {overlap}) → 인스턴스 {insts?.Length} = 템플릿1 1 + 정적 2",
                  overlap == 3 && okIn && insts.Length == 3);
        }

        // ── D) SetCollected · 목록 정리 ──
        static void TestSetCollected(StringBuilder sb, ref int pass, ref int total, ProbeVolumeBaker baker, MeshFilter ground, MeshFilter wall, List<GameObject> roots)
        {
            baker.templates = new ProbeVolumeBaker.TemplateInput[0];
            baker.staticSource = ProbeVolumeBaker.StaticSource.Roots;
            baker.SetCollected(null);
            bool nullOk = baker.staticSource == ProbeVolumeBaker.StaticSource.Collected && baker.collectedStatics != null && baker.collectedStatics.Length == 0;

            var noMesh = Hidden("NoMesh", roots).AddComponent<MeshFilter>();
            baker.SetCollected(new[] { null, ground, noMesh, ground, wall });
            var list = baker.StaticFilters(out int overlap);
            Check(sb, ref pass, ref total, $"D: SetCollected → Collected · null → 빈 배열 · null/메시 없음/중복 건너뜀 → [{Names(list)}]",
                  nullOk && overlap == 0 && list.Count == 2 && list[0] == ground && list[1] == wall);
        }

        // ── E) 빈 Collected ──
        static void TestEmptyCollected(StringBuilder sb, ref int pass, ref int total, ProbeVolumeBaker baker, List<ProbeVolumeAsset> made)
        {
            baker.templates = new ProbeVolumeBaker.TemplateInput[0];
            baker.SetCollected(new MeshFilter[0]);
            var aC = ProbeVolumeBakerTests.Bake(baker, made);   // 경고 1줄(의도) 후 하늘만 씬
            baker.staticSource = ProbeVolumeBaker.StaticSource.Roots;
            baker.staticRoots = new Transform[0];
            var aR = ProbeVolumeBakerTests.Bake(baker, made);
            Check(sb, ref pass, ref total, "E: 빈 Collected → 정적 없이 진행 = Roots + 빈 staticRoots 와 비트 동일",
                  aC != null && aR != null && SamePacked(aC, aR));
        }

        // ───────────────────────── 헬퍼 ─────────────────────────

        // v15.18 까지의 정적 루프(템플릿 없음) 그대로 — Roots 경로가 바뀌지 않았음을 고정
        static void LegacyMirror(ProbeVolumeBaker b, out Tri[][] tris, out Vector3[] albedo, out TwoLevelBVH.Instance[] insts, out bool[] cb)
        {
            var triList = new List<Tri[]>(); var albList = new List<Vector3>(); var cbList = new List<bool>(); var instList = new List<TwoLevelBVH.Instance>();
            var meshToIdx = new Dictionary<Mesh, int>();
            var seen = new HashSet<MeshFilter>();
            foreach (var root in b.staticRoots)
            {
                if (root == null) continue;
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
                {
                    var mesh = mf.sharedMesh;
                    if (mesh == null || !seen.Add(mf) || !mesh.isReadable) continue;
                    if (!meshToIdx.TryGetValue(mesh, out int mi))
                    {
                        mi = triList.Count;
                        meshToIdx[mesh] = mi;
                        triList.Add(TemplateInstanceSource.MeshToLocalTris(mesh));
                        var lin = b.staticDefaultAlbedo.linear;   // 머티리얼 없음 → 기본 알베도
                        albList.Add(new Vector3(Mathf.Clamp01(lin.r), Mathf.Clamp01(lin.g), Mathf.Clamp01(lin.b)));
                        cbList.Add(!b.staticTwoSided);
                    }
                    instList.Add(new TwoLevelBVH.Instance { MeshIndex = mi, LocalToWorld = mf.transform.localToWorldMatrix });
                }
            }
            tris = triList.ToArray(); albedo = albList.ToArray(); insts = instList.ToArray(); cb = cbList.ToArray();
        }

        static bool SameInput(Tri[][] ta, Vector3[] aa, TwoLevelBVH.Instance[] ia, bool[] ca, Tri[][] tb, Vector3[] ab, TwoLevelBVH.Instance[] ib, bool[] cbb)
        {
            if (ta == null || tb == null || ta.Length != tb.Length || aa.Length != ab.Length || ia.Length != ib.Length || ca.Length != cbb.Length) return false;
            for (int m = 0; m < ta.Length; m++)
            {
                if (ta[m].Length != tb[m].Length || !aa[m].Equals(ab[m]) || ca[m] != cbb[m]) return false;
                for (int t = 0; t < ta[m].Length; t++)
                    if (!ta[m][t].V0.Equals(tb[m][t].V0) || !ta[m][t].V1.Equals(tb[m][t].V1) || !ta[m][t].V2.Equals(tb[m][t].V2)) return false;
            }
            for (int i = 0; i < ia.Length; i++)
                if (ia[i].MeshIndex != ib[i].MeshIndex || !ia[i].LocalToWorld.Equals(ib[i].LocalToWorld)) return false;
            return true;
        }

        static bool SamePacked(ProbeVolumeAsset x, ProbeVolumeAsset y)
        {
            if (x.shPacked.Length != y.shPacked.Length) return false;
            for (int i = 0; i < x.shPacked.Length; i++)   // 성분별 정확 비교(Vector4 == 는 ε 근사)
            {
                Vector4 a = x.shPacked[i], b = y.shPacked[i];
                if (a.x != b.x || a.y != b.y || a.z != b.z || a.w != b.w) return false;
            }
            return true;
        }

        static GameObject Hidden(string name, List<GameObject> roots)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            roots.Add(go);
            return go;
        }

        // 월드(또는 local=true 면 부모 기준) TRS 의 박스 MeshFilter + MeshRenderer(머티리얼 없음 → 기본 알베도)
        static MeshFilter Child(Transform parent, string name, Mesh mesh, Matrix4x4 trs, bool local = false)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(parent, false);
            Vector3 pos = trs.GetColumn(3);
            if (local) { go.transform.localPosition = pos; go.transform.localRotation = trs.rotation; }
            else { go.transform.position = pos; go.transform.rotation = trs.rotation; }
            go.transform.localScale = trs.lossyScale;
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            go.AddComponent<MeshRenderer>();
            return mf;
        }

        static string Names(List<MeshFilter> l)
        {
            var s = new string[l.Count];
            for (int i = 0; i < s.Length; i++) s[i] = l[i] != null ? l[i].name : "∅";
            return string.Join(", ", s);
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
