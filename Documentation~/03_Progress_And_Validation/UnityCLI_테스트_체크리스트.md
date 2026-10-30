# Unity CLI 테스트 체크리스트 — RunAll 40종 · RunAll 밖 · 실베이크 스모크

> 기준: v15.24(2026-10-29) · `main` = v15.22 B2(지형 추종 2.5D) 포함 · Unity 6000.x + URP · Unity CLI beta(`unity command`)
> 목적: 에디터 GUI 를 손으로 누르지 않고 **Unity CLI 로 연결된 에디터에 C# 를 보내** 컴파일·셰이더·테스트·실베이크를 확인한다. 2026-10-28 검증(`run_script` 로 클래스별 RunAll — 38종 419/419)과 같은 방식을 정리했다.
> 우선순위: **§2.2 B2 신규 2종**(에디터에서 한 번도 안 돌림) → §2.3 Track B 회귀 → 나머지.
> 관련: 수동(GUI) 절차·메뉴 = `06_Manual/챕터별_테스트_실행_매뉴얼.md` §3 · 머지분 에디터 확정 항목 = `에디터검증_체크리스트_v15.12-v15.18.md`(§8 = v15.22).
> 순서대로 진행 — 앞 단계가 깨지면 뒤 단계 결과는 의미가 없다. FAIL 은 **그 `[FAIL]` 줄 + 그 클래스의 `--- n/m PASS ---` 줄**을 함께 기록.

---

## 0. 준비

### 0.1 CLI · 에디터 연결

- [x] CLI 설치 확인 — `unity --version` (beta 채널. `screenshot` 명령은 0.1.0-beta.8 이상) — 2026-10-29: `1.0.0-beta.14`
- [x] Pipeline 패키지 — 호스트 프로젝트에 한 번: `unity pipeline install --project-path "$P"` — 호스트는 `0.7.0-exp.1` 고정(RP Core 17.7.0 이 0.8.0+ 와 컴파일 불가). 이 조합에선 `unity status` 를 부를 때마다 콘솔에 `No command named 'editor status'` 에러 1줄이 찍힌다 — 패키지와 무관, §1.5 에서 제외
- [x] 에디터 띄우기 — 둘 중 하나 (2026-10-29: GUI)
  - **GUI(권장)**: `unity open "$P"` → `unity status --format json` 에서 state `ready`. 씬 뷰 스크린샷(§4.3)까지 가능
  - **배치 상주**: `<Unity 실행 파일> -batchmode -projectPath "$P" -logFile editor.log &` — **`-quit` 없이**(상주), **`-nographics` 금지**(렌더 테스트가 전부 `[SKIP]`). 이 방식은 `unity status` 에 **안 뜬다** → `unity command --project-path "$P"` 로 연결 확인
