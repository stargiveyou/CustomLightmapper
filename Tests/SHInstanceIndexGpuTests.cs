using System.Text;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// SH-5 검증(GPU 인스턴스 오프셋 인덱싱): <see cref="SHEvalProbeGpuTests"/> 가 못 잡는 갭을 닫는다.
    ///
    /// <para>기존 <c>SHEvalProbeGpuTests</c> 는 <c>StructuredBuffer&lt;SHPackedGPU&gt;</c>(stride 112)의
    /// <c>[0]</c> 한 프로브만 읽어 **디코드 수학(언팩·상수·클램프)** 만 검증한다. 따라서 실제 인스턴싱
    /// 셰이더(<c>InstancedSH_BuiltIn</c>=<c>iid*7</c>, <c>InstancedSH_URP</c>=<c>iid*14</c>)가 쓰는
    /// **<c>StructuredBuffer&lt;float4&gt;</c>(stride 16) 평탄 버퍼의 인스턴스 오프셋 인덱싱**은 미포착이었다.</para>
    ///
    /// <para>이 테스트는 **실제 경로와 동일하게** <see cref="SHInstancedBuffer.Create"/> 로 stride-16 평탄
    /// 버퍼를 만들고(서로 다른 N개 프로브), <c>SHEvalInstance.shader</c> 가 공유 <c>EvaluateSH9.hlsl</c> 의
    /// <c>EvaluateInstanceSH</c>(iid*7)·<c>EvaluateInstanceSH2Axis</c>(iid*14) 로 인스턴스별 평가를 거쳐
    /// ReadPixels 회수한 값을 CPU <see cref="SH9.Evaluate"/> 와 ε 대조한다. 오프셋을 무시하거나 stride를
    /// 틀리면 다른 인스턴스 값을 돌려주므로(프로브가 전부 distinct → 비자명성 가드로 확인) FAIL 로 드러난다.</para>
    ///
    /// 컨벤션: 다른 SH 테스트와 동일 <c>static RunAll()→문자열</c>, <see cref="LightmapEvaluateDebugger"/>
    /// "Run All Tests" 및 전용 메뉴 배선. **렌더 컨텍스트 필요**(Blit/ReadPixels) — 헤드리스 CI 불가.
    /// </summary>
    public static class SHInstanceIndexGpuTests
    {
        /// <param name="epsilon">절대 허용오차(GPU/CPU fp32 반올림 흡수).</param>
        /// <param name="projDirs">테스트 SH 생성용 프로젝션 방향 수.</param>
        /// <param name="instanceCount">업로드할 서로 다른 인스턴스(프로브) 수.</param>
        public static string RunAll(float epsilon = 2e-3f, int projDirs = 512, int instanceCount = 5)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== SHEvalInstance GPU 인스턴스 오프셋 인덱싱 (SH-5, iid*7 / iid*14) ===");
            int pass = 0, total = 0;

            var shader = Shader.Find("HuskyLibs/SHEvalInstance");
            if (shader == null) { sb.AppendLine("  [FAIL] 셰이더 'HuskyLibs/SHEvalInstance' 없음 (등록/컴파일 확인)"); return sb.ToString(); }
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
            { sb.AppendLine("  [SKIP] ARGBFloat RT 미지원 플랫폼 — GPU 검증 불가"); return sb.ToString(); }
            if (SystemInfo.maxComputeBufferInputsFragment <= 0)
                sb.AppendLine("  [WARN] 프래그먼트 StructuredBuffer 제한 가능(결과 0 이면 이 원인)");
            if (instanceCount < 2) instanceCount = 2; // 오프셋 인덱싱 검증은 최소 2 인스턴스

            var mat = new Material(shader);
            var rt = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { useMipMap = false };
            rt.Create();
            var tex = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            var prevActive = RenderTexture.active;
            var normals = TestNormals();

            float maxErr1 = RunSingleProbe(sb, ref pass, ref total, mat, rt, tex, normals, instanceCount, projDirs, epsilon);
            float maxErr2 = RunTwoProbe(sb, ref pass, ref total, mat, rt, tex, normals, instanceCount, projDirs, epsilon);

            RenderTexture.active = prevActive;
            rt.Release(); Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(mat);

            sb.AppendLine($"--- {pass}/{total} PASS (단일 maxErr={maxErr1:0.00000}, 2프로브 maxErr={maxErr2:0.00000}, ε={epsilon}) ---");
            return sb.ToString();
        }

        // ── 1) 단일 프로브(iid*7) — InstancedSH_BuiltIn 경로 ──
        static float RunSingleProbe(StringBuilder sb, ref int pass, ref int total,
            Material mat, RenderTexture rt, Texture2D tex, Vector3[] normals,
            int n, int projDirs, float epsilon)
        {
            mat.SetFloat("_Mode", 0f);

            var sh = new SH9[n];
            var na = new NativeArray<SH9>(n, Allocator.Temp);
            for (int i = 0; i < n; i++) { sh[i] = BuildDistinctSH(i, 2 * n, projDirs); na[i] = sh[i]; }
            var ib = SHInstancedBuffer.Create(na, 1); // 실 경로: stride 16, iid*7
            na.Dispose();
            if (ib.Count != n) Check(sb, ref pass, ref total, $"단일 버퍼 Count({ib.Count})==인스턴스({n})", false);
            ib.Bind(mat, "_InstanceSH");

            float maxErr = 0f;
            for (int i = 0; i < n; i++)
            {
                mat.SetFloat("_InstanceID", i);
                for (int k = 0; k < normals.Length; k++)
                {
                    Vector3 nn = normals[k].normalized;
                    Vector3 gpu = SampleGpu(mat, rt, tex, nn);
                    Vector3 cpu = sh[i].Evaluate(nn);
                    float err = MaxAbs(gpu - cpu);
                    maxErr = Mathf.Max(maxErr, err);
                    Check(sb, ref pass, ref total,
                        $"단일 iid={i} n=({nn.x:0.00},{nn.y:0.00},{nn.z:0.00}) " +
                        $"GPU=({gpu.x:0.000},{gpu.y:0.000},{gpu.z:0.000}) CPU=({cpu.x:0.000},{cpu.y:0.000},{cpu.z:0.000}) err={err:0.00000}",
                        err <= epsilon);
                }
            }
            ib.Dispose();

            // 비자명성 가드: 인스턴스가 실제로 distinct 여야(인덱싱 미동작 시 전부 동일 → 위 대조가 의미없어짐)
            float d = MaxAbs(sh[0].Evaluate(Vector3.up) - sh[1].Evaluate(Vector3.up))
                    + MaxAbs(sh[0].Evaluate(Vector3.right) - sh[1].Evaluate(Vector3.right));
            Check(sb, ref pass, ref total, $"단일 distinctness(inst0≠inst1, Δ={d:0.000}>0.05)", d > 0.05f);
            return maxErr;
        }

        // ── 2) 2-프로브(iid*14, 상/하 블렌드) — InstancedSH_URP 경로 ──
        static float RunTwoProbe(StringBuilder sb, ref int pass, ref int total,
            Material mat, RenderTexture rt, Texture2D tex, Vector3[] normals,
            int n, int projDirs, float epsilon)
        {
            mat.SetFloat("_Mode", 1f);
            // 비자명 블렌드 축(월드 +Y 아님) → upW 전달·해석까지 검증
            Vector3 upW = new Vector3(0.2f, 1f, -0.3f).normalized;
            mat.SetVector("_UpW", new Vector4(upW.x, upW.y, upW.z, 0f));

            var top = new SH9[n];
            var bot = new SH9[n];
            var na = new NativeArray<SH9>(n * 2, Allocator.Temp); // 레이아웃: [2i]=top, [2i+1]=bottom
            for (int i = 0; i < n; i++)
            {
                top[i] = BuildDistinctSH(i, 4 * n, projDirs);
                bot[i] = BuildDistinctSH(i + 2 * n, 4 * n, projDirs); // top 과 겹치지 않는 인덱스대 → top≠bottom
                na[2 * i] = top[i];
                na[2 * i + 1] = bot[i];
            }
            var ib = SHInstancedBuffer.Create(na, 2); // 실 경로: stride 16, iid*14 (top=base, bottom=base+7)
            na.Dispose();
            if (ib.Count != n) Check(sb, ref pass, ref total, $"2프로브 버퍼 Count({ib.Count})==인스턴스({n})", false);
            ib.Bind(mat, "_InstanceSH");

            float maxErr = 0f;
            for (int i = 0; i < n; i++)
            {
                mat.SetFloat("_InstanceID", i);
                for (int k = 0; k < normals.Length; k++)
                {
                    Vector3 nn = normals[k].normalized;
                    Vector3 gpu = SampleGpu(mat, rt, tex, nn);
                    // CPU 미러: EvaluateInstanceSH2Axis = lerp(botEval, topEval, saturate(dot(n,upW)*0.5+0.5))
                    //   (각 프로브는 Evaluate 에서 이미 max(0) 클램프 — HLSL 과 동일 순서)
                    Vector3 eTop = top[i].Evaluate(nn);
                    Vector3 eBot = bot[i].Evaluate(nn);
                    float w = Mathf.Clamp01(Vector3.Dot(nn, upW) * 0.5f + 0.5f);
                    Vector3 cpu = Vector3.Lerp(eBot, eTop, w);
                    float err = MaxAbs(gpu - cpu);
                    maxErr = Mathf.Max(maxErr, err);
                    Check(sb, ref pass, ref total,
                        $"2프로브 iid={i} n=({nn.x:0.00},{nn.y:0.00},{nn.z:0.00}) w={w:0.00} " +
                        $"GPU=({gpu.x:0.000},{gpu.y:0.000},{gpu.z:0.000}) CPU=({cpu.x:0.000},{cpu.y:0.000},{cpu.z:0.000}) err={err:0.00000}",
                        err <= epsilon);
                }
            }
            ib.Dispose();

            // 비자명성 가드: top≠bottom (같으면 블렌드·축 검증이 무의미)
            float d = MaxAbs(top[0].Evaluate(upW) - bot[0].Evaluate(upW))
                    + MaxAbs(top[0].Evaluate(-upW) - bot[0].Evaluate(-upW));
            Check(sb, ref pass, ref total, $"2프로브 distinctness(top≠bottom, Δ={d:0.000}>0.05)", d > 0.05f);
            return maxErr;
        }

        static Vector3 SampleGpu(Material mat, RenderTexture rt, Texture2D tex, Vector3 n)
        {
            mat.SetVector("_ProbeNormal", new Vector4(n.x, n.y, n.z, 0f));
            Graphics.Blit(Texture2D.whiteTexture, rt, mat);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
            tex.Apply(false);
            Color g = tex.GetPixel(0, 0);
            return new Vector3(g.r, g.g, g.b);
        }

        /// <summary>인덱스별 '좁은 방향 로브 + 고유 컬러' 를 프로젝션 → 인스턴스마다 전 계수 distinct.</summary>
        static SH9 BuildDistinctSH(int idx, int spread, int dirs)
        {
            var pts = BurstSHBaker.FibonacchiSphere(dirs, Allocator.Temp);
            float w = 4f * Mathf.PI / dirs;
            // 인덱스별 로브 축(구 전체에 분산) — 황금각 기반 수직 분산으로 축이 겹치지 않게.
            float t = (idx + 0.5f) / Mathf.Max(1, spread);
            float az = t * 6.2831853f;              // 방위각
            float el = (idx * 2.399963f) % 6.2831853f; // 고도각(황금각 스텝)
            Vector3 axis = new Vector3(
                Mathf.Cos(az) * Mathf.Cos(el),
                Mathf.Sin(el),
                Mathf.Sin(az) * Mathf.Cos(el)).normalized;
            // 인덱스별 고유 컬러(채널 비대칭 → 패킹 채널 스왑/오독도 검출).
            Vector3 color = new Vector3(
                0.3f + 0.7f * Frac(idx * 0.2113f + 0.10f),
                0.3f + 0.7f * Frac(idx * 0.3797f + 0.37f),
                0.3f + 0.7f * Frac(idx * 0.5331f + 0.71f));
            var sh = new SH9();
            for (int i = 0; i < pts.Length; i++)
            {
                Vector3 d = pts[i];
                float lobe = Mathf.Max(0f, Vector3.Dot(d, axis));
                lobe *= lobe;                        // 좁은 로브 → L1/L2 유의(DC 외 계수도 채움)
                Vector3 L = color * (0.2f + lobe);   // DC + 방향성
                sh.Accumulate(d, L, w);
            }
            pts.Dispose();
            return sh;
        }

        static float Frac(float v) => v - Mathf.Floor(v);

        static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));

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
