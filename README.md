# Husky Custom Lightmapper

Burst/GPU 기반 커스텀 라이트맵 베이커. UV 파라미터화 → 차트 패킹 → 아틀라스 할당 → 텍셀 복원 → 경로추적 GI/SH 베이크 → 시임 스티칭/딜레이션까지의 파이프라인을 포함한다.

## 설치 (Embedded / Local / Git)

- **Embedded** (현재): 이 폴더가 `Packages/com.huskylibs.customlightmapper/` 에 있으면 Unity가 자동 인식.
- **Local**: 다른 프로젝트의 `Packages/manifest.json` 에
  `"com.huskylibs.customlightmapper": "file:../../경로/com.huskylibs.customlightmapper"`
- **Git**: 별도 저장소로 분리 후
  `"com.huskylibs.customlightmapper": "https://.../CustomLightmapper.git"`

## 의존성

`com.unity.burst`, `com.unity.collections`, `com.unity.mathematics` (공식 패키지). Unity 2021.3+.

이 브랜치(`unity-2021.3`)의 셰이더는 **Built-In RP 전용**이다. URP 변형(`InstancedSH_URP.shader`)은 BIRP 전용 프로젝트에서 컴파일 에러를 내므로 제거했고, `LightmapDebug.shader` 는 `UnityCG.cginc` 기반으로 변환했다. URP 판이 필요하면 `main` 브랜치를 쓴다.

## 어셈블리 구조

| 어셈블리 | 위치 | 플랫폼 | 내용 |
|---|---|---|---|
| `HuskyLibs.CustomLightmapper` | `Runtime/` | 전체 | 순수 베이크 로직 (배포 대상) |
| `HuskyLibs.CustomLightmapper.Editor` | `Editor/` | Editor | 에디터 디버그 툴 (UVLayoutViewer 등) |
| `HuskyLibs.CustomLightmapper.Tests` | `Tests/` | Editor | 자체 검증 클래스 (`static RunAll()→string`) |
| `HuskyLibs.CustomLightmapper.Samples` | `Samples~/` | Editor | MonoBehaviour 데모/디버그 하니스 + 씬 + FBX (Import 해야 컴파일됨) |

의존 방향: `Runtime` ← `Tests` ← `Editor`, `Samples`. Runtime은 다른 내부 어셈블리에 의존하지 않는다.

셰이더/컴퓨트는 `Shaders/` 에 있고, 런타임 로드 대상 컴퓨트(`PathTrace`, `BvhTraverse`)는 `Shaders/Resources/` 에 배치되어 `Resources.Load` 로 에디터·빌드 공통 로드된다.

## 샘플 (Samples~)

데모/디버그 하니스는 `Samples~/` 에 있다. 틸드 폴더는 Unity 가 임포트·컴파일에서 제외하므로 **소비자 프로젝트의 빌드를 오염시키지 않는다.** 쓰려면 Package Manager → 이 패키지 → **Samples → "Debug & Verification Scenes" → Import** (→ `Assets/Samples/...` 로 복사된다).

패키지를 직접 개발할 때는 임포트한 사본이 아니라 `Samples~/` 원본을 편집하고, 변경분을 다시 반영해야 한다.

> 주의: `Samples~` 를 임포트하면 asmdef 가 `HuskyLibs.CustomLightmapper.Tests` 를 참조한다. Tests 는 NUnit 이 아니라 `static RunAll()→string` 자체 검증 클래스 모음이고 패키지에 항상 포함되므로 그대로 해석된다.

## 사용 (기본 흐름)

씬에 `AtlasApplyDebug` 컴포넌트를 붙이고 대상 MeshFilter 를 지정한 뒤, 인스펙터 우클릭 → **Bake & Apply**. 자세한 내용은 `Documentation~/` 참조.