- [x] `eval` 명령 존재 — `unity command --project-path "$P"` 목록에 `eval` · `eval_file`. 없으면 `unity pipeline upgrade`, 또는 프로젝트에 있는 `run_script` 류 명령(같은 C# 본문을 받음 — 2026-10-28 사용)
- [x] 연결 시험 — `unity command eval 'return UnityEngine.Application.unityVersion;' --project-path "$P"` → 버전 문자열 (`6000.7.0b4`)
- [ ] 연결이 안 되면 먼저 **Safe Mode**(컴파일 에러) 의심 — `unity pipeline list`. Safe Mode 에선 Pipeline 이 안 떠서 CLI 가 붙지 못한다 → 에러를 고치고 에디터 재시작

### 0.2 공통 규칙

| 항목 | 규칙 |
|---|---|
| 프로젝트 경로 | `P=/path/to/HostProject` 로 두고 모든 명령에 `--project-path "$P"`(에디터가 여럿 떠 있어도 대상 고정) |
| 타임아웃 | 기본 30초 → **`--timeout 600`**. GPU·베이크 테스트는 수십 초~분 단위. 일괄 실행(부록 B)은 `--timeout 1800` |
| 호출 단위 | **클래스별로 따로**(2026-10-28 방식). 한 번에 40종은 메인 스레드를 오래 잡아 타임아웃 위험 — 일괄은 부록 B 의 `only` 로 묶음을 나눈다. 타임아웃이 나도 에디터 쪽 실행은 계속될 수 있다 → 콘솔/Editor.log 로 결과 확인 |
| 이름 | `using` 없이 **전체 이름**: 테스트·베이커 모두 `HuskyLibs.CustomLightmapper.Bake.` 네임스페이스 — **예외 `SHPackedTests`** 는 `HuskyLibs.CustomLightmapper.SHPackedTests`. Unity 타입도 `UnityEngine.`/`UnityEditor.` 를 붙인다 |
| 따옴표 | bash/zsh: 본문을 **작은따옴표**로 감싼다(C# 문자열의 큰따옴표가 그대로 전달). C# 안에 작은따옴표(char 리터럴)를 쓰지 않는다. **PowerShell**: 네이티브 인자 따옴표 처리가 버전마다 달라 **`eval_file` 권장** |
| 긴 스니펫 | 부록의 본문을 파일로 저장 → `unity command eval_file <파일.cs> --project-path "$P" --timeout 600`. 파일은 `Assets/` **밖**에 둔다(Unity 가 컴파일하려 들지 않게) |
| 결과 받기 | `--format json` 이면 `data.result.result` 가 반환 문자열(줄바꿈 이스케이프 없이 읽힘) · 컴파일 에러는 `data.result.diagnostics`. 출력 JSON 뒤에 `<claude-code-hint …/>` 한 줄이 붙을 수 있다 — 파싱 전에 마지막 `}` 까지만 자른다 |
| Burst | **동기 컴파일 켬** — 메뉴 `Jobs ▸ Burst ▸ Synchronous Compilation`(또는 eval 이 Unity.Burst 를 참조하면 `Unity.Burst.BurstCompiler.Options.EnableBurstCompileSynchronously = true;`). 꺼져 있으면 첫 실행이 관리 코드로 돌아 Burst ≡ 비교(비트 동일) 항목이 흔들린다 |
| 코드 최적화 | 성능 수치·CPU Diff 는 에디터 **Release** 모드에서. Debug 면 관리 코드 경로가 몇 배 느리다 |
| 열린 씬 | 테스트는 임시 오브젝트를 숨김으로 만들고 지운다(씬 무변경). **§3 일부와 §4 는 씬 필드·에셋을 바꾼다** — 샘플 씬에 저장 안 된 변경이 있으면 먼저 저장 여부를 정한다(아래 확인 명령) |

```bash
unity command eval 'return UnityEngine.SceneManagement.SceneManager.GetActiveScene().path + " dirty=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty;' --project-path "$P"
```

- [x] 열린 씬 = `Mesh_Struct_Sample_Scene`(§3·§4 가 쓰는 `Debuggers` 하위 오브젝트가 있음) · `dirty=False` 확인. 다른 씬이면 저장 후 샘플 씬을 연다:

```bash
unity command eval 'var g = UnityEditor.AssetDatabase.FindAssets("Mesh_Struct_Sample_Scene t:Scene"); if (g.Length == 0) return "샘플 씬 없음"; if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty) return "열린 씬에 저장 안 된 변경 — 먼저 저장/버림 결정"; var p = UnityEditor.AssetDatabase.GUIDToAssetPath(g[0]); UnityEditor.SceneManagement.EditorSceneManager.OpenScene(p); return "opened " + p;' --project-path "$P"
```

---

## 1. 컴파일 · 셰이더 (가장 먼저)

- [x] **1.1 스크립트 컴파일 실패 없음**
  ```bash
  unity command eval 'return UnityEditor.EditorUtility.scriptCompilationFailed ? "FAIL: 컴파일 에러" : "OK";' --project-path "$P"
  ```
- [x] **1.2 B2 신규 타입이 로드됨** — `ProbeTerrainLayers`(런타임) · `ProbeTerrainLayersTests` · `ProbeVolumeTerrainTests`(Tests 어셈블리). `ProbeGrid.Bake.cs` 는 partial 이라 `ProbeGrid` 의 `BakeBurstAt` 메서드로 확인
  ```bash
  unity command eval 'var s = ""; foreach (var n in new[] { "HuskyLibs.CustomLightmapper.Bake.ProbeTerrainLayers, HuskyLibs.CustomLightmapper", "HuskyLibs.CustomLightmapper.Bake.ProbeTerrainLayersTests, HuskyLibs.CustomLightmapper.Tests", "HuskyLibs.CustomLightmapper.Bake.ProbeVolumeTerrainTests, HuskyLibs.CustomLightmapper.Tests" }) s += (System.Type.GetType(n) != null ? "OK  " : "없음 ") + n + "\n"; var g = System.Type.GetType("HuskyLibs.CustomLightmapper.Bake.ProbeGrid, HuskyLibs.CustomLightmapper"); s += "ProbeGrid.BakeBurstAt " + (g != null && g.GetMethod("BakeBurstAt") != null ? "OK" : "없음"); return s;' --project-path "$P"
  ```
- [x] **1.3 compute 3종** — 강제 재임포트 후 메시지·커널 지원(부록 A). 기대: 에러 0 · **X4714(레지스터 압박) 없음** · 경고는 X3556(`PathTrace.compute` `WrapIndex` 정수 나머지)뿐 · 전 커널 `IsSupported`
  - 2026-10-29: 3종 `error 0 · warning 0` · 커널 8/4/2 전부 지원. 단 `GetComputeShaderMessages` 는 경고를 0 으로 돌려주고, X3556(`integer modulus may be much slower`, PathTrace 8커널)은 **콘솔에만** 찍힌다 → 경고 판정은 콘솔에서
  - `PathTrace`: CSAO · CSDirect · CSIndirect · CSRadiance · CSRadianceAccum · CSSHBake · CSSHBakeMulti · CSSHDirect
  - `LightmapPost`: CSStitchT1 · CSStitchT2Sample · CSStitchT2Gather · CSDilate
  - `BvhTraverse`: CSClosestHit · CSOccluded
- [x] **1.4 셰이더 6종** — `ShaderHasError = False` · 에러 메시지 0(부록 A). B2 가 바꾼 `HuskyLibs/InstancedVegetation_URP`(`HuskyProbeVolume.hlsl` 의 `_HuskyPV_Height` Load · 정점 단계 `_HUSKY_PV_PIVOT`)와 `Hidden/HuskyLibs/ProbeVolumeSampleTest` 가 핵심. 셰이더 변형은 실제로 그릴 때 컴파일되므로 **§2 의 렌더 테스트(G 등)를 돌린 뒤 한 번 더** 확인
- [x] **1.5 콘솔/로그 에러 0** — Burst 에러 포함. GUI 는 콘솔, 배치 상주는 `-logFile` 로 준 로그(기본 위치: Windows `%LOCALAPPDATA%\Unity\Editor\Editor.log` · macOS `~/Library/Logs/Unity/Editor.log` · Linux `~/.config/unity3d/Editor.log`)에서 `error CS` · `Burst` · `Shader error` 검색. 패키지 경고는 UAC1001(직렬화 분석기)만 알려진 것

---

## 2. RunAll 40종 — 클래스별 실행

### 2.1 호출 · 읽는 법

```bash
unity command eval 'return HuskyLibs.CustomLightmapper.Bake.ProbeTerrainLayersTests.RunAll();' --project-path "$P" --timeout 600
```

- 반환 문자열 = 콘솔 리포트와 같다: `=== 이름 ===` → 항목별 `[PASS]` / `[FAIL]` / `[SKIP]` → 끝줄 `--- n/m PASS ---`(`SH9BakeTextureTest` 만 `--- n/m passed ---`).
- `[SKIP]` 은 실패가 아니다 — 필요한 환경(compute · URP 셰이더 · 그래픽 디바이스)이 없다는 뜻. 사유가 같은 줄에 찍힌다. 배치 상주에서 SKIP 이 많으면 `-nographics` 여부부터 확인.
- 40종 일괄 + 요약(클래스별 끝줄 · FAIL/SKIP 줄 · 합계 · 시간)은 **부록 B**.
- `SH9BakeTextureTest` 는 Samples 어셈블리(`HuskyLibs.CustomLightmapper.Samples`), 나머지는 Tests 어셈블리. 네임스페이스는 모두 같다.

### 2.2 B2 신규 — 에디터 미실행 (최우선)

| # | 클래스 | 요구 | 기대 | 확인하는 것 | 결과 |
|---|---|---|---|---|---|
| 39 | `ProbeTerrainLayersTests` | 없음 | **10/10** (헤드리스 .NET 에서 10/10 확인됨) | 높이 없음·상수 높이 ≡ 균일 격자(비트 동일) · 열 높이 bilinear · 울퉁불퉁 지형 샘플 · 지면 레이·미스 보간 · 절감 리포트 | ✅ 10/10 |
| 40 | `ProbeVolumeTerrainTests` | 없음(G: 렌더) | **14/14** | A 평지 베이크(9×4×9 · 열 높이 ≈ 0 · 높이 텍스처 9×9 · Validate 음성) · B 에셋 ≡ 코어 직접(`BakeBurstAt → BackfaceRatio → Classify → Dilate`, 비트 동일) · C 12° 경사(열 높이 · 층 위치) · D 지면 규칙(수관 템플릿 제외 · `groundRoots`) · E 인스턴스 샘플 ≡ 지형 추종 CPU 샘플(비트 동일) · F 바인딩 · G GPU ≡ CPU | ✅ 14/14 (SKIP 0) |

- [x] 두 클래스 모두 PASS(G 가 `[SKIP] G: ARGBFloat RT 미지원` 이면 그래픽 디바이스 확인 후 재실행)
- [x] 실행 뒤 §1.4 셰이더 메시지 재확인(G 가 `ProbeVolumeSampleTest` 의 지형 추종 변형을 실제 컴파일) — 6종 error 0 · ShaderHasError False 유지

### 2.3 Track B 회귀 — B2 가 손댄 경로 (Uniform 기본 = 비트 동일이어야 함)

B2 는 `ProbeGrid`(partial 분리) · `ProbeVolumeAsset`(열 높이·높이 텍스처) · `ProbeVolumeBaker`(배치 분기) · `ProbeVolumeShaderBinding`(`_HuskyPV_Params.w`) · `HuskyProbeVolume.hlsl` 을 바꿨다. 기존 테스트는 항목 수가 바뀌지 않았다.

| # | 클래스 | 요구 | 이전(2026-10-28) | 결과 |
|---|---|---|---|---|
| 31 | `ProbeGridTests` | 없음(F: Compute) | PASS(개별 수 미기록) | ✅ 15/15 |
| 32 | `ProbeValidityTests` | 없음 | PASS(개별 수 미기록) | ✅ 17/17 |
| 33 | `ProbeVolumeBakerTests` | 없음(H·I: URP 셰이더) | 19/19 | ✅ 19/19 |
| 34 | `ProbeVolumeGpuSampleTests` | 렌더 | PASS(개별 수 미기록) — B2 에서 헬퍼 `internal` 공유 · CPU 미러 호출이 열 높이 오버로드로 바뀜(균일 에셋이면 같은 값) | ✅ 11/11 |
| 35 | `VertexAOBakeTests` | 없음(F: URP 셰이더) | 7/7 | ✅ 7/7 |
| 36 | `ProbeAlphaTests` | 없음(E: 그래픽 디바이스 · F: Compute) | 12/12 | ✅ 12/12 |
| 38 | `ProbeVolumeC0Tests` | 없음 | 8/8 (E 경고 1줄이 정상) | ✅ 8/8 (E 경고 1줄 — 정상) |

- [x] 7종 모두 이전과 같은 수 PASS (합 89/89)

### 2.4 나머지 31종

| # | 클래스 | 요구 | 이전(2026-10-28) | 결과 |
|---|---|---|---|---|
| 1 | `ChartFlattenTests` | 없음 | 17/17 | ✅ 17/17 |
| 2 | `LightmapEvaluateTests` | 없음 | PASS | ✅ 17/17 |
| 3 | `BVHTests` | 없음 | PASS | ✅ 12/12 |
| 4 | `RadianceIndirectTests` | 없음 | PASS | ✅ 5/5 |
| 5 | `InstancedSceneModeBTests` | 없음 | PASS | ✅ 7/7 |
| 6 | `BurstSceneTests` | 없음 | PASS | ✅ 7/7 |
| 7 | `BurstRadianceCompareTests` | 없음 | 2/2 | ✅ 2/2 |
| 8 | `TemplateInstanceSourceTests` | 없음 | PASS | ✅ 7/7 |
| 9 | `SH9BakeTextureTest` | 없음 | 4/4 passed | ✅ 4/4 passed |
| 10 | `BurstSHBakerTests` | 없음 | PASS | ✅ 17/17 |
| 11 | `SHPackedTests` | 없음 — **네임스페이스 `HuskyLibs.CustomLightmapper`**(`.Bake` 아님) | PASS | ✅ 4/4 |
| 12 | `SHEvalProbeGpuTests` | 렌더 | 10/10 | ✅ 10/10 |
| 13 | `SHInstanceIndexGpuTests` | 렌더 | PASS | ✅ 102/102 |
| 14 | `GpuBvhCompareTests` | Compute | PASS | ✅ 4/4 |
| 15 | `GpuRadianceCompareTests` | Compute | 4/4 | ✅ 4/4 |
| 16 | `GpuSHBakeCompareTests` | Compute | 3/3 | ✅ 3/3 |
| 17 | `GpuBakeLimitsTests` | 없음 | 4/4 | ✅ 4/4 |
| 18 | `AtlasBatchBakeTests` | URP+Compute | 4/4 | ✅ 4/4 |
| 19 | `ProgressiveAccumulatorTests` | 없음 | 5/5 | ✅ 5/5 |
| 20 | `LightmapPostPlanTests` | 없음 | 4/4 | ✅ 4/4 |
| 21 | `ProgressiveBakeTests` | URP+Compute | 7/7 | ✅ 7/7 |
| 22 | `BakeLightTests` | 없음 | 7/7 | ✅ 7/7 |
| 23 | `LocalLightBakeTests` | URP+Compute | 4/4 | ✅ 4/4 |
| 24 | `SHLightProjectionTests` | 없음 | 5/5 | ✅ 5/5 |
| 25 | `SHLocalLightTests` | Compute(일부) | 5/5 | ✅ 5/5 |
| 26 | `AlphaCutoutTests` | 없음 | PASS | ✅ 16/16 |
| 27 | `AlphaGpuCompareTests` | Compute | 4/4 | ✅ 4/4 |
| 28 | `AtlasHdrOutputTests` | URP | 6/6 | ✅ 6/6 |
| 29 | `InstanceSceneBakerTests` | 없음(일부 URP) | 10/10 | ✅ 10/10 |
| 30 | `AtlasBakeAssetTests` | URP | 8/8 | ✅ 8/8 |
| 37 | `BakeSceneCollectPolicyTests` | 없음 | 19/19 | ✅ 19/19 |

"PASS" 만 적힌 칸은 2026-10-28 합계(419)에는 들어 있지만 클래스별 수를 기록하지 않은 것 — 2026-10-29 에 채웠다(31종 합 330).

- [x] **합계** — 2026-10-28 38종 **419/419** + B2 신규 24(10 + 14) → **40종 443/443 · SKIP 0 · FAIL 0** 기대(GPU 있는 GUI 에디터 기준) — 2026-10-29: **443/443 · SKIP 0 · FAIL 0** (Release · Burst 동기, 묶음 5회 호출 합 약 8초)

---

## 3. RunAll 밖 테스트

| 항목 | 호출 | 요구 · 주의 | 이전 | 결과 |
|---|---|---|---|---|
| `LightmapAllocatorTests` | `return HuskyLibs.CustomLightmapper.Bake.LightmapAllocatorTests.RunAll();` | 없음 | — | ✅ 11/11 (`--- 11 passed, 0 failed ---`) |
| `LightmapSSAATests` | `return HuskyLibs.CustomLightmapper.Bake.LightmapSSAATests.RunAll();` | 없음 (= `AtlasApplyDebug` "SSAA Self-Tests") | 28/28 | ✅ 28/28 |
| `LightmapPostProcessCompare` | `return HuskyLibs.CustomLightmapper.Bake.LightmapPostProcessCompare.RunAll();` | 없음 (Dilate Burst vs Serial) | — | ✅ 2/2 · maxDiff 0 · 512² 8회 Serial 43.6ms / Burst 9.6ms / NativeArray 3.4ms |
| `LightmapDenoiseCompare` | `return HuskyLibs.CustomLightmapper.Bake.LightmapDenoiseCompare.RunAll();` | 없음 (Denoise Burst vs Serial + 품질) | — | ✅ 4/4 · RMS 0.0582→0.0055 · Serial 2002ms / Burst 135ms (13s) |
| Radiance Diff (BruteForce vs BVH) | 부록 C — `AtlasApplyDiffTests.RadianceDiff(t)` | `C2.5_AtlasApplyDebug` · **`atlasResolution` 을 32 로 낮춰서**(기본값은 수 시간) — 스니펫이 끝나면 되돌림 | 10,202 텍셀 maxDiff 0 MATCH (res 32, 93s) | ✅ 10,202 텍셀 maxDiff 0 MATCH (res 32, 118s) |
| RadianceGI Backend Diff (CPU vs Burst) | 부록 C — `AtlasApplyDiffTests.RadianceGiBackendDiff(t)` | 같은 오브젝트 · Release 권장 | 656,003 텍셀 MATCH (32s) | ✅ 656,003 텍셀 maxDiff 0.021 · over 3 MATCH (res 1024, 47s) |
| RadianceGI Backend Diff (Burst vs GPU) | 부록 C — `AtlasApplyDiffTests.RadianceGiBackendDiffGpu(t)` | Compute | 656,003 텍셀 MATCH | ✅ 656,003 텍셀 maxDiff 0.0045 · over 1 MATCH (Burst 886ms · GPU 377ms) |
| `InstancedSceneTester` | 부록 C — `C2_InstancedSceneTester` 의 `Run()` | 샘플 씬 인스턴스 18개(컴포넌트에 `targets` 필드는 없음) | 5/5 PASS | ✅ 5/5 (1.8s) |
| BVH Scene Test | 부록 C — `C2_LightmapEvaluateDebugger` 의 `RunBVHSceneTest()` | `occluders` 18개 | — | ✅ 3/3 (Median · SAH · 2단 인스턴스 miss 0/3000, 7.1s) |
| BVH Mesh Test | 부록 C — 같은 오브젝트의 `RunBVHMeshTest()` | `receiver` 또는 자식 MeshFilter 필요(없으면 경고 1줄로 끝 — 정상) | — | ✅ 경고 1줄로 끝(receiver 없음 — 정상) |

- Diff 3종 · `InstancedSceneTester` · BVH Mesh/Scene 은 결과를 반환하지 않고 콘솔에 찍는다 → 부록 C 의 **로그 캡처** 스니펫으로 받는다.
- Diff 3종은 씬에 라이트맵을 적용하지 않는다(내부 베이크 후 비교만).

- [x] 위 10개 PASS/MATCH — Backend Diff 2종 수치는 `에디터검증_체크리스트_v15.12-v15.18.md` 의 2026-10-28 기록과 같다

---

## 4. 실베이크 스모크 (씬 필드·에셋을 바꿈)

### 4.1 `ProbeVolumeBaker` — Uniform → TerrainLayers (B2 실베이크, 미확인 항목)

샘플 씬 `B1_ProbeVolumeBaker`(볼륨 12×10×12 · 간격 1 · `staticSource = Roots` · 템플릿 없음)를 두 배치로 차례로 굽는다(부록 D). `volumeAsset` 이 비어 있으면 **새 에셋** `Assets/CustomLightmapper/BakeOutput/Mesh_Struct_Sample_Scene_B1_ProbeVolumeBaker_ProbeVolume.asset`(+ EXR)이 생기고, 두 번째 베이크는 같은 파일에 덮어쓴다. 스니펫은 끝나면 `layout` 을 원래 값으로 되돌린다.

- [x] Uniform: `layout:` 줄 = `Uniform 13×11×13 = 1859 프로브` · `IsTerrainLayers = False` · `heightTexture = null` · `Validate = True` — 2026-10-29: 그대로 · 무효 19 → dilation 19 · 0.5s
- [x] TerrainLayers: `layout:` 줄 = `TerrainLayers 13×7×13 = 1183 프로브 (… MB) · 지면 … · 균일 환산 … → −…%`(층 7 = `layerHeight` 6 ÷ 간격 1 + 1) · `IsTerrainLayers = True` · `columnHeights` 169 · `heightTexture` 13×13 · `Validate = True` — 2026-10-29: `TerrainLayers 13×7×13 = 1183 프로브 (0.06MB) · 지면 -1.72~4.97 · 균일 환산 13×14×13 = 2366 (0.13MB) → −50.0% · 지면 미스 153열(이웃 보간) · 지면 메시 정적(Roots) 18개` · 무효 0 · 0.2s
- [x] 미스 열 수 기록 — 샘플 씬엔 지형이 없어 지면 메시 = 정적 입력 전부(큐브·캡슐 위로 층이 올라가는 게 정상). 아래에 아무것도 없는 열은 `지면 미스 N열(이웃 보간)` — **153/169열**(바닥 평면이 없고 큐브·캡슐이 가운데에 모여 있어 16열만 맞음)
- [x] 베이크 로그의 `scene:` · `alpha:` 줄에 에러 없음 (`static(Roots) 18 → BLAS 2 · 인스턴스 18` · alpha DISABLED)
- [x] 정리 결정: 생긴 에셋을 남길지(지울 땐 `UnityEditor.AssetDatabase.DeleteAsset(<경로>)`) · 씬 변경(`volumeAsset` 연결)을 저장할지 — 2026-10-29: 에셋(.asset + .exr)은 남김(호스트 `Assets/CustomLightmapper/` 는 untracked) · 씬은 저장하지 않고 디스크에서 다시 열어 버림(테스트 전 `dirty=False`)

### 4.2 `AtlasApplyDebug` — RadianceGI GPU 스모크 (선택, Track A 회귀)

부록 D-2: `mode = RadianceGI` · `radianceBackend = Gpu` · `atlasResolution = 256` · `saveBakeResult = false` 로 잠깐 바꿔 `BakeAndApply()` → 로그 캡처 → 필드 원복. **씬 렌더러에 라이트맵이 적용**되므로 확인 뒤 `RestoreOriginals()`.

- [x] GPU 로 구움(폴백 로그 없음) · 완료 로그에 에러 없음 — 2026-10-29: `18 insts → atlas 256×256×1 RGBAHalf, util=44.8 % … gpuChunk=65536` · total 0.15s · 경고는 "RadianceGI 매우 느림" 안내 1줄뿐
- [x] `RestoreOriginals()` 후 렌더러 원복 — 적용 전/복원 후 스크린샷 동일 · 필드(mode·backend·res·save) 원값

### 4.3 스크린샷 (GUI 에디터만)

```bash
unity command eval 'var go = UnityEngine.GameObject.Find("B1_ProbeVolumeBaker"); UnityEditor.Selection.activeGameObject = go; if (UnityEditor.SceneView.lastActiveSceneView != null) UnityEditor.SceneView.lastActiveSceneView.FrameSelected(); return go != null ? "selected" : "없음";' --project-path "$P"
unity command screenshot --output pv_terrain.png --project-path "$P"
```

- [ ] 기즈모 — 프로브 구가 지면(정적 메시 윗면)을 따라 휘는지 · 무효(빨강 와이어)가 지면에 몰리지 않는지(몰리면 `groundOffset` ↑). 화면 판정은 §5
  - 2026-10-29: **CLI 캡처엔 기즈모가 안 나온다** — `unity command screenshot --view scene` · `capture_scene_view` 둘 다 씬 카메라 렌더만 담는다(기즈모·핸들 없음). `--output` 은 절대 경로로(상대 경로면 위치가 불분명). 기즈모 판정은 에디터 화면에서 직접 → §5

---

## 5. 사람 눈으로 확인 (CLI 로 판정 못 함)

- [ ] 4.3 스크린샷의 층 모양 · 무효 프로브 분포 → §5.1 검증 씬에서
- [ ] **실지형 씬**(샘플 씬엔 없음): `layout = TerrainLayers` + `groundRoots` = 지형 메시만 → 지붕·바위 위로 층이 올라가지 않음 · `layout:` 절감률 · 미스 열
  - 2026-10-30 §5.1 검증 씬 생성 베이크: 절감률 −41.7% · 미스 0열(로그는 §5.1). 지붕·바위 비교는 육안 남음
- [ ] 식생(`shading = PerPixelVolume`) 표시 — 언덕·경사에서 균일 볼륨 대비 이상 없음 · 볼륨 가장자리 페이드 → §5.1
- 이 항목은 `에디터검증_체크리스트_v15.12-v15.18.md` §8 과 같다 — 확인하면 양쪽에 표시

### 5.1 검증 씬 — `CL_Verification_Scene` (2026-10-30)

§5 와 `에디터검증_체크리스트_v15.12-v15.18.md` 의 실씬 육안 항목(§3.5 L2 · §3.6 B1-6 · §8 B2)을 한 씬에서 본다. 생성 스크립트 = [`scripts/BuildVerificationScene.cs`](scripts/BuildVerificationScene.cs)(`eval_file` 본문 — `Documentation~` 안이라 Unity 가 컴파일하지 않음).

```bash
unity command eval_file "Packages/com.huskylibs.customlightmapper/Documentation~/03_Progress_And_Validation/scripts/BuildVerificationScene.cs" --project-path "$P" --timeout 600 --format json
```

- 결과: `Assets/CustomLightmapper/Verification/CL_Verification_Scene.unity` + `Generated/`(메시·알파 텍스처·머티리얼) · 베이크 결과 `Assets/CustomLightmapper/BakeOutput/CL_Verification_Scene_*`
- **호스트 `Assets/` 에 두는 이유**: 씬이 베이크 결과(호스트 `BakeOutput`)를 참조한다 — 패키지에 넣으면 샘플 씬과 같은 호스트 참조 문제가 생긴다. Assets 씬이라 내장 라이트매퍼 대조(§3.4 면광원 밝기, `AtlasApplyDebug` 추가 필요)에도 쓸 수 있다. 패키지 저장소엔 스크립트만 둔다
- 폴더가 이미 있으면 멈춘다 → 다시 만들려면 스크립트 첫 줄 `overwrite = true`(Verification 폴더와 이 씬 이름의 BakeOutput 결과를 지우고 새로). 고정 시드라 배치는 매번 같다. 열린 씬에 저장 안 된 변경이 있으면 멈춘다(덮어쓸 검증 씬 자신은 버림)
- 주의: `Light.shadows = None` 이면 베이커가 그 광원의 그림자 레이를 쏘지 않는다(로그 `그림자없음N`) — 스크립트는 램프를 Soft 로 만든다. 새로 추가하는 라이트도 확인

| 오브젝트 | 구성 | 보는 것 |
|---|---|---|
| `Environment/Terrain` | 64×64m 언덕 메시(1m 격자 · 언덕 2개 + 잔물결 + 동쪽 12° 경사) | `groundRoots` 대상 · 층이 지면을 따라 휨 |
| `Environment/Building` · `Rock_0~3` | 6×6m 상자 건물 · 구 바위 4개 | `groundRoots` 를 비우면 지붕·바위 위로 층이 올라감 |
| `Vegetation/Trees/Tree_00~11` | 줄기 = 씬 MeshRenderer(URP Lit, 템플릿 `render = false`) · 잎 = 템플릿(카드 18장 · 컷아웃 `VT_LeafVeg` · PerPixelVolume) | B1-6 잎 차폐 · C0 템플릿 중복 −12 |
| `Vegetation/Grass` (600) | 십자 카드 3장 · `occluder = Excluded` · PerPixelVolume + 밑동 샘플(`_HUSKY_PV_PIVOT`) | 언덕·경사 식생 · 가장자리 페이드 |
| `Vegetation/Bushes` (16) | 구 · InstanceProbes(InstancedSH_URP) | 2-프로브 표시 |
| `Lamps` | Point(나무 A 수관 아래 · 주황) · Spot(풀밭 위 6m · 하늘색 · 아래로) · Rect 2×1m(건물 남쪽 벽 · 바깥으로 · Baked) — 그림자 Soft | L2 |
| `Props` (상자 8) | 점광원 둘레 | `ISB_Baker` 램프 반응 |
| `Debuggers/PV_Baker` | 볼륨 64×18×64 · 간격 (2,1,2) · `TerrainLayers`(groundOffset 0.35 · layerHeight 6) · `groundRoots = Terrain` · `staticSource = Collected`(C0 수집) · Burst · 기즈모 상한 10000 | B2 · B1-6 · L2 |
| `Debuggers/ISB_Baker` | `InstanceSceneBaker` · 템플릿 Cube ← `Props` | L2 |

2026-10-30 생성 베이크(1.4s, Burst):

```
[C0] 수집 완료 — receiver 18 · occluder 0 · excluded 0 → 템플릿 인스턴스와 겹치는 12건은 베이크 때 제외
[ProbeVolumeBaker] dims=33×7×33=7623, 무효 58 → dilation 58, bounces=1, dirs=256, lights=P1 S1 R1 D0(Scene), alpha=ON
  layout: TerrainLayers 33×7×33 = 7623 프로브 (0.41MB) · 지면 -0.5~3.78 · 균일 환산 33×12×33 = 13068 (0.70MB) → −41.7% · 지면 메시 groundRoots 1개
  scene: templates 3메시·40인스턴스 + static(Collected) 6 (템플릿 중복 −12) → BLAS 6 · 인스턴스 46
  alpha: cutout mats=1 · 'VT_LeafVeg' [_BaseMap] 128×128 cutoff=0.500 불투명=27.5%
[InstanceSceneBaker] 템플릿 1, 인스턴스 8, bounces=1, dirs=512, lights=P1 S1 R1 D0(Scene)
```

확인 순서(에디터 화면 · Bake 는 컴포넌트 우클릭 — 결과는 에디터검증 체크리스트 해당 절에도 표시):

- [ ] **B2 층 모양**: `PV_Baker` 선택 → 기즈모 프로브가 지형을 따라 휨 · 무효(빨강 와이어)가 지면에 몰리지 않음(몰리면 `groundOffset` ↑) — 에디터검증 §8
- [ ] **B2 groundRoots**: `groundRoots` 비우고 Bake → 건물 지붕·바위 위로 층이 올라감 · `layout:` 지면 범위가 넓어짐 → 되돌리고 Bake — §8
- [ ] **B2 대비 Uniform**: `layout = Uniform` 으로 Bake → TerrainLayers 와 식생 밝기 비교(언덕·경사 이상 없음) · 볼륨 가장자리 페이드 → 되돌리고 Bake — §8
- [ ] **B1-6**: `alphaCutout` 끄고 Bake → 나무 아래 풀·땅 쪽이 어두워짐(잎 카드가 통짜 차폐) → 켜고 Bake — §3.6
- [ ] **L2 ProbeVolumeBaker**: 점광원 둘레 잎·풀이 주황으로 밝아지고 램프 쪽이 밝음 · Spot 아래 풀이 하늘색 · `localLights.source = None` 으로 Bake 해 비교 — §3.5
- [ ] **L2 InstanceSceneBaker**: `ISB_Baker` 상자의 점광원 쪽 면이 밝음 · None 비교 — §3.5
- [ ] **L2 백엔드**: `PV_Baker.backend = Gpu` 로 Bake → Burst 와 같은 모습 — §3.5
- 끝나면 씬을 저장하지 않고 다시 열거나 `overwrite = true` 로 다시 만든다(기준 상태 유지)

---

## 6. 결과 기록

| 날짜 | Unity · GPU · 그래픽 API | 실행 방식(GUI / 배치 상주) · CLI 버전 | 1 컴파일·셰이더 | 2 RunAll (PASS/총 · SKIP) | 3 RunAll 밖 | 4 실베이크 | 비고 |
|---|---|---|---|---|---|---|---|
| 2026-10-29 | 6000.7.0b4 · RTX 4080 · D3D11 | GUI · CLI 1.0.0-beta.14 · Pipeline 0.7.0-exp.1 · `eval_file` | ✅ compute 3 · 셰이더 6 error 0 · 로그 에러 0 | ✅ **443/443** · SKIP 0 · FAIL 0 | ✅ 10/10 (Diff 3종 MATCH) | ✅ 4.1 · 4.2 / 4.3 기즈모는 CLI 캡처 불가 | Release · Burst 동기(끝나고 Debug · 비동기로 복원). 부록 B 의 `SHPackedTests` 네임스페이스 문제 발견 → 수정. §5 미확인 |
| 2026-10-30 | 6000.7.0b4 · RTX 4080 · D3D11 | GUI · `LightmapEvaluateDebugger` "Run All Tests"(수동) · 로그 `Logs/Editor.log` | — | ✅ **443/443** · SKIP 0 · FAIL 0 (클래스별 수 10-29 와 동일) | — | §5.1 검증 씬 생성 베이크 OK | 로그: LogError·예외 0 · 경고 27 = RadianceGI/Radiance 느림 안내 23 · `ProbeVolumeC0Tests` E 1(정상) · X3556 1 · 무관 2(Entities.Graphics Forward+ · AI Assistant 계정 API) |

---

## 부록 A — compute · 셰이더 메시지 (`eval_file`)

```csharp
var sb = new System.Text.StringBuilder();
sb.AppendLine("scriptCompilationFailed = " + UnityEditor.EditorUtility.scriptCompilationFailed);

var computes = new[] {
    new[] { "PathTrace", "CSAO", "CSDirect", "CSIndirect", "CSRadiance", "CSRadianceAccum", "CSSHBake", "CSSHBakeMulti", "CSSHDirect" },
    new[] { "LightmapPost", "CSStitchT1", "CSStitchT2Sample", "CSStitchT2Gather", "CSDilate" },
    new[] { "BvhTraverse", "CSClosestHit", "CSOccluded" },
};
foreach (var c in computes)
{
    var cs = UnityEngine.Resources.Load<UnityEngine.ComputeShader>(c[0]);
    if (cs == null) { sb.AppendLine("[FAIL] compute " + c[0] + " 로드 실패"); continue; }
    UnityEditor.AssetDatabase.ImportAsset(UnityEditor.AssetDatabase.GetAssetPath(cs), UnityEditor.ImportAssetOptions.ForceUpdate);
    cs = UnityEngine.Resources.Load<UnityEngine.ComputeShader>(c[0]);
    int err = 0, warn = 0;
    foreach (var m in UnityEditor.ShaderUtil.GetComputeShaderMessages(cs))
    {
        if (m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error) err++; else warn++;
        sb.AppendLine("    " + m.severity + " " + m.platform + " " + m.file + ":" + m.line + " " + m.message);
    }
    var unsupported = "";
    for (int k = 1; k < c.Length; k++)
    {
        int idx = cs.HasKernel(c[k]) ? cs.FindKernel(c[k]) : -1;
        if (idx < 0 || !cs.IsSupported(idx)) unsupported += " " + c[k];
    }
    sb.AppendLine((err == 0 && unsupported.Length == 0 ? "[PASS] " : "[FAIL] ") + "compute " + c[0] +
                  " — error " + err + " · warning " + warn + (unsupported.Length > 0 ? " · 미지원 커널:" + unsupported : " · 커널 " + (c.Length - 1) + "개 지원"));
}

foreach (var n in new[] { "HuskyLibs/InstancedVegetation_URP", "Hidden/HuskyLibs/ProbeVolumeSampleTest", "HuskyLibs/InstancedSH_URP",
                          "CustomLightmapper/LightmapDebug", "HuskyLibs/SHEvalProbe", "HuskyLibs/SHEvalInstance" })
{
    var s = UnityEngine.Shader.Find(n);
    if (s == null) { sb.AppendLine("[FAIL] shader " + n + " 없음"); continue; }
    int err = 0, warn = 0;
    foreach (var m in UnityEditor.ShaderUtil.GetShaderMessages(s))
    {
        if (m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error) err++; else warn++;
        sb.AppendLine("    " + m.severity + " " + m.platform + " " + m.file + ":" + m.line + " " + m.message);
    }
    bool hasErr = UnityEditor.ShaderUtil.ShaderHasError(s);
    sb.AppendLine((err == 0 && !hasErr ? "[PASS] " : "[FAIL] ") + "shader " + n + " — error " + err + " · warning " + warn + " · ShaderHasError " + hasErr);
}
return sb.ToString();
```

## 부록 B — RunAll 40종 일괄 + 요약 (`eval_file`, `--timeout 1800`)

`only` 에 클래스 이름을 쉼표로 넣으면 그것만 실행(예: B2 신규 2종 → `"ProbeTerrainLayersTests,ProbeVolumeTerrainTests"`). 각 클래스 전체 리포트는 콘솔/Editor.log 에도 남긴다.

```csharp
var only = "";   // 비우면 40종 전부
var names = new[] {
    "ChartFlattenTests", "LightmapEvaluateTests", "BVHTests", "RadianceIndirectTests", "InstancedSceneModeBTests",
    "BurstSceneTests", "BurstRadianceCompareTests", "TemplateInstanceSourceTests", "SH9BakeTextureTest", "BurstSHBakerTests",
    "SHPackedTests", "SHEvalProbeGpuTests", "SHInstanceIndexGpuTests", "GpuBvhCompareTests", "GpuRadianceCompareTests",
    "GpuSHBakeCompareTests", "GpuBakeLimitsTests", "AtlasBatchBakeTests", "ProgressiveAccumulatorTests", "LightmapPostPlanTests",
    "ProgressiveBakeTests", "BakeLightTests", "LocalLightBakeTests", "SHLightProjectionTests", "SHLocalLightTests",
    "AlphaCutoutTests", "AlphaGpuCompareTests", "AtlasHdrOutputTests", "InstanceSceneBakerTests", "AtlasBakeAssetTests",
    "ProbeGridTests", "ProbeValidityTests", "ProbeVolumeBakerTests", "ProbeVolumeGpuSampleTests", "VertexAOBakeTests",
    "ProbeAlphaTests", "BakeSceneCollectPolicyTests", "ProbeVolumeC0Tests", "ProbeTerrainLayersTests", "ProbeVolumeTerrainTests",
};
var pick = only.Length > 0 ? only.Split(',') : null;
var rx = new System.Text.RegularExpressions.Regex(@"---\s*(\d+)/(\d+)\s*(PASS|passed)");
var sum = new System.Text.StringBuilder();
var detail = new System.Text.StringBuilder();
int P = 0, T = 0, skips = 0, fails = 0;
var all = System.Diagnostics.Stopwatch.StartNew();
for (int i = 0; i < names.Length; i++)
{
    if (pick != null && System.Array.IndexOf(pick, names[i]) < 0) continue;
    System.Type t = null;
    foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())   // SHPackedTests 만 .Bake 밖
    { t = asm.GetType("HuskyLibs.CustomLightmapper.Bake." + names[i]) ?? asm.GetType("HuskyLibs.CustomLightmapper." + names[i]); if (t != null) break; }
    var m = t?.GetMethod("RunAll", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
    if (m == null) { sum.AppendLine($"#{i + 1,2} {names[i],-30} [FAIL] 타입/RunAll 없음"); fails++; continue; }
    var ps = m.GetParameters();
    var args = new object[ps.Length];
    for (int k = 0; k < ps.Length; k++) args[k] = ps[k].DefaultValue;   // SHEvalProbeGpuTests 등 기본 인자
    var sw = System.Diagnostics.Stopwatch.StartNew();
    string r;
    try { r = (string)m.Invoke(null, args); }
    catch (System.Exception e) { r = "  [FAIL] 예외 " + (e.InnerException ?? e); }
    sw.Stop();
    UnityEngine.Debug.Log(r);
    var mm = rx.Match(r);
    int p = mm.Success ? int.Parse(mm.Groups[1].Value) : 0, tot = mm.Success ? int.Parse(mm.Groups[2].Value) : 0;
    int s = 0, f = 0;
    foreach (var line in r.Split(new[] { "\n" }, System.StringSplitOptions.None))
    {
        if (line.Contains("[FAIL]")) { f++; detail.AppendLine($"#{i + 1} {names[i]}: {line.Trim()}"); }
        else if (line.Contains("[SKIP]")) { s++; detail.AppendLine($"#{i + 1} {names[i]}: {line.Trim()}"); }
    }
    if (!mm.Success) { f++; detail.AppendLine($"#{i + 1} {names[i]}: 끝줄(--- n/m PASS ---) 없음"); }
    P += p; T += tot; skips += s; fails += f;
    sum.AppendLine($"#{i + 1,2} {names[i],-30} {p}/{tot}" + (s > 0 ? $" SKIP {s}" : "") + (f > 0 ? $" FAIL {f}" : "") + $"  {sw.Elapsed.TotalSeconds:0.0}s");
}
sum.AppendLine($"합계 {P}/{T} PASS · SKIP {skips} · FAIL {fails} · {all.Elapsed.TotalSeconds:0}s");
return sum + (detail.Length > 0 ? "\n[FAIL/SKIP 줄]\n" + detail : "");
```

## 부록 C — 로그 캡처 (결과를 반환하지 않는 메서드)

본문의 `// ▼ 실행` 한 줄만 바꿔 쓴다. 예는 Radiance Diff(BruteForce vs BVH) — `atlasResolution` 을 32 로 낮췄다가 되돌린다.

```csharp
var log = new System.Text.StringBuilder();
UnityEngine.Application.LogCallback cb = (msg, st, type) => log.AppendLine("[" + type + "] " + msg);
UnityEngine.Application.logMessageReceived += cb;
var sw = System.Diagnostics.Stopwatch.StartNew();
try
{
    var t = UnityEngine.GameObject.Find("C2.5_AtlasApplyDebug").GetComponent<HuskyLibs.CustomLightmapper.Bake.AtlasApplyDebug>();
    int res = t.atlasResolution;
    t.atlasResolution = 32;
    try { HuskyLibs.CustomLightmapper.Bake.AtlasApplyDiffTests.RadianceDiff(t); }   // ▼ 실행
    finally { t.atlasResolution = res; }
}
catch (System.Exception e) { log.AppendLine("[Exception] " + e); }
finally { UnityEngine.Application.logMessageReceived -= cb; }
return log + $"({sw.Elapsed.TotalSeconds:0.0}s)";
```

| 대상 | `// ▼ 실행` 자리에 넣을 것 |
|---|---|
| CPU vs Burst | `HuskyLibs.CustomLightmapper.Bake.AtlasApplyDiffTests.RadianceGiBackendDiff(t);` (해상도 원복 줄은 그대로 두거나 지운다 — 2026-10-28 수치는 기본 해상도) |
| Burst vs GPU | `HuskyLibs.CustomLightmapper.Bake.AtlasApplyDiffTests.RadianceGiBackendDiffGpu(t);` |
| `InstancedSceneTester` | `try` 블록 전체를 `UnityEngine.GameObject.Find("C2_InstancedSceneTester").GetComponent<HuskyLibs.CustomLightmapper.Bake.InstancedSceneTester>().Run();` 로 |
| BVH Scene / Mesh | `try` 블록 전체를 `UnityEngine.GameObject.Find("C2_LightmapEvaluateDebugger").GetComponent<HuskyLibs.CustomLightmapper.Bake.LightmapEvaluateDebugger>().RunBVHSceneTest();`(또는 `RunBVHMeshTest()`)로 |

## 부록 D — 실베이크 스모크 (`eval_file`)

**D-1 `ProbeVolumeBaker` Uniform → TerrainLayers** (§4.1)

```csharp
var log = new System.Text.StringBuilder();
UnityEngine.Application.LogCallback cb = (msg, st, type) => log.AppendLine("  [" + type + "] " + msg);
var sb = new System.Text.StringBuilder();
var go = UnityEngine.GameObject.Find("B1_ProbeVolumeBaker");
if (go == null) return "B1_ProbeVolumeBaker 없음 — 샘플 씬을 여세요";
var b = go.GetComponent<HuskyLibs.CustomLightmapper.Bake.ProbeVolumeBaker>();
var oldLayout = b.layout;
UnityEngine.Application.logMessageReceived += cb;
try
{
    foreach (var lay in new[] { HuskyLibs.CustomLightmapper.Bake.ProbeVolumeBaker.Layout.Uniform,
                                HuskyLibs.CustomLightmapper.Bake.ProbeVolumeBaker.Layout.TerrainLayers })
    {
        b.layout = lay;
        log.Clear();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        b.Bake();
        var a = b.volumeAsset;
        if (a == null) { sb.AppendLine(lay + ": 에셋 없음(실패)\n" + log); continue; }
        bool ok = a.Validate(out string err);
        sb.AppendLine($"{lay}: {sw.Elapsed.TotalSeconds:0.0}s · Validate={ok} {err}");
        sb.AppendLine($"  layoutLog: {a.layoutLog}");
        sb.AppendLine($"  IsTerrainLayers={a.IsTerrainLayers} · columnHeights={a.columnHeights.Length} · heightTexture=" +
                      (a.heightTexture != null ? a.heightTexture.width + "x" + a.heightTexture.height : "null"));
        sb.AppendLine($"  asset: {UnityEditor.AssetDatabase.GetAssetPath(a)}");
        sb.Append(log);
    }
}
catch (System.Exception e) { sb.AppendLine("[Exception] " + e); }
finally { UnityEngine.Application.logMessageReceived -= cb; b.layout = oldLayout; }
return sb.ToString();
```

**D-2 `AtlasApplyDebug` RadianceGI GPU** (§4.2)

```csharp
var log = new System.Text.StringBuilder();
UnityEngine.Application.LogCallback cb = (msg, st, type) => log.AppendLine("[" + type + "] " + msg);
var t = UnityEngine.GameObject.Find("C2.5_AtlasApplyDebug").GetComponent<HuskyLibs.CustomLightmapper.Bake.AtlasApplyDebug>();
var mode = t.mode; var be = t.radianceBackend; var res = t.atlasResolution; var save = t.saveBakeResult;
UnityEngine.Application.logMessageReceived += cb;
var sw = System.Diagnostics.Stopwatch.StartNew();
try
{
    t.mode = HuskyLibs.CustomLightmapper.Bake.AtlasApplyDebug.BakeMode.RadianceGI;
    t.radianceBackend = HuskyLibs.CustomLightmapper.Bake.AtlasApplyDebug.RadianceBackend.Gpu;
    t.atlasResolution = 256;
    t.saveBakeResult = false;
    t.BakeAndApply();
}
catch (System.Exception e) { log.AppendLine("[Exception] " + e); }
finally
{
    UnityEngine.Application.logMessageReceived -= cb;
    t.mode = mode; t.radianceBackend = be; t.atlasResolution = res; t.saveBakeResult = save;
}
return log + $"({sw.Elapsed.TotalSeconds:0.0}s) — 확인 후 RestoreOriginals() 실행";
```

```bash
unity command eval 'UnityEngine.GameObject.Find("C2.5_AtlasApplyDebug").GetComponent<HuskyLibs.CustomLightmapper.Bake.AtlasApplyDebug>().RestoreOriginals(); return "restored";' --project-path "$P"
```

## 부록 E — 문제 해결

| 증상 | 원인 · 조치 |
|---|---|
| `unity command` 가 연결 못 함 | 에디터 미실행 / Pipeline 미설치(`unity pipeline install`) / **Safe Mode**(컴파일 에러 — `unity pipeline list`) / 에디터가 여럿이면 `--project-path` |
| 배치 상주 에디터가 `unity status` 에 없음 | 정상(하트비트 방식이 다름) — `unity command --project-path "$P"` 로 확인 |
| 타임아웃 | `--timeout` ↑ · 클래스별 호출 · 부록 B `only` 로 나눔. 결과는 콘솔/Editor.log 에도 남는다 |
| 렌더·Compute 항목이 전부 `[SKIP]` | `-nographics` 로 띄움 / compute 미지원 GPU / URP 미활성 |
| Burst ≡ 비교(비트 동일) 항목만 FAIL | Burst 비동기 컴파일 — 동기 컴파일 켜고 재실행 · 그래도 FAIL 이면 `Library/BurstCache` 삭제 후 재컴파일 |
| `eval` 컴파일 에러 "형식을 찾을 수 없음" | 전체 이름(`HuskyLibs.CustomLightmapper.Bake.…`) 확인 · 패키지 어셈블리가 eval 참조에 없으면 부록 B 처럼 리플렉션으로 부른다 |
| bash 에서 본문이 잘림 | 본문 안 작은따옴표 · `$` · 백틱 — `eval_file` 로 |
| PowerShell 에서 큰따옴표가 사라짐 | 네이티브 인자 전달 규칙 차이 — `eval_file` 로 |
| `GameObject.Find` 가 null | 샘플 씬이 안 열림 / 오브젝트가 비활성(Find 는 활성만 찾음) / 이름 변경(`06_Manual/샘플씬_Debuggers_구성.md`) |
| 한글이 깨져 보임 | 터미널 인코딩(UTF-8) — 또는 `--format json` 으로 받아 파일로 저장 |
| 부록 B 에서 `#11 SHPackedTests [FAIL] 타입/RunAll 없음` | 이 클래스만 `HuskyLibs.CustomLightmapper` 네임스페이스 — 2026-10-29 판 부록 B 는 두 네임스페이스를 다 찾는다 |
| 콘솔에 `No command named 'editor status'` 에러 | Pipeline 0.7.0 에서 `unity status` 를 부를 때마다 찍힘 — 패키지 에러 아님 |
| 스크린샷에 기즈모가 없음 | CLI 캡처(`screenshot` · `capture_scene_view`)는 카메라 렌더만 — 기즈모는 에디터 화면에서 확인 |
| 스크린샷에 식생·덤불·인스턴스(베이커가 그리는 것)가 없음 | 베이커 표시는 `Update()` 의 `Graphics.DrawMeshInstancedIndirect` — 편집 모드에선 씬이 바뀔 때만 Update 가 돌아 캡처 프레임에 드로가 없다. [`scripts/CaptureWithDraws.cs`](scripts/CaptureWithDraws.cs)(Update 직접 호출 → 같은 프레임에 Main Camera 를 RT 로 렌더 → PNG) |
| 베이크 결과 이름에 ` 1` 이 붙음 | `volumeAsset`/`bakeAsset` 이 빈 새 베이커가 같은 이름의 기존 결과를 만나면 새 이름으로 만든다 — 이전 결과를 지우고 다시 굽거나 필드에 기존 에셋을 연결 |
| `eval_file` 이 `Main thread operation timed out after 5000ms` 로 실패 | 에디터가 다른 일(에셋 임포트 등)로 바빠 요청을 받지 못한 것 — 스크립트는 나중에 실행됐을 수 있다. 다시 보내기 전에 Editor.log · 결과 파일로 실행 여부부터 확인 |
