// CustomLightmapper 검증 씬 생성 — Unity CLI `eval_file` 본문 (UnityCLI_테스트_체크리스트.md §5.1)
//   unity command eval_file BuildVerificationScene.cs --project-path "$P" --timeout 600 --format json
// 만드는 것: Assets/CustomLightmapper/Verification/ 아래 씬 1개 + 생성 에셋(지형 메시·나무·풀 메시, 잎·풀 알파 텍스처, 머티리얼)
// 확인 대상: B2 실지형(층 모양·절감률·미스 열·groundRoots) · B1-6 잎 카드 차폐 · 식생 PerPixelVolume · L2 램프 3종 · C0 템플릿 중복
// 이 파일은 Documentation~ 안에 있어 Unity 가 컴파일하지 않는다. 다시 만들려면 overwrite = true.

bool overwrite = false;   // true = 기존 Verification 폴더(씬·생성 에셋)를 지우고 다시 만든다
bool bake = true;         // false = 씬만 만들고 베이크는 손으로

const string Dir = "Assets/CustomLightmapper/Verification";
const string Gen = Dir + "/Generated";
const string ScenePath = Dir + "/CL_Verification_Scene.unity";

if (UnityEditor.EditorApplication.isPlaying) return "플레이 모드 — 종료 후 실행";
var opened = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
if (opened.isDirty && !(overwrite && opened.path.StartsWith(Dir))) return "열린 씬에 저장 안 된 변경 — 먼저 저장/버림 결정";   // 덮어쓸 검증 씬 자신은 버린다
if (UnityEditor.AssetDatabase.IsValidFolder(Dir))
{
    if (!overwrite) return Dir + " 가 이미 있음 — 다시 만들려면 overwrite = true";
    if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().path.StartsWith(Dir))   // 지울 씬이 열려 있으면 먼저 닫는다
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
    UnityEditor.AssetDatabase.DeleteAsset(Dir);
}
if (overwrite)   // 이전 베이크 결과(이 씬 이름으로 시작) — 남아 있으면 새 결과가 '… 1.asset' 으로 생긴다
    foreach (var guid in UnityEditor.AssetDatabase.FindAssets("CL_Verification_Scene_", new[] { "Assets/CustomLightmapper/BakeOutput" }))
    {
        string p = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
        if (System.IO.Path.GetFileName(p).StartsWith("CL_Verification_Scene_")) UnityEditor.AssetDatabase.DeleteAsset(p);
    }
if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/CustomLightmapper")) UnityEditor.AssetDatabase.CreateFolder("Assets", "CustomLightmapper");
UnityEditor.AssetDatabase.CreateFolder("Assets/CustomLightmapper", "Verification");
UnityEditor.AssetDatabase.CreateFolder(Dir, "Generated");

var rng = new System.Random(20261030);
float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);

// ───────────── 지형 높이: 언덕 2개 + 잔물결 + 동쪽 12° 경사 ─────────────
float H(float x, float z)
{
    float h = 3.2f * UnityEngine.Mathf.Exp(-((x - 10f) * (x - 10f) + (z - 8f) * (z - 8f)) / 90f)
            + 2.4f * UnityEngine.Mathf.Exp(-((x + 13f) * (x + 13f) + (z + 11f) * (z + 11f)) / 70f)
            + 0.5f * UnityEngine.Mathf.Sin(x * 0.17f) * UnityEngine.Mathf.Cos(z * 0.13f);
    if (x > 16f) h += (x - 16f) * 0.2126f;   // tan 12°
    return h;
}

UnityEngine.Mesh SaveMesh(string name, System.Collections.Generic.List<UnityEngine.Vector3> v, System.Collections.Generic.List<UnityEngine.Vector3> n,
                          System.Collections.Generic.List<UnityEngine.Vector2> uv, System.Collections.Generic.List<int> t)
{
    var m = new UnityEngine.Mesh { name = name };
    m.SetVertices(v);
    m.SetUVs(0, uv);
    m.SetTriangles(t, 0);
    if (n != null) m.SetNormals(n); else m.RecalculateNormals();
    m.RecalculateBounds();
    m.RecalculateTangents();
    UnityEditor.AssetDatabase.CreateAsset(m, Gen + "/" + name + ".asset");   // Read/Write 켜진 채 저장(베이크 필수)
    return m;
}

void Quad(System.Collections.Generic.List<UnityEngine.Vector3> v, System.Collections.Generic.List<UnityEngine.Vector3> n, System.Collections.Generic.List<UnityEngine.Vector2> uv,
          System.Collections.Generic.List<int> t, UnityEngine.Vector3 c, UnityEngine.Quaternion q, float w, float h, float y0, UnityEngine.Vector3 normalBias)
{
    int b = v.Count;
    v.Add(c + q * new UnityEngine.Vector3(-w * 0.5f, y0, 0f));
    v.Add(c + q * new UnityEngine.Vector3(w * 0.5f, y0, 0f));
    v.Add(c + q * new UnityEngine.Vector3(w * 0.5f, y0 + h, 0f));
    v.Add(c + q * new UnityEngine.Vector3(-w * 0.5f, y0 + h, 0f));
    var nn = (q * UnityEngine.Vector3.back + normalBias).normalized;
    for (int i = 0; i < 4; i++) n.Add(nn);
    uv.Add(new UnityEngine.Vector2(0, 0)); uv.Add(new UnityEngine.Vector2(1, 0)); uv.Add(new UnityEngine.Vector2(1, 1)); uv.Add(new UnityEngine.Vector2(0, 1));
    t.Add(b); t.Add(b + 2); t.Add(b + 1); t.Add(b); t.Add(b + 3); t.Add(b + 2);
}

UnityEngine.Texture2D SaveTex(string name, UnityEngine.Color32[] px, int w)
{
    var tex = new UnityEngine.Texture2D(w, w, UnityEngine.TextureFormat.RGBA32, false);
    tex.SetPixels32(px);
    tex.Apply();
    string path = Gen + "/" + name + ".png";
    System.IO.File.WriteAllBytes(path, UnityEngine.ImageConversion.EncodeToPNG(tex));
    UnityEngine.Object.DestroyImmediate(tex);
    UnityEditor.AssetDatabase.ImportAsset(path);
    var ti = (UnityEditor.TextureImporter)UnityEditor.AssetImporter.GetAtPath(path);
    ti.alphaIsTransparency = true;
    ti.wrapMode = UnityEngine.TextureWrapMode.Clamp;
    ti.SaveAndReimport();
    return UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Texture2D>(path);
}

UnityEngine.Material SaveMat(string name, UnityEngine.Shader sh, UnityEngine.Color col)
{
    var m = new UnityEngine.Material(sh) { name = name, enableInstancing = true };
    if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", col);
    if (m.HasProperty("_Color")) m.SetColor("_Color", col);
    UnityEditor.AssetDatabase.CreateAsset(m, Gen + "/" + name + ".mat");
    return m;
}

var sb = new System.Text.StringBuilder();

// ───────────── 메시 ─────────────
const int Cells = 64; const float Size = 64f;
var tv = new System.Collections.Generic.List<UnityEngine.Vector3>(); var tuv = new System.Collections.Generic.List<UnityEngine.Vector2>(); var tt = new System.Collections.Generic.List<int>();
for (int j = 0; j <= Cells; j++)
    for (int i = 0; i <= Cells; i++)
    {
        float x = -Size * 0.5f + Size * i / Cells, z = -Size * 0.5f + Size * j / Cells;
        tv.Add(new UnityEngine.Vector3(x, H(x, z), z));
        tuv.Add(new UnityEngine.Vector2((float)i / Cells, (float)j / Cells));
    }
for (int j = 0; j < Cells; j++)
    for (int i = 0; i < Cells; i++)
    {
        int a = j * (Cells + 1) + i, b = a + 1, c = a + Cells + 1, d = c + 1;
        tt.Add(a); tt.Add(c); tt.Add(b); tt.Add(b); tt.Add(c); tt.Add(d);
    }
var terrainMesh = SaveMesh("VT_Terrain", tv, null, tuv, tt);

var kv = new System.Collections.Generic.List<UnityEngine.Vector3>(); var kn = new System.Collections.Generic.List<UnityEngine.Vector3>();
var kuv = new System.Collections.Generic.List<UnityEngine.Vector2>(); var kt = new System.Collections.Generic.List<int>();
const int Seg = 10;
for (int i = 0; i <= Seg; i++)
{
    float ang = 2f * UnityEngine.Mathf.PI * i / Seg;
    var dir = new UnityEngine.Vector3(UnityEngine.Mathf.Cos(ang), 0f, UnityEngine.Mathf.Sin(ang));
    kv.Add(dir * 0.2f + UnityEngine.Vector3.up * -0.3f); kn.Add(dir); kuv.Add(new UnityEngine.Vector2((float)i / Seg, 0f));
    kv.Add(dir * 0.12f + UnityEngine.Vector3.up * 3.6f); kn.Add(dir); kuv.Add(new UnityEngine.Vector2((float)i / Seg, 1f));
}
for (int i = 0; i < Seg; i++) { int a = i * 2; kt.Add(a); kt.Add(a + 1); kt.Add(a + 2); kt.Add(a + 2); kt.Add(a + 1); kt.Add(a + 3); }
var trunkMesh = SaveMesh("VT_TreeTrunk", kv, kn, kuv, kt);

var lv = new System.Collections.Generic.List<UnityEngine.Vector3>(); var ln = new System.Collections.Generic.List<UnityEngine.Vector3>();
var luv = new System.Collections.Generic.List<UnityEngine.Vector2>(); var lt = new System.Collections.Generic.List<int>();
for (int k = 0; k < 18; k++)   // 수관 = 잎 카드 18장(1.5m, 무작위 방향)
{
    var c = new UnityEngine.Vector3(R(-1.3f, 1.3f), R(2.5f, 4.5f), R(-1.3f, 1.3f));
    Quad(lv, ln, luv, lt, c, UnityEngine.Quaternion.Euler(R(-40f, 40f), R(0f, 360f), R(-40f, 40f)), 1.5f, 1.5f, -0.75f, UnityEngine.Vector3.zero);
}
var leavesMesh = SaveMesh("VT_TreeLeaves", lv, ln, luv, lt);

var gv = new System.Collections.Generic.List<UnityEngine.Vector3>(); var gn = new System.Collections.Generic.List<UnityEngine.Vector3>();
var guv = new System.Collections.Generic.List<UnityEngine.Vector2>(); var gt = new System.Collections.Generic.List<int>();
for (int k = 0; k < 3; k++)   // 풀 = 십자 카드 3장, 피벗 = 밑동
    Quad(gv, gn, guv, gt, UnityEngine.Vector3.zero, UnityEngine.Quaternion.Euler(0f, 60f * k, 0f), 0.7f, 0.55f, 0f, UnityEngine.Vector3.up * 2f);
var grassMesh = SaveMesh("VT_Grass", gv, gn, guv, gt);

