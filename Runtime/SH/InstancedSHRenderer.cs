using System;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// SH-5 런타임: per-instance SH9 를 DrawMeshInstancedIndirect 로 렌더(Built-In · D3D11 StructuredBuffer).
    ///
    /// 인덱싱 일치(생명선): 어댑터 대표점 순서 = SH 버퍼 순서 = 인스턴스 행렬 버퍼 순서 = SV_InstanceID.
    ///   _InstanceMatrix : StructuredBuffer<float4x4>  (인스턴스 L2W)
    ///   _InstanceSH     : StructuredBuffer<float4> (SHPacked, 인스턴스당 7개) — SHInstanceBuffer.Create
    /// 셰이더: "HuskyLibs/InstancedSH_BuiltIn".
    ///
    /// 대용량 대비: 버퍼는 GraphicsBuffer(Structured/IndirectArguments). 행렬 배열은 호출측이
    ///   Job/Burst 로 만든 NativeArray 를 Matrix4x4[] 로 넘기거나 SetData(NativeArray) 로 직접 업로드 가능.
    /// 서브메시: 메시의 전 submesh 를 각각 그린다(머티리얼별 다중 submesh 실 FBX 대응 — 베이크 경로와 정합).
    /// </summary>
    public sealed class InstancedSHRenderer : IDisposable
    {
        readonly Mesh _mesh;
        readonly Material _mat;
        readonly int _count;
        readonly Bounds _bounds;
        readonly bool _ownsSh;

        GraphicsBuffer _args;
        GraphicsBuffer _matrices;
        SHInstancedBuffer _sh;
        MaterialPropertyBlock _mpb;   // per-렌더러 버퍼(머티리얼 공유 시 덮어쓰기 방지)


        static readonly int ID_Matrix = Shader.PropertyToID("_InstanceMatrix");
        static readonly int ID_Color = Shader.PropertyToID("_Color");

        /// <param name="ownsSh">true 면 이 렌더러가 Dispose 시 SH 버퍼도 해제.</param>
        /// <param name="albedo">표면 알베도(_Color). 머티리얼 공유 시 템플릿별 색을 MPB 로 주입.
        ///   null 이면 셰이더 기본값(흰색) 유지.</param>
        public InstancedSHRenderer(Mesh mesh, Material material, Matrix4x4[] matrices, SHInstancedBuffer sh, Bounds worldBounds, bool ownSh = false, Vector4? albedo = null)
        {
            if (mesh == null || material == null || matrices == null || sh == null)
                throw new ArgumentNullException();
            if (matrices.Length != sh.Count)
                throw new ArgumentException($"행렬 수({matrices.Length}) ≠ SH 수({sh.Count}) — SV_InstanceID 순서 불일치");
            if (SystemInfo.maxComputeBufferInputsVertex <= 0 && !SystemInfo.supportsInstancing)
                Debug.LogWarning("[InstancedSHRenderer] 플랫폼이 StructuredBuffer(정점) 또는 인스턴싱을 제한할 수 있음.");

            _mesh = mesh; _mat = material; _sh = sh;
            _count = matrices.Length; _bounds = worldBounds; _ownsSh = ownSh;

            _matrices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 16);
            _matrices.SetData(matrices);

            // args: submesh 당 { indexCount, instanceCount, startIndex, baseVertex, startInstance } 5 uint
            int subMeshCount = mesh.subMeshCount;
            _args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, subMeshCount, 5 * sizeof(uint));
            var argData = new uint[subMeshCount * 5];
            for (int s = 0; s < subMeshCount; s++)
            {
                argData[s * 5 + 0] = mesh.GetIndexCount(s);
                argData[s * 5 + 1] = (uint)_count;
                argData[s * 5 + 2] = mesh.GetIndexStart(s);
                argData[s * 5 + 3] = mesh.GetBaseVertex(s);
                argData[s * 5 + 4] = 0u;
            }
            _args.SetData(argData);

            _mpb = new MaterialPropertyBlock();
            _mpb.SetBuffer(ID_Matrix, _matrices);
            if (albedo.HasValue) _mpb.SetVector(ID_Color, albedo.Value); // 템플릿별 _Color 주입(공유 머티리얼 대응)
            _sh.Bind(_mpb, SHInstancedBuffer.DefaultShaderProp);

        }

        /// <summary>인스턴스 행렬 갱신(정적이면 불필요; 동적/스트리밍용).</summary>
        public void UpdateMatrices(Matrix4x4[] matrices)
        {
            if (matrices.Length != _count) throw new ArgumentException("행렬 수 변경 불가(재생성 필요)");
            _matrices.SetData(matrices);
        }

        /// <summary>매 프레임 호출(예: MonoBehaviour.Update / LateUpdate). 전 submesh 를 각각 드로우.</summary>
        public void Draw()
        {
            int subMeshCount = _mesh.subMeshCount;
            for (int s = 0; s < subMeshCount; s++)
            {
                Graphics.DrawMeshInstancedIndirect(
                    _mesh, s, _mat, _bounds, _args,
                    argsOffset: s * 5 * sizeof(uint), properties: _mpb,
                    castShadows: UnityEngine.Rendering.ShadowCastingMode.On,
                    receiveShadows: true);
            }
        }

        public void Dispose()
        {
            _args?.Dispose(); _args = null;
            _matrices?.Dispose(); _matrices = null;
            if (_ownsSh) { _sh?.Dispose(); }
            _sh = null;
        }
    }


}