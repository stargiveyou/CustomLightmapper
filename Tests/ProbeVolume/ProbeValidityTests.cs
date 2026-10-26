using System.Text;
using Unity.Collections;
using UnityEngine;
using static HuskyLibs.CustomLightmapper.Bake.ProbeGridTests;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Track B B1-2 검증: <see cref="BurstProbeValidity"/> + <see cref="ProbeGrid.Sample(SH9[], bool[], Vector3)"/> valid 가중.
    ///  A) 닫힌 박스(외향 와인딩) + 안팎 걸친 10³ 격자: 내부 64개 r≈1·무효 / 외부 r=0·유효 — 분류 ≡ 기하 내부 판정.
    ///  B) Unity 큐브 프리미티브(실 와인딩) · 회전+비균등 스케일 인스턴스 · 미러(음수 스케일) 인스턴스: 내부 무효 / 외부 유효
    ///     (역전치 노멀 행렬이 외향을 보존하는지).
    ///  C) 단면 지면 플레인: 아래 프로브 r=0.5 → 무효, countBackface=false 면 r=0 → 유효 · 위 프로브 r=0.
    ///  D) Dilation: 단일 무효 = 6-이웃 평균 · 3³ 블록(링 2개: 중심 = 링1 이웃 평균, 링1 꼭짓점 = 유효 3-이웃 평균,
    ///     maxIterations=1 → 26개만) · 유효 프로브·valid 마스크 불변 · 전부 무효 → 0개.
    ///  E) valid 가중 샘플: 무효 탭(쓰레기 1e6) 무시 — 축 위 점 = 유효 이웃 값, 8탭 결과 ∈ [유효 탭 min, max],
    ///     8탭 전부 무효 → 무가중 폴백.
    ///  F) 파이프라인(지면 아래 층): 베이크 → 유효성 → dilation → 지면 아래 층 = 위 층(비트 동일) ·
    ///     지면 높이 샘플(valid 가중) = 위 층 값 — 누수 차단.
    /// 결정적. 호출: Debug.Log(ProbeValidityTests.RunAll());
    /// </summary>
    public static class ProbeValidityTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Track B ProbeValidity (B1-2) ===");
            int pass = 0, total = 0;
            TestClosedBox(sb, ref pass, ref total);
            TestUnityCube(sb, ref pass, ref total);
            TestSingleSidedPlane(sb, ref pass, ref total);
            TestDilation(sb, ref pass, ref total);
            TestValidWeightedSample(sb, ref pass, ref total);
            TestPipeline(sb, ref pass, ref total);
            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ── A) 닫힌 박스 ──
        static void TestClosedBox(StringBuilder sb, ref int pass, ref int total)
        {
            var meshes = new[] { OutwardBox(Vector3.zero, 2f) };   // 면 ±1
            var insts = new[] { new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = Matrix4x4.identity } };
            using var bvh = new TwoLevelBVH(meshes, insts, Allocator.TempJob, BVH.BuildQuality.SAH);
            using var scene = BurstScene.Create(bvh, null, Allocator.TempJob);   // 알베도 배열 생성(잡 스케줄 시 컨테이너 전부 유효)
            // -2.25..2.25 간격 0.5 → 면(±1)에 걸치는 프로브 없음, 축당 내부 4개 → 64
            var g = new ProbeGrid(new Vector3(-2.25f, -2.25f, -2.25f), Vector3.one * 0.5f, new Vector3Int(10, 10, 10));
            var valid = BurstProbeValidity.ComputeGrid(scene, g, out var ratio);

            int inside = 0, insideInvalid = 0, outsideValid = 0, mismatch = 0;
            float minIn = 1f, maxOut = 0f;
            for (int i = 0; i < g.Count; i++)
            {
                Vector3 p = g.Position(i);
                bool isIn = Mathf.Abs(p.x) < 1f && Mathf.Abs(p.y) < 1f && Mathf.Abs(p.z) < 1f;
                if (isIn) { inside++; minIn = Mathf.Min(minIn, ratio[i]); if (!valid[i]) insideInvalid++; }
                else { maxOut = Mathf.Max(maxOut, ratio[i]); if (valid[i]) outsideValid++; }
                if (valid[i] == isIn) mismatch++;
            }
            Check(sb, ref pass, ref total, $"A: 닫힌 박스 내부 {insideInvalid}/{inside}(64) 무효 · min r={minIn:0.000} (≈1)", inside == 64 && insideInvalid == 64 && minIn > 0.9f);
            Check(sb, ref pass, ref total, $"A: 외부 {outsideValid}/{g.Count - inside} 유효 · max r={maxOut:0.000} (≈0) · 분류 불일치 {mismatch}", mismatch == 0 && maxOut < 0.05f);
        }

        // ── B) Unity 큐브 프리미티브 · 변환·미러 인스턴스 ──
        static void TestUnityCube(StringBuilder sb, ref int pass, ref int total)
        {
            var cubeMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");   // 프리미티브 큐브와 같은 내장 메시(에셋 — 파괴 금지)
            if (cubeMesh == null) { Check(sb, ref pass, ref total, "B: 내장 Cube 메시 로드 실패", false); return; }
            if (!cubeMesh.isReadable) { sb.AppendLine("  [SKIP] B: 내장 Cube 메시 Read/Write 불가"); return; }
            var cubeTris = TemplateInstanceSource.MeshToLocalTris(cubeMesh);   // 로컬 ±0.5

            var m0 = Matrix4x4.TRS(new Vector3(-3f, 0.5f, 0f), Quaternion.Euler(20f, 35f, 10f), new Vector3(2f, 1.5f, 1f));
            var m1 = Matrix4x4.TRS(new Vector3(3f, -0.5f, 1f), Quaternion.Euler(-15f, 60f, 5f), new Vector3(-2f, 1.5f, 1f));   // 미러(det<0)
            var meshes = new[] { cubeTris };
            var insts = new[]
            {
                new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = m0 },
                new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = m1 },
            };
            using var bvh = new TwoLevelBVH(meshes, insts, Allocator.TempJob, BVH.BuildQuality.SAH);
            using var scene = BurstScene.Create(bvh, null, Allocator.TempJob);

            var localIn = new[] { Vector3.zero, new Vector3(0.3f, 0.2f, -0.3f), new Vector3(-0.3f, -0.35f, 0.3f) };
            var localOut = new[] { new Vector3(0.9f, 0f, 0f), new Vector3(0f, -0.9f, 0f), new Vector3(0f, 0.1f, 0.95f) };
            var pts = new Vector3[12];
            for (int i = 0; i < 3; i++)
            {
                pts[i] = m0.MultiplyPoint3x4(localIn[i]);
                pts[3 + i] = m0.MultiplyPoint3x4(localOut[i]);
                pts[6 + i] = m1.MultiplyPoint3x4(localIn[i]);
                pts[9 + i] = m1.MultiplyPoint3x4(localOut[i]);
            }
            var ratio = BurstProbeValidity.BackfaceRatio(scene, pts);
            var valid = BurstProbeValidity.Classify(ratio);

            bool ok0 = true, ok1 = true;
            float minIn = 1f, maxOut = 0f;
            for (int i = 0; i < 3; i++)
            {
                ok0 &= !valid[i] && valid[3 + i];
                ok1 &= !valid[6 + i] && valid[9 + i];
                minIn = Mathf.Min(minIn, Mathf.Min(ratio[i], ratio[6 + i]));
                maxOut = Mathf.Max(maxOut, Mathf.Max(ratio[3 + i], ratio[9 + i]));
            }
            Check(sb, ref pass, ref total, $"B: Unity 큐브(실 와인딩) 회전·비균등 스케일 — 내부 무효/외부 유효 (min r_in={minIn:0.000}, max r_out={maxOut:0.000})", ok0);
            Check(sb, ref pass, ref total, "B: 미러(음수 스케일) 인스턴스도 내부 무효/외부 유효 — 역전치 노멀 외향 보존", ok1);
        }

        // ── C) 단면 지면 플레인 ──
        static void TestSingleSidedPlane(StringBuilder sb, ref int pass, ref int total)
        {
            var meshes = new[] { Quad(1000f, 0f) };   // 노멀 +Y
            var insts = new[] { new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = Matrix4x4.identity } };
            using var bvh = new TwoLevelBVH(meshes, insts, Allocator.TempJob, BVH.BuildQuality.SAH);
            using var scene = BurstScene.Create(bvh, null, Allocator.TempJob);
            var pts = new[] { new Vector3(0.3f, -1f, -0.7f), new Vector3(0.3f, 1f, -0.7f) };   // 쿼드 대각선(x=z) 밖

            var r = BurstProbeValidity.BackfaceRatio(scene, pts);
            var v = BurstProbeValidity.Classify(r);
            Check(sb, ref pass, ref total, $"C: 지면 아래 r={r[0]:0.000} (=0.5, 위쪽 절반이 뒷면) → 무효 · 위 r={r[1]:0.000} → 유효",
                Mathf.Abs(r[0] - 0.5f) < 1e-6f && !v[0] && r[1] == 0f && v[1]);

            var r2 = BurstProbeValidity.BackfaceRatio(scene, pts, countBackfacePerMesh: new[] { false });
            Check(sb, ref pass, ref total, $"C: countBackface=false(양면 취급) → 아래 r={r2[0]:0.000} → 유효", r2[0] == 0f && r2[1] == 0f);
        }

        // ── D) Dilation ──
        static void TestDilation(StringBuilder sb, ref int pass, ref int total)
        {
            var rng = new System.Random(11);
            var g = new ProbeGrid(Vector3.zero, Vector3.one, new Vector3Int(5, 5, 5));
            var orig = new SH9[g.Count];
            for (int i = 0; i < orig.Length; i++)
                for (int k = 0; k < SH9.Count; k++)
                    SetCoeff(ref orig[i], k, new Vector3(Lerp(rng, 0f, 2f), Lerp(rng, 0f, 2f), Lerp(rng, -1f, 1f)));

            // D1) 단일 무효(중심)
            {
                var sh = (SH9[])orig.Clone();
                var valid = AllTrue(g.Count);
                int c = g.Index(2, 2, 2);
                valid[c] = false;
                int filled = BurstProbeValidity.Dilate(g, sh, valid);
                var mean = Mean(orig, g.Index(1, 2, 2), g.Index(3, 2, 2), g.Index(2, 1, 2), g.Index(2, 3, 2), g.Index(2, 2, 1), g.Index(2, 2, 3));
                Check(sb, ref pass, ref total, $"D: 단일 무효 → 채움 {filled}(1) = 6-이웃 평균 (Δc={MaxCoeffDiff(sh[c], mean):0.0e0}) · 나머지 불변",
                    filled == 1 && MaxCoeffDiff(sh[c], mean) < 1e-6f && UnchangedExcept(orig, sh, valid) && !valid[c]);
            }

            // D2) 3³ 블록(1..3) 무효 — 링 1: 블록 표면 26개, 링 2: 중심
            {
                var sh = (SH9[])orig.Clone();
                var valid = AllTrue(g.Count);
                for (int k = 1; k <= 3; k++) for (int j = 1; j <= 3; j++) for (int i = 1; i <= 3; i++) valid[g.Index(i, j, k)] = false;
                var validCopy = (bool[])valid.Clone();
                int filled = BurstProbeValidity.Dilate(g, sh, valid);

                int c = g.Index(2, 2, 2);
                var ring2 = Mean(sh, g.Index(1, 2, 2), g.Index(3, 2, 2), g.Index(2, 1, 2), g.Index(2, 3, 2), g.Index(2, 2, 1), g.Index(2, 2, 3));
                var corner = Mean(orig, g.Index(0, 1, 1), g.Index(1, 0, 1), g.Index(1, 1, 0));
                bool maskSame = true;
                for (int i = 0; i < valid.Length; i++) maskSame &= valid[i] == validCopy[i];
                Check(sb, ref pass, ref total, $"D: 3³ 블록 → 채움 {filled}(27) · 중심 = 링1 이웃 평균 (Δc={MaxCoeffDiff(sh[c], ring2):0.0e0})",
                    filled == 27 && MaxCoeffDiff(sh[c], ring2) < 1e-6f);
                Check(sb, ref pass, ref total, $"D: 블록 꼭짓점(1,1,1) = 유효 3-이웃 평균 (Δc={MaxCoeffDiff(sh[g.Index(1, 1, 1)], corner):0.0e0}) · 유효 프로브·valid 마스크 불변",
                    MaxCoeffDiff(sh[g.Index(1, 1, 1)], corner) < 1e-6f && UnchangedExcept(orig, sh, valid) && maskSame);

                var sh1 = (SH9[])orig.Clone();
                int filled1 = BurstProbeValidity.Dilate(g, sh1, valid, maxIterations: 1);
                Check(sb, ref pass, ref total, $"D: maxIterations=1 → 링1만 {filled1}(26), 중심 원래 값",
                    filled1 == 26 && MaxCoeffDiff(sh1[c], orig[c]) == 0f);
            }

            // D3) 전부 무효
            {
                var sh = (SH9[])orig.Clone();
                var valid = new bool[g.Count];
                int filled = BurstProbeValidity.Dilate(g, sh, valid);
                bool same = true;
                for (int i = 0; i < sh.Length; i++) same &= MaxCoeffDiff(sh[i], orig[i]) == 0f;
                Check(sb, ref pass, ref total, $"D: 전부 무효 → 채움 {filled}(0), 값 불변", filled == 0 && same);
            }
        }

        // ── E) valid 가중 샘플 ──
        static void TestValidWeightedSample(StringBuilder sb, ref int pass, ref int total)
        {
            var rng = new System.Random(5);
            var g = new ProbeGrid(Vector3.zero, Vector3.one, new Vector3Int(4, 4, 4));
            var sh = Fill(g, new LinearField(rng));
            var valid = AllTrue(g.Count);
            int bad = g.Index(1, 1, 1);
            valid[bad] = false;
            var garbage = new SH9();
            for (int k = 0; k < SH9.Count; k++) SetCoeff(ref garbage, k, Vector3.one * 1e6f);
            sh[bad] = garbage;

            // 축 위 점: 탭 2개((1,1,1) 무효 0.7, (2,1,1) 유효 0.3) → 유효 이웃 값
            var pAxis = g.Position(1, 1, 1) + new Vector3(0.3f, 0f, 0f);
            float dAxis = MaxCoeffDiff(g.Sample(sh, valid, pAxis), sh[g.Index(2, 1, 1)]);
            float contaminated = MaxAbs(g.Sample(sh, pAxis).c0);
            Check(sb, ref pass, ref total, $"E: 축 위 점 → 유효 이웃 값 (Δc={dAxis:0.0e0}) · 무마스크면 오염 |c0|={contaminated:0.0e0}",
                dAxis < 1e-4f && contaminated > 1e5f);

            // 8탭(1개 무효) → 유효 탭의 볼록 결합
            var p8 = g.Position(1, 1, 1) + new Vector3(0.4f, 0.35f, 0.6f);
            var s8 = g.Sample(sh, valid, p8);
            bool inHull = true;
            for (int k = 0; k < SH9.Count; k++)
            {
                Vector3 lo = Vector3.one * float.MaxValue, hi = Vector3.one * float.MinValue;
                for (int c = 0; c < 8; c++)
                {
                    int idx = g.Index(1 + (c & 1), 1 + ((c >> 1) & 1), 1 + ((c >> 2) & 1));
                    if (!valid[idx]) continue;
                    lo = Vector3.Min(lo, Coeff(sh[idx], k)); hi = Vector3.Max(hi, Coeff(sh[idx], k));
                }
                Vector3 v = Coeff(s8, k);
                inHull &= v.x >= lo.x - 1e-4f && v.x <= hi.x + 1e-4f && v.y >= lo.y - 1e-4f && v.y <= hi.y + 1e-4f && v.z >= lo.z - 1e-4f && v.z <= hi.z + 1e-4f;
            }
            Check(sb, ref pass, ref total, "E: 8탭(1개 무효) 결과 ∈ [유효 탭 min, max] — 무효 값 미반영", inHull);

            // 8탭 전부 무효 → 무가중 폴백(= 무마스크 샘플)
            var none = AllTrue(g.Count);
            for (int c = 0; c < 8; c++) none[g.Index(1 + (c & 1), 1 + ((c >> 1) & 1), 1 + ((c >> 2) & 1))] = false;
            float dFallback = MaxCoeffDiff(g.Sample(sh, none, p8), g.Sample(sh, p8));
            Check(sb, ref pass, ref total, $"E: 8탭 전부 무효 → 무가중 trilinear 폴백 (Δc={dFallback})", dFallback == 0f);
        }

        // ── F) 파이프라인: 지면 아래 층 ──
        static void TestPipeline(StringBuilder sb, ref int pass, ref int total)
        {
            var meshes = new[] { Quad(50f, 0f) };   // 단면 지면(노멀 +Y)
            var albedo = new[] { new Vector3(0.6f, 0.6f, 0.6f) };
            var insts = new[] { new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = Matrix4x4.identity } };
            using var bvh = new TwoLevelBVH(meshes, insts, Allocator.TempJob, BVH.BuildQuality.SAH);
            using var scene = BurstScene.Create(bvh, albedo, Allocator.TempJob);
            var sun = new DirectionalLight { Direction = new Vector3(-0.3f, -1f, -0.2f).normalized, Color = Vector3.one, Intensity = 1.2f };
            var sky = BurstSky.Gradient(new Vector3(0.5f, 0.7f, 1.0f), new Vector3(0.1f, 0.1f, 0.12f));
            // 층 y = -0.5(지면 아래), 0.5, 1.5. x≠z 라 쿼드 대각선 위 프로브 없음
            var g = new ProbeGrid(new Vector3(-1.1f, -0.5f, -0.9f), Vector3.one, new Vector3Int(3, 3, 3));
            var q = new BakeQualitySettings { MaxBounces = 1, RRStartDepth = 3, RayBias = 1e-4f };
            var sh = g.BakeBurst(scene, sky, sun, q, 256, 1u);

            var valid = BurstProbeValidity.ComputeGrid(scene, g, out var ratio);
            bool layerOk = true; int invalid = 0;
            for (int i = 0; i < g.Count; i++)
            {
                bool below = g.Coord(i).y == 0;
                layerOk &= valid[i] != below;
                if (!valid[i]) invalid++;
            }
            Check(sb, ref pass, ref total, $"F: 유효성 — 지면 아래 층만 무효 ({invalid}/9)", layerOk && invalid == 9);

            // dilation 전: 아래 층은 지면 뒷면을 보고 틀린 값(위 층과 다름)
            float before = 0f;
            for (int k = 0; k < 3; k++) for (int i = 0; i < 3; i++)
                before = Mathf.Max(before, MaxAbs(sh[g.Index(i, 0, k)].Evaluate(Vector3.up) - sh[g.Index(i, 1, k)].Evaluate(Vector3.up)));
            Vector3 pGround = new Vector3(0.3f, 0f, -0.4f), pAbove = new Vector3(0.3f, 0.5f, -0.4f);
            float leakBefore = MaxAbs(g.Sample(sh, pGround).Evaluate(Vector3.up) - g.Sample(sh, pAbove).Evaluate(Vector3.up));
            float maskedBefore = MaxCoeffDiff(g.Sample(sh, valid, pGround), g.Sample(sh, pAbove));

            int filled = BurstProbeValidity.Dilate(g, sh, valid);
            float after = 0f;
            for (int k = 0; k < 3; k++) for (int i = 0; i < 3; i++)
                after = Mathf.Max(after, MaxCoeffDiff(sh[g.Index(i, 0, k)], sh[g.Index(i, 1, k)]));
            Check(sb, ref pass, ref total, $"F: dilation 채움 {filled}(9) · 아래 층 = 위 층 비트 동일 (전 ΔE(up)={before:0.000} → 후 Δc={after})",
                filled == 9 && after == 0f && before > 1e-3f);

            float maskedAfter = MaxCoeffDiff(g.Sample(sh, valid, pGround), g.Sample(sh, pAbove));
            float plainAfter = MaxCoeffDiff(g.Sample(sh, pGround), g.Sample(sh, pAbove));
            Check(sb, ref pass, ref total, $"F: 지면 높이 샘플 = 위 층 값 — valid 가중(전 {maskedBefore:0.0e0}/후 {maskedAfter:0.0e0}) · dilation 후 무가중 {plainAfter:0.0e0} (무처리 누수 ΔE={leakBefore:0.000})",
                maskedBefore < 1e-5f && maskedAfter < 1e-5f && plainAfter < 1e-5f);
        }

        // ───────────────────────── 헬퍼 ─────────────────────────

        // LightmapEvaluateTests.MakeBox 는 와인딩이 섞여 있음 → 면 노멀이 중심 바깥을 향하도록 정렬(Unity 메시와 같은 외향 규약).
        static Tri[] OutwardBox(Vector3 center, float size)
        {
            var t = LightmapEvaluateTests.MakeBox(center, size);
            for (int i = 0; i < t.Length; i++)
            {
                Vector3 n = Vector3.Cross(t[i].V1 - t[i].V0, t[i].V2 - t[i].V0);
                Vector3 ct = (t[i].V0 + t[i].V1 + t[i].V2) / 3f;
                if (Vector3.Dot(n, ct - center) < 0f) { var tmp = t[i].V1; t[i].V1 = t[i].V2; t[i].V2 = tmp; }
            }
            return t;
        }

        static bool[] AllTrue(int n)
        {
            var v = new bool[n];
            for (int i = 0; i < n; i++) v[i] = true;
            return v;
        }

        static SH9 Mean(SH9[] sh, params int[] idx)
        {
            var m = new SH9();
            foreach (int i in idx) ProbeGrid.AddScaled(ref m, sh[i], 1f);
            ProbeGrid.Scale(ref m, 1f / idx.Length);
            return m;
        }

        // valid 프로브는 비트 동일하게 그대로인지
        static bool UnchangedExcept(SH9[] orig, SH9[] sh, bool[] valid)
        {
            for (int i = 0; i < sh.Length; i++)
                if (valid[i] && MaxCoeffDiff(orig[i], sh[i]) != 0f) return false;
            return true;
        }
    }
}
