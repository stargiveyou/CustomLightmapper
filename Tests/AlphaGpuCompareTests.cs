using System.Text;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// α-V5 : GPU 알파 컷아웃 any-hit ≡ Burst.
    ///
    /// 검증 방식 — <b>CSDirect</b> 를 쓴다. 이 커널은 무작위가 전혀 없고(그림자 = TlasOccluded 한 번),
    /// 알파 판정도 정수 비트 조회라 **초월함수 발산이 개입할 여지가 없다** → 통계가 아니라
    /// per-texel 정확 일치를 요구할 수 있다. (G5 의 AO/Indirect 가 통계 기준인 것과 대비.)
    ///
    /// 씬: 체커보드 마스크를 입힌 단위 쿼드(z=0) 아래에 수광점들을 깔고, 태양을 +z 에서 -z 로 쏜다.
    ///   → 불투명 텍셀 아래 점은 그림자(0), 투명 텍셀 아래 점은 조명(>0).
    ///   마스크 경계를 가로지르므로 순회·UV 보간·비트 조회가 모두 동원된다.
    ///
    /// ⚠️ 렌더 컨텍스트 필요(에디터/플레이). compute 미지원 → SKIP.
    /// 호출: Debug.Log(AlphaGpuCompareTests.RunAll());
    /// </summary>
    public static class AlphaGpuCompareTests
    {
        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== α GPU 알파 컷아웃 any-hit ≡ Burst (CSDirect) ===");

            if (!SystemInfo.supportsComputeShaders)
            { sb.AppendLine("  [SKIP] compute shader 미지원 플랫폼"); return sb.ToString(); }

            var cs = Resources.Load<ComputeShader>("PathTrace");
            if (cs == null)
            { sb.AppendLine("  [FAIL] PathTrace.compute 로드 실패"); return sb.ToString(); }

            int kDir = cs.FindKernel("CSDirect");
            if (kDir < 0) { sb.AppendLine("  [FAIL] CSDirect 커널 미발견"); return sb.ToString(); }

            int pass = 0, total = 0;
            Compare(sb, ref pass, ref total, cs, kDir, alphaOn: true);
            Compare(sb, ref pass, ref total, cs, kDir, alphaOn: false);

            sb.AppendLine($"--- {pass}/{total} PASS ---");
            return sb.ToString();
        }

        static void Compare(StringBuilder sb, ref int pass, ref int total, ComputeShader cs, int kDir, bool alphaOn)
        {
            const int M = 16;
            var alpha = AlphaCutoutTests.MakeCheckerQuadScene(M, out Tri[][] meshes, out TwoLevelBVH.Instance[] insts);
            if (!alphaOn) alpha = AlphaSceneData.Disabled;

            using var bvh = new TwoLevelBVH(meshes, insts);
            using var scene = BurstScene.Create(bvh, null, alpha, Allocator.Persistent);
            using var gpu = new GpuScene(scene);

            // 쿼드 아래 수광점 격자(텍셀 중앙 정렬). 노멀은 +z(태양을 향함).
            int n = M * M;
            var pts = new Vector3[n];
            var nrm = new Vector3[n];
            for (int y = 0; y < M; y++)
                for (int x = 0; x < M; x++)
                {
                    int i = y * M + x;
                    pts[i] = new Vector3((x + 0.5f) / M, (y + 0.5f) / M, -0.25f);
                    nrm[i] = new Vector3(0, 0, 1);
                }

            var sun = new DirectionalLight
            {
                Direction = new Vector3(0, 0, -1),      // 진행 방향 -z → L = +z
                Color = new Vector3(1f, 1f, 1f),
                Intensity = 1f,
            };

            // ── Burst 정답 ──
            var naPts = new NativeArray<Vector3>(pts, Allocator.TempJob);
            var naNrm = new NativeArray<Vector3>(nrm, Allocator.TempJob);
            var naVal = new NativeArray<bool>(n, Allocator.TempJob);
            for (int i = 0; i < n; i++) naVal[i] = true;
            var cpuDir = BurstDirect.Compute(scene, naPts, naNrm, naVal, sun, Allocator.TempJob);

            // ── GPU ──
            var validU = new uint[n];
            var seedsU = new uint[n];
            for (int i = 0; i < n; i++) { validU[i] = 1u; seedsU[i] = 1u; }

            var ptsBuf = new ComputeBuffer(n, 12, ComputeBufferType.Structured);
            var nrmBuf = new ComputeBuffer(n, 12, ComputeBufferType.Structured);
            var valBuf = new ComputeBuffer(n, sizeof(uint), ComputeBufferType.Structured);
            var seedBuf = new ComputeBuffer(n, sizeof(uint), ComputeBufferType.Structured);
            var dirBuf = new ComputeBuffer(n, 12, ComputeBufferType.Structured);
            ptsBuf.SetData(pts); nrmBuf.SetData(nrm); valBuf.SetData(validU); seedBuf.SetData(seedsU);

            var gpuDir = new Vector3[n];
            try
            {
                gpu.Bind(cs, kDir);
                gpu.BindLighting(cs, kDir);
                gpu.BindAlpha(cs, kDir);
                cs.SetBuffer(kDir, "_Points", ptsBuf);
                cs.SetBuffer(kDir, "_Normals", nrmBuf);
                cs.SetBuffer(kDir, "_Valid", valBuf);
                cs.SetBuffer(kDir, "_Seeds", seedBuf);
                cs.SetBuffer(kDir, "_DirectOut", dirBuf);
                cs.SetInt("_Count", n);
                cs.SetVector("_SunDir", sun.Direction);
                cs.SetVector("_SunColor", sun.Color);
                cs.SetFloat("_SunIntensity", sun.Intensity);
                cs.SetInt("_SkyType", 0);
                cs.SetVector("_SkyTop", Vector3.zero);
                cs.SetVector("_SkyBottom", Vector3.zero);

                cs.Dispatch(kDir, (n + 63) / 64, 1, 1);
                dirBuf.GetData(gpuDir);
            }
            finally
            {
                ptsBuf.Dispose(); nrmBuf.Dispose(); valBuf.Dispose(); seedBuf.Dispose(); dirBuf.Dispose();
            }

            // ── 대조 ──
            int miss = 0, lit = 0, shadow = 0, expectMiss = 0;
            for (int i = 0; i < n; i++)
            {
                bool cpuLit = cpuDir[i].x > 0.5f;
                bool gLit = gpuDir[i].x > 0.5f;
                if (cpuLit != gLit) miss++;
                if (cpuLit) lit++; else shadow++;

                if (alphaOn)
                {
                    int x = i % M, y = i / M;
                    bool expectShadow = ((x + y) & 1) == 0;      // 짝수 칸 = 불투명 = 그림자
                    if (cpuLit == expectShadow) expectMiss++;
                }
            }

            naPts.Dispose(); naNrm.Dispose(); naVal.Dispose(); cpuDir.Dispose();

            string tag = alphaOn ? "alpha ON" : "alpha OFF";
            Check(sb, ref pass, ref total, $"[{tag}] GPU Direct ≡ Burst (miss={miss}/{n})", miss == 0);

            if (alphaOn)
            {
                Check(sb, ref pass, ref total,
                    $"[{tag}] 체커보드 그림자 해석적 일치 (miss={expectMiss}, 조명={lit}, 그림자={shadow})",
                    expectMiss == 0 && lit > 0 && shadow > 0);
            }
            else
            {
                // 알파를 끄면 쿼드가 통짜 불투명 → 전부 그림자(현재 나무가 통짜로 나오는 상태)
                Check(sb, ref pass, ref total, $"[{tag}] 쿼드 통짜 차폐 (그림자={shadow}/{n})", shadow == n);
            }
        }

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        {
            total++; if (ok) pass++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }
    }
}
