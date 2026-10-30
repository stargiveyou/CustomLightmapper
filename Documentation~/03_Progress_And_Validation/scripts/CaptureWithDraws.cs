// 베이커 표시(식생·인스턴스)까지 담는 카메라 캡처 — Unity CLI `eval_file` 본문 (UnityCLI_테스트_체크리스트.md 부록 E)
//   unity command eval_file CaptureWithDraws.cs --project-path "$P" --timeout 120 --format json
// ProbeVolumeBaker · InstanceSceneBaker 는 Update() 에서 Graphics.DrawMeshInstancedIndirect 로 그린다. 편집 모드에선 Update 가
// 씬이 바뀔 때만 돌아 `unity command screenshot` 프레임엔 드로가 없다 → Update 를 직접 부르고 같은 프레임에 카메라를 RT 로 렌더한다.

string outPath = System.IO.Path.GetFullPath(UnityEngine.Application.dataPath + "/../Temp/verification_capture.png");
int w = 1600, h = 900;

var cam = UnityEngine.Camera.main;
if (cam == null) return "Main Camera 없음";
var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
int calls = 0;
foreach (var mb in UnityEngine.Object.FindObjectsByType<UnityEngine.MonoBehaviour>(UnityEngine.FindObjectsInactive.Exclude))
{
    if (!(mb is HuskyLibs.CustomLightmapper.Bake.ProbeVolumeBaker) && !(mb is HuskyLibs.CustomLightmapper.Bake.InstanceSceneBaker)) continue;
    var u = mb.GetType().GetMethod("Update", flags);
    if (u != null) { u.Invoke(mb, null); calls++; }
}

var rt = new UnityEngine.RenderTexture(w, h, 24, UnityEngine.RenderTextureFormat.ARGB32);
var prevTarget = cam.targetTexture;
cam.targetTexture = rt;
cam.Render();
cam.targetTexture = prevTarget;
var prevActive = UnityEngine.RenderTexture.active;
UnityEngine.RenderTexture.active = rt;
var tex = new UnityEngine.Texture2D(w, h, UnityEngine.TextureFormat.RGB24, false);
tex.ReadPixels(new UnityEngine.Rect(0, 0, w, h), 0, 0);
tex.Apply();
UnityEngine.RenderTexture.active = prevActive;
System.IO.File.WriteAllBytes(outPath, UnityEngine.ImageConversion.EncodeToPNG(tex));
UnityEngine.Object.DestroyImmediate(tex);
rt.Release();
UnityEngine.Object.DestroyImmediate(rt);
return "베이커 Draw " + calls + "개 → " + outPath;
