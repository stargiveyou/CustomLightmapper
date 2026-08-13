using System.Text;
using UnityEngine;
using HuskyLibs.CustomLightmapper.Bake;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// LightmapAllocator 검증용 디버그 컴포넌트.
    ///  - "Run Self Tests"  : 내장 기본 2케이스
    ///  - "Run Full Tests"  : 확장 11케이스(엣지케이스 포함)
    ///  - "Run Mesh Test"   : 실제 입력 메시들로 월드면적→할당→ST/비겹침 검증
    /// </summary>
    [ExecuteAlways]
    public class LightmapAllocatorDebug : MonoBehaviour
    {
        [Header("Mesh Test 입력")]
        [Tooltip("비우면 이 오브젝트와 자식의 MeshFilter 를 모두 수집.")]
        [SerializeField] MeshFilter[] targets;

        [Header("Allocation Settings")]
        public int atlasResolution = 1024;
        public float texelsPerWorldUnit = 64f;
        public int gutterTexels = 2;
        public int maxPages = 8;

        AllocationSettings BuildSettings() => new AllocationSettings
        {
            AtlasResolution = atlasResolution,
            TexelsPerWorldUnit = texelsPerWorldUnit,
            GutterTexels = gutterTexels,
            MaxPages = maxPages,
        };

        [ContextMenu("Run Self Tests")]
        public void RunSelfTests() => Log(LightmapAllocator.RunSelfTests());

        [ContextMenu("Run Full Tests")]
        public void RunFullTests() => Log(LightmapAllocatorTests.RunAll());

        [ContextMenu("Run Mesh Test")]
        public void RunMeshTest()
        {
            var filters = ResolveTargets();
            if (filters.Length == 0)
            {
                Debug.LogWarning("[LMAlloc] 대상 MeshFilter 가 없습니다. targets 를 지정하거나 자식에 MeshFilter 를 두세요.", this);
                return;
            }

            // 1) 메시별 월드 표면적 → LightmapInstance
            var insts = new LightmapInstance[filters.Length];
            var sb = new StringBuilder();
            sb.AppendLine($"=== LightmapAllocator Mesh Test ({filters.Length} instances) ===");
            for (int i = 0; i < filters.Length; i++)
            {
                var mf = filters[i];
                var mesh = mf.sharedMesh;
                float area = LightmapAllocator.WorldArea(mesh, mf.transform.localToWorldMatrix);
                insts[i] = new LightmapInstance { InstanceId = i, WorldArea = area }; // 추적용 — 인덱스로 충분

                sb.AppendLine($"  [{i}] {mf.name}: worldArea={area:0.000}");
            }

            // 2) 할당
            var s = BuildSettings();
            var r = LightmapAllocator.Allocate(insts, s);

            // 3) 검증 (확장 테스트의 공개 헬퍼 재사용)
            bool stOk = LightmapAllocatorTests.StInRange(r);
            bool noOv = LightmapAllocatorTests.NoOverlap(r);
            bool pagesOk = r.PageCount >= 1 && r.PageCount <= s.MaxPages;
            bool ok = stOk && noOv && pagesOk && !r.Overflow;

            sb.AppendLine($"--- pages={r.PageCount}, util={r.Utilization:P1}, overflow={r.Overflow}");
            sb.AppendLine($"[{Mark(stOk)}] ST∈[0,1]   [{Mark(noOv)}] noOverlap   [{Mark(pagesOk)}] pages≤max   [{Mark(!r.Overflow)}] noOverflow");

            // 인스턴스별 ST/페이지 (매핑 확인용)
            for (int i = 0; i < filters.Length; i++)
            {
                var lm = r.Instances[i];
                sb.AppendLine($"  {filters[i].name}: page={lm.LightmapIndex}, ST={lm.ScaleOffset}");
            }

            if (ok) Debug.Log(sb.ToString(), this);
            else Debug.LogWarning(sb.ToString(), this);
        }

        MeshFilter[] ResolveTargets()
        {
            if (targets != null && targets.Length > 0)
            {
                // null/메시없음 제거
                var list = new System.Collections.Generic.List<MeshFilter>();
                foreach (var mf in targets) if (mf != null && mf.sharedMesh != null) list.Add(mf);
                return list.ToArray();
            }
            // 폴백: 자기 자신+자식에서 수집
            var found = GetComponentsInChildren<MeshFilter>();
            var res = new System.Collections.Generic.List<MeshFilter>();
            foreach (var mf in found) if (mf.sharedMesh != null) res.Add(mf);
            return res.ToArray();
        }

        static string Mark(bool ok) => ok ? "PASS" : "FAIL";

        // FAIL 이 하나라도 있으면 경고로 강조
        void Log(string report)
        {
            if (report.Contains("FAIL")) Debug.LogWarning(report, this);
            else Debug.Log(report, this);
        }
    }
}