using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 하늘/ 환경광. 미스 레이가 받는 복사 휘도 (Linear RGB) -> 추후 SKY/HDRI 자리?
    /// </summary>
    public interface ISky
    {
        Vector3 Radiance(Vector3 dir);
    }
    /// <summary>균일 하늘. 반구 적분 시 조도 = π·Radiance.</summary>
    public struct UniformSky : ISky
    {
        public Vector3 L;
        public UniformSky(Vector3 l) { L = l; }
        public Vector3 Radiance(Vector3 dir) { return L; }

    }

    public struct GradientSky : ISky
    {
        public Vector3 Top, Bottom;
        public GradientSky(Vector3 top, Vector3 bottom) { Top = top; Bottom = bottom; }
        public Vector3 Radiance(Vector3 dir)
        {
            float t = Mathf.Clamp01(dir.y * 0.5f + 0.5f);
            return Vector3.Lerp(Bottom, Top, t);
        }
    }
}