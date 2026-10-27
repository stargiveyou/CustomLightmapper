using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// C0 사용자 오버라이드 표식(결정 ⑧) — 씬 수집기가 자동 판정 대신 이 지정을 따른다.
    /// 수집을 다시 돌려도 유지되는 손 수정 지점(수집 결과 배열은 재수집 때 덮어써진다).
    /// <see cref="includeChildren"/> 면 자식 MeshFilter 에도 적용(가장 가까운 조상 표식이 이긴다) — 나무 프리팹 루트에 1개.
    /// 베이크 경로는 이 컴포넌트를 모른다 — 수집기(Editor)만 읽는다.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SceneCollectOverride : MonoBehaviour
    {
        public enum Mode
        {
            OccluderOnly,   // 굽지 않고 차폐만(잎·대량 프롭) → CollectReason.UserOccluder
            Exclude,        // 양쪽 모두에서 제외 → CollectReason.UserExcluded
        }

        public Mode mode = Mode.OccluderOnly;
        [Tooltip("자식 MeshFilter 에도 적용. 끄면 이 GameObject 의 MeshFilter 만.")]
        public bool includeChildren = true;
    }
}
