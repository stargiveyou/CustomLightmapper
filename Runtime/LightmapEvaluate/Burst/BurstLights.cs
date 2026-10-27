using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>Burst 그림자 질의 — <see cref="BurstTwoLevelBVH.Occluded"/>(α any-hit 포함) 래퍼.</summary>
    public struct BurstSceneQuery : IShadowQuery
    {
        public BurstScene Scene;
        public bool Occluded(Vector3 o, Vector3 d, float maxDist) => BurstTwoLevelBVH.Occluded(Scene, o, d, maxDist);
    }

    /// <summary>
    /// Burst 로컬 광원 합산 — <see cref="LightSampling.AddPrimary{TQ}"/>/<see cref="LightSampling.AddBounce{TQ}"/> 와 같은 순서·같은 식
    /// (광원 목록만 BurstScene.lights). 광원 0개면 입력을 그대로 돌려준다(비트 동일).
    /// </summary>
    public static class BurstLights
    {
        public static Vector3 AddPrimary(Vector3 direct, in BurstScene s, Vector3 p, Vector3 n, uint seed)
        {
            if (s.lightCount <= 0) return direct;
            var q = new BurstSceneQuery { Scene = s };
            float rot = RadianceCore.SunConeRotation(seed);
            for (int i = 0; i < s.lightCount; i++)
            {
                BakeLight L = s.lights[i];
                if ((L.Flags & BakeLight.FlagDirect) != 0)
                    direct += LightSampling.Irradiance(L, ref q, p, n, L.Samples, rot);
            }
            return direct;
        }

        /// <summary>SH 직접광 투영(L2) — <see cref="LightSampling.AddDirectSH{TQ}"/> 와 같은 순서·같은 식(위상 = SunConeRotation(probeIndex + 1)).</summary>
        public static void AddDirectSH(ref SH9 sh, in BurstScene s, Vector3 p, uint probeIndex)
        {
            if (s.lightCount <= 0) return;
            var q = new BurstSceneQuery { Scene = s };
            float rot = RadianceCore.SunConeRotation(probeIndex + 1u);
            for (int i = 0; i < s.lightCount; i++)
            {
                BakeLight L = s.lights[i];
                if ((L.Flags & BakeLight.FlagDirect) != 0)
                    LightSampling.ProjectSH(ref sh, L, ref q, p, L.Samples, rot);
            }
        }

        public static Vector3 AddBounce(Vector3 eD, in BurstScene s, Vector3 hp, Vector3 hn)
        {
            if (s.lightCount <= 0) return eD;
            var q = new BurstSceneQuery { Scene = s };
            for (int i = 0; i < s.lightCount; i++)
            {
                BakeLight L = s.lights[i];
                eD += LightSampling.Irradiance(L, ref q, hp, hn, 1, 0f);
            }
            return eD;
        }
    }
}
