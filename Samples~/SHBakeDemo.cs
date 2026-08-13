using HuskyLibs.CustomLightmapper.Bake;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper
{
    /// <summary>
    /// SH-1~4 엔드투엔드 데모: (Mesh+Matrix 어댑터) → BurstScene → BurstSHBaker → SHDebugView.
    /// 씬 오브젝트에 붙이고 ContextMenu "Bake SH (Demo)" 실행 → 대표점에 SH 기즈모 표시.
    /// 실제 프로젝트에선 templates/matrices 를 인스턴싱 소스에서 주입. 여기선 데모용 자동 생성.
    /// 주의: MatrixInstanceSource/BurstScene/BurstSHBaker/SH9(전달) + 실측 게이트 전제.
    /// </summary>
    /// 

    /*
    [1] 데모 템플릿·행렬 생성   MakeBox / MakeMatrices
        ↓
    [2] 어댑터                 MatrixInstanceSource.Build(input)
        ↓  (Mesh+Matrix → 로컬 Tri·Instance·대표점·알베도, 코어 무수정)
    [3] Burst 씬               s.ToBurstScene()  → BurstScene(POD)
        ↓
    [4] SH 베이크              BurstSHBaker.Bake(bscene, sky, sun, points, dirs)
        ↓  (인스턴스당 구면 샘플 → 입사 조도 → SH9 프로젝션)
    [5] 시각화                 SHDebugView.SetData(points, sh)
    */

    [RequireComponent(typeof(SHDebugView))]
    public class SHBakeDemo : MonoBehaviour
    {
        [Header("데모 씬")]
        public int propCountPerTemplate = 200;
        public float spread = 10f;
        public int shDirs = 512;
        [Tooltip("대표점(=박스 AABB 중심)은 솔리드 내부라 자기차폐로 SH가 0(검정)이 됨. " +
                 "프로브를 박스 위 이만큼 띄워 하늘/이웃이 보이게 함(가장 큰 박스 반높이+여유 이상).")]
        [Min(0f)] public float probeLift = 0.8f;

        [Header("광원/하늘")]
        public Vector3 sunDir = new Vector3(-0.3f, -1f, -0.2f);
        public float sunItensity = 1.3f;
        public Vector3 skyTop = new Vector3(0.5f, 0.7f, 1.0f);
        public Vector3 skyBottom = new Vector3(0.1f, 0.1f, 0.12f);

        [ContextMenu("Bake SH (Demo)")]
        public void Bake()
        {
            //데모 탬플릿 2종(작은 박스)
            var m0 = MakeBox(0.3f);
            var m1 = MakeBox(0.5f);
            var input = new MatrixInstanceInput
            {
                templates = new[] { m0, m1 },
                templateAlbedo = new[] { new Vector3(0.7f, 0.4f, 0.3f), new Vector3(0.35f, 0.6f, 0.4f) },
                instanceMatrices = new[] { MakeMatrices(propCountPerTemplate, 1), MakeMatrices(propCountPerTemplate, 2) }
            };
            // 대표점을 박스 윗면 위로 probeLift 만큼 올려 솔리드 내부 자기차폐(갇힌 프로브) 회피.
            using var s = TemplateInstanceSource.BuildScene(input, Unity.Collections.Allocator.TempJob, BVH.BuildQuality.SAH, surfaceLift: probeLift);
            using var bscene = s.ToBurstScene(Unity.Collections.Allocator.TempJob);

            var sun = new DirectionalLight() { Direction = sunDir, Color = Vector3.one, Intensity = sunItensity };
            var sky = BurstSky.Gradient(skyTop, skyBottom);

            // 대표점(s.instancePoints)은 BuildScene 의 surfaceLift 로 이미 박스 윗면 위로 올라가 있음.
            var pts = new NativeArray<Vector3>(s.instancePoints.Length, Allocator.TempJob);
            for (int i = 0; i < pts.Length; i++) pts[i] = s.instancePoints[i];

            var sh = BurstSHBaker.Bake(bscene, sky, sun, pts, shDirs, Allocator.TempJob);

            var shMgd = new SH9[sh.Length];
            sh.CopyTo(shMgd);

            GetComponent<SHDebugView>().SetData(s.instancePoints, shMgd);

            Debug.Log($"[SHBakeDemo] SH 베이크 완료: instances={sh.Length}, dirs={shDirs}. {TemplateInstanceSource.Summary(s)}");

            sh.Dispose(); pts.Dispose();
            DestroyImmediate(m0); DestroyImmediate(m1);
        }


        Matrix4x4[] MakeMatrices(int count, int seed)
        {
            var rng = new System.Random(seed);
            var arr = new Matrix4x4[count];
            for (int i = 0; i < count; i++)
            {
                var pos = new Vector3(R(rng) * spread, 0.5f + R(rng) * 0.3f, R(rng) * spread);
                var rot = Quaternion.Euler(0, (float)rng.NextDouble() * 360f, 0);
                arr[i] = Matrix4x4.TRS(pos, rot, Vector3.one);
            }
            return arr;
        }
        static float R(System.Random rng) => (float)(rng.NextDouble() * 2 - 1);

        static Mesh MakeBox(float h)
        {
            var v = new[]
            {
                new Vector3(-h,-h,-h), new Vector3(h,-h,-h), new Vector3(h,h,-h), new Vector3(-h,h,-h),
                new Vector3(-h,-h,h),  new Vector3(h,-h,h),  new Vector3(h,h,h),  new Vector3(-h,h,h),
            };
            var t = new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5, 0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2 };
            var m = new Mesh { name = "demoBox" };
            m.vertices = v; m.triangles = t; m.RecalculateBounds();
            return m;
        }


    }
}
