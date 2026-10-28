using System;
using System.Text;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// C0-0 검증: <see cref="BakeSceneCollectPolicy"/> · <see cref="BakeSceneCollection"/> (개발문서 §8 T1~T8 + 확장).
    ///  T1 순서 보존(C0-V1) · T2 결정성(C0-V1) · T3 배타성(C0-V2) · T4 사유 우선순위 · T5 NotReadable(C0-V4) ·
    ///  T6 LOD(C0-V5) · T7 NonGiAsOccluder(ContributeGI 규칙 제외로 주석 — 2026-10-28) · T8 경고 무제외(WarnOnly).
    ///  T9 판정 순서 전수(§4.3 — 각 규칙이 뒤 규칙 조건을 모두 가진 후보에서 이긴다) ·
    ///  T10 확장 사유(RendererDisabled·NonTriangleTopology·WarnOnly=false) · T11 경고 부착 범위 ·
    ///  T12 Report(§7 형식·조치 문구·상세 절단·결정성) · T13 Pick(병렬 목록 대응·길이 불일치 예외) ·
    ///  T14 SceneMembers(프로브 볼륨 정적 입력 = Receiver ∪ OccluderOnly, Entries 순서 — v15.19).
    /// 순수 함수라 씬·에디터·GPU 불필요(헤드리스). 호출: Debug.Log(BakeSceneCollectPolicyTests.RunAll());
    /// </summary>
    public static class BakeSceneCollectPolicyTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== C0 BakeSceneCollectPolicy (씬 수집·분류) ===");
            int pass = 0, total = 0;
            TestOrder(sb, ref pass, ref total);
            TestDeterminism(sb, ref pass, ref total);
            TestExclusivity(sb, ref pass, ref total);
            TestReasonPriority(sb, ref pass, ref total);
            TestNotReadable(sb, ref pass, ref total);
            TestLod(sb, ref pass, ref total);
            // TestNonGiOption(sb, ref pass, ref total);   // ContributeGI 규칙 제외(2026-10-28)
            TestWarnOnly(sb, ref pass, ref total);
            TestRuleOrder(sb, ref pass, ref total);
            TestExtendedReasons(sb, ref pass, ref total);
            TestWarningScope(sb, ref pass, ref total);
            TestReport(sb, ref pass, ref total);
            TestPick(sb, ref pass, ref total);
            TestSceneMembers(sb, ref pass, ref total);
            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ── 후보 생성기 ──

        /// <summary>모든 조건을 통과하는 정상 receiver 후보.</summary>
        static CollectCandidate Valid(int order, string name = null) => new CollectCandidate
        {
            Order = order,
            Name = name ?? $"Obj_{order}",
            HasMeshFilter = true,
            HasRenderer = true,
            MeshReadable = true,
            VertexCount = 24,
            TriangleCount = 12,
            SubMeshCount = 1,
            HasUv0 = true,
            BoundsExtentSq = 0.75f,
            ContributeGI = true,
        };

        /// <summary>결정적 혼합 씬 — 분류 3종·사유 다수가 섞이게(시드 고정).</summary>
        static CollectCandidate[] Mixed(int n, int seed)
        {
            var rng = new Random(seed);
            var c = new CollectCandidate[n];
            for (int i = 0; i < n; i++)
            {
                var x = Valid(i);
                switch (rng.Next(10))
                {
                    case 0: x.MeshReadable = false; break;
                    case 1: x.IsLodChild = true; break;
                    case 2: x.ContributeGI = false; break;
                    case 3: x.ReceiveGiLightProbes = true; break;
                    case 4: x.UserOccluder = true; break;
                    case 5: x.HasUv0 = false; break;
                    case 6: x.TriangleCount = 0; break;
                }
                c[i] = x;
            }
            return c;
        }

        // ── T1 순서 보존 ──
        static void TestOrder(StringBuilder sb, ref int pass, ref int total)
        {
            var col = BakeSceneCollectPolicy.Run(Mixed(200, 7), BakeSceneCollectOptions.Default);
            bool okOrder = col.Entries.Length == 200;
            for (int i = 0; i < col.Entries.Length; i++) okOrder &= col.Entries[i].Order == i;
            Check(sb, ref pass, ref total, "T1: Entries[i].Order == i (입력 순서 그대로, 정렬 없음)", okOrder);
            Check(sb, ref pass, ref total, "T1: ReceiverIdx · OccluderIdx 엄격 오름차순",
                  StrictlyAscending(col.ReceiverIdx) && StrictlyAscending(col.OccluderIdx));
        }

        // ── T2 결정성 ──
        static void TestDeterminism(StringBuilder sb, ref int pass, ref int total)
        {
            var a = BakeSceneCollectPolicy.Run(Mixed(300, 11), BakeSceneCollectOptions.Default);
            var b = BakeSceneCollectPolicy.Run(Mixed(300, 11), BakeSceneCollectOptions.Default);
            bool same = a.Entries.Length == b.Entries.Length
                     && SameInts(a.ReceiverIdx, b.ReceiverIdx) && SameInts(a.OccluderIdx, b.OccluderIdx)
                     && a.ReceiverCount == b.ReceiverCount && a.OccluderCount == b.OccluderCount && a.ExcludedCount == b.ExcludedCount;
            for (int i = 0; same && i < a.Entries.Length; i++)
            {
                var x = a.Entries[i]; var y = b.Entries[i];
                same &= x.Order == y.Order && x.Name == y.Name && x.Class == y.Class && x.Reason == y.Reason && x.Warnings == y.Warnings;
            }
            Check(sb, ref pass, ref total, "T2: 같은 입력 2회 Run → Entries·인덱스·집계 완전 일치", same);
        }

        // ── T3 배타성 ──
        static void TestExclusivity(StringBuilder sb, ref int pass, ref int total)
        {
            var col = BakeSceneCollectPolicy.Run(Mixed(500, 3), BakeSceneCollectOptions.Default);
            var seen = new bool[col.Entries.Length];
            bool disjoint = true;
            foreach (int i in col.ReceiverIdx) seen[i] = true;
            foreach (int i in col.OccluderIdx) { disjoint &= !seen[i]; seen[i] = true; }
            bool sum = col.ReceiverCount + col.OccluderCount + col.ExcludedCount == col.Entries.Length
                    && col.ReceiverCount == col.ReceiverIdx.Length && col.OccluderCount == col.OccluderIdx.Length;
            bool classMatch = true;
            for (int i = 0; i < col.Entries.Length; i++)
                classMatch &= seen[i] == (col.Entries[i].Class != CollectClass.Excluded);
            // ContributeGI 규칙 제외(2026-10-28) — NotContributeGI 사유가 안 붙어 아래 검사는 항상 참(무의미)
            // bool nonGiNeither = true;
            // foreach (var e in col.Entries)
            //     if (e.Reason == CollectReason.NotContributeGI) nonGiNeither &= e.Class == CollectClass.Excluded;
            Check(sb, ref pass, ref total,
                  $"T3: receiver {col.ReceiverCount} ∩ occluder {col.OccluderCount} = ∅ · 합 + excluded {col.ExcludedCount} = {col.Entries.Length}",
                  disjoint && sum && classMatch);
            // Check(sb, ref pass, ref total, "T3: ContributeGI off(기본 옵션) → receiver·occluder 어느 쪽도 아님", nonGiNeither);
        }

        // ── T4 사유 우선순위 ──
        static void TestReasonPriority(StringBuilder sb, ref int pass, ref int total)
        {
            var c = Valid(0); c.UserExcluded = true; c.MeshReadable = false;
            var cls = BakeSceneCollectPolicy.Classify(c, BakeSceneCollectOptions.Default, out var r);
            Check(sb, ref pass, ref total, "T4: UserExcluded ∧ !MeshReadable → Excluded(UserExcluded)",
                  cls == CollectClass.Excluded && r == CollectReason.UserExcluded);

            c = Valid(0); c.UserOccluder = true; c.MeshReadable = false;
            cls = BakeSceneCollectPolicy.Classify(c, BakeSceneCollectOptions.Default, out r);
            Check(sb, ref pass, ref total, "T4: UserOccluder 도 R/W 미설정은 못 이긴다 → Excluded(NotReadable)",
                  cls == CollectClass.Excluded && r == CollectReason.NotReadable);

            c = Valid(0); c.UserOccluder = true; c.IsLodChild = true; c.ContributeGI = false; c.ReceiveGiLightProbes = true;
            cls = BakeSceneCollectPolicy.Classify(c, BakeSceneCollectOptions.Default, out r);
            Check(sb, ref pass, ref total, "T4: UserOccluder 는 LOD·ContributeGI·LightProbes 자동 판정을 이긴다",
                  cls == CollectClass.OccluderOnly && r == CollectReason.UserOccluder);
        }

        // ── T5 NotReadable ──
        static void TestNotReadable(StringBuilder sb, ref int pass, ref int total)
        {
            var c = Valid(0); c.MeshReadable = false;   // ContributeGI 켜짐
            var cls = BakeSceneCollectPolicy.Classify(c, BakeSceneCollectOptions.Default, out var r);
            var cands = new[] { Valid(0), c, Valid(2) };
            cands[1].Order = 1;
            var col = BakeSceneCollectPolicy.Run(cands, BakeSceneCollectOptions.Default);
            bool inNeither = Array.IndexOf(col.ReceiverIdx, 1) < 0 && Array.IndexOf(col.OccluderIdx, 1) < 0;
            Check(sb, ref pass, ref total, "T5: readable=false(ContributeGI on) → Excluded(NotReadable) · receiver·occluder 양쪽에서 빠짐",
                  cls == CollectClass.Excluded && r == CollectReason.NotReadable && inNeither && col.ReceiverCount == 2);
        }

        // ── T6 LOD ──
        static void TestLod(StringBuilder sb, ref int pass, ref int total)
        {
            var c = Valid(0); c.IsLodChild = true;
            var clsDef = BakeSceneCollectPolicy.Classify(c, BakeSceneCollectOptions.Default, out var rDef);
            var opt = BakeSceneCollectOptions.Default; opt.LodChildAsOccluder = true;
            var clsOcc = BakeSceneCollectPolicy.Classify(c, opt, out var rOcc);
            Check(sb, ref pass, ref total, "T6: IsLodChild → 기본 Excluded(LodChild) · LodChildAsOccluder 면 OccluderOnly(LodChild)",
                  clsDef == CollectClass.Excluded && rDef == CollectReason.LodChild
                  && clsOcc == CollectClass.OccluderOnly && rOcc == CollectReason.LodChild);
        }

        // ── T7 NonGiAsOccluder ── ContributeGI 규칙 제외(2026-10-28)로 주석
        // static void TestNonGiOption(StringBuilder sb, ref int pass, ref int total)
        // {
        //     var c = Valid(0); c.ContributeGI = false;
        //     var clsOff = BakeSceneCollectPolicy.Classify(c, BakeSceneCollectOptions.Default, out var rOff);
        //     var opt = BakeSceneCollectOptions.Default; opt.NonGiAsOccluder = true;
        //     var clsOn = BakeSceneCollectPolicy.Classify(c, opt, out var rOn);
        //     Check(sb, ref pass, ref total, "T7: !ContributeGI → NonGiAsOccluder off=Excluded / on=OccluderOnly (사유 NotContributeGI)",
        //           clsOff == CollectClass.Excluded && clsOn == CollectClass.OccluderOnly
        //           && rOff == CollectReason.NotContributeGI && rOn == CollectReason.NotContributeGI);
        // }

        // ── T8 경고 무제외 ──
        static void TestWarnOnly(StringBuilder sb, ref int pass, ref int total)
        {
            var c = Valid(0); c.HasUv0 = false;
            var col = BakeSceneCollectPolicy.Run(new[] { c }, BakeSceneCollectOptions.Default);
            var e = col.Entries[0];
            Check(sb, ref pass, ref total, "T8: !HasUv0 정상 후보 → Receiver 유지 + 경고 NoUv0 (WarnOnly 기본)",
                  e.Class == CollectClass.Receiver && e.Reason == CollectReason.None && e.Warnings == CollectWarning.NoUv0);
        }

        // ── T9 판정 순서 전수(§4.3) ──
        // 규칙 k 의 조건 + 뒤 규칙들의 조건을 모두 켠 후보 → 사유는 규칙 k. 앞 규칙은 꺼져 있어야 한다.
        static void TestRuleOrder(StringBuilder sb, ref int pass, ref int total)
        {
            var rules = new (CollectReason reason, CollectClass cls, Action<CollectCandidate[]> set)[]
            {
                (CollectReason.UserExcluded,        CollectClass.Excluded,     a => a[0].UserExcluded = true),
                (CollectReason.NoMeshFilter,        CollectClass.Excluded,     a => a[0].HasMeshFilter = false),
                (CollectReason.NoMesh,              CollectClass.Excluded,     a => a[0].MeshNull = true),
                (CollectReason.NotReadable,         CollectClass.Excluded,     a => a[0].MeshReadable = false),
                (CollectReason.NoRenderer,          CollectClass.Excluded,     a => a[0].HasRenderer = false),
                (CollectReason.RendererDisabled,    CollectClass.Excluded,     a => a[0].RendererDisabled = true),
                (CollectReason.NonTriangleTopology, CollectClass.Excluded,     a => a[0].NonTriangleTopology = true),
                (CollectReason.NoTriangles,         CollectClass.Excluded,     a => a[0].TriangleCount = 0),
                (CollectReason.EmptyBounds,         CollectClass.Excluded,     a => a[0].BoundsExtentSq = 0f),
                (CollectReason.UserOccluder,        CollectClass.OccluderOnly, a => a[0].UserOccluder = true),
                (CollectReason.LodChild,            CollectClass.Excluded,     a => a[0].IsLodChild = true),
                // (CollectReason.NotContributeGI,     CollectClass.Excluded,     a => a[0].ContributeGI = false),   // ContributeGI 규칙 제외(2026-10-28)
                (CollectReason.ReceiveGiLightProbes,CollectClass.OccluderOnly, a => a[0].ReceiveGiLightProbes = true),
            };
            bool ok = true;
            string firstFail = null;
            for (int k = 0; k < rules.Length; k++)
            {
                var box = new[] { Valid(0) };
                for (int j = k; j < rules.Length; j++) rules[j].set(box);
                var cls = BakeSceneCollectPolicy.Classify(box[0], BakeSceneCollectOptions.Default, out var r);
                bool hit = cls == rules[k].cls && r == rules[k].reason;
                if (!hit && firstFail == null) firstFail = $"{rules[k].reason} → {cls}({r})";
                ok &= hit;
            }
            var none = BakeSceneCollectPolicy.Classify(Valid(0), BakeSceneCollectOptions.Default, out var rn);
            ok &= none == CollectClass.Receiver && rn == CollectReason.None;
            Check(sb, ref pass, ref total, $"T9: 판정 순서 {rules.Length}규칙 + 기본 Receiver — 앞 규칙이 항상 사유" +
                  (firstFail != null ? $" (첫 불일치 {firstFail})" : ""), ok);
        }

        // ── T10 확장 사유 ──
        static void TestExtendedReasons(StringBuilder sb, ref int pass, ref int total)
        {
            var strict = BakeSceneCollectOptions.Default; strict.WarnOnly = false;

            var c = Valid(0); c.HasUv0 = false;
            var clsUv = BakeSceneCollectPolicy.Classify(c, strict, out var rUv);
            c = Valid(0); c.SubMeshCount = 256;
            var clsSub = BakeSceneCollectPolicy.Classify(c, strict, out var rSub);
            var clsSubWarn = BakeSceneCollectPolicy.Classify(c, BakeSceneCollectOptions.Default, out _);
            Check(sb, ref pass, ref total, "T10: WarnOnly=false → !HasUv0 Excluded(NoUv0) · 서브메시 256 Excluded(TooManySubMeshes) · WarnOnly=true 면 Receiver",
                  clsUv == CollectClass.Excluded && rUv == CollectReason.NoUv0
                  && clsSub == CollectClass.Excluded && rSub == CollectReason.TooManySubMeshes
                  && clsSubWarn == CollectClass.Receiver);

            c = Valid(0); c.SubMeshCount = 255;
            var cls255 = BakeSceneCollectPolicy.Classify(c, strict, out _);
            c = Valid(0); c.BoundsExtentSq = float.NaN;
            var clsNaN = BakeSceneCollectPolicy.Classify(c, BakeSceneCollectOptions.Default, out var rNaN);
            Check(sb, ref pass, ref total, "T10: 서브메시 255 는 한도 이내(Receiver) · bounds NaN → Excluded(EmptyBounds)",
                  cls255 == CollectClass.Receiver && clsNaN == CollectClass.Excluded && rNaN == CollectReason.EmptyBounds);

            var raw = new BakeSceneCollectOptions();   // WarnOnly=false — Default 를 안 쓰면 경고 항목까지 제외됨(문서화된 함정)
            c = Valid(0); c.HasUv0 = false;
            var clsRaw = BakeSceneCollectPolicy.Classify(c, raw, out _);
            Check(sb, ref pass, ref total, "T10: new BakeSceneCollectOptions() 는 WarnOnly=false (Default 와 다름 — 주석 계약)",
                  BakeSceneCollectOptions.Default.WarnOnly && !raw.WarnOnly && clsRaw == CollectClass.Excluded);
        }

        // ── T11 경고 부착 범위 ──
        static void TestWarningScope(StringBuilder sb, ref int pass, ref int total)
        {
            var a = Valid(0); a.HasUv0 = false; a.UserOccluder = true;                    // occluder + 경고
            var b = Valid(1); b.HasUv0 = false; b.MeshReadable = false;                   // excluded → 경고 없음
            var c = Valid(2); c.HasUv0 = false; c.SubMeshCount = 300;                     // receiver + 경고 2종
            var col = BakeSceneCollectPolicy.Run(new[] { a, b, c }, BakeSceneCollectOptions.Default);
            Check(sb, ref pass, ref total, "T11: 경고는 베이크에 들어가는 항목에만 — occluder NoUv0 · excluded None · receiver NoUv0|TooManySubMeshes",
                  col.Entries[0].Class == CollectClass.OccluderOnly && col.Entries[0].Warnings == CollectWarning.NoUv0
                  && col.Entries[1].Class == CollectClass.Excluded && col.Entries[1].Warnings == CollectWarning.None
                  && col.Entries[2].Class == CollectClass.Receiver
                  && col.Entries[2].Warnings == (CollectWarning.NoUv0 | CollectWarning.TooManySubMeshes));
        }

        // ── T12 Report ──
        static void TestReport(StringBuilder sb, ref int pass, ref int total)
        {
            var list = new System.Collections.Generic.List<CollectCandidate>();
            int o = 0;
            for (int i = 0; i < 5; i++) list.Add(Valid(o++, $"Recv_{i}"));
            for (int i = 0; i < 3; i++) { var x = Valid(o++, $"Probe_{i}"); x.ReceiveGiLightProbes = true; list.Add(x); }
            for (int i = 0; i < 25; i++) { var x = Valid(o++, $"Fence_{i:00}"); x.MeshReadable = false; list.Add(x); }
            { var x = Valid(o++, "Rock_01"); x.HasUv0 = false; list.Add(x); }
            { var x = Valid(o++, "Tree_LOD1"); x.IsLodChild = true; list.Add(x); }
            var col = BakeSceneCollectPolicy.Run(list.ToArray(), BakeSceneCollectOptions.Default);
            col.RootCount = 4; col.ElapsedMs = 21;
            string rep = col.Report(maxDetail: 20);

            bool head = rep.StartsWith("[C0] 수집 완료 — receiver 6 · occluder 3 · excluded 26") && rep.Contains("(roots 4, 순회 35건, 21ms)");
            int iNr = rep.IndexOf("NotReadable", StringComparison.Ordinal);
            int iPr = rep.IndexOf("ReceiveGiLightProbes", StringComparison.Ordinal);
            int iLod = rep.IndexOf("LodChild", StringComparison.Ordinal);
            bool sorted = iNr >= 0 && iPr > iNr && iLod > iPr;                       // 25 > 3 > 1 건수 내림차순
            bool action = rep.Contains("임포터 Read/Write Enabled 를 켜세요") && rep.Contains("← 아틀라스 슬롯만 먹고 차폐에서 빠지던 케이스");
            bool warn = rep.Contains("UV0 없음 1건") && rep.Contains("Rock_01");
            bool detail = rep.Contains("상세(NotReadable):") && rep.Contains("Fence_19") && !rep.Contains("Fence_20") && rep.Contains("Fence_19, …")
                       && rep.Contains("상세(LodChild):") && !rep.Contains("상세(ReceiveGiLightProbes)");   // 상세는 Excluded 만
            bool determ = rep == col.Report(maxDetail: 20);
            Check(sb, ref pass, ref total, "T12: Report 머리줄(분류 수·roots·순회·ms) · 사유 건수 내림차순", head && sorted);
            Check(sb, ref pass, ref total, "T12: NotReadable 조치 문구 · UV0 경고 · 상세 절단(20건 + …) · 상세는 Excluded 만 · 결정적",
                  action && warn && detail && determ);
            if (!(head && sorted && action && warn && detail && determ)) sb.AppendLine(rep);
        }

        // ── T13 Pick ──
        static void TestPick(StringBuilder sb, ref int pass, ref int total)
        {
            var cands = Mixed(40, 5);
            var names = new string[cands.Length];
            for (int i = 0; i < names.Length; i++) names[i] = cands[i].Name;
            var col = BakeSceneCollectPolicy.Run(cands, BakeSceneCollectOptions.Default);
            var rec = BakeSceneCollectPolicy.Pick(col, names, col.ReceiverIdx);
            bool ok = rec.Length == col.ReceiverCount;
            for (int i = 0; ok && i < rec.Length; i++) ok &= rec[i] == col.Entries[col.ReceiverIdx[i]].Name;
            bool threw = false;
            try { BakeSceneCollectPolicy.Pick(col, new string[cands.Length - 1], col.ReceiverIdx); }
            catch (ArgumentException) { threw = true; }
            Check(sb, ref pass, ref total, "T13: Pick = 병렬 목록에서 인덱스 순서대로 · 길이 불일치 → ArgumentException", ok && threw);
        }

        // ── T14 SceneMembers ──
        static void TestSceneMembers(StringBuilder sb, ref int pass, ref int total)
        {
            var col = BakeSceneCollectPolicy.Run(Mixed(200, 9), BakeSceneCollectOptions.Default);
            var m = BakeSceneCollectPolicy.SceneMembers(col);

            // 집합 = Receiver ∪ OccluderOnly(서로소) · 순서 = Entries 오름차순(두 목록을 이어 붙인 것이 아님)
            bool count = m.Length == col.ReceiverCount + col.OccluderCount && col.ReceiverCount > 0 && col.OccluderCount > 0;
            bool asc = StrictlyAscending(m);
            bool member = true;
            foreach (int i in m) member &= col.Entries[i].Class != CollectClass.Excluded;
            var merged = new int[col.ReceiverIdx.Length + col.OccluderIdx.Length];
            col.ReceiverIdx.CopyTo(merged, 0);
            col.OccluderIdx.CopyTo(merged, col.ReceiverIdx.Length);
            Array.Sort(merged);
            bool same = SameInts(m, merged);
            // 섞여 있어야 '이어 붙이기'와 구별된다 — occluder 가 receiver 보다 앞선 곳이 1개 이상
            bool interleaved = col.OccluderIdx.Length > 0 && col.ReceiverIdx.Length > 0 && col.OccluderIdx[0] < col.ReceiverIdx[col.ReceiverIdx.Length - 1];

            var empty = BakeSceneCollectPolicy.SceneMembers(BakeSceneCollectPolicy.Run(null, BakeSceneCollectOptions.Default));
            bool threw = false;
            try { BakeSceneCollectPolicy.SceneMembers(null); }
            catch (ArgumentNullException) { threw = true; }
            Check(sb, ref pass, ref total,
                  $"T14: SceneMembers = Receiver {col.ReceiverCount} ∪ Occluder {col.OccluderCount} = {m.Length} · Entries 오름차순(섞인 순서 유지) · Excluded 없음 · 빈 입력 0 · null → 예외",
                  count && asc && member && same && interleaved && empty.Length == 0 && threw);
        }

        // ── 유틸 ──
        static bool StrictlyAscending(int[] a)
        {
            for (int i = 1; i < a.Length; i++) if (a[i] <= a[i - 1]) return false;
            return true;
        }

        static bool SameInts(int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
