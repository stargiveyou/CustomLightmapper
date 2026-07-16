using System.IO;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// SH-1 검증 디버거: 해석적 환경광 L(d)를 SH9로 몬테카를로 프로젝션한 뒤
    ///   (1) 수치 자기검증 — 균등 하늘 라운드트립(R≈L, E≈π·L)·선형성
    ///   (2) 등장방형(equirectangular) 텍스처로 [참조 L | SH 라디언스 R | SH 조도 E] 3분할 출력
    /// 을 만들어 눈+수치로 SH9.Accumulate/Basis/Evaludate 를 검증한다.
    ///
    /// 참조 L(해석식)과 동일 함수를 구 균등 N샘플로 프로젝션 → SH가 저주파 성분을 얼마나
    /// 되살리는지 육안 비교. 라디언스 R = Σ cₗ·Yₗₘ(d) 는 입력 L의 band-limited 근사여야 하고,
    /// 조도 E(n) = Σ Aₗ·cₗ·Yₗₘ(n) 은 Lambert 코사인 컨볼루션(A0=π,A1=2π/3,A2=π/4) 결과다.
    ///
    /// 인스펙터 우클릭 → "Bake SH & Write Texture", "Run Self Tests".
    /// </summary>
    [ExecuteAlways]
    public class SH9BakeTextureTest : MonoBehaviour
    {
        public enum Environment { UniformSky, GradientSky, DirectionalLobe }

        [Header("Environment L(d)  (Linear HDR)")]
        public Environment environment = Environment.GradientSky;
        [Tooltip("UniformSky 전체색 / GradientSky 천정(위쪽)색.")]
        [ColorUsage(false, true)] public Color skyColor = new Color(0.6f, 0.75f, 1.1f);
        [Tooltip("GradientSky 지평(아래쪽)색.")]
        [ColorUsage(false, true)] public Color groundColor = new Color(0.22f, 0.18f, 0.14f);
        [Tooltip("DirectionalLobe 로브 색(HDR).")]
        [ColorUsage(false, true)] public Color lightColor = new Color(3f, 2.8f, 2.4f);
        [Tooltip("빛이 진행하는 방향(로브는 -방향에서 밝음).")]
        public Vector3 lightDirection = new Vector3(-0.3f, -1f, -0.2f);
        [Min(1f)] public float lobeSharpness = 32f;

        [Header("Projection")]
        [Tooltip("N: 구 균등 몬테카를로 샘플 수. weight = 4π/N.")]
        [Min(1)] public int sampleCount = 4096;
        public uint seed = 12345;

        [Header("Texture Output")]
        [Min(8)] public int panelWidth = 256;
        [Min(4)] public int panelHeight = 128;
        [Tooltip("SH 폴더에 저장할 PNG 파일명(확장자 제외).")]
        public string fileName = "SH9_BakeTest";
        [Tooltip("표시용 감마(1/2.2) 인코딩. 끄면 선형 클램프.")]
        public bool gammaEncode = true;

        [Header("Result (read-only)")]
        [SerializeField] Vector3 _c0;
        [SerializeField] string _lastPath;

        SH9 _sh;

        // ── 해석적 환경광 L(d) ── (프로젝션과 참조 패널이 공유하는 단일 진리원)
        Vector3 EvalEnv(Vector3 d)
        {
            switch (environment)
            {
                case Environment.UniformSky:
                    return new Vector3(skyColor.r, skyColor.g, skyColor.b);

                case Environment.GradientSky:
                    {
                        float t = Mathf.Clamp01(d.y * 0.5f + 0.5f); // 아래(-y)=0, 위(+y)=1
                        return new Vector3(
                            Mathf.Lerp(groundColor.r, skyColor.r, t),
                            Mathf.Lerp(groundColor.g, skyColor.g, t),
                            Mathf.Lerp(groundColor.b, skyColor.b, t));
                    }

                default: // DirectionalLobe
                    {
                        Vector3 toLight = -(lightDirection.sqrMagnitude > 1e-8f ? lightDirection.normalized : Vector3.down);
                        float c = Mathf.Max(0f, Vector3.Dot(d, toLight));
                        float g = Mathf.Pow(c, lobeSharpness);
                        return new Vector3(lightColor.r, lightColor.g, lightColor.b) * g;
                    }
            }
        }

        [ContextMenu("Bake SH & Write Texture")]
        public void BakeAndWrite()
        {
            _sh = Project(out int used);
            _c0 = _sh.c0;

            int W = Mathf.Max(8, panelWidth), H = Mathf.Max(4, panelHeight);
            // 세로 3패널: [위] 참조 L | [중] SH 라디언스 R | [아래] SH 조도 E
            var tex = new Texture2D(W, 3 * H, TextureFormat.RGBA32, false, true);
            for (int py = 0; py < H; py++)
            {
                for (int px = 0; px < W; px++)
                {
                    Vector3 d = PixelToDir(px, py, W, H);
                    tex.SetPixel(px, py + 2 * H, Enc(EvalEnv(d)));                 // 참조 L(d)
                    tex.SetPixel(px, py + 1 * H, Enc(ReconstructRadiance(_sh, d))); // Σ c·Y(d)
                    tex.SetPixel(px, py + 0 * H, Enc(_sh.Evaluate(d)));            // E(n)
                }
            }
            tex.Apply();

            string dir = Path.Combine(Application.dataPath, "Study/CustomLightmapper/Script/SH");
            string path = Path.Combine(dir, fileName + ".png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            _lastPath = path;
            if (Application.isPlaying) Destroy(tex); else DestroyImmediate(tex);

            Debug.Log($"[SH9Test] {environment} 프로젝션 N={used}, c0={_sh.c0} → {path}\n" +
                      "패널(위→아래): 참조 L | SH 라디언스(Σc·Y) | SH 조도 E(π 컨볼루션)", this);
#if UNITY_EDITOR
            UnityEditor.AssetDatabase.Refresh();
#endif
        }

        // 구 균등 N샘플 몬테카를로 프로젝션: coeff += L(d)·Y(d)·(4π/N)
        SH9 Project(out int used)
        {
            var sh = new SH9();
            var rng = new Rng(seed);
            used = Mathf.Max(1, sampleCount);
            float weight = 4f * Mathf.PI / used; // 4π = 구 전체 입체각, N = 샘플 수 → 균등 pdf 역수
            for (int i = 0; i < used; i++)
            {
                Vector3 d = UniformSphere(ref rng);
                sh.Accumulate(d, EvalEnv(d), weight);
            }
            return sh;
        }

        // SH 라디언스 재구성 R(d) = Σ cₗₘ·Yₗₘ(d)  (코사인 로브 Aₗ 미적용 — 입력 L의 저주파 근사)
        static Vector3 ReconstructRadiance(SH9 sh, Vector3 d)
        {
            SH9.Basis(d, out float y0, out float y1, out float y2, out float y3,
                         out float y4, out float y5, out float y6, out float y7, out float y8);
            return sh.c0 * y0
                 + sh.c1 * y1 + sh.c2 * y2 + sh.c3 * y3
                 + sh.c4 * y4 + sh.c5 * y5 + sh.c6 * y6 + sh.c7 * y7 + sh.c8 * y8;
        }

        // 균등 구 샘플: z=1-2u, r=√(1-z²), φ=2πv  (pdf = 1/4π)
        static Vector3 UniformSphere(ref Rng rng)
        {
            float z = 1f - 2f * rng.Next();
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
            float phi = 2f * Mathf.PI * rng.Next();
            return new Vector3(r * Mathf.Cos(phi), z, r * Mathf.Sin(phi));
        }

        // equirect 픽셀 → 방향(위=+y). u→방위각, v→극각.
        static Vector3 PixelToDir(int px, int py, int W, int H)
        {
            float u = (px + 0.5f) / W;
            float v = (py + 0.5f) / H;
            float phi = u * 2f * Mathf.PI - Mathf.PI; // [-π, π]
            float theta = (1f - v) * Mathf.PI;         // py=0(아래)→θ=π, py=H(위)→θ=0
            float sT = Mathf.Sin(theta);
            return new Vector3(sT * Mathf.Cos(phi), Mathf.Cos(theta), sT * Mathf.Sin(phi));
        }

        // 선형 RGB → 표시용 Color(감마 근사 + 클램프)
        Color Enc(Vector3 lin)
        {
            if (gammaEncode)
                return new Color(
                    Mathf.Clamp01(Mathf.Pow(Mathf.Max(0f, lin.x), 1f / 2.2f)),
                    Mathf.Clamp01(Mathf.Pow(Mathf.Max(0f, lin.y), 1f / 2.2f)),
                    Mathf.Clamp01(Mathf.Pow(Mathf.Max(0f, lin.z), 1f / 2.2f)), 1f);
            return new Color(Mathf.Clamp01(lin.x), Mathf.Clamp01(lin.y), Mathf.Clamp01(lin.z), 1f);
        }

        [ContextMenu("Run Self Tests")]
        public void RunSelfTestsMenu() => Debug.Log(RunAll(), this);

        /// <summary>
        /// 헤드리스 수치 자기검증. 텍스처 없이 SH9 프로젝션/재구성/조도의 항등식을 단언.
        ///  T1 균등 하늘 L=c: 라디언스 R(any d)≈c, 조도 E(any n)≈π·c
        ///  T2 선형성: proj(a·L)= a·proj(L)  (c0 스케일)
        ///  T3 그라디언트: 위쪽(+y) 조도 > 아래쪽(-y) 조도 (방향성 보존)
        /// </summary>
        public static string RunAll()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== SH9 Bake Self Tests ===");
            int pass = 0, total = 0;

            // T1: 균등 하늘 라운드트립
            //   상수 L=c 는 coeff0 = c·k0·4π = c·3.544908 이 (몬테카를로 노이즈 없이) 정확.
            //   고차 밴드는 0 기대값이나 유한 샘플 잔차가 있어, E 는 6축 평균으로 노이즈를 상쇄해 π·c 와 비교.
            {
                Vector3 c = new Vector3(0.3f, 0.6f, 0.9f);
                var sh = ProjectFunc(_ => c, 8192, 777u);
                bool okC0 = Approx(sh.c0, c * 3.544908f, 0.01f);
                Vector3 dirs6 = Vector3.zero;
                Vector3[] axes = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
                foreach (var a in axes) dirs6 += sh.Evaluate(a);
                Vector3 eMean = dirs6 / axes.Length;
                bool okE = Approx(eMean, c * Mathf.PI, 0.03f);
                Check(sb, ref pass, ref total, "T1a 균등 coeff0 = c·3.5449", okC0, $"c0={sh.c0} c·3.5449={c * 3.544908f}");
                Check(sb, ref pass, ref total, "T1b 균등 조도 mean(E)≈π·c", okE, $"E̅={eMean} π·c={c * Mathf.PI}");
            }

            // T2: 선형성 (c0 는 3.5449·c 근처지만, 정확값 대신 스케일 비례만 검증)
            {
                Vector3 c = new Vector3(0.4f, 0.4f, 0.4f);
                var sh1 = ProjectFunc(_ => c, 4096, 42u);
                var sh2 = ProjectFunc(_ => c * 2.5f, 4096, 42u);
                bool ok = Approx(sh2.c0, sh1.c0 * 2.5f, 2e-3f);
                Check(sb, ref pass, ref total, "T2 선형성 proj(2.5L)=2.5·proj(L)", ok, $"c0₁={sh1.c0} c0₂={sh2.c0}");
            }

            // T3: 그라디언트 방향성 (위 밝고 아래 어두운 하늘 → 위 노멀 조도가 더 큼)
            {
                Vector3 zenith = new Vector3(1f, 1f, 1f), ground = new Vector3(0.05f, 0.05f, 0.05f);
                var sh = ProjectFunc(d => Vector3.Lerp(ground, zenith, Mathf.Clamp01(d.y * 0.5f + 0.5f)), 8192, 99u);
                float up = sh.Evaluate(Vector3.up).x;
                float down = sh.Evaluate(Vector3.down).x;
                bool ok = up > down + 0.05f;
                Check(sb, ref pass, ref total, "T3 그라디언트 방향성 E(up)>E(down)", ok, $"up={up:F3} down={down:F3}");
            }

            sb.AppendLine($"--- {pass}/{total} passed ---");
            return sb.ToString();
        }

        // 임의 L(d) 함수를 구 균등 N샘플로 프로젝션(정적, 테스트 공용)
        static SH9 ProjectFunc(System.Func<Vector3, Vector3> L, int n, uint seed)
        {
            var sh = new SH9();
            var rng = new Rng(seed);
            n = Mathf.Max(1, n);
            float w = 4f * Mathf.PI / n;
            for (int i = 0; i < n; i++)
            {
                Vector3 d = UniformSphere(ref rng);
                sh.Accumulate(d, L(d), w);
            }
            return sh;
        }

        static bool Approx(Vector3 a, Vector3 b, float eps) =>
            Mathf.Abs(a.x - b.x) <= eps && Mathf.Abs(a.y - b.y) <= eps && Mathf.Abs(a.z - b.z) <= eps;

        static void Check(System.Text.StringBuilder sb, ref int pass, ref int total, string name, bool ok, string detail)
        {
            total++;
            if (ok) { pass++; sb.AppendLine($"  [PASS] {name}"); }
            else sb.AppendLine($"  [FAIL] {name}  ({detail})");
        }
    }
}