// ───────────── 알파 텍스처(잎 · 풀) ─────────────
const int LW = 128;
var lpx = new UnityEngine.Color32[LW * LW];
for (int i = 0; i < lpx.Length; i++) lpx[i] = new UnityEngine.Color32(40, 70, 20, 0);
for (int k = 0; k < 24; k++)
{
    float cx = 64f + R(-46f, 46f), cy = 64f + R(-46f, 46f);
    float a = R(10f, 16f), b = R(4.5f, 7.5f), ang = R(0f, UnityEngine.Mathf.PI), cs = UnityEngine.Mathf.Cos(ang), sn = UnityEngine.Mathf.Sin(ang);
    var col = new UnityEngine.Color32((byte)R(60f, 95f), (byte)R(110f, 160f), (byte)R(30f, 60f), 255);
    for (int y = (int)UnityEngine.Mathf.Max(0f, cy - a); y < (int)UnityEngine.Mathf.Min(LW, cy + a + 1f); y++)
        for (int x = (int)UnityEngine.Mathf.Max(0f, cx - a); x < (int)UnityEngine.Mathf.Min(LW, cx + a + 1f); x++)
        {
            float dx = x - cx, dy = y - cy, u = dx * cs + dy * sn, w = -dx * sn + dy * cs;
            if ((u / a) * (u / a) + (w / b) * (w / b) <= 1f) lpx[y * LW + x] = col;
        }
}
var leafTex = SaveTex("VT_Leaf", lpx, LW);

const int GW = 64;
var gpx = new UnityEngine.Color32[GW * GW];
for (int i = 0; i < gpx.Length; i++) gpx[i] = new UnityEngine.Color32(60, 100, 30, 0);
for (int k = 0; k < 10; k++)
{
    float bx = R(4f, 60f), top = R(36f, 63f), lean = R(-8f, 8f), w0 = R(2.5f, 4.5f);
    for (int y = 0; y < (int)top; y++)
    {
        float t = y / top, cx = bx + lean * t * t, hw = w0 * (1f - t);
        for (int x = (int)UnityEngine.Mathf.Max(0f, cx - hw); x <= (int)UnityEngine.Mathf.Min(GW - 1, cx + hw); x++)
            gpx[y * GW + x] = new UnityEngine.Color32((byte)(70 + 40 * t), (byte)(120 + 55 * t), (byte)(40 + 30 * t), 255);
    }
}
var grassTex = SaveTex("VT_Grass", gpx, GW);

// ───────────── 머티리얼 ─────────────
var lit = UnityEngine.Shader.Find("Universal Render Pipeline/Lit");
var veg = UnityEngine.Shader.Find("HuskyLibs/InstancedVegetation_URP");
if (lit == null || veg == null) return "셰이더 없음 — URP Lit / HuskyLibs/InstancedVegetation_URP";
var groundMat = SaveMat("VT_Ground", lit, new UnityEngine.Color(0.36f, 0.42f, 0.22f));
var buildingMat = SaveMat("VT_Building", lit, new UnityEngine.Color(0.62f, 0.60f, 0.56f));
var rockMat = SaveMat("VT_Rock", lit, new UnityEngine.Color(0.45f, 0.44f, 0.42f));
var barkMat = SaveMat("VT_Bark", lit, new UnityEngine.Color(0.33f, 0.24f, 0.16f));
var leafMat = SaveMat("VT_LeafVeg", veg, UnityEngine.Color.white);
leafMat.SetTexture("_BaseMap", leafTex); leafMat.SetFloat("_Cutoff", 0.5f); leafMat.SetFloat("_Translucency", 0.35f);
var grassMat = SaveMat("VT_GrassVeg", veg, UnityEngine.Color.white);
grassMat.SetTexture("_BaseMap", grassTex); grassMat.SetFloat("_Cutoff", 0.4f);
grassMat.SetFloat("_SamplePivot", 1f); grassMat.EnableKeyword("_HUSKY_PV_PIVOT");   // 풀: 밑동 한 점에서 볼륨 샘플
UnityEditor.EditorUtility.SetDirty(leafMat); UnityEditor.EditorUtility.SetDirty(grassMat);
UnityEditor.AssetDatabase.SaveAssets();

// ───────────── 씬 ─────────────
var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.DefaultGameObjects, UnityEditor.SceneManagement.NewSceneMode.Single);
UnityEngine.Light sun = null; UnityEngine.Camera cam = null;
foreach (var root in scene.GetRootGameObjects())
{
    if (sun == null) sun = root.GetComponent<UnityEngine.Light>();
    if (cam == null) cam = root.GetComponent<UnityEngine.Camera>();
}
sun.name = "Sun";
sun.intensity = 1.3f;
sun.color = new UnityEngine.Color(1f, 0.96f, 0.88f);
sun.transform.rotation = UnityEngine.Quaternion.Euler(48f, -35f, 0f);
cam.transform.position = new UnityEngine.Vector3(-26f, 16f, -30f);
cam.transform.LookAt(new UnityEngine.Vector3(2f, 2f, 0f));

