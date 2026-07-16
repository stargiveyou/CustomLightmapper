using System.Text;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// SH-5 검증(GPU↔CPU): 셰이더 SH 디코드/평가 ↔ CPU <see cref="SH9.Evaluate"/> 수치 대조.
    /// **실 StructuredBuffer 왕복** — <c>SHPacked</c> 1개를 stride-112 GraphicsBuffer(<c>StructuredBuffer&lt;SHPackedGPU&gt;</c>)
    /// 로 업로드하고 <c>SHEvalProbe.shader</c> 를 1×1 float RT 로 Blit → ReadPixels 회수 → 같은 SH·10개 노멀의
    /// CPU 평가와 ε 비교. 패킹 순서(c0..c8↔p0..p6)·상수(k·A·W)·클램프까지 GPU=CPU end-to-end 확인.
    ///
    /// 컨벤션: 다른 SH/Burst 테스트와 동일하게 <c>static RunAll()→문자열</c>. <see cref="LightmapEvaluateDebugger"/>
    /// "Run All Tests" 및 전용 메뉴에 배선. **렌더 컨텍스트 필요**(에디터/플레이) — Blit/ReadPixels 때문에 헤드리스 CI 불가.
    /// </summary>
    public static class SHEvalProbeGpuTests
    {
        /// <param name="epsilon">절대 허용오차(GPU/CPU fp32 반올림 흡수; 실측 err≈0).</param>
        /// <param name="projDirs">테스트 SH 생성용 프로젝션 방향 수.</param>
        public static string RunAll(float epsilon = 2e-3f, int projDirs = 512)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== SHEvalProbe GPU↔CPU (SH-5, 실 StructuredBuffer 왕복) ===");
            int pass = 0, total = 0;

            var shader = Shader.Find("HuskyLibs/SHEvalProbe");
            if (shader == null) { sb.AppendLine("  [FAIL] 셰이더 'HuskyLibs/SHEvalProbe' 없음 (등록/컴파일 확인)"); return sb.ToString(); }
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
            { sb.AppendLine("  [SKIP] ARGBFloat RT 미지원 플랫폼 — GPU 검증 불가"); return sb.ToString(); }
            if (SystemInfo.maxComputeBufferInputsFragment <= 0)
                sb.AppendLine("  [WARN] 프래그먼트 StructuredBuffer 제한 가능(결과 0 이면 이 원인)");

            // 방향 의존 컬러 환경 프로젝션 → 전 계수·채널 비대칭 SH(패킹 채널 스왑도 검출).
            SH9 sh = BuildTestSH(projDirs);

            var buf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, SHPacked.Stride); // stride 112, 단일 프로브
            buf.SetData(new[] { SHPacked.Pack(sh) });
            var mat = new Material(shader);
            mat.SetBuffer("_ProbeSH", buf);

            var rt = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { useMipMap = false };
            rt.Create();
            var tex = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            var prevActive = RenderTexture.active;
            float maxErr = 0f;

            var normals = TestNormals();
            for (int i = 0; i < normals.Length; i++)
            {
                Vector3 n = normals[i].normalized;
                mat.SetVector("_ProbeNormal", new Vector4(n.x, n.y, n.z, 0f));

                Graphics.Blit(Texture2D.whiteTexture, rt, mat);    // 프래그먼트가 _ProbeSH[0]·_ProbeNormal 로 상수 출력
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
                tex.Apply(false);
                Color g = tex.GetPixel(0, 0);
                Vector3 gpu = new Vector3(g.r, g.g, g.b);

                Vector3 cpu = sh.Evaluate(n);
                float err = Mathf.Max(Mathf.Abs(gpu.x - cpu.x),
                            Mathf.Max(Mathf.Abs(gpu.y - cpu.y), Mathf.Abs(gpu.z - cpu.z)));
                maxErr = Mathf.Max(maxErr, err);
                Check(sb, ref pass, ref total,
                    $"n=({n.x:0.00},{n.y:0.00},{n.z:0.00}) " +
                    $"GPU=({gpu.x:0.000},{gpu.y:0.000},{gpu.z:0.000}) CPU=({cpu.x:0.000},{cpu.y:0.000},{cpu.z:0.000}) err={err:0.00000}",
                    err <= epsilon);
            }
            RenderTexture.active = prevActive;

            buf.Dispose();
            rt.Release(); Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(mat);

            sb.AppendLine($"--- {pass}/{total} PASS (maxErr={maxErr:0.00000}, ε={epsilon}) ---");
            return sb.ToString();
        }

        /// <summary>방향별 컬러 로브를 프로젝션 → 전 계수·채널 비대칭 SH(패킹 순서 검증에 유리).</summary>
        static SH9 BuildTestSH(int dirs)
        {
            var pts = BurstSHBaker.FibonacchiSphere(dirs, Allocator.Temp);
            float w = 4f * Mathf.PI / dirs;
            var sh = new SH9();
            for (int i = 0; i < pts.Length; i++)
            {
                Vector3 d = pts[i];
                Vector3 L = new Vector3(
                    0.5f + 0.5f * Mathf.Max(0f, d.x),
                    0.4f + 0.6f * Mathf.Max(0f, d.y),
                    0.5f + 0.5f * Mathf.Max(0f, d.z));
                sh.Accumulate(d, L, w);
            }
            pts.Dispose();
            return sh;
        }

        static Vector3[] TestNormals() => new[]
        {
            Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back,
            new Vector3(1, 1, 1), new Vector3(-1, 1, 0.5f),
            new Vector3(0.3f, -0.7f, 0.6f), new Vector3(-0.8f, -0.2f, -0.5f),
        };

        static void Check(StringBuilder sb, ref int pass, ref int total, string name, bool ok)
        { total++; if (ok) pass++; sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
    }
}
