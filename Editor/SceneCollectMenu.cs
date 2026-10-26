#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake.EditorTools
{
    /// <summary>
    /// C0-3 배선 — 씬 수집 → <see cref="AtlasApplyDebug.SetCollected"/>(targets/occluders 교체) + 리포트 로그.
    /// AtlasApplyDebug 에는 세터 하나만 있고 수집·판정은 여기와 <see cref="SceneCollector"/> 에 둔다(C0 결정 ①).
    /// 손 수정: 결과 배열을 인스펙터에서 고치거나, 재수집에도 남기려면 <see cref="SceneCollectOverride"/> 표식(결정 ⑧).
    /// 옵션(<see cref="SceneCollectOptions"/>)은 메뉴 토글(EditorPrefs)로 고른다.
    /// </summary>
    public static class SceneCollectMenu
    {
        const string Root = "HuskyLibs/CustomLightmapper/Scene Collect (C0)/";
        const string PrefNonGi = "HuskyLibs.CustomLightmapper.C0.NonGiAsOccluder";
        const string PrefLod = "HuskyLibs.CustomLightmapper.C0.LodChildAsOccluder";
        const string PrefStrict = "HuskyLibs.CustomLightmapper.C0.Strict";

        // ── AtlasApplyDebug 인스펙터 우클릭 ──

        [MenuItem("CONTEXT/AtlasApplyDebug/Collect Scene (C0)")]
        static void CollectActive(MenuCommand c) => CollectInto((AtlasApplyDebug)c.context, false);

        [MenuItem("CONTEXT/AtlasApplyDebug/Collect Scene - All Loaded Scenes (C0)")]
        static void CollectAll(MenuCommand c) => CollectInto((AtlasApplyDebug)c.context, true);

        // ── 상단 메뉴 ──

        [MenuItem(Root + "Collect Scene → Selected AtlasApplyDebug")]
        static void CollectSelected()
        {
            var dbg = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponent<AtlasApplyDebug>() : null;
            if (dbg == null) { Debug.LogWarning("[C0] AtlasApplyDebug 를 가진 오브젝트를 선택하세요."); return; }
            CollectInto(dbg, false);
        }

        [MenuItem(Root + "Report Only (Dry Run)")]
        static void DryRun()
        {
            if (InPrefabStage()) return;
            Debug.Log(SceneCollector.Collect(null, Options()).Report());
        }

        /// <summary>C0-V1: 같은 씬을 2회 수집해 순서·분류·MeshFilter 목록이 완전히 같은지.</summary>
        [MenuItem(Root + "Verify Determinism (C0-V1)")]
        static void VerifyDeterminism()
        {
            if (InPrefabStage()) return;
            var opt = Options();
            var fa = new List<MeshFilter>();
            var fb = new List<MeshFilter>();
            var a = SceneCollector.Collect(null, opt, outFilters: fa);
            var b = SceneCollector.Collect(null, opt, outFilters: fb);
            int firstDiff = -1;
            int n = Mathf.Max(fa.Count, fb.Count);
            for (int i = 0; i < n && firstDiff < 0; i++)
            {
                bool same = i < fa.Count && i < fb.Count && fa[i] == fb[i]
                         && a.Entries[i].Class == b.Entries[i].Class && a.Entries[i].Reason == b.Entries[i].Reason;
                if (!same) firstDiff = i;
            }
            if (firstDiff < 0)
                Debug.Log($"[C0-V1] PASS — 2회 수집 완전 일치 ({fa.Count}건 · receiver {a.ReceiverCount} · occluder {a.OccluderCount} · excluded {a.ExcludedCount})");
            else
                Debug.LogError($"[C0-V1] FAIL — 첫 불일치 인덱스 {firstDiff} ({(firstDiff < fa.Count ? fa[firstDiff].name : "∅")} vs {(firstDiff < fb.Count ? fb[firstDiff].name : "∅")})");
        }

        /// <summary>C0-4 진단: 선택(자식 포함) MeshFilter 의 용접·퇴화 삼각형 수. 메시·차폐 씬은 건드리지 않는다.</summary>
        [MenuItem(Root + "Diagnose Selected Meshes (Weld · Degenerate)")]
        static void DiagnoseSelected()
        {
            var sb = new System.Text.StringBuilder("[C0] 메시 정제 진단(보고만 — 차폐 씬은 원본 삼각형 순서 유지):\n");
            var seen = new HashSet<Mesh>();
            int count = 0;
            foreach (var go in Selection.gameObjects)
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                {
                    var mesh = mf.sharedMesh;
                    if (mesh == null || !seen.Add(mesh)) continue;
                    count++;
                    if (!SceneCollector.DiagnoseCleanup(mesh, out int welded, out int degen, out int weldedCount))
                    { sb.AppendLine($"  {mesh.name}: Read/Write 꺼짐 — 진단 불가"); continue; }
                    sb.AppendLine($"  {mesh.name}: 정점 {mesh.vertexCount} → 용접 후 {weldedCount} (−{welded}) · 퇴화 삼각형 {degen}");
                }
            if (count == 0) sb.AppendLine("  (선택에 MeshFilter 없음)");
            Debug.Log(sb.ToString());
        }

        // ── 옵션 토글 ──

        [MenuItem(Root + "Options/Non-GI Static As Occluder")]
        static void ToggleNonGi() => EditorPrefs.SetBool(PrefNonGi, !EditorPrefs.GetBool(PrefNonGi, false));
        [MenuItem(Root + "Options/Non-GI Static As Occluder", true)]
        static bool ToggleNonGiValidate() { Menu.SetChecked(Root + "Options/Non-GI Static As Occluder", EditorPrefs.GetBool(PrefNonGi, false)); return true; }

        [MenuItem(Root + "Options/LOD1+ As Occluder")]
        static void ToggleLod() => EditorPrefs.SetBool(PrefLod, !EditorPrefs.GetBool(PrefLod, false));
        [MenuItem(Root + "Options/LOD1+ As Occluder", true)]
        static bool ToggleLodValidate() { Menu.SetChecked(Root + "Options/LOD1+ As Occluder", EditorPrefs.GetBool(PrefLod, false)); return true; }

        [MenuItem(Root + "Options/Strict (Exclude No-UV0 · >255 Submeshes)")]
        static void ToggleStrict() => EditorPrefs.SetBool(PrefStrict, !EditorPrefs.GetBool(PrefStrict, false));
        [MenuItem(Root + "Options/Strict (Exclude No-UV0 · >255 Submeshes)", true)]
        static bool ToggleStrictValidate() { Menu.SetChecked(Root + "Options/Strict (Exclude No-UV0 · >255 Submeshes)", EditorPrefs.GetBool(PrefStrict, false)); return true; }

        // ── 공통 ──

        static SceneCollectOptions Options()
        {
            var o = SceneCollectOptions.Default;
            o.NonGiAsOccluder = EditorPrefs.GetBool(PrefNonGi, false);
            o.LodChildAsOccluder = EditorPrefs.GetBool(PrefLod, false);
            o.WarnOnly = !EditorPrefs.GetBool(PrefStrict, false);
            return o;
        }

        static void CollectInto(AtlasApplyDebug dbg, bool allLoadedScenes)
        {
            if (dbg == null || InPrefabStage()) return;

            var filters = new List<MeshFilter>();
            var col = SceneCollector.Collect(SceneCollector.SceneRoots(allLoadedScenes), Options(), outFilters: filters);
            string report = col.Report();

            // targets 가 비면 ResolveTargets 가 자식 폴백으로 바뀐다 → receiver 0 이면 적용하지 않는다(엉뚱한 집합을 굽는 사고 방지)
            if (col.ReceiverCount == 0)
            {
                Debug.LogWarning("[C0] receiver 0건 — 적용하지 않음(빈 targets 는 자식 폴백으로 동작). 리포트:\n" + report, dbg);
                return;
            }

            var receivers = SceneCollectPolicy.Pick(col, filters, col.ReceiverIdx);
            var occluders = SceneCollectPolicy.Pick(col, filters, col.OccluderIdx);
            Undo.RecordObject(dbg, "Collect Scene (C0)");
            dbg.SetCollected(receivers, occluders);
            PrefabUtility.RecordPrefabInstancePropertyModifications(dbg);
            EditorUtility.SetDirty(dbg);
            Debug.Log(report + $"  → {dbg.name}: targets {receivers.Length} · occluders {occluders.Length} 적용(Undo 가능)", dbg);

            // 다른 씬 오브젝트 참조는 씬 저장 시 직렬화되지 않는다(교차 씬 참조 미지원) — 이번 세션 베이크에만 유효
            int crossScene = 0;
            foreach (var mf in receivers) if (mf.gameObject.scene != dbg.gameObject.scene) crossScene++;
            foreach (var mf in occluders) if (mf.gameObject.scene != dbg.gameObject.scene) crossScene++;
            if (crossScene > 0)
                Debug.LogWarning($"[C0] {crossScene}건이 {dbg.name} 와 다른 씬 — 교차 씬 참조는 저장되지 않으므로 씬을 다시 열면 비어 있다. 베이크는 지금 세션에서 하거나 씬별로 수집할 것.", dbg);
        }

        // 프리팹 편집 중엔 수집 대상(메인 씬 vs 편집 중 프리팹)이 모호하다 → 경고 후 중단(개발문서 §10-2)
        static bool InPrefabStage()
        {
            if (PrefabStageUtility.GetCurrentPrefabStage() == null) return false;
            Debug.LogWarning("[C0] 프리팹 편집 모드에서는 씬 수집을 하지 않습니다. 프리팹 스테이지를 닫고 다시 실행하세요.");
            return true;
        }
    }
}
#endif
