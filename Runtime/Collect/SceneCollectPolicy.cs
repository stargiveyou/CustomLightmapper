using System;
using System.Collections.Generic;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// C0 수집 정책 — 후보(<see cref="CollectCandidate"/>) → Receiver / OccluderOnly / Excluded 3분류 + 사유.
    /// 순수 함수: 부작용 없음 · 결정적 · UnityEditor 미의존(결정 ③) → 헤드리스 테스트 대상(<c>SceneCollectPolicyTests</c>).
    ///
    /// 판정은 단락 평가이고 <b>순서가 곧 리포트 사유의 우선순위</b>다(개발문서 §4.3):
    ///   사용자 제외 → 지오메트리 유효성(사고) → 사용자 occluder 지정 → 라이트맵 플래그(의도).
    ///   사고를 먼저 보고해야 리포트가 쓸모 있고, 사용자 지정은 자동 판정을 항상 이긴다(결정 ⑧).
    /// </summary>
    public static class SceneCollectPolicy
    {
        /// <summary>면적 0 판정 임계(bounds.extents.sqrMagnitude).</summary>
        public const float EmptyBoundsEpsSq = 1e-12f;
        /// <summary>α TriSubmesh(byte) 한도.</summary>
        public const int MaxSubMeshes = 255;

        /// <summary>후보 1건 분류. 부작용 없음 · 결정적 · UnityEditor 미의존.</summary>
        public static CollectClass Classify(in CollectCandidate c, in SceneCollectOptions o, out CollectReason reason)
        {
            // 1~7(+확장): 사용자 제외 · 지오메트리 유효성 — 차폐에도 못 넣는다
            if (c.UserExcluded)          return Excluded(CollectReason.UserExcluded, out reason);
            if (!c.HasMeshFilter)        return Excluded(CollectReason.NoMeshFilter, out reason);
            if (c.MeshNull)              return Excluded(CollectReason.NoMesh, out reason);
            if (!c.MeshReadable)         return Excluded(CollectReason.NotReadable, out reason);   // 결정 ⑤: 양쪽에서 일관되게 제외
            if (!c.HasRenderer)          return Excluded(CollectReason.NoRenderer, out reason);
            if (c.RendererDisabled)      return Excluded(CollectReason.RendererDisabled, out reason);
            if (c.NonTriangleTopology)   return Excluded(CollectReason.NonTriangleTopology, out reason);
            if (c.TriangleCount <= 0 || c.VertexCount <= 0) return Excluded(CollectReason.NoTriangles, out reason);
            if (!(c.BoundsExtentSq > EmptyBoundsEpsSq))     return Excluded(CollectReason.EmptyBounds, out reason);   // NaN 도 제외
            if (!o.WarnOnly)
            {
                if (!c.HasUv0)                         return Excluded(CollectReason.NoUv0, out reason);
                if (c.SubMeshCount > MaxSubMeshes)     return Excluded(CollectReason.TooManySubMeshes, out reason);
            }

            // 8: 사용자 지정은 자동 판정(9~11)을 이긴다
            if (c.UserOccluder) { reason = CollectReason.UserOccluder; return CollectClass.OccluderOnly; }

            // 9~11: 라이트맵 플래그
            if (c.IsLodChild)
            {
                reason = CollectReason.LodChild;
                return o.LodChildAsOccluder ? CollectClass.OccluderOnly : CollectClass.Excluded;
            }
            if (!c.ContributeGI)
            {
                reason = CollectReason.NotContributeGI;
                return o.NonGiAsOccluder ? CollectClass.OccluderOnly : CollectClass.Excluded;
            }
            if (c.ReceiveGiLightProbes) { reason = CollectReason.ReceiveGiLightProbes; return CollectClass.OccluderOnly; }

            reason = CollectReason.None;
            return CollectClass.Receiver;
        }

        /// <summary>제외하지 않는 경고(개발문서 §4.5). 분류와 무관하게 후보만 본다 — <see cref="Run"/> 이 Excluded 에는 붙이지 않는다.</summary>
        public static CollectWarning Warnings(in CollectCandidate c)
        {
            var w = CollectWarning.None;
            if (!c.HasUv0) w |= CollectWarning.NoUv0;
            if (c.SubMeshCount > MaxSubMeshes) w |= CollectWarning.TooManySubMeshes;
            return w;
        }

        /// <summary>후보 배열 → <see cref="SceneCollection"/>. 입력 순서를 그대로 유지한다(정렬 금지 — 결정 ②).</summary>
        public static SceneCollection Run(CollectCandidate[] cands, in SceneCollectOptions o)
        {
            cands ??= Array.Empty<CollectCandidate>();
            var entries = new CollectEntry[cands.Length];
            var rec = new List<int>();
            var occ = new List<int>();
            int excluded = 0;
            for (int i = 0; i < cands.Length; i++)
            {
                var cls = Classify(cands[i], o, out var reason);
                entries[i] = new CollectEntry
                {
                    Order = cands[i].Order,
                    Name = cands[i].Name,
                    Class = cls,
                    Reason = reason,
                    Warnings = cls == CollectClass.Excluded ? CollectWarning.None : Warnings(cands[i]),
                };
                switch (cls)
                {
                    case CollectClass.Receiver: rec.Add(i); break;
                    case CollectClass.OccluderOnly: occ.Add(i); break;
                    default: excluded++; break;
                }
            }
            return new SceneCollection
            {
                Entries = entries,
                ReceiverIdx = rec.ToArray(),
                OccluderIdx = occ.ToArray(),
                ReceiverCount = rec.Count,
                OccluderCount = occ.Count,
                ExcludedCount = excluded,
            };
        }

        /// <summary>
        /// <see cref="SceneCollection.Entries"/> 와 1:1 인 병렬 목록(예: 수집기의 MeshFilter 목록)에서 인덱스 순서대로 뽑는다.
        /// 길이가 다르면 예외 — 병렬 대응이 깨진 결과를 조용히 쓰지 않는다.
        /// </summary>
        public static T[] Pick<T>(SceneCollection col, IReadOnlyList<T> parallel, int[] indices)
        {
            if (col == null) throw new ArgumentNullException(nameof(col));
            if (parallel == null || parallel.Count != col.Entries.Length)
                throw new ArgumentException($"병렬 목록 길이 {parallel?.Count ?? 0} ≠ Entries {col.Entries.Length}", nameof(parallel));
            var outp = new T[indices?.Length ?? 0];
            for (int i = 0; i < outp.Length; i++) outp[i] = parallel[indices[i]];
            return outp;
        }

        static CollectClass Excluded(CollectReason r, out CollectReason reason)
        {
            reason = r;
            return CollectClass.Excluded;
        }
    }
}