UnityEngine.GameObject Node(string name, UnityEngine.Transform parent)
{
    var go = new UnityEngine.GameObject(name);
    if (parent != null) go.transform.SetParent(parent, false);
    return go;
}
UnityEngine.GameObject MeshObj(string name, UnityEngine.Transform parent, UnityEngine.Mesh mesh, UnityEngine.Material mat)
{
    var go = Node(name, parent);
    go.AddComponent<UnityEngine.MeshFilter>().sharedMesh = mesh;
    go.AddComponent<UnityEngine.MeshRenderer>().sharedMaterial = mat;
    return go;
}
UnityEngine.GameObject Prim(string name, UnityEngine.Transform parent, UnityEngine.PrimitiveType type, UnityEngine.Material mat)
{
    var go = UnityEngine.GameObject.CreatePrimitive(type);
    go.name = name;
    go.transform.SetParent(parent, false);
    go.GetComponent<UnityEngine.MeshRenderer>().sharedMaterial = mat;
    return go;
}

// 환경(정적) — 지형 · 건물(지붕 = groundRoots 비교용) · 바위
var env = Node("Environment", null).transform;
var terrain = MeshObj("Terrain", env, terrainMesh, groundMat);
const float BX = -14f, BZ = 12f, BH = 3f;   // 건물 중심 XZ · 반폭
float hMin = float.MaxValue, hMax = float.MinValue;
for (int j = 0; j <= 4; j++) for (int i = 0; i <= 4; i++)
    { float h = H(BX - BH + 1.5f * i, BZ - BH + 1.5f * j); hMin = UnityEngine.Mathf.Min(hMin, h); hMax = UnityEngine.Mathf.Max(hMax, h); }
float bBottom = hMin - 0.3f, bTop = hMax + 4f;
var building = Prim("Building", env, UnityEngine.PrimitiveType.Cube, buildingMat);
building.transform.position = new UnityEngine.Vector3(BX, (bBottom + bTop) * 0.5f, BZ);
building.transform.localScale = new UnityEngine.Vector3(BH * 2f, bTop - bBottom, BH * 2f);
var rockSpots = new[] { new UnityEngine.Vector4(-2f, 14f, 2.2f, 1.4f), new UnityEngine.Vector4(16f, -14f, 3f, 2f), new UnityEngine.Vector4(-20f, -20f, 1.6f, 1.2f), new UnityEngine.Vector4(20f, 20f, 2.5f, 1.5f) };
for (int k = 0; k < rockSpots.Length; k++)
{
    var r = rockSpots[k];
    var rock = Prim("Rock_" + k, env, UnityEngine.PrimitiveType.Sphere, rockMat);
    rock.transform.position = new UnityEngine.Vector3(r.x, H(r.x, r.y) + r.w * 0.25f, r.y);
    rock.transform.localScale = new UnityEngine.Vector3(r.z, r.w, r.z * 0.8f);
    rock.transform.rotation = UnityEngine.Quaternion.Euler(0f, R(0f, 360f), 0f);
}
bool InBuilding(float x, float z, float pad) => UnityEngine.Mathf.Abs(x - BX) < BH + pad && UnityEngine.Mathf.Abs(z - BZ) < BH + pad;

