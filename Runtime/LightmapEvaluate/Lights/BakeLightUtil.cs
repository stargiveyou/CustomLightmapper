using System.Collections.Generic;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// Unity <see cref="Light"/> → <see cref="BakeLight"/> 변환 · 씬 수집.
    /// Directional 은 제외(태양은 베이커의 DirectionalLight 설정). 면광원 크기·그림자 반경·베이크 모드는 Unity 가
    /// 에디터 전용으로만 노출하므로 플레이어 빌드에서는 기본값(면 1×1, 반경 0, 전부 Baked)으로 대체한다.
    /// Rectangle/Disc 는 LightType 정수값(3/4)으로 판별 — 2021.3(Area) ~ Unity 6(Rectangle) 명칭 차이와 무관.
    /// </summary>
    public static class BakeLightUtil
    {
        const int UnityRectangle = 3;   // LightType.Area(2021.3) = LightType.Rectangle(Unity 6)
        const int UnityDisc = 4;

        public struct Options
        {
            /// <summary>면광원 1차 직사 샘플 수.</summary>
            public int AreaSamples;
            /// <summary>그림자 반경이 있는 Point/Spot 의 1차 직사 샘플 수(반경 0 이면 1).</summary>
            public int SoftSamples;
            /// <summary>true = Unity 베이크 모드를 따름(Baked = 직사+간접, Mixed = 간접만, Realtime = 제외). false = 전부 직사+간접.</summary>
            public bool FollowBakeType;

            public static Options Default => new Options { AreaSamples = 16, SoftSamples = 8, FollowBakeType = false };
        }

        /// <summary>변환 가능한(켜진 Point/Spot/Rect/Disc, 강도 &gt; 0) 광원이면 true.</summary>
        public static bool TryConvert(Light light, Options opt, out BakeLight result)
        {
            result = default;
            if (light == null || !light.isActiveAndEnabled || light.intensity <= 0f) return false;
            int type = (int)light.type;
            if (light.type != LightType.Point && light.type != LightType.Spot && type != UnityRectangle && type != UnityDisc) return false;

            int flags = BakeLight.FlagDirect;
            if (light.shadows != LightShadows.None) flags |= BakeLight.FlagShadows;
#if UNITY_EDITOR
            if (opt.FollowBakeType)
            {
                var mode = light.lightmapBakeType;
                if (mode == LightmapBakeType.Realtime) return false;
                if (mode == LightmapBakeType.Mixed) flags &= ~BakeLight.FlagDirect;   // 직사는 실시간 라이트가 그림
            }
#endif
            // URP 실시간과 같은 색: Linear 색 × (색온도 사용 시) 색온도 RGB
            Color c = light.color.linear;
            if (light.useColorTemperature && UnityEngine.Rendering.GraphicsSettings.lightsUseColorTemperature)
                c *= Mathf.CorrelatedColorTemperatureToRGB(light.colorTemperature);
            var color = new Vector3(c.r, c.g, c.b);
            var tr = light.transform;

            float radius = 0f;
            Vector2 area = Vector2.one;
#if UNITY_EDITOR
            radius = light.shadowRadius;
            area = light.areaSize;
#endif
            int soft = radius > 0f ? Mathf.Max(1, opt.SoftSamples) : 1;

            if (light.type == LightType.Point)
                result = BakeLight.Point(tr.position, color, light.intensity, light.range, radius, soft, flags);
            else if (light.type == LightType.Spot)
                result = BakeLight.Spot(tr.position, tr.forward, color, light.intensity, light.range, light.spotAngle, light.innerSpotAngle, radius, soft, flags);
            else if (type == UnityRectangle)
                result = BakeLight.Rect(tr.position, tr.forward, tr.right, tr.up, area.x, area.y, color, light.intensity, Mathf.Max(1, opt.AreaSamples), flags);
            else
                result = BakeLight.Disc(tr.position, tr.forward, tr.right, tr.up, area.x, color, light.intensity, Mathf.Max(1, opt.AreaSamples), flags);
            return true;
        }

        /// <summary>목록 변환(변환 불가 항목은 건너뜀, 같은 Light 중복 제거).</summary>
        public static BakeLight[] Convert(IEnumerable<Light> lights, Options opt)
        {
            var list = new List<BakeLight>();
            var seen = new HashSet<Light>();
            if (lights != null)
                foreach (var l in lights)
                    if (l != null && seen.Add(l) && TryConvert(l, opt, out var bl)) list.Add(bl);
            return list.ToArray();
        }

        /// <summary>로드된 씬의 켜진 로컬 광원 전부(활성 오브젝트만). 순서 = 인스턴스 ID 오름차순(결정적).</summary>
        public static BakeLight[] CollectScene(Options opt)
        {
            // FindObjectsOfType: 2021.3~Unity6 공통(U6 에선 deprecated 경고만) — AtlasApplyDebug 의 기존 사용과 같은 규약
            var all = new List<Light>(Object.FindObjectsOfType<Light>());
            all.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            return Convert(all, opt);
        }

        /// <summary>종류별 개수 요약(로그용): "P2 S1 R1 D0 · 간접만 1 · 그림자 없음 0".</summary>
        public static string Summary(BakeLight[] lights)
        {
            if (lights == null || lights.Length == 0) return "0";
            int p = 0, s = 0, r = 0, d = 0, indirectOnly = 0, noShadow = 0;
            foreach (var l in lights)
            {
                switch ((BakeLightType)l.Type)
                {
                    case BakeLightType.Point: p++; break;
                    case BakeLightType.Spot: s++; break;
                    case BakeLightType.Rect: r++; break;
                    default: d++; break;
                }
                if ((l.Flags & BakeLight.FlagDirect) == 0) indirectOnly++;
                if ((l.Flags & BakeLight.FlagShadows) == 0) noShadow++;
            }
            return $"P{p} S{s} R{r} D{d}" + (indirectOnly > 0 ? $" 간접만{indirectOnly}" : "") + (noShadow > 0 ? $" 그림자없음{noShadow}" : "");
        }
    }
}
