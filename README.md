# Husky Custom Lightmapper

Burst/GPU 기반 커스텀 라이트맵 베이커. UV 파라미터화 → 차트 패킹 → 아틀라스 할당 → 텍셀 복원 → 경로추적 GI/SH 베이크 → 시임 스티칭/딜레이션까지의 파이프라인을 포함한다.

## 설치 (Embedded / Local / Git)

- **Embedded** (현재): 이 폴더가 `Packages/com.huskylibs.customlightmapper/` 에 있으면 Unity가 자동 인식.
- **Local**: 다른 프로젝트의 `Packages/manifest.json` 에
  `"com.huskylibs.customlightmapper": "file:../../경로/com.huskylibs.customlightmapper"`
- **Git**: 별도 저장소로 분리 후
  `"com.huskylibs.customlightmapper": "https://.../CustomLightmapper.git"`

## 의존성

`com.unity.burst`, `com.unity.collections`, `com.unity.mathematics` (공식 패키지). Unity 6000.0+.

## 어셈블리 구조

| 어셈블리 | 위치 | 플랫폼 | 내용 |
|---|---|---|---|
| `HuskyLibs.CustomLightmapper` | `Runtime/` | 전체 | 순수 베이크 로직 + 실사용 베이커 컴포넌트(`AtlasApplyDebug`·`InstanceSceneBaker`·`ProbeVolumeBaker`) (배포 대상) |
| `HuskyLibs.CustomLightmapper.Editor` | `Editor/` | Editor | 에디터 디버그 툴 (UVLayoutViewer 등) |
| `HuskyLibs.CustomLightmapper.Tests` | `Tests/` | Editor | 자체 검증 클래스 (`static RunAll()→string`) |
| `HuskyLibs.CustomLightmapper.Samples` | `Samples/` | Editor | MonoBehaviour 데모/디버그 하니스 + 씬 + FBX |

의존 방향: `Runtime` ← `Tests` ← `Editor`, `Samples`. Runtime은 다른 내부 어셈블리에 의존하지 않는다.

셰이더/컴퓨트는 `Shaders/` 에 있고, 런타임 로드 대상 컴퓨트(`PathTrace`, `BvhTraverse`)는 `Shaders/Resources/` 에 배치되어 `Resources.Load` 로 에디터·빌드 공통 로드된다.

## 배포 전 마지막 단계 (Samples~ 전환)

개발 중에는 데모가 `Samples/` (틸드 없음)에 있어 프로젝트에서 바로 편집·실행된다. **외부 배포 시** 소비자 빌드 오염을 막으려면 `Samples/` 를 `Samples~/Demo/` 로 이동한다(틸드 폴더는 Unity가 임포트/컴파일에서 제외 → Package Manager "Import Sample" 로만 제공).

```
mkdir Samples~ && git mv Samples "Samples~/Demo"
```

그리고 `package.json` 에 아래 `samples` 배열을 되살린다(개발 중에는 존재하지 않는 경로 검증 오류를 막기 위해 제거해 둠):

```json
"samples": [
  { "displayName": "Integration Demo",
    "description": "AtlasApplyDebug + SHRenderDemo 통합 검증 씬 (에디터 하니스).",
    "path": "Samples~/Demo" }
],
```

## 사용 (기본 흐름)

- **텍스처 라이트맵**: 씬에 `AtlasApplyDebug` 컴포넌트를 붙이고 대상 MeshFilter 를 지정한 뒤, 인스펙터 우클릭 → **Bake & Apply**. 라이팅 모드 결과는 `AtlasBakeAsset`(SO) + EXR 로 저장되고 OnEnable 에 다시 적용된다.
  - 대상 지정 대신 인스펙터 우클릭 → **Collect Scene (C0)**: 활성 씬을 훑어 LODGroup · `receiveGI` · Read/Write 기준으로(Static `Contribute GI` 는 보지 않는다 — v15.21) receiver(`targets`) / occluder-only(`occluders`) / 제외를 나누고 사유별 리포트를 로그로 남긴다. 자동 판정을 고정하려면 해당 오브젝트(또는 조상)에 `BakeSceneCollectOverride`.
- **인스턴싱 SH(프롭)**: `InstanceSceneBaker` — 템플릿 메시 + 인스턴스 행렬 → 2-프로브 SH → `InstancedSH_URP`.
- **식생 프로브 볼륨(Track B)**: `ProbeVolumeBaker` — 볼륨 박스·간격 → SH9 프로브 격자(+ 유효성·정점 AO) → 인스턴스 대표점 샘플(`InstancedSH_URP`) 또는 픽셀 샘플(`InstancedVegetation_URP`).
- 검증: `Samples/LightmapEvaluateDebugger` 우클릭 **Run All Tests**(38종).
- **사용법·테스트 매뉴얼**: `Documentation~/06_Manual/챕터별_테스트_실행_매뉴얼.md` — 챕터(A1~A4 · C0 · C1 · C2 · 광원 · Burst · GPU · 텍스처 라이트맵 · 후처리 · α · SH · Track B)별 실행 소스와 테스트 실행법. 그 밖의 문서는 `Documentation~/` 참조.
