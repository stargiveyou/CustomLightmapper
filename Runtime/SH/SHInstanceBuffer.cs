using System;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper
{
    /// <summary>
    /// SH-3: per-instance SH9 → GPU 버퍼. DrawMeshInstancedIndirect 의 InstanceID 순서로 바인딩.
    ///
    /// GLSL(SSBO std430)·HLSL(StructuredBuffer) 양쪽 호환을 위해 SH9(Vector3×9)를
    /// float4 × 7 (= 112B, 16B 정렬)로 패킹한다. vec3 배열은 std140/430 에서 16B 정렬 함정이 있어
    /// float4 로 묶어 정렬 명확화(28 float = 9계수×3 + 1 패딩).
    ///
    ///   packed[0] = (c0.rgb, c1.r)
    ///   packed[1] = (c1.gb, c2.rg)
    ///   packed[2] = (c2.b, c3.rgb)
    ///   packed[3] = (c4.rgb, c5.r)
    ///   packed[4] = (c5.gb, c6.rg)
    ///   packed[5] = (c6.b, c7.rgb)
    ///   packed[6] = (c8.rgb, pad)
    /// GLSL 디코드는 SH-5 셰이더에서 동일 순서로 복원.
    /// 50k 인스턴스 × 112B ≈ 5.6MB.
    /// </summary>
    public struct SHPacked // 16B x 7, bittable
    {
        public Vector4 p0, p1, p2, p3, p4, p5, p6;
        public const int Float4Count = 7;
        public const int Stride = 7 * 16;

        public static SHPacked Pack(in SH9 s)
        {
            return new SHPacked()
            {
                p0 = new Vector4(s.c0.x, s.c0.y, s.c0.z, s.c1.x),
                p1 = new Vector4(s.c1.y, s.c1.z, s.c2.x, s.c2.y),
                p2 = new Vector4(s.c2.z, s.c3.x, s.c3.y, s.c3.z),
                p3 = new Vector4(s.c4.x, s.c4.y, s.c4.z, s.c5.x),
                p4 = new Vector4(s.c5.y, s.c5.z, s.c6.x, s.c6.y),
                p5 = new Vector4(s.c6.z, s.c7.x, s.c7.y, s.c7.z),
                p6 = new Vector4(s.c8.x, s.c8.y, s.c8.z, 0)
            };
        }
        public SH9 Unpacked()
        {
            return new SH9
            {
                c0 = new Vector3(p0.x, p0.y, p0.z),
                c1 = new Vector3(p0.w, p1.x, p1.y),
                c2 = new Vector3(p1.z, p1.w, p2.x),
                c3 = new Vector3(p2.y, p2.z, p2.w),
                c4 = new Vector3(p3.x, p3.y, p3.z),
                c5 = new Vector3(p3.w, p4.x, p4.y),
                c6 = new Vector3(p4.z, p4.w, p5.x),
                c7 = new Vector3(p5.y, p5.z, p5.w),
                c8 = new Vector3(p6.x, p6.y, p6.z),
            };
        }
    }
    /// <summary>SH9 배열 → 정렬 패킹 → GraphicsBuffer(SSBO/StructuredBuffer) 업로드. IDisposable.</summary>
    public sealed class SHInstancedBuffer : IDisposable
    {
        public GraphicsBuffer Buffer { get; private set; }
        public int Count { get; private set; }
        public const string DefaultShaderProp = "_InstanceSH";

        /// <param name="SH">패킹할 SH9 배열. 다중 프로브면 인스턴스당 probesPerInstance 개가
        ///   연속(inst0_probe0, inst0_probe1, inst1_probe0, …) 이어야 함(셰이더 인덱싱과 정합).</param>
        /// <param name="probesPerInstance">인스턴스당 SH 프로브 수. 셰이더는 instanceID*(7*P)+probe*7+k 로 읽음.
        ///   1=단일(기존), 2=상/하 블렌드(면별 수직음영, EvaluateInstanceSH2).</param>
        public static SHInstancedBuffer Create(NativeArray<SH9> SH, int probesPerInstance = 1)
        {
            if (probesPerInstance < 1) throw new ArgumentException("probesPerInstance ≥ 1");
            if (SH.Length % probesPerInstance != 0)
                throw new ArgumentException($"SH 길이({SH.Length})가 probesPerInstance({probesPerInstance}) 배수 아님");
            int total = SH.Length;                     // 인스턴스 × 프로브
            int n = total / probesPerInstance;         // 인스턴스 수(=Count, 렌더러 행렬 수와 정합)
            // 셰이더는 _InstanceSH 를 StructuredBuffer<float4>(stride 16)로 보고 프로브당 7개를
            // 연속 인덱스로 읽는다(EvaluateSH9.hlsl). 따라서 버퍼도 반드시 float4 × (total·7),
            // stride 16 으로 만들어야 stride 정합(112 로 만들면 SRV stride 불일치 → 오독/바인딩 거부).
            var flat = new Vector4[total * SHPacked.Float4Count];
            for (int i = 0; i < total; i++)
            {
                var p = SHPacked.Pack(SH[i]);
                int b = i * SHPacked.Float4Count;
                flat[b + 0] = p.p0; flat[b + 1] = p.p1; flat[b + 2] = p.p2; flat[b + 3] = p.p3;
                flat[b + 4] = p.p4; flat[b + 5] = p.p5; flat[b + 6] = p.p6;
            }

            var buf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, total * SHPacked.Float4Count, sizeof(float) * 4);
            buf.SetData(flat);
            return new SHInstancedBuffer() { Buffer = buf, Count = n }; // Count=인스턴스 수 유지 — 렌더러 정합 검사용

        }


        /// <summary>머티리얼에 바인딩(Structured/SSBO). DrawMeshInstancedIndirect 시 transform 버퍼와 동일 인덱싱.</summary>
        public void Bind(Material mat, string prop = DefaultShaderProp) => mat.SetBuffer(prop, Buffer);
        public void Bind(MaterialPropertyBlock mpb, string prop = DefaultShaderProp) => mpb.SetBuffer(prop, Buffer);

        public void Dispose()
        {
            Buffer?.Dispose();
            Buffer = null;
        }
    }
}
