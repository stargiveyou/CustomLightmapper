using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// G4: <see cref="BurstScene"/>(SoA POD) 를 GPU ComputeBuffer 로 업로드.
    ///   BvhTraverse.compute 의 StructuredBuffer 들과 1:1 매핑. 조명 없음(순수 순회).
    ///
    /// 재패킹 정책(트랩 회피):
    ///  ① 노드/삼각형은 명시적 GPU struct(<see cref="GpuNode"/>/<see cref="GpuTri"/>)로 재패킹 →
    ///     NativeArray&lt;BVH.Node&gt;/&lt;Tri&gt; 의 Vector3 정렬 모호성 제거. stride 32/36.
    ///  ② 행렬은 float4x4 를 통째로 올려 mul() 에 의존하지 않는다(column-major 트랩 회피).
    ///     Unity Matrix4x4.GetRow(r) = (mR0,mR1,mR2,mR3) 를 3행만 업로드 →
    ///     셰이더가 MultiplyPoint3x4/MultiplyVector 를 명시적 dot 으로 재현.
    ///
    /// StructuredBuffer 는 tight(scalar) packing → C# [StructLayout(Sequential)] 의
    /// byte 크기(=Stride 상수)와 HLSL struct 가 정확히 일치해야 함(SRV stride 불일치=오독).
    ///
    /// IDisposable — 모든 ComputeBuffer 해제.
    /// </summary>
    public sealed class GpuScene : IDisposable
    {
        // ── HLSL struct 미러 (stride 는 HLSL 과 반드시 동일) ──
        [StructLayout(LayoutKind.Sequential)]
        public struct GpuNode                 // 32B
        {
            public Vector3 bmin;              // 0
            public Vector3 bmax;              // 12
            public int leftFirst;             // 24
            public int count;                 // 28
            public const int Stride = 32;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GpuTri                  // 36B
        {
            public Vector3 v0, v1, v2;
            public const int Stride = 36;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GpuInstance             // 64B
        {
            public Vector4 w2lRow0, w2lRow1, w2lRow2;
            public int meshIndex;
            public int pad0, pad1, pad2;
            public const int Stride = 64;
        }

        // G5(추가): 인스턴스 노멀 행렬 3행. MultiplyVector(상단 3x3, 평행이동 무시) 재현용.
        // GpuInstance(w2l) 와 병렬(같은 인스턴스 인덱스). 별도 버퍼로 두어 G4 GpuInstance(64B) 를 건드리지 않음.
        [StructLayout(LayoutKind.Sequential)]
        public struct GpuInstNormal           // 48B : instNormalMatrix.GetRow(0..2)
        {
            public Vector4 n2wRow0, n2wRow1, n2wRow2;
            public const int Stride = 48;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GpuRay                  // 32B
        {
            public Vector3 origin;
            public float tmin;
            public Vector3 dir;
            public float tmax;
            public const int Stride = 32;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GpuHit                  // 32B
        {
            public int valid;
            public float t;
            public int inst;
            public int mesh;
            public int tri;
            public int pad0, pad1, pad2;
            public const int Stride = 32;
        }

        // ── 씬 버퍼 ──
        public readonly ComputeBuffer TlasNodes;
        public readonly ComputeBuffer InstIdx;
        public readonly ComputeBuffer Instances;
        public readonly ComputeBuffer BlasNodes;
        public readonly ComputeBuffer BlasTriIdx;
        public readonly ComputeBuffer BlasTris;
        public readonly ComputeBuffer BlasNodeStart;
        public readonly ComputeBuffer BlasNodeCount;
        public readonly ComputeBuffer BlasTriIdxStart;
        public readonly ComputeBuffer BlasTriStart;
        // G5(추가): 조명 경로추적용. 순회 버퍼와 별개 — G4 순회는 이들을 참조하지 않음.
        public readonly ComputeBuffer InstNormals;   // 48B: instNormalMatrix 3행 (월드 노멀 변환)
        public readonly ComputeBuffer MeshAlbedo;    // 12B: 메시별 float3 알베도 (모드 A)
        public readonly int TlasCount;

        public GpuScene(in BurstScene s)
        {
            TlasCount = s.tlasCount;

            // 노드 재패킹 (TLAS / BLAS)
            TlasNodes = MakeNodeBuffer(s.tlasNodes);
            BlasNodes = MakeNodeBuffer(s.blasNodes);

            // 삼각형 재패킹
            {
                int n = s.blasTris.Length;
                var arr = new GpuTri[Mathf.Max(1, n)];
                for (int i = 0; i < n; i++)
                {
                    Tri t = s.blasTris[i];
                    arr[i] = new GpuTri { v0 = t.V0, v1 = t.V1, v2 = t.V2 };
                }
                BlasTris = new ComputeBuffer(Mathf.Max(1, n), GpuTri.Stride, ComputeBufferType.Structured);
                BlasTris.SetData(arr);
            }

            // 인스턴스: w2l 3행 + meshIndex
            {
                int n = s.instWorldToLocal.Length;
                var arr = new GpuInstance[Mathf.Max(1, n)];
                for (int i = 0; i < n; i++)
                {
                    Matrix4x4 w2l = s.instWorldToLocal[i];
                    arr[i] = new GpuInstance
                    {
                        w2lRow0 = w2l.GetRow(0),   // (m00,m01,m02,m03)
                        w2lRow1 = w2l.GetRow(1),
                        w2lRow2 = w2l.GetRow(2),
                        meshIndex = s.instBlas[i],
                    };
                }
                Instances = new ComputeBuffer(Mathf.Max(1, n), GpuInstance.Stride, ComputeBufferType.Structured);
                Instances.SetData(arr);
            }

            // G5: 인스턴스 노멀행렬 3행 (MultiplyVector = dot(rowR.xyz, localN)). w2l 와 동일 인덱스 병렬.
            {
                int n = s.instNormalMatrix.IsCreated ? s.instNormalMatrix.Length : 0;
                var arr = new GpuInstNormal[Mathf.Max(1, n)];
                for (int i = 0; i < n; i++)
                {
                    Matrix4x4 nm = s.instNormalMatrix[i];
                    arr[i] = new GpuInstNormal
                    {
                        n2wRow0 = nm.GetRow(0),
                        n2wRow1 = nm.GetRow(1),
                        n2wRow2 = nm.GetRow(2),
                    };
                }
                InstNormals = new ComputeBuffer(Mathf.Max(1, n), GpuInstNormal.Stride, ComputeBufferType.Structured);
                InstNormals.SetData(arr);
            }

            // G5: 메시별 알베도(float3, tight 12B). 미생성 씬(G4 순회 전용)은 fallback 0.5 1개.
            {
                int n = s.meshAlbedo.IsCreated ? s.meshAlbedo.Length : 0;
                var arr = new Vector3[Mathf.Max(1, n)];
                for (int i = 0; i < n; i++) arr[i] = s.meshAlbedo[i];
                if (n == 0) arr[0] = new Vector3(0.5f, 0.5f, 0.5f);
                MeshAlbedo = new ComputeBuffer(Mathf.Max(1, n), 12, ComputeBufferType.Structured);
                MeshAlbedo.SetData(arr);
            }

            // int 배열들 — NativeArray 직접 SetData(GC 없음)
            InstIdx        = MakeIntBuffer(s.instIdx);
            BlasTriIdx     = MakeIntBuffer(s.blasTriIdx);
            BlasNodeStart  = MakeIntBuffer(s.blasNodeStart);
            BlasNodeCount  = MakeIntBuffer(s.blasNodeCount);
            BlasTriIdxStart = MakeIntBuffer(s.blasTriIdxStart);
            BlasTriStart   = MakeIntBuffer(s.blasTriStart);
        }

        static ComputeBuffer MakeNodeBuffer(Unity.Collections.NativeArray<BVH.Node> src)
        {
            int n = src.Length;
            var arr = new GpuNode[Mathf.Max(1, n)];
            for (int i = 0; i < n; i++)
            {
                BVH.Node nd = src[i];
                arr[i] = new GpuNode { bmin = nd.Min, bmax = nd.Max, leftFirst = nd.LeftFirst, count = nd.Count };
            }
            var cb = new ComputeBuffer(Mathf.Max(1, n), GpuNode.Stride, ComputeBufferType.Structured);
            cb.SetData(arr);
            return cb;
        }

        static ComputeBuffer MakeIntBuffer(Unity.Collections.NativeArray<int> src)
        {
            int n = Mathf.Max(1, src.Length);
            var cb = new ComputeBuffer(n, sizeof(int), ComputeBufferType.Structured);
            if (src.Length > 0) cb.SetData(src);
            return cb;
        }

        /// <summary>커널에 모든 씬 SRV + 스칼라 uniform 을 바인딩.</summary>
        public void Bind(ComputeShader cs, int kernel)
        {
            cs.SetBuffer(kernel, "_TlasNodes", TlasNodes);
            cs.SetBuffer(kernel, "_InstIdx", InstIdx);
            cs.SetBuffer(kernel, "_Instances", Instances);
            cs.SetBuffer(kernel, "_BlasNodes", BlasNodes);
            cs.SetBuffer(kernel, "_BlasTriIdx", BlasTriIdx);
            cs.SetBuffer(kernel, "_BlasTris", BlasTris);
            cs.SetBuffer(kernel, "_BlasNodeStart", BlasNodeStart);
            cs.SetBuffer(kernel, "_BlasNodeCount", BlasNodeCount);
            cs.SetBuffer(kernel, "_BlasTriIdxStart", BlasTriIdxStart);
            cs.SetBuffer(kernel, "_BlasTriStart", BlasTriStart);
            cs.SetInt("_TlasCount", TlasCount);
        }

        /// <summary>G5: 경로추적 커널에 노멀행렬·메시알베도 SRV 를 추가 배선. <see cref="Bind"/> 이후 호출.
        /// (G4 순회 커널은 이 두 버퍼를 선언하지 않으므로 호출하지 않는다.)</summary>
        public void BindLighting(ComputeShader cs, int kernel)
        {
            cs.SetBuffer(kernel, "_InstNormals", InstNormals);
            cs.SetBuffer(kernel, "_MeshAlbedo", MeshAlbedo);
        }

        public void Dispose()
        {
            TlasNodes?.Dispose();
            InstIdx?.Dispose();
            Instances?.Dispose();
            BlasNodes?.Dispose();
            BlasTriIdx?.Dispose();
            BlasTris?.Dispose();
            BlasNodeStart?.Dispose();
            BlasNodeCount?.Dispose();
            BlasTriIdxStart?.Dispose();
            BlasTriStart?.Dispose();
            InstNormals?.Dispose();
            MeshAlbedo?.Dispose();
        }
    }
}