// 식생 — 나무(줄기 = 씬 MeshRenderer → C0 템플릿 중복 −12 · 잎 = 템플릿 렌더) · 풀 · 덤불
var vegRoot = Node("Vegetation", null).transform;
var trees = Node("Trees", vegRoot).transform;
var treeXZ = new[] {
    new UnityEngine.Vector2(8.5f, -2.5f), new UnityEngine.Vector2(3.2f, -1.8f), new UnityEngine.Vector2(6.8f, -6f), new UnityEngine.Vector2(4.5f, -5.2f),
    new UnityEngine.Vector2(9.4f, -5.6f), new UnityEngine.Vector2(2.4f, -4.4f), new UnityEngine.Vector2(7f, 0.2f),     // A: 점광원 둘레
    new UnityEngine.Vector2(10f, 8f), new UnityEngine.Vector2(13f, 10.5f), new UnityEngine.Vector2(8f, 11f),            // B: 언덕 위
    new UnityEngine.Vector2(-8.5f, 9f), new UnityEngine.Vector2(-10f, 16.5f) };                                          // C: 건물 옆
for (int k = 0; k < treeXZ.Length; k++)
{
    var p = treeXZ[k];
    var tree = MeshObj("Tree_" + k.ToString("00"), trees, trunkMesh, barkMat);
    tree.transform.position = new UnityEngine.Vector3(p.x, H(p.x, p.y), p.y);
    tree.transform.rotation = UnityEngine.Quaternion.Euler(0f, R(0f, 360f), 0f);
    tree.transform.localScale = UnityEngine.Vector3.one * R(0.85f, 1.25f);
}
var grass = Node("Grass", vegRoot).transform;
int grassCount = 0;
while (grassCount < 600)
{
    float x = R(-30f, 30f), z = R(-30f, 30f);
    if (InBuilding(x, z, 0.6f)) continue;
    bool nearTrunk = false;
    foreach (var p in treeXZ) if ((p - new UnityEngine.Vector2(x, z)).sqrMagnitude < 0.36f) nearTrunk = true;
    if (nearTrunk) continue;
    var g = Node("G", grass).transform;
    g.position = new UnityEngine.Vector3(x, H(x, z), z);
    g.rotation = UnityEngine.Quaternion.Euler(0f, R(0f, 360f), 0f);
    g.localScale = UnityEngine.Vector3.one * R(0.8f, 1.3f);
    grassCount++;
}
var bushes = Node("Bushes", vegRoot).transform;
for (int k = 0; k < 16; )
{
    float x = R(-28f, 28f), z = R(-28f, 28f);
    if (InBuilding(x, z, 1.5f)) continue;
    float sxz = R(0.9f, 1.4f), sy = R(0.6f, 0.9f);
    var b = Node("Bush_" + k.ToString("00"), bushes).transform;
    b.position = new UnityEngine.Vector3(x, H(x, z) + sy * 0.25f, z);
    b.localScale = new UnityEngine.Vector3(sxz, sy, sxz);
    k++;
}

// 램프 — Point(나무 A 수관 아래) · Spot(풀밭 위, 아래로) · Rect Area(건물 남쪽 벽, 바깥으로)
var lamps = Node("Lamps", null).transform;
UnityEngine.Light MakeLight(string name, UnityEngine.LightType type, UnityEngine.Vector3 pos, UnityEngine.Quaternion rot, UnityEngine.Color col, float intensity, float range)
{
    var l = Node(name, lamps).AddComponent<UnityEngine.Light>();
    l.type = type; l.color = col; l.intensity = intensity; l.range = range;
    l.shadows = UnityEngine.LightShadows.Soft;   // None 이면 베이커가 그림자 레이를 안 쏜다(로그 '그림자없음N') — 잎 카드 차폐가 안 보임
    l.transform.position = pos; l.transform.rotation = rot;
    return l;
}
var lampP = new UnityEngine.Vector3(6f, H(6f, -3f) + 2.2f, -3f);
MakeLight("Lamp_Point", UnityEngine.LightType.Point, lampP, UnityEngine.Quaternion.identity, new UnityEngine.Color(1f, 0.72f, 0.42f), 6f, 9f);
var spot = MakeLight("Lamp_Spot", UnityEngine.LightType.Spot, new UnityEngine.Vector3(-6f, H(-6f, -10f) + 6f, -10f), UnityEngine.Quaternion.Euler(90f, 0f, 0f),
                     new UnityEngine.Color(0.55f, 0.75f, 1f), 12f, 14f);
