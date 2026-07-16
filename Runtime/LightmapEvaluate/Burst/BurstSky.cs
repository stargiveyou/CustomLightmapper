using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>ISky 의 POD 미러(Burst). UniformSky/GradientSky.Radiance 와 동일.</summary>
    public struct BurstSky : ISky
    {
        public int Type;      // 0=uniform, 1=gradient
        public Vector3 A, B;  // uniform: A=L | gradient: A=Top, B=Bottom

        public static BurstSky Uniform(Vector3 l) => new BurstSky { Type = 0, A = l };
        public static BurstSky Gradient(Vector3 top, Vector3 bottom) => new BurstSky { Type = 1, A = top, B = bottom };


        public readonly Vector3 Radiance(Vector3 dir)
        {
            if (Type == 1)
            {
                float t = Mathf.Clamp01(dir.y * 0.5f + 0.5f);
                return Vector3.Lerp(B, A, t); // Lerp(Bottom, Top, t)
            }
            return A;
        }

        public static BurstSky FromSky(ISky sky)
        {
            if (sky is UniformSky u) return Uniform(u.L);
            if (sky is GradientSky g) return Gradient(g.Top, g.Bottom);
            return Uniform(Vector3.zero);
        }
    }
}