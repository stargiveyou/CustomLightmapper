using System.Text;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// α 트랙 검증 (α-V2 ~ α-V4, α-V6) — 렌더 컨텍스트 불필요(순수 CPU/Burst).
    ///
    ///  ① RayTriUV ≡ RayTri      : hit/T 비트동일 + barycentric 유효성 (α 결정 ④의 전제)
    ///  ② AlphaMath.TexelBit     : wrap/타일링/음수 UV 알려진 값
    ///  ③ CPU any-hit 해석적 검증 : 체커보드 마스크 쿼드를 관통하는 레이 = 마스크 비트와 일치
    ///  ④ Burst ≡ CPU            : 같은 씬·같은 레이, **불일치 0**(정수 비트 판정이라 ε 아님)
    ///  ⑤ 회귀                    : alpha 비활성이면 알파 이전 경로와 결과 동일
    ///
    /// 호출: Debug.Log(AlphaCutoutTests.RunAll());
    /// </summary>
    public static class AlphaCutoutTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== α 알파 컷아웃 any-hit (CPU ground truth + Burst 미러) ===");
            int pass = 0, total = 0;

            RayTriUvEquiv(sb, ref pass, ref total);
            TexelBitKnownValues(sb, ref pass, ref total);
            CpuAnalytic(sb, ref pass, ref total);
            BurstEquivCpu(sb, ref pass, ref total);
            DisabledRegression(sb, ref pass, ref total);

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        // ── ① RayTriUV ≡ RayTri ────────────────────────────────────────────────
        static void RayTriUvEquiv(StringBuilder sb, ref int pass, ref int total)
        {
            var rng = new System.Random(9182);
            int n = 4000, hitMiss = 0, tMiss = 0, baryBad = 0, posBad = 0, hits = 0;

            for (int k = 0; k < n; k++)
            {
                var t = new Tri
                {
                    V0 = RandPoint(rng, 2f),
                    V1 = RandPoint(rng, 2f),
                    V2 = RandPoint(rng, 2f),
                };
                Vector3 o = RandPoint(rng, 3f);
                Vector3 d = RandDir(rng);

                bool a = RayGeometry.RayTri(o, d, t, 0f, 100f, out float ha);
                bool b = RayGeometry.RayTriUV(o, d, t, 0f, 100f, out float hb, out float bu, out float bv);

                if (a != b) { hitMiss++; continue; }
                if (!a) continue;
                hits++;

                // 비트동일이어야 한다(같은 연산 순서의 복제본).
                if (ha != hb) tMiss++;
                if (bu < -1e-6f || bv < -1e-6f || bu + bv > 1f + 1e-6f) baryBad++;

                // barycentric 재구성 = 레이 파라메트릭 위치
                Vector3 pBary = t.V0 + (t.V1 - t.V0) * bu + (t.V2 - t.V0) * bv;
                Vector3 pRay = o + d * hb;
                if ((pBary - pRay).sqrMagnitude > 1e-6f) posBad++;
            }

            Check(sb, ref pass, ref total, $"RayTriUV hit 플래그 ≡ RayTri (miss={hitMiss}/{n}, hits={hits})", hitMiss == 0 && hits > 0);
            Check(sb, ref pass, ref total, $"RayTriUV T 비트동일 (miss={tMiss})", tMiss == 0);
            Check(sb, ref pass, ref total, $"barycentric 유효범위 (bad={baryBad})", baryBad == 0);
            Check(sb, ref pass, ref total, $"barycentric 재구성 ≡ 레이 위치 (bad={posBad})", posBad == 0);
        }

        // ── ② TexelBit 알려진 값 ───────────────────────────────────────────────
        static void TexelBitKnownValues(StringBuilder sb, ref int pass, ref int total)
        {
            var id = new Vector4(1f, 1f, 0f, 0f);

            // 8×8, 항등 ST: (0.06,0.06) → (0,0) ; (0.99,0.99) → (7,7) = 63
            bool ok1 = AlphaMath.TexelBit(0.06f, 0.06f, 8, 8, id) == 0;
            bool ok2 = AlphaMath.TexelBit(0.99f, 0.99f, 8, 8, id) == 7 * 8 + 7;
            // 정확히 중앙: floor(0.5*8)=4
            bool ok3 = AlphaMath.TexelBit(0.5f, 0.5f, 8, 8, id) == 4 * 8 + 4;
            // 음수 UV wrap: -0.06 → floor(-0.48) = -1 → +8 = 7
            bool ok4 = AlphaMath.TexelBit(-0.06f, 0.06f, 8, 8, id) == 0 * 8 + 7;
            // UV>1 wrap: 1.06 → floor(8.48)=8 → %8 = 0
            bool ok5 = AlphaMath.TexelBit(1.06f, 0.06f, 8, 8, id) == 0;
            // 타일링 2배: u=0.3, st.x=2 → 0.6 → floor(4.8)=4
            bool ok6 = AlphaMath.TexelBit(0.3f, 0.06f, 8, 8, new Vector4(2f, 1f, 0f, 0f)) == 4;
            // NaN 방어: 0 텍셀로 접힘
            bool ok7 = AlphaMath.TexelBit(float.NaN, 0.06f, 8, 8, id) == 0;

            Check(sb, ref pass, ref total, "TexelBit 기본/경계", ok1 && ok2 && ok3);
            Check(sb, ref pass, ref total, "TexelBit Repeat wrap(음수·>1)", ok4 && ok5);
            Check(sb, ref pass, ref total, "TexelBit 타일링 ST", ok6);
            Check(sb, ref pass, ref total, "TexelBit NaN 방어", ok7);
        }

        // ── ③ CPU any-hit 해석적 검증 ──────────────────────────────────────────
        // 체커보드 마스크를 입힌 단위 쿼드(z=0). 위에서 아래로 쏜 레이의 차폐 여부가
        // 정확히 그 지점의 마스크 비트와 같아야 한다.
        static void CpuAnalytic(StringBuilder sb, ref int pass, ref int total)
        {
            const int M = 8;
            var alpha = MakeCheckerQuadScene(M, out Tri[][] meshes, out TwoLevelBVH.Instance[] insts);

            using var bvh = new TwoLevelBVH(meshes, insts);
            bvh.SetAlpha(alpha);

            int miss = 0, opaqueHits = 0, transparentPass = 0;
            for (int ty = 0; ty < M; ty++)
            {
                for (int tx = 0; tx < M; tx++)
                {
                    // 텍셀 중앙 — 경계 부동소수 이슈 회피
                    float u = (tx + 0.5f) / M;
                    float v = (ty + 0.5f) / M;
                    Vector3 o = new Vector3(u, v, 1f);
                    bool expectOpaque = ((tx + ty) & 1) == 0;

                    bool occ = bvh.Occluded(o, new Vector3(0, 0, -1), 2f);
                    if (occ != expectOpaque) miss++;
                    else if (expectOpaque) opaqueHits++;
                    else transparentPass++;
                }
            }

            Check(sb, ref pass, ref total,
                $"CPU Occluded ≡ 체커보드 마스크 ({M}×{M}, miss={miss}, 차폐={opaqueHits}, 통과={transparentPass})",
                miss == 0 && opaqueHits > 0 && transparentPass > 0);

            // 최근접 교차도 같은 규칙을 따라야 한다(투명 텍셀은 히트로 잡히지 않음).
            int hitMiss = 0;
            for (int ty = 0; ty < M; ty++)
                for (int tx = 0; tx < M; tx++)
                {
                    float u = (tx + 0.5f) / M, v = (ty + 0.5f) / M;
                    var h = bvh.IntersectInstanced(new Vector3(u, v, 1f), new Vector3(0, 0, -1), 0f, 2f);
                    if (h.Valid != (((tx + ty) & 1) == 0)) hitMiss++;
                }
            Check(sb, ref pass, ref total, $"CPU IntersectInstanced ≡ 마스크 (miss={hitMiss})", hitMiss == 0);
        }

        // ── ④ Burst ≡ CPU ─────────────────────────────────────────────────────
        static void BurstEquivCpu(StringBuilder sb, ref int pass, ref int total)
        {
            const int M = 16;
            var alpha = MakeCheckerQuadScene(M, out Tri[][] meshes, out TwoLevelBVH.Instance[] insts);

            using var bvh = new TwoLevelBVH(meshes, insts);
            bvh.SetAlpha(alpha);
            using var scene = BurstScene.Create(bvh, null, alpha, Allocator.Persistent);

            var rng = new System.Random(31337);
            const int Rays = 5000;
            int occMiss = 0, validMiss = 0, tMiss = 0, hits = 0;

            for (int k = 0; k < Rays; k++)
            {
                // 쿼드 위쪽 반구에서 쿼드 쪽으로 쏘는 레이(마스크 경계를 폭넓게 훑는다)
                Vector3 o = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), 0.5f + (float)rng.NextDouble());
                Vector3 target = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), 0f);
                Vector3 d = (target - o).normalized;

                if (bvh.Occluded(o, d, 5f) != BurstTwoLevelBVH.Occluded(scene, o, d, 5f)) occMiss++;

                var hc = bvh.IntersectInstanced(o, d, 0f, 5f);
                var hbst = BurstTwoLevelBVH.IntersectInstanced(scene, o, d, 0f, 5f);
                if (hc.Valid != hbst.Valid) validMiss++;
                else if (hc.Valid)
                {
                    hits++;
                    if (hc.T != hbst.T || hc.MeshTriIndex != hbst.MeshTriIndex) tMiss++;
                }
            }

            Check(sb, ref pass, ref total, $"Burst Occluded ≡ CPU (miss={occMiss}/{Rays})", occMiss == 0);
            Check(sb, ref pass, ref total, $"Burst Intersect Valid ≡ CPU (miss={validMiss}, hits={hits})", validMiss == 0 && hits > 0);
            Check(sb, ref pass, ref total, $"Burst Intersect T·tri 비트동일 (miss={tMiss})", tMiss == 0);
        }

        // ── ⑤ 회귀: 알파 비활성 = 알파 도입 이전 경로 ──────────────────────────
        static void DisabledRegression(StringBuilder sb, ref int pass, ref int total)
        {
            const int M = 8;
            var alpha = MakeCheckerQuadScene(M, out Tri[][] meshes, out TwoLevelBVH.Instance[] insts);

            using var withAlpha = new TwoLevelBVH(meshes, insts);
            withAlpha.SetAlpha(AlphaSceneData.Disabled);          // 명시적으로 끈다
            using var plain = new TwoLevelBVH(meshes, insts);      // 알파 개념 자체가 없는 BVH
            using var sceneOff = BurstScene.Create(plain, null, Allocator.Persistent);

            var rng = new System.Random(555);
            int miss = 0, allOccluded = 0;
            for (int k = 0; k < 2000; k++)
            {
                Vector3 o = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), 1f);
                Vector3 d = new Vector3(0, 0, -1);
                bool a = withAlpha.Occluded(o, d, 2f);
                bool b = plain.Occluded(o, d, 2f);
                bool c = BurstTwoLevelBVH.Occluded(sceneOff, o, d, 2f);
                if (a != b || b != c) miss++;
                if (a) allOccluded++;
            }

            Check(sb, ref pass, ref total, $"알파 OFF: CPU/Burst 모두 기존 경로와 동일 (miss={miss})", miss == 0);
            // 알파가 꺼지면 쿼드는 통짜 불투명 → 전부 차폐(= 지금 나무가 통짜 그림자인 상태)
            Check(sb, ref pass, ref total, $"알파 OFF: 쿼드가 통짜 차폐 ({allOccluded}/2000)", allOccluded == 2000);
            Check(sb, ref pass, ref total, "BurstScene 알파 더미 할당(잡 스케줄 안전)", sceneOff.alpha.triUV.IsCreated && !sceneOff.alpha.enabled);
        }

        // ── 합성 씬: 단위 쿼드(z=0) + 체커보드 마스크 ──────────────────────────
        //   uv == (x,y) 가 되도록 삼각형/UV 를 잡는다(해석적 기대값 계산이 가능해짐).
        internal static AlphaSceneData MakeCheckerQuadScene(int m, out Tri[][] meshes, out TwoLevelBVH.Instance[] insts)
        {
            var v0 = new Vector3(0, 0, 0);
            var v1 = new Vector3(1, 0, 0);
            var v2 = new Vector3(1, 1, 0);
            var v3 = new Vector3(0, 1, 0);

            meshes = new Tri[][]
            {
                new []
                {
                    new Tri { V0 = v0, V1 = v1, V2 = v2 },
                    new Tri { V0 = v0, V1 = v2, V2 = v3 },
                }
            };
            insts = new[] { new TwoLevelBVH.Instance { MeshIndex = 0, LocalToWorld = Matrix4x4.identity } };

            int words = (m * m + 31) / 32;
            var bits = new uint[words];
            for (int y = 0; y < m; y++)
                for (int x = 0; x < m; x++)
                    if (((x + y) & 1) == 0)                    // 체커: 짝수 칸 = 불투명
                    {
                        int bit = y * m + x;
                        bits[bit >> 5] |= 1u << (bit & 31);
                    }

            return new AlphaSceneData
            {
                Enabled = true,
                TriUV = new[]
                {
                    new TriUV { UV0 = new Vector2(0,0), UV1 = new Vector2(1,0), UV2 = new Vector2(1,1) },
                    new TriUV { UV0 = new Vector2(0,0), UV1 = new Vector2(1,1), UV2 = new Vector2(0,1) },
                },
                TriSubmesh = new byte[] { 0, 0 },
                MeshHasCutout = new byte[] { 1 },
                MeshTriStart = new[] { 0 },
                InstMatBase = new[] { 0 },
                MatSlot = new[] { 0 },
                MaskBits = bits,
                MaskWord = new[] { 0 },
                MaskW = new[] { m },
                MaskH = new[] { m },
                MaskST = new[] { new Vector4(1f, 1f, 0f, 0f) },
            };
        }

        // ── 유틸 ────────────────────────────────────────────────────────────────
        static Vector3 RandPoint(System.Random rng, float e)
            => new Vector3(Rand(rng, e), Rand(rng, e), Rand(rng, e));
        static float Rand(System.Random rng, float half) => (float)(rng.NextDouble() * 2.0 - 1.0) * half;
        static Vector3 RandDir(System.Random rng)
        {
            float z = (float)(rng.NextDouble() * 2.0 - 1.0);
            float a = (float)(rng.NextDouble() * 2.0 * System.Math.PI);
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
            return new Vector3(r * Mathf.Cos(a), r * Mathf.Sin(a), z);
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        {
            total++; if (ok) pass++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }
    }
}