spot.spotAngle = 70f;
float wallZ = BZ - BH - 0.05f;
var area = MakeLight("Lamp_AreaRect", UnityEngine.LightType.Rectangle, new UnityEngine.Vector3(BX, H(BX, wallZ) + 2f, wallZ), UnityEngine.Quaternion.Euler(0f, 180f, 0f),
                     new UnityEngine.Color(1f, 0.95f, 0.85f), 4f, 10f);
area.areaSize = new UnityEngine.Vector2(2f, 1f);
area.lightmapBakeType = UnityEngine.LightmapBakeType.Baked;   // URP 면광원은 Baked 전용

// 소품(InstanceSceneBaker 대상) — 점광원 둘레 상자 8개
var props = Node("Props", null).transform;
for (int k = 0; k < 8; )
{
    float ang = R(0f, 2f * UnityEngine.Mathf.PI), rad = R(1.8f, 3.5f);
    float x = lampP.x + UnityEngine.Mathf.Cos(ang) * rad, z = lampP.z + UnityEngine.Mathf.Sin(ang) * rad;
    bool hit = false;
    foreach (var p in treeXZ) if ((p - new UnityEngine.Vector2(x, z)).sqrMagnitude < 0.8f) hit = true;
    if (hit) continue;
    var c = Node("Crate_" + k, props).transform;
    c.position = new UnityEngine.Vector3(x, H(x, z) + 0.3f, z);
    c.rotation = UnityEngine.Quaternion.Euler(0f, R(0f, 360f), 0f);
    c.localScale = UnityEngine.Vector3.one * 0.7f;
    k++;
}

// 베이커
var dbg = Node("Debuggers", null).transform;
var pv = Node("PV_Baker", dbg).AddComponent<HuskyLibs.CustomLightmapper.Bake.ProbeVolumeBaker>();
pv.transform.position = new UnityEngine.Vector3(0f, 6f, 0f);
pv.volumeSize = new UnityEngine.Vector3(64f, 18f, 64f);
pv.probeSpacing = new UnityEngine.Vector3(2f, 1f, 2f);
pv.layout = HuskyLibs.CustomLightmapper.Bake.ProbeVolumeBaker.Layout.TerrainLayers;
pv.groundOffset = 0.35f;
pv.layerHeight = 6f;
pv.groundRoots = new[] { terrain.transform };   // 비우면 건물 지붕·바위 위로도 층이 올라간다(비교용)
pv.staticSource = HuskyLibs.CustomLightmapper.Bake.ProbeVolumeBaker.StaticSource.Collected;
pv.sun = sun;
pv.maxGizmoProbes = 10000;
pv.gizmoProbeSize = 0.12f;
var cube = UnityEngine.Resources.GetBuiltinResource<UnityEngine.Mesh>("Cube.fbx");
var sphere = UnityEngine.Resources.GetBuiltinResource<UnityEngine.Mesh>("Sphere.fbx");
pv.templates = new[]
{
    new HuskyLibs.CustomLightmapper.Bake.ProbeVolumeBaker.TemplateInput {   // 줄기: 표시는 씬 MeshRenderer, 베이크엔 차폐로만
        mesh = trunkMesh, albedo = new UnityEngine.Color(0.33f, 0.24f, 0.16f), captureSource = trees,
        render = false, vertexAO = false },
    new HuskyLibs.CustomLightmapper.Bake.ProbeVolumeBaker.TemplateInput {   // 잎: 컷아웃 카드(B1-6) · 픽셀 볼륨 샘플
        mesh = leavesMesh, albedo = new UnityEngine.Color(0.35f, 0.5f, 0.2f), captureSource = trees, twoSided = true,
        shading = HuskyLibs.CustomLightmapper.Bake.ProbeVolumeBaker.ShadingMode.PerPixelVolume, material = leafMat },
    new HuskyLibs.CustomLightmapper.Bake.ProbeVolumeBaker.TemplateInput {   // 풀: 표시만(차폐 제외) · 밑동 샘플
        mesh = grassMesh, albedo = new UnityEngine.Color(0.4f, 0.55f, 0.25f), captureSource = grass, twoSided = true,
        occluder = HuskyLibs.CustomLightmapper.Bake.ProbeVolumeBaker.OccluderMode.Excluded,
        shading = HuskyLibs.CustomLightmapper.Bake.ProbeVolumeBaker.ShadingMode.PerPixelVolume, material = grassMat, vertexAO = false },
    new HuskyLibs.CustomLightmapper.Bake.ProbeVolumeBaker.TemplateInput {   // 덤불: 인스턴스 2-프로브(InstancedSH_URP)
        mesh = sphere, albedo = new UnityEngine.Color(0.3f, 0.45f, 0.2f), captureSource = bushes },
};
pv.CaptureMatricesFromSources();

