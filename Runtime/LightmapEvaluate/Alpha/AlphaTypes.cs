using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 삼각형 UV0 3개. <see cref="Tri"/> 와 '병렬 배열'로 같은 인덱스에 대응한다(α 결정 ②).
    /// Tri(36B) 자체는 GpuTri.Stride=36 · BVH 빌드 버퍼에 직결되므로 절대 확장하지 않는다.
    /// GPU stride 24 (3 × float2) — PathTrace.compute 의 GpuTriUV 와 1:1.
    /// </summary>
    public struct TriUV
    {
        public Vector2 UV0, UV1, UV2;
        public const int Stride = 24;
    }

    /// <summary>
    /// 알파 컷아웃 판정의 '부동소수 부분'을 한 곳에 모은 순수 함수 모음.
    ///
    /// 왜 한 곳인가: CPU/Burst/GPU 세 백엔드가 같은 결과를 내야 하는데, 마스크 비트 조회는
    /// 정수 연산이라 자동으로 일치하고 **위험한 곳은 오직 UV→텍셀 좌표 변환의 float 연산뿐**이다.
    /// 그 연산을 이 파일 하나로 국한시켜, HLSL 미러가 이 함수만 정확히 베끼면 되게 한다.
    ///
    /// ⚠ mad 융합 금지: uu = u*st.x + st.z 를 한 줄로 쓰면 HLSL 이 mad 로 융합해 CPU 와
    ///   마지막 비트가 갈릴 수 있다(텍셀 경계에서 1텍셀 차이). 곱→대입→덧셈으로 분리하고
    ///   HLSL 쪽은 precise 로 못박는다.
    /// </summary>
    public static class AlphaMath
    {
        /// <summary>barycentric(bu,bv) → UV 보간. RayTriUV 규약: bu↔V1, bv↔V2, w0=1-bu-bv↔V0.</summary>
        public static Vector2 InterpUV(in TriUV t, float bu, float bv)
        {
            float w0 = 1f - bu - bv;
            return new Vector2(
                t.UV0.x * w0 + t.UV1.x * bu + t.UV2.x * bv,
                t.UV0.y * w0 + t.UV1.y * bu + t.UV2.y * bv);
        }

        /// <summary>
        /// UV → 마스크 내 선형 비트 인덱스(Repeat wrap). w/h 는 &gt;0 이어야 한다.
        /// NaN/Inf/오버플로는 0번 텍셀로 접는다(판정 불능 시 '불투명 쪽'이 아니라 결정적 값).
        /// </summary>
        public static int TexelBit(float u, float v, int w, int h, Vector4 st)
        {
            float uu = u * st.x; uu = uu + st.z;   // ⚠ 융합 금지(두 줄로 분리)
            float vv = v * st.y; vv = vv + st.w;

            int x = WrapFloor(uu, w);
            int y = WrapFloor(vv, h);
            return y * w + x;
        }

        /// <summary>floor(t * n) 을 [0,n) 으로 접는다. 음수 UV·타일링 반복을 포함.</summary>
        static int WrapFloor(float t, int n)
        {
            float f = Mathf.Floor(t * n);
            // NaN 은 모든 비교가 false → 이 조건이 false 가 되어 0 으로 접힌다(의도).
            if (!(f > -2.1e9f && f < 2.1e9f)) return 0;
            int i = (int)f;
            if (i >= 0 && i < n) return i;    // 빠른 경로: UV∈[0,1) 인 대다수 (나머지 연산 회피)
            i %= n;
            if (i < 0) i += n;
            return i;
        }
    }

    /// <summary>
    /// 알파 컷아웃 씬 데이터 — **세 백엔드의 공통 원본**(관리형 배열).
    ///
    /// 이 프로젝트의 기존 패턴(`Tri[][] uniqueMeshes` → 관리형 BVH / BurstScene / GpuScene)과
    /// 동일하게, 관리형 원본 하나를 만들어 각 백엔드가 자기 형태로 물질화한다.
    ///  - CPU  : 이 객체를 그대로 사용(ground truth)
    ///  - Burst: <see cref="BurstAlpha"/> 가 NativeArray 로 복사
    ///  - GPU  : <see cref="GpuScene.BindAlpha"/> 가 ComputeBuffer 로 업로드
    ///
    /// 인덱싱 규약
    ///  - <see cref="TriUV"/>/<see cref="TriSubmesh"/> : `MeshTriStart[mesh] + 메시로컬삼각형인덱스`
    ///    (= BurstScene.blasTriStart 와 동일 오프셋. 둘 다 유니크 메시 삼각형 수의 누적합이다.)
    ///  - matId = `MatSlot[ InstMatBase[instance] + TriSubmesh[...] ]`, 음수면 불투명.
    /// </summary>
    public sealed class AlphaSceneData
    {
        /// <summary>컷아웃 머티리얼이 하나도 없으면 false → 전 백엔드가 기존 경로를 그대로 탄다(결정 ⑥).</summary>
        public bool Enabled;

        // ── 삼각형 속성(메시 concat) ──
        public TriUV[] TriUV = System.Array.Empty<TriUV>();
        public byte[] TriSubmesh = System.Array.Empty<byte>();

        // ── 메시별 ──
        public byte[] MeshHasCutout = System.Array.Empty<byte>();  // 0 이면 그 BLAS 는 early-exit 유지
        public int[] MeshTriStart = System.Array.Empty<int>();

        // ── 인스턴스별 머티리얼 슬롯 ──
        public int[] InstMatBase = System.Array.Empty<int>();
        public int[] MatSlot = System.Array.Empty<int>();          // -1 = 불투명

        // ── 마스크 테이블(matId 로 인덱싱) ──
        public uint[] MaskBits = System.Array.Empty<uint>();       // 1bit/texel, LSB-first
        public int[] MaskWord = System.Array.Empty<int>();         // MaskBits 시작 워드
        public int[] MaskW = System.Array.Empty<int>();            // 0 = 마스크 없음(불투명 취급)
        public int[] MaskH = System.Array.Empty<int>();
        public Vector4[] MaskST = System.Array.Empty<Vector4>();   // (tiling.xy, offset.xy)

        /// <summary>인스턴스+메시로컬삼각형 → matId. 음수면 불투명.</summary>
        public int MatIdOf(int matBase, int mesh, int localTri)
        {
            int slot = matBase + TriSubmesh[MeshTriStart[mesh] + localTri];
            return (uint)slot < (uint)MatSlot.Length ? MatSlot[slot] : -1;
        }

        /// <summary>matId 의 (u,v) 지점이 불투명한가. 마스크가 없으면 항상 true.</summary>
        public bool Opaque(int matId, float u, float v)
        {
            if (matId < 0) return true;
            int w = MaskW[matId];
            if (w == 0) return true;

            int bit = AlphaMath.TexelBit(u, v, w, MaskH[matId], MaskST[matId]);
            uint word = MaskBits[MaskWord[matId] + (bit >> 5)];
            return (word & (1u << (bit & 31))) != 0u;
        }

        /// <summary>히트한 삼각형이 불투명한가(= 차폐로 인정할 것인가). α 순회의 단일 진입점.</summary>
        public bool HitOpaque(int matBase, int mesh, int localTri, float bu, float bv)
        {
            int matId = MatIdOf(matBase, mesh, localTri);
            if (matId < 0) return true;
            Vector2 uv = AlphaMath.InterpUV(TriUV[MeshTriStart[mesh] + localTri], bu, bv);
            return Opaque(matId, uv.x, uv.y);
        }

        /// <summary>이 메시에 컷아웃 서브메시가 하나라도 있는가(BLAS 단위 게이트).</summary>
        public bool MeshCutout(int mesh) => MeshHasCutout[mesh] != 0;

        public static readonly AlphaSceneData Disabled = new AlphaSceneData { Enabled = false };
    }
}
