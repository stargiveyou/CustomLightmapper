using System;
using System.Collections.Generic;
using System.Text;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>C0 3분류. v14.11 receiver/occluder 계약에 그대로 대응한다(설계 결정 ④).</summary>
    public enum CollectClass
    {
        Receiver = 0,       // 라이트맵을 굽는다 → AtlasApplyDebug.targets
        OccluderOnly = 1,   // 차폐만 참여, 굽지 않는다 → AtlasApplyDebug.occluders
        Excluded = 2,       // 양쪽 모두에서 뺀다
    }

    /// <summary>Excluded/강등 사유. 리포트에 그대로 찍히므로 사용자가 읽을 수 있어야 한다(결정 ⑧).</summary>
    public enum CollectReason
    {
        None = 0,
        UserExcluded,        // 사용자가 명시 제외
        NoMeshFilter,        // MeshFilter 없음
        NoMesh,              // sharedMesh == null
        NotReadable,         // mesh.isReadable == false  ← 아틀라스 슬롯만 먹고 차폐에서 빠지던 케이스(설계 §1.3)
        NoRenderer,          // MeshRenderer 없음
        EmptyBounds,         // 렌더러 bounds 가 0 → 면적 0
        NoTriangles,         // 인덱스 0
        LodChild,            // LODGroup 의 LOD1+ 전용 렌더러
        NotContributeGI,     // StaticEditorFlags.ContributeGI 꺼짐
        ReceiveGiLightProbes,// receiveGI = LightProbes → 라이트맵 안 받음(→ OccluderOnly)
        UserOccluder,        // 사용자가 occluder-only 로 지정(→ OccluderOnly)
        RendererDisabled,    // MeshRenderer.enabled == false — 비활성 오브젝트와 같은 이유로 굽지도 차폐하지도 않는다
        NonTriangleTopology, // 서브메시 토폴로지 ≠ Triangles — GetIndexCount/3 이 삼각형 수가 아니다(개발문서 §10-5)
        NoUv0,               // UV0 없음 — WarnOnly=false 일 때만 제외 사유(기본은 경고)
        TooManySubMeshes,    // 서브메시 > 255 — WarnOnly=false 일 때만 제외 사유(기본은 경고)
    }

    /// <summary>제외하지 않고 리포트에만 남기는 경고(개발문서 §4.5). 비트 OR.</summary>
    [Flags]
    public enum CollectWarning
    {
        None = 0,
        NoUv0 = 1 << 0,              // α 알파 컷아웃이 이 메시에서 자동 비활성(AlphaMaskBuilder meshHasUv → matId=-1)
        TooManySubMeshes = 1 << 1,   // TriSubmesh 가 byte 라 255 에서 clamp(α 설계 §10.4)
    }

    /// <summary>
    /// 정책 입력 1건 — <b>이미 조회가 끝난 값만</b> 담는다. 정책은 UnityEditor·메시 배열에 접근하지 않는다(결정 ③·⑦).
    /// Editor 어댑터(<c>SceneCollector</c>)가 배열 복사 없는 API(GetIndexCount·HasVertexAttribute·vertexCount·bounds)로 채운다.
    /// </summary>
    public struct CollectCandidate
    {
        public int    Order;          // 씬 순회 순서. 정책은 이 값을 재정렬하지 않는다(결정 ②)
        public string Name;           // 로그 전용

        // 구성 요소 유무
        public bool   HasMeshFilter;
        public bool   HasRenderer;
        public bool   RendererDisabled; // MeshRenderer.enabled == false (기본값 false = 켜짐 — POD 기본값이 안전한 쪽)
        public bool   MeshNull;

        // 메시 메타데이터 — 전부 배열 복사 없는 접근(개발문서 §4.4)
        public bool   MeshReadable;   // mesh.isReadable
        public int    VertexCount;    // mesh.vertexCount
        public int    TriangleCount;  // Σ GetIndexCount(s) / 3
        public int    SubMeshCount;   // mesh.subMeshCount
        public bool   HasUv0;         // mesh.HasVertexAttribute(VertexAttribute.TexCoord0)
        public bool   NonTriangleTopology; // 어느 서브메시든 GetTopology(s) != Triangles

        // 트랜스폼/렌더러
        public float  BoundsExtentSq; // renderer.bounds.extents.sqrMagnitude (면적 0 판정용 — WorldArea 아님)

        // 라이트맵 관련 플래그(Editor 어댑터가 조회)
        public bool   ContributeGI;
        public bool   ReceiveGiLightProbes;
        public bool   IsLodChild;

        // 사용자 오버라이드
        public bool   UserOccluder;
        public bool   UserExcluded;
    }

    /// <summary>분류 결과 1건. <see cref="SceneCollection.Entries"/> 의 원소.</summary>
    public struct CollectEntry
    {
        public int            Order;
        public string         Name;
        public CollectClass   Class;
        public CollectReason  Reason;    // Receiver 면 None
        public CollectWarning Warnings;  // Excluded 면 None(베이크에 안 들어가므로 무의미)
    }

    /// <summary>정책 옵션. <b><see cref="Default"/> 를 쓸 것</b> — new() 는 WarnOnly=false(경고 항목까지 제외).</summary>
    public struct SceneCollectOptions
    {
        /// <summary>ContributeGI 가 꺼진 정적 오브젝트를 occluder 로 살릴지. 기본 false(제외).</summary>
        public bool NonGiAsOccluder;
        /// <summary>LOD1+ 를 occluder 로 살릴지. 기본 false — LOD0 가 이미 같은 자리를 차폐한다.</summary>
        public bool LodChildAsOccluder;
        /// <summary>서브메시 255 초과·UV0 없음을 경고로만 남길지(true) 제외할지(false). 기본 true.</summary>
        public bool WarnOnly;

        public static SceneCollectOptions Default => new SceneCollectOptions { WarnOnly = true };
    }

    /// <summary>
    /// C0 수집 결과 — 직렬화 대상이 아닌 값(결정 ⑧). <see cref="SceneCollectPolicy.Run"/> 이 만들고
    /// Editor 수집기가 <see cref="RootCount"/>·<see cref="ElapsedMs"/> 를 채운다.
    /// </summary>
    public sealed class SceneCollection
    {
        public CollectEntry[] Entries = Array.Empty<CollectEntry>();   // 입력 순서 = 씬 순회 순서(결정 ②)
        public int[] ReceiverIdx = Array.Empty<int>();                 // Entries 인덱스, 순서 보존
        public int[] OccluderIdx = Array.Empty<int>();
        public int ReceiverCount, OccluderCount, ExcludedCount;

        // 리포트 머리줄용(수집기가 채움 — 정책만 돌리면 0)
        public int RootCount;
        public double ElapsedMs;

        /// <summary>
        /// 사유별 집계 → 경고 → 상세(Excluded 사유별 이름 목록, 최대 <paramref name="maxDetail"/>건) — 개발문서 §7 형식.
        /// 집계는 건수 내림차순(동률은 분류·사유 enum 순 — 결정적).
        /// </summary>
        public string Report(int maxDetail = 20)
        {
            var sb = new StringBuilder();
            sb.Append($"[C0] 수집 완료 — receiver {ReceiverCount} · occluder {OccluderCount} · excluded {ExcludedCount}");
            sb.AppendLine($"  (roots {RootCount}, 순회 {Entries.Length}건, {ElapsedMs:0}ms)");

            // ── 분류 사유 집계(Receiver/None 제외) ──
            var groups = new Dictionary<(CollectClass, CollectReason), List<string>>();
            foreach (var e in Entries)
            {
                if (e.Reason == CollectReason.None) continue;
                var key = (e.Class, e.Reason);
                if (!groups.TryGetValue(key, out var names)) groups[key] = names = new List<string>();
                names.Add(e.Name);
            }
            var keys = new List<(CollectClass c, CollectReason r)>(groups.Keys);
            keys.Sort((a, b) =>
            {
                int d = groups[b].Count.CompareTo(groups[a].Count);
                if (d != 0) return d;
                d = a.c.CompareTo(b.c);
                return d != 0 ? d : a.r.CompareTo(b.r);
            });
            if (keys.Count > 0)
            {
                sb.AppendLine("  분류 사유:");
                foreach (var k in keys)
                {
                    sb.Append($"    {k.c,-12}  {k.r,-20}  {groups[k].Count,5}");
                    if (k.r == CollectReason.NotReadable) sb.Append("   ← 아틀라스 슬롯만 먹고 차폐에서 빠지던 케이스");
                    sb.AppendLine();
                }
            }

            // ── 경고(베이크에 들어가는 항목만) ──
            var noUv = new List<string>();
            var manySub = new List<string>();
            foreach (var e in Entries)
            {
                if ((e.Warnings & CollectWarning.NoUv0) != 0) noUv.Add(e.Name);
                if ((e.Warnings & CollectWarning.TooManySubMeshes) != 0) manySub.Add(e.Name);
            }
            if (noUv.Count > 0 || manySub.Count > 0)
            {
                sb.AppendLine("  경고:");
                if (noUv.Count > 0)
                    sb.AppendLine($"    UV0 없음 {noUv.Count}건 → 해당 메시는 α 알파 컷아웃 비활성 ({Names(noUv, maxDetail)})");
                if (manySub.Count > 0)
                    sb.AppendLine($"    서브메시 255 초과 {manySub.Count}건 → α 서브메시 인덱스(byte) clamp ({Names(manySub, maxDetail)})");
            }

            // ── 상세: Excluded 사유별 이름(조치 문구 포함) ──
            foreach (var k in keys)
            {
                if (k.c != CollectClass.Excluded) continue;
                sb.AppendLine($"  상세({k.r}):");
                sb.Append($"    {Names(groups[k], maxDetail)}");
                string action = Action(k.r);
                if (action != null) sb.Append($"   → {action}");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        static string Names(List<string> names, int max)
        {
            int n = Math.Min(names.Count, Math.Max(0, max));
            var s = string.Join(", ", names.GetRange(0, n));
            return names.Count > n ? s + ", …" : s;
        }

        static string Action(CollectReason r) => r switch
        {
            CollectReason.NotReadable => "임포터 Read/Write Enabled 를 켜세요",
            CollectReason.NonTriangleTopology => "Triangles 토폴로지 메시만 지원합니다",
            CollectReason.NotContributeGI => "Static ▸ Contribute GI 를 켜거나 NonGiAsOccluder 로 차폐만 참여",
            CollectReason.LodChild => "LOD0 가 굽고 차폐합니다(LodChildAsOccluder 로 차폐 참여 가능)",
            _ => null,
        };
    }
}