var isb = Node("ISB_Baker", dbg).AddComponent<HuskyLibs.CustomLightmapper.Bake.InstanceSceneBaker>();
isb.sun = sun;
isb.templates = new[] { new HuskyLibs.CustomLightmapper.Bake.InstanceSceneBaker.TemplateInput {
    mesh = cube, albedo = new UnityEngine.Color(0.7f, 0.55f, 0.35f), captureSource = props } };
isb.CaptureMatricesFromSources();

UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, ScenePath);   // 베이크 출력 파일명 = 씬 이름 + 오브젝트 이름

// C0 수집 → pv.collectedStatics (메뉴 'Collect Scene (C0)' 와 같은 경로)
var log = new System.Text.StringBuilder();
UnityEngine.Application.LogCallback cb = (msg, st, type) => log.AppendLine("  [" + type + "] " + (msg.Length > 900 ? msg.Substring(0, 900) + " …" : msg));
UnityEngine.Application.logMessageReceived += cb;
var sw = System.Diagnostics.Stopwatch.StartNew();
try
{
    System.Type menu = null;
    foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        if ((menu = asm.GetType("HuskyLibs.CustomLightmapper.Bake.EditorTools.BakeSceneCollectMenu")) != null) break;
    var collect = menu?.GetMethod("CollectIntoProbe", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    if (collect == null) log.AppendLine("  [FAIL] BakeSceneCollectMenu.CollectIntoProbe 없음");
    else collect.Invoke(null, new object[] { pv, false });

    if (bake)
    {
        pv.Bake();
        isb.Bake();
    }
}
catch (System.Exception e) { log.AppendLine("  [Exception] " + (e.InnerException ?? e)); }
finally { UnityEngine.Application.logMessageReceived -= cb; }
UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);

var sv = UnityEditor.SceneView.lastActiveSceneView;
if (sv != null) sv.LookAt(new UnityEngine.Vector3(2f, 3f, 0f), UnityEngine.Quaternion.Euler(32f, 40f, 0f), 30f);

sb.AppendLine("scene: " + ScenePath);
sb.AppendLine($"terrain {terrainMesh.vertexCount} verts · trees {treeXZ.Length} · grass {grassCount} · bushes 16 · crates 8 · lamps 3 (Point/Spot/Rect)");
sb.AppendLine($"pv.collectedStatics = {pv.collectedStatics.Length} · volumeAsset = {(pv.volumeAsset != null ? UnityEditor.AssetDatabase.GetAssetPath(pv.volumeAsset) : "null")}");
sb.AppendLine($"isb.bakeAsset = {(isb.bakeAsset != null ? UnityEditor.AssetDatabase.GetAssetPath(isb.bakeAsset) : "null")}");
sb.AppendLine($"({sw.Elapsed.TotalSeconds:0.0}s)");
return sb + "log:\n" + log;
