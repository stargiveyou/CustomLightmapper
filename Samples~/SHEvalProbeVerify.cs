using UnityEngine;
using HuskyLibs.CustomLightmapper.Bake;   // SHEvalProbeGpuTests

namespace HuskyLibs.CustomLightmapper
{
    /// <summary>
    /// SH-5 GPU↔CPU 검증의 씬 컴포넌트 진입점(선택). 실제 로직은 <see cref="SHEvalProbeGpuTests.RunAll"/>
    /// (static, LightmapEvaluateDebugger "Run All Tests" 에도 배선). 이 컴포넌트는 인스펙터에서 ε·샘플 수를
    /// 바꿔가며 단독 실행하고 싶을 때만 쓰는 얇은 래퍼 — 검증 로직은 한 곳(static)에만 둔다.
    /// </summary>
    public class SHEvalProbeVerify : MonoBehaviour
    {
        [Tooltip("비교 허용오차(절대). GPU/CPU fp32 반올림 차이 흡수(보통 err<1e-4).")]
        public float epsilon = 2e-3f;

        [Tooltip("테스트 SH 생성용 프로젝션 방향 샘플 수.")]
        [Min(16)] public int projDirs = 512;

        [ContextMenu("Verify GPU vs CPU SH")]
        public void Verify() => Debug.Log(SHEvalProbeGpuTests.RunAll(epsilon, projDirs), this);
    }
}
