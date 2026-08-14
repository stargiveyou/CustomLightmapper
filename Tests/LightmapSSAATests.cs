using System.Text;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// SSAA(텍셀 내부 슈퍼샘플링) 자체테스트 — 레이 없이 순수 기하/로직만 검증한다.
    ///  - SubOffset          : 텍셀 안(0..1)에 들어오는가, 두 축 모두 S² 단계로 흩어지는가
    ///  - MapSubsamples      : 마스크 준수, 슬롯 매핑 왕복, 서브샘플이 자기 텍셀 안에 복원되는가
    ///  - DetectEdges        : 라이팅 계단만 잡는가, 노멀/거리 게이팅이 정상 불연속을 걸러내는가
    ///  - 시드 규약          : TexelSeed 가 기존 seed+li*const 와 동일한가(백엔드 교차검증 보존)
    /// </summary>
    public static class LightmapSSAATests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== SSAA Self-Tests ===");
            int pass = 0, total = 0;

            // ── SubOffset ────────────────────────────────────────────────
            for (int S = 2; S <= 4; S++)
            {
                int n = S * S;
                bool inRange = true;
                float minU = 1f, maxU = 0f, minV = 1f, maxV = 0f;
                var us = new float[n]; var vs = new float[n];
                for (int i = 0; i < n; i++)
                {
                    TexelMapper.SubOffset(S, i, out float u, out float v);
                    us[i] = u; vs[i] = v;
                    if (u < 0f || u >= 1f || v < 0f || v >= 1f) inRange = false;
                    minU = Mathf.Min(minU, u); maxU = Mathf.Max(maxU, u);
                    minV = Mathf.Min(minV, v); maxV = Mathf.Max(maxV, v);
                }
                Check(sb, ref pass, ref total, $"SubOffset S={S} 모두 텍셀 내부[0,1)", inRange);

                // 두 축 모두 n 개의 서로 다른 값 → 어느 방향 경계든 n 단계 계조가 나온다.
                Check(sb, ref pass, ref total, $"SubOffset S={S} u 가 {n}개로 분리", DistinctCount(us, 1e-4f) == n);
                Check(sb, ref pass, ref total, $"SubOffset S={S} v 가 {n}개로 분리", DistinctCount(vs, 1e-4f) == n);

                // 정규격자였다면 v 는 S 단계뿐 — 그 함정에 다시 빠지지 않는지 못박아 둔다.
                Check(sb, ref pass, ref total, $"SubOffset S={S} v 가 격자({S}단계)보다 조밀", DistinctCount(vs, 1e-4f) > S);
            }

            // ── MapSubsamples ────────────────────────────────────────────
            {
                // XZ 평면 단위 쿼드, uv2 = (x,z) → worldPos 가 곧 uv. 텍셀↔월드 대응을 눈으로 검산 가능.
                var mesh = UnitQuadXZ();
                const int res = 4;
                int S = 3, spt = S * S;

                // (a) 마스크 준수 — 대각선 텍셀만 요청
                var mask = new bool[res * res];
                for (int d = 0; d < res; d++) mask[d * res + d] = true;

                var ss = TexelMapper.MapSubsamples(mesh, res, Matrix4x4.identity, S, mask);
                Check(sb, ref pass, ref total, "MapSubsamples 슬롯 수 = 마스크 수", ss.SlotCount == res);

                bool slotRoundTrip = true, maskRespected = true;
                for (int li = 0; li < res * res; li++)
                {
                    int slot = ss.SlotOfTexel[li];
                    if (mask[li]) { if (slot < 0 || ss.TexelOfSlot[slot] != li) slotRoundTrip = false; }
                    else if (slot >= 0) maskRespected = false;
                }
                Check(sb, ref pass, ref total, "MapSubsamples 슬롯 매핑 왕복(li↔slot)", slotRoundTrip);
                Check(sb, ref pass, ref total, "MapSubsamples 마스크 밖 텍셀은 슬롯 없음", maskRespected);

                // (b) 쿼드가 uv 전면을 덮으므로 모든 서브샘플이 유효해야 한다.
                int validSubs = 0;
                for (int i = 0; i < ss.Valid.Length; i++) if (ss.Valid[i]) validSubs++;
                Check(sb, ref pass, ref total, "MapSubsamples 전면 커버 텍셀은 S² 전부 유효", validSubs == ss.SlotCount * spt);

                // (c) 각 서브샘플이 '자기 텍셀'의 월드 풋프린트 안에 복원됐는가 (= 래스터/바리센트릭 정합)
                bool insideFootprint = true, normalUp = true;
                float texel = 1f / res;
                for (int slot = 0; slot < ss.SlotCount; slot++)
                {
                    int li = ss.TexelOfSlot[slot];
                    int tx = li % res, ty = li / res;
                    for (int s = 0; s < spt; s++)
                    {
                        int o = slot * spt + s;
                        if (!ss.Valid[o]) continue;
                        Vector3 p = ss.WorldPos[o];
                        if (p.x < tx * texel - 1e-4f || p.x > (tx + 1) * texel + 1e-4f ||
                            p.z < ty * texel - 1e-4f || p.z > (ty + 1) * texel + 1e-4f ||
                            Mathf.Abs(p.y) > 1e-4f) insideFootprint = false;
                        if (Vector3.Dot(ss.WorldNormal[o], Vector3.up) < 0.999f) normalUp = false;
                    }
                }
                Check(sb, ref pass, ref total, "MapSubsamples 서브샘플이 자기 텍셀 안에 복원", insideFootprint);
                Check(sb, ref pass, ref total, "MapSubsamples 서브샘플 노멀 = 면 노멀", normalUp);

                // (d) 서브샘플 위치가 서로 다르다 = 실제로 텍셀 내부를 훑는다(1점 반복이 아니다)
                bool distinctPos = true;
                for (int slot = 0; slot < ss.SlotCount && distinctPos; slot++)
                    for (int a = 0; a < spt && distinctPos; a++)
                        for (int b = a + 1; b < spt; b++)
                            if ((ss.WorldPos[slot * spt + a] - ss.WorldPos[slot * spt + b]).sqrMagnitude < 1e-10f)
                            { distinctPos = false; break; }
                Check(sb, ref pass, ref total, "MapSubsamples 서브샘플 위치가 모두 상이", distinctPos);

                // (e) 마스크가 비면 슬롯 0 + 안전 반환
                var empty = TexelMapper.MapSubsamples(mesh, res, Matrix4x4.identity, S, new bool[res * res]);
                Check(sb, ref pass, ref total, "MapSubsamples 빈 마스크 → 슬롯 0", empty.SlotCount == 0 && empty.WorldPos.Length == 0);

                UnityEngine.Object.DestroyImmediate(mesh);
            }

            // ── DetectEdges ──────────────────────────────────────────────
            {
                const int res = 8;
                float cos45 = Mathf.Cos(45f * Mathf.Deg2Rad);
                const float maxDist = 0.4f;   // 텍셀 0.1 월드 × 4

                // 평평한 격자에 x=4 에서 밝기 계단
                var lm = FlatLumel(res, out var radiance, stepAtX: 4);

                var mask = LightmapSSAA.DetectEdges(lm, radiance, res, 0.1f, cos45, maxDist, 0, out int edgeCount);
                bool onlyStepColumns = true;
                for (int y = 0; y < res; y++)
                    for (int x = 0; x < res; x++)
                        if (mask[y * res + x] != (x == 3 || x == 4)) onlyStepColumns = false;
                Check(sb, ref pass, ref total, "DetectEdges 계단 양쪽 열만 검출", onlyStepColumns && edgeCount == 2 * res);

                // 1링 확장 → 계단 양쪽으로 한 칸씩
                var maskD = LightmapSSAA.DetectEdges(lm, radiance, res, 0.1f, cos45, maxDist, 1, out _);
                bool dilated = true;
                for (int y = 0; y < res; y++)
                    for (int x = 0; x < res; x++)
                        if (maskD[y * res + x] != (x >= 2 && x <= 5)) dilated = false;
                Check(sb, ref pass, ref total, "DetectEdges 1링 확장", dilated);

                // 평탄한 조도 → 엣지 없음
                var flatRad = new Vector3[res * res];
                for (int i = 0; i < flatRad.Length; i++) flatRad[i] = Vector3.one;
                LightmapSSAA.DetectEdges(lm, flatRad, res, 0.1f, cos45, maxDist, 1, out int flatCount);
                Check(sb, ref pass, ref total, "DetectEdges 평탄 조도 → 0개", flatCount == 0);

                // 노멀 게이팅: 계단 오른쪽 면을 꺾으면 '정상 불연속'이라 검출하지 않아야 한다.
                var lmBent = FlatLumel(res, out _, stepAtX: 4);
                for (int y = 0; y < res; y++)
                    for (int x = 4; x < res; x++)
                        lmBent.WorldNormal[y * res + x] = Vector3.right;
                LightmapSSAA.DetectEdges(lmBent, radiance, res, 0.1f, cos45, maxDist, 0, out int bentCount);
                Check(sb, ref pass, ref total, "DetectEdges 노멀 게이팅(각진 면 제외)", bentCount == 0);

                // 거리 게이팅: 아틀라스에선 이웃이지만 월드에선 멀면 다른 차트 → 비교 안 함.
                var lmFar = FlatLumel(res, out _, stepAtX: 4);
                for (int y = 0; y < res; y++)
                    for (int x = 4; x < res; x++)
                        lmFar.WorldPos[y * res + x] += new Vector3(100f, 0f, 0f);
                LightmapSSAA.DetectEdges(lmFar, radiance, res, 0.1f, cos45, maxDist, 0, out int farCount);
                Check(sb, ref pass, ref total, "DetectEdges 거리 게이팅(다른 차트 제외)", farCount == 0);
            }

            // ── 시드 규약 ────────────────────────────────────────────────
            {
                const uint seed = 12345u;
                bool legacy = true;
                for (int li = 0; li < 64; li++)
                    if (LightmapSSAA.TexelSeed(seed, li) != seed + (uint)li * 2654435761u) legacy = false;
                Check(sb, ref pass, ref total, "TexelSeed = 기존 seed+li*2654435761 규약 유지", legacy);

                // 서브샘플 시드는 서로 달라야 평균이 MC 노이즈까지 줄인다.
                var seen = new System.Collections.Generic.HashSet<uint>();
                bool distinct = true;
                for (int s = 0; s < 16; s++) if (!seen.Add(LightmapSSAA.SubSeed(seed, 7, s))) distinct = false;
                Check(sb, ref pass, ref total, "SubSeed 서브샘플별 상이", distinct);
                Check(sb, ref pass, ref total, "SubSeed ≠ TexelSeed(같은 텍셀)", !seen.Contains(LightmapSSAA.TexelSeed(seed, 7)));
            }

            sb.AppendLine($"--- {pass}/{total} passed ---");
            return sb.ToString();
        }

        // ── helpers ─────────────────────────────────────────────────────

        /// XZ 평면 단위 쿼드(uv2 = xz). worldPos == uv 라 텍셀↔월드 검산이 자명해진다.
        static Mesh UnitQuadXZ()
        {
            var m = new Mesh { name = "SSAATest_Quad", hideFlags = HideFlags.HideAndDontSave };
            m.vertices = new[]
            {
                new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,0,1), new Vector3(0,0,1),
            };
            m.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            m.uv2 = new[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            return m;
        }

        /// 전 텍셀 valid 인 평평한 루멜맵 + x&lt;stepAtX 는 밝고 나머지는 어두운 조도.
        static LumelMap FlatLumel(int res, out Vector3[] radiance, int stepAtX)
        {
            var lm = new LumelMap
            {
                Resolution = res,
                WorldPos = new Vector3[res * res],
                WorldNormal = new Vector3[res * res],
                Valid = new bool[res * res],
            };
            radiance = new Vector3[res * res];
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                {
                    int i = y * res + x;
                    lm.WorldPos[i] = new Vector3(x * 0.1f, 0f, y * 0.1f);
                    lm.WorldNormal[i] = Vector3.up;
                    lm.Valid[i] = true;
                    radiance[i] = (x < stepAtX) ? Vector3.one : Vector3.zero;
                }
            return lm;
        }

        static int DistinctCount(float[] v, float eps)
        {
            int c = 0;
            for (int i = 0; i < v.Length; i++)
            {
                bool dup = false;
                for (int j = 0; j < i; j++) if (Mathf.Abs(v[i] - v[j]) < eps) { dup = true; break; }
                if (!dup) c++;
            }
            return c;
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        {
            total++;
            if (ok) pass++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }
    }
}
