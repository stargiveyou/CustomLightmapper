using System;
using Unity.Collections;
using UnityEngine;
using ReadOnlyAttribute = Unity.Collections.ReadOnlyAttribute;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// α 트랙 Burst POD — <see cref="AlphaSceneData"/>(관리형 원본)의 NativeArray 미러.
    ///
    /// <see cref="BurstScene"/> 의 필드로 들어간다. 잡(AoJob/DirectJob/IndirectJob/SHJob)은 이미
    /// BurstScene 을 통째로 들고 있으므로 **잡 시그니처를 하나도 바꾸지 않고** 알파가 배선된다.
    ///
    /// ⚠ 잡 안전 시스템: BurstScene.cs 상단 주석과 같은 이유로 모든 NativeArray 에 [ReadOnly] 필수.
    ///   또한 잡 스케줄 시 NativeArray 필드는 '항상 할당돼 있어야' 하므로, 알파가 꺼진 씬에서도
    ///   1원소 더미를 할당한다(<see cref="CreateDisabled"/>). enabled=false 면 순회가 이 배열들을
    ///   아예 읽지 않는다.
    /// </summary>
    public struct BurstAlpha : IDisposable
    {
        public bool enabled;

        [ReadOnly] public NativeArray<TriUV> triUV;
        [ReadOnly] public NativeArray<byte> triSubmesh;
        [ReadOnly] public NativeArray<byte> meshHasCutout;
        [ReadOnly] public NativeArray<int> meshTriStart;
        [ReadOnly] public NativeArray<int> instMatBase;
        [ReadOnly] public NativeArray<int> matSlot;

        [ReadOnly] public NativeArray<uint> maskBits;
        [ReadOnly] public NativeArray<int> maskWord;
        [ReadOnly] public NativeArray<int> maskW;
        [ReadOnly] public NativeArray<int> maskH;
        [ReadOnly] public NativeArray<Vector4> maskST;

        // ── 판정 (AlphaSceneData 와 문자 그대로 같은 식) ────────────────────────

        /// <summary>메시 단위 게이트: 컷아웃 서브메시가 없으면 early-exit 순회를 유지한다.</summary>
        public bool MeshCutout(int mesh) => enabled && meshHasCutout[mesh] != 0;

        public int MatIdOf(int matBase, int mesh, int localTri)
        {
            int slot = matBase + triSubmesh[meshTriStart[mesh] + localTri];
            return (uint)slot < (uint)matSlot.Length ? matSlot[slot] : -1;
        }

        public bool Opaque(int matId, float u, float v)
        {
            if (matId < 0) return true;
            int w = maskW[matId];
            if (w == 0) return true;

            int bit = AlphaMath.TexelBit(u, v, w, maskH[matId], maskST[matId]);
            uint word = maskBits[maskWord[matId] + (bit >> 5)];
            return (word & (1u << (bit & 31))) != 0u;
        }

        /// <summary>히트한 삼각형이 불투명한가(= 차폐로 인정할 것인가).</summary>
        public bool HitOpaque(int matBase, int mesh, int localTri, float bu, float bv)
        {
            int matId = MatIdOf(matBase, mesh, localTri);
            if (matId < 0) return true;
            Vector2 uv = AlphaMath.InterpUV(triUV[meshTriStart[mesh] + localTri], bu, bv);
            return Opaque(matId, uv.x, uv.y);
        }

        // ── 생성/해제 ───────────────────────────────────────────────────────────

        /// <summary>알파 미사용 씬용 더미(1원소). 잡 스케줄 시 미할당 NativeArray 예외를 피하기 위함.</summary>
        public static BurstAlpha CreateDisabled(Allocator allocator)
        {
            return new BurstAlpha
            {
                enabled = false,
                triUV = new NativeArray<TriUV>(1, allocator),
                triSubmesh = new NativeArray<byte>(1, allocator),
                meshHasCutout = new NativeArray<byte>(1, allocator),
                meshTriStart = new NativeArray<int>(1, allocator),
                instMatBase = new NativeArray<int>(1, allocator),
                matSlot = new NativeArray<int>(1, allocator),
                maskBits = new NativeArray<uint>(1, allocator),
                maskWord = new NativeArray<int>(1, allocator),
                maskW = new NativeArray<int>(1, allocator),
                maskH = new NativeArray<int>(1, allocator),
                maskST = new NativeArray<Vector4>(1, allocator),
            };
        }

        public static BurstAlpha Create(AlphaSceneData src, Allocator allocator)
        {
            if (src == null || !src.Enabled) return CreateDisabled(allocator);

            var a = new BurstAlpha
            {
                enabled = true,
                triUV = Copy(src.TriUV, allocator),
                triSubmesh = Copy(src.TriSubmesh, allocator),
                meshHasCutout = Copy(src.MeshHasCutout, allocator),
                meshTriStart = Copy(src.MeshTriStart, allocator),
                instMatBase = Copy(src.InstMatBase, allocator),
                matSlot = Copy(src.MatSlot, allocator),
                maskBits = Copy(src.MaskBits, allocator),
                maskWord = Copy(src.MaskWord, allocator),
                maskW = Copy(src.MaskW, allocator),
                maskH = Copy(src.MaskH, allocator),
                maskST = Copy(src.MaskST, allocator),
            };
            return a;
        }

        // 길이 0 배열도 잡 안전 시스템상 '할당된' 상태여야 하므로 최소 1원소를 보장한다.
        static NativeArray<T> Copy<T>(T[] src, Allocator allocator) where T : struct
        {
            int n = (src != null) ? src.Length : 0;
            var dst = new NativeArray<T>(Mathf.Max(1, n), allocator);
            for (int i = 0; i < n; i++) dst[i] = src[i];
            return dst;
        }

        public void Dispose()
        {
            if (triUV.IsCreated) triUV.Dispose();
            if (triSubmesh.IsCreated) triSubmesh.Dispose();
            if (meshHasCutout.IsCreated) meshHasCutout.Dispose();
            if (meshTriStart.IsCreated) meshTriStart.Dispose();
            if (instMatBase.IsCreated) instMatBase.Dispose();
            if (matSlot.IsCreated) matSlot.Dispose();
            if (maskBits.IsCreated) maskBits.Dispose();
            if (maskWord.IsCreated) maskWord.Dispose();
            if (maskW.IsCreated) maskW.Dispose();
            if (maskH.IsCreated) maskH.Dispose();
            if (maskST.IsCreated) maskST.Dispose();
            enabled = false;
        }
    }
}
