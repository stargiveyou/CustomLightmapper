using System;
using Unity.Collections;
using UnityEngine;
using ReadOnlyAttribute = Unity.Collections.ReadOnlyAttribute;
namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// POD 빌더 + 순회 (Interfaceless) : 향후 Job/GPU 이식 토대(G1 AO부터 실제 Job화). 
    /// 관리/디스패치 없이 순수 구조체·함수로 구현.
    /// TwoLevelBVH.IntersectInstanced/Occluded/TransformNormalToWorld와 동일 로직 -> BVH.RayAABB·RayGeometry.RayTri 재사용 → 관리형과 비트 동일.
    /// </summary>
    public struct BurstScene : IDisposable
    {
        // 모든 NativeArray 는 잡 안에서 '읽기 전용'(BVH/씬 데이터). IJobParallelFor 에서 BurstScene 을
        // 필드로 쓸 때 [ReadOnly] 가 없으면 병렬 writer 로 간주되어 임의 인덱스 접근이 막힌다
        // (IndexOutOfRange: ReadWriteBuffers are restricted to the job index). 순회는 stack[] 으로
        // 임의 노드/인스턴스/삼각형을 읽으므로 반드시 [ReadOnly] 표시.

        //TLAS 월드
        [ReadOnly] public NativeArray<BVH.Node> tlasNodes;
        public int tlasCount;
        [ReadOnly] public NativeArray<int> instIdx; // TLAS 리프 슬롯 + 인스턴스

        //인스턴스
        [ReadOnly] public NativeArray<Matrix4x4> instWorldToLocal;
        [ReadOnly] public NativeArray<Matrix4x4> instNormalMatrix;
        [ReadOnly] public NativeArray<int> instBlas;

        // BLAS 연결 + 오프셋 (메시당 [START, COUNT])
        [ReadOnly] public NativeArray<BVH.Node> blasNodes;
        [ReadOnly] public NativeArray<int> blasTriIdx;
        [ReadOnly] public NativeArray<Tri> blasTris;
        [ReadOnly] public NativeArray<int> blasNodeStart, blasNodeCount, blasTriIdxStart, blasTriStart;


        // G3: 메시별 알베도(모드 A). **항상 할당된다** — 알베도를 안 넘긴 Create 도 fallback(0.5)로 채운다.
        //   잡 안전 시스템은 중첩 구조체 안의 NativeArray 까지 '할당됨'을 요구한다
        //   (미할당이면 스케줄 시 "DirectJob.scene.meshAlbedo has not been assigned or constructed").
        //   값은 ClosestHit 의 기존 fallback(0.5)과 동일하므로 거동 변화 없음.
        [ReadOnly] public NativeArray<Vector3> meshAlbedo;

        // α: 알파 컷아웃 any-hit 데이터. 항상 할당된다(꺼진 씬은 1원소 더미) — 잡 안전 시스템 요구.
        //    alpha.enabled=false 면 BurstTwoLevelBVH 가 기존 early-exit 순회를 그대로 탄다.
        public BurstAlpha alpha;

        public static BurstScene Create(TwoLevelBVH bvh, Vector3[] albedo, Allocator allocator)
            => Create(bvh, albedo, null, allocator);

        public static BurstScene Create(TwoLevelBVH bvh, Vector3[] albedo, AlphaSceneData alphaData, Allocator allocator)
        {
            // Create(bvh, allocator) 가 이미 meshAlbedo 를 fallback(0.5)로 채워 뒀다 → 재할당 없이 덮어쓰기.
            var s = Create(bvh, allocator);
            int meshCount = bvh.BlasCount;
            for (int i = 0; i < meshCount; i++)
            {
                if (albedo != null && i < albedo.Length) s.meshAlbedo[i] = albedo[i];
            }
            s.alpha.Dispose();                                  // Create(bvh,...) 가 만든 더미 해제
            s.alpha = BurstAlpha.Create(alphaData, allocator);
            return s;

        }

        public static BurstScene Create(TwoLevelBVH bvh, Allocator allocator = Allocator.TempJob)
        {
            var s = new BurstScene();

            var tlas = bvh.TlasRO;
            int tn = tlas.Length;
            s.tlasCount = bvh.TlasNodeCount;
            s.tlasNodes = new NativeArray<BVH.Node>(tn, allocator);
            for (int i = 0; i < tn; i++) s.tlasNodes[i] = tlas[i];

            var ii = bvh.InstIdxRO;
            int slotN = ii.Length;
            s.instIdx = new NativeArray<int>(slotN, allocator);
            for (int i = 0; i < slotN; i++) s.instIdx[i] = ii[i];

            int nInst = bvh.InstanceCount;
            s.instWorldToLocal = new NativeArray<Matrix4x4>(nInst, allocator);
            s.instNormalMatrix = new NativeArray<Matrix4x4>(nInst, allocator);
            s.instBlas = new NativeArray<int>(nInst, allocator);
            for (int i = 0; i < nInst; i++)
            {
                s.instWorldToLocal[i] = bvh.InstanceWorldToLocal(i);
                s.instNormalMatrix[i] = bvh.InstanceNormalMatrix(i);
                s.instBlas[i] = bvh.InstanceMesh(i);
            }


            int meshCount = bvh.BlasCount;
            s.blasNodeStart = new NativeArray<int>(meshCount, allocator);
            s.blasNodeCount = new NativeArray<int>(meshCount, allocator);
            s.blasTriIdxStart = new NativeArray<int>(meshCount, allocator);
            s.blasTriStart = new NativeArray<int>(meshCount, allocator);

            int totN = 0, totTi = 0, totTr = 0;
            for (int m = 0; m < meshCount; m++)
            {
                var bl = bvh.Blas(m);
                totN += bl.NodesRO.Length; totTi += bl.TriIdxRO.Length; totTr += bl.TrisRO.Length;
            }
            s.blasNodes = new NativeArray<BVH.Node>(totN, allocator);
            s.blasTriIdx = new NativeArray<int>(totTi, allocator);
            s.blasTris = new NativeArray<Tri>(totTr, allocator);

            int no = 0, to = 0, tro = 0;
            for (int m = 0; m < meshCount; m++)
            {
                var bn = bvh.Blas(m).NodesRO; var bt = bvh.Blas(m).TriIdxRO; var br = bvh.Blas(m).TrisRO;
                s.blasNodeStart[m] = no; s.blasNodeCount[m] = bn.Length;
                s.blasTriIdxStart[m] = to; s.blasTriStart[m] = tro;
                for (int i = 0; i < bn.Length; i++) s.blasNodes[no + i] = bn[i];
                for (int i = 0; i < bt.Length; i++) s.blasTriIdx[to + i] = bt[i];
                for (int i = 0; i < br.Length; i++) s.blasTris[tro + i] = br[i];
                no += bn.Length; to += bt.Length; tro += br.Length;
            }

            // 잡 스케줄 시 '미할당 컨테이너' 예외를 막기 위해 알베도·알파를 항상 할당한다(길이 최소 1).
            //   meshAlbedo 는 ClosestHit 의 기존 fallback 과 같은 0.5 로 채운다 → 값 거동 불변.
            s.meshAlbedo = new NativeArray<Vector3>(Mathf.Max(1, meshCount), allocator);
            for (int i = 0; i < s.meshAlbedo.Length; i++) s.meshAlbedo[i] = new Vector3(0.5f, 0.5f, 0.5f);

            s.alpha = BurstAlpha.CreateDisabled(allocator);     // 알파 미사용 기본값(잡 스케줄용 더미)
            return s;
        }



        public void Dispose()
        {
            if (tlasNodes.IsCreated) tlasNodes.Dispose();
            if (instIdx.IsCreated) instIdx.Dispose();
            if (instWorldToLocal.IsCreated) instWorldToLocal.Dispose();
            if (instNormalMatrix.IsCreated) instNormalMatrix.Dispose();
            if (instBlas.IsCreated) instBlas.Dispose();
            if (blasNodes.IsCreated) blasNodes.Dispose();
            if (blasTriIdx.IsCreated) blasTriIdx.Dispose();
            if (blasTris.IsCreated) blasTris.Dispose();
            if (blasNodeStart.IsCreated) blasNodeStart.Dispose();
            if (blasNodeCount.IsCreated) blasNodeCount.Dispose();
            if (blasTriIdxStart.IsCreated) blasTriIdxStart.Dispose();
            if (blasTriStart.IsCreated) blasTriStart.Dispose();
            if (meshAlbedo.IsCreated) meshAlbedo.Dispose();
            alpha.Dispose();
        }
    }
}