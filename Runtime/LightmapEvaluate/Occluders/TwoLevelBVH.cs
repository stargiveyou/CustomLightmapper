using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 2단 BVH ( C1 확장 ) . DXR 구조
    ///     - BLAS : 유니크 메시당 단일 레벨 BVH ( 로컬 공간 ) -> 인스턴스 간 공유
    ///     - TLAS : 인스턴스 BVH. 인스턴스 = { 월드 AABB, world->local, BLAS 참조 }
    /// 
    /// 순회 : TLAS -> 인스턴스 AABB 적중 -> 레이를 인스턴스 로컬로 변환 -> BLAS 순회.
    /// 핵심 : 방향을 '정규화하지 않고' 변환(MultiplyVector) -> 파라메트릭 T가 월드와 일관
    ///     따라서 best.T /maxDist를 그대로 BLAS로 넘겨 가지치기, 차폐가 정확
    /// </summary>
    public sealed class TwoLevelBVH : IOccluder, IDisposable
    {
        public struct Instance
        {
            public int MeshIndex;       //uniqueMeshes Index
            public Matrix4x4 LocalToWorld;
        }
        struct InstanceRec
        {
            public Matrix4x4 WorldToLocal; //   레이 변환용
            public Matrix4x4 NormalMatrix; // // 로컬→월드 노멀 변환 = (M^-1)^T = WorldToLocal^T
            public int Blas;                //  BLAS  인덱스
        }

        // 인스턴스, 메시, 삼각형까지 식별하는 최근접 히트 (속성 조회용)
        public struct InstancedHit
        {
            public bool Valid;
            public float T;
            public int InstanceIndex;
            public int MeshIndex;
            public int MeshTriIndex;


        }
        BVH[] _blas;                        //  메시당 BLAS (로컬)
        NativeArray<BVH.Node> _tlas;        //  TLAS 평탄 노드 (월드)
        NativeArray<int> _instIdx;          //  TLAS 리프 -> 인스턴스 슬롯
        NativeArray<InstanceRec> _inst;     //  인스턴스 레코드
        int _tlasCount;

        public int InstanceCount => _inst.IsCreated ? _inst.Length : 0;
        public int BlasCount => _blas != null ? _blas.Length : 0;
        public int TlasNodeCount => _tlasCount;

        const int TlasLeafMax = 2;

        // G0b: BurstScene POD 빌더용 읽기전용 접근(로직 무변경).
        public NativeArray<BVH.Node>.ReadOnly TlasRO => _tlas.AsReadOnly();
        public NativeArray<int>.ReadOnly InstIdxRO => _instIdx.AsReadOnly();
        public Matrix4x4 InstanceWorldToLocal(int i) => _inst[i].WorldToLocal;
        public Matrix4x4 InstanceNormalMatrix(int i) => _inst[i].NormalMatrix;
        public int InstanceMesh(int i) => _inst[i].Blas;
        public BVH Blas(int m) => _blas[m];


        public TwoLevelBVH(Tri[][] uniqueMeshes, Instance[] instances, Allocator allocator = Allocator.Persistent, BVH.BuildQuality blasQuality = BVH.BuildQuality.SAH)
        {
            int meshCount = uniqueMeshes?.Length ?? 0;
            int instCount = instances?.Length ?? 0;

            // 1) BLAS 빌드 (메시당 1회)
            _blas = new BVH[meshCount];
            for (int m = 0; m < meshCount; m++)
            {
                _blas[m] = new BVH(uniqueMeshes[m] ?? Array.Empty<Tri>(), allocator, blasQuality);
            }

            _inst = new NativeArray<InstanceRec>(instCount, allocator);
            if (instCount == 0)
            {
                _tlas = new NativeArray<BVH.Node>(0, allocator);
                _instIdx = new NativeArray<int>(0, allocator);
                _tlasCount = 0;
                return;
            }


            // 2) 인스턴스 월드 AABB (= BLAS 로컬 루트 박스를 8코너 변환 )
            var instMin = new Vector3[instCount];
            var instMax = new Vector3[instCount];
            var instCenter = new Vector3[instCount];
            for (int i = 0; i < instCount; i++)
            {
                Instance inst = instances[i];
                _inst[i] = new InstanceRec
                {
                    WorldToLocal = inst.LocalToWorld.inverse,
                    NormalMatrix = inst.LocalToWorld.inverse.transpose, // (M⁻¹)ᵀ : 로컬 노멀 → 월드
                    Blas = inst.MeshIndex
                };
                BVH blas = _blas[inst.MeshIndex];
                WorldAabbOfBlas(blas, inst.LocalToWorld, out instMin[i], out instMax[i]);
                instCenter[i] = (instMin[i] + instMax[i]) / 2f;
            }

            // 3) TLAS 빌드(인스턴스에 대한 median BVH)
            var idx = new int[instCount];
            for (int i = 0; i < instCount; i++)
            {
                idx[i] = i;
            }

            var nodes = new List<BVH.Node>(instCount * 2);
            nodes.Add(new BVH.Node { LeftFirst = 0, Count = instCount }); // Root Node
            UpdateTlasBounds(nodes, 0, idx, instMin, instMax);

            var cmp = new InstAxisComparer { Centroid = instCenter };
            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                int ni = stack.Pop();
                BVH.Node node = nodes[ni];
                int first = node.LeftFirst;
                int count = node.Count;

                if (count <= TlasLeafMax) continue;


                int axis = LongestAxis(idx, first, count, instCenter);
                cmp.Axis = axis;
                System.Array.Sort(idx, first, count, cmp);
                int mid = first + count / 2;

                int li = nodes.Count;
                int ri = li + 1;
                nodes.Add(new BVH.Node { LeftFirst = first, Count = mid - first });
                nodes.Add(new BVH.Node { LeftFirst = mid, Count = first + count - mid });
                UpdateTlasBounds(nodes, li, idx, instMin, instMax);
                UpdateTlasBounds(nodes, ri, idx, instMin, instMax);

                node.LeftFirst = li;
                node.Count = 0;
                nodes[ni] = node;

                stack.Push(li);
                stack.Push(ri);
            }

            _tlasCount = nodes.Count;
            _tlas = new NativeArray<BVH.Node>(_tlasCount, allocator);
            for (int i = 0; i < _tlasCount; i++)
            {
                _tlas[i] = nodes[i];
            }
            _instIdx = new NativeArray<int>(instCount, allocator);
            for (int i = 0; i < instCount; i++)
            {
                _instIdx[i] = idx[i];
            }

        }

        static int LongestAxis(int[] idx, int first, int count, Vector3[] instCenter)
        {
            Vector3 mn = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 mx = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

            int end = first + count;
            for (int s = first; s < end; s++)
            {
                Vector3 ce = instCenter[idx[s]];
                mn = Vector3.Min(mn, ce);
                mx = Vector3.Max(mx, ce);
            }

            Vector3 ext = mx - mn;
            if (ext.x >= ext.y && ext.x >= ext.z) return 0;

            return ext.y >= ext.z ? 1 : 2;

        }

        static void UpdateTlasBounds(List<BVH.Node> nodes, int ni, int[] idx, Vector3[] instMin, Vector3[] instMax)
        {
            BVH.Node node = nodes[ni];
            Vector3 lo = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 hi = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

            int end = node.LeftFirst + node.Count;
            for (int s = node.LeftFirst; s < end; s++)
            {
                int t = idx[s];
                lo = Vector3.Min(lo, instMin[t]);
                hi = Vector3.Max(hi, instMax[t]);
            }

            node.Min = lo;
            node.Max = hi;
            nodes[ni] = node;
        }


        // -- 헬퍼 --
        static void WorldAabbOfBlas(BVH blas, Matrix4x4 localToWorld, out Vector3 instMin, out Vector3 instMax)
        {
            Vector3 a = blas.RootMin;
            Vector3 b = blas.RootMax;
            instMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            instMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = new Vector3(
                    (c & 1) == 0 ? a.x : b.x,
                    (c & 2) == 0 ? a.y : b.y,
                    (c & 4) == 0 ? a.z : b.z);
                Vector3 w = localToWorld.MultiplyPoint3x4(corner);
                instMin = Vector3.Min(instMin, w);
                instMax = Vector3.Max(instMax, w);
            }

        }

        // -- IOccluder --
        public Hit Intersect(Vector3 o, Vector3 d, float tmin, float tmax)
        {
            Hit best = new Hit { Valid = false, T = tmax };
            if (_tlasCount == 0) return best;
            Vector3 invD = new Vector3(1f / d.x, 1f / d.y, 1f / d.z);

            Span<int> stack = stackalloc int[64];
            int sp = 0;
            stack[sp++] = 0;
            while (sp > 0)
            {
                BVH.Node node = _tlas[stack[--sp]];
                if (!BVH.RayAABB(o, invD, node.Min, node.Max, tmin, best.T))
                    continue;

                if (node.Count > 0)
                {
                    int end = node.LeftFirst + node.Count;
                    for (int s = node.LeftFirst; s < end; s++)
                    {
                        int instIdx = _instIdx[s];
                        InstanceRec rec = _inst[instIdx];

                        // 월드 레이 -> 인스턴스 로컬 레이
                        // 방향은 MultiplyVector로 변환하여 비정규화 상태 유지 (T값 일관성)
                        Vector3 lo = rec.WorldToLocal.MultiplyPoint3x4(o);
                        Vector3 ld = rec.WorldToLocal.MultiplyVector(d);

                        Hit hit = _blas[rec.Blas].Intersect(lo, ld, tmin, best.T);
                        if (hit.Valid && hit.T < best.T)
                        {
                            best.Valid = true;
                            best.T = hit.T;
                            best.TriIndex = hit.TriIndex;
                        }
                    }
                }
                else
                {
                    stack[sp++] = node.LeftFirst;
                    stack[sp++] = node.LeftFirst + 1;
                }
            }
            return best;
        }

        public bool Occluded(Vector3 o, Vector3 d, float maxDist)
        {
            if (_tlasCount == 0)
                return false;
            Vector3 invD = new Vector3(1f / d.x, 1f / d.y, 1f / d.z);

            Span<int> stack = stackalloc int[64];
            int sp = 0;
            stack[sp++] = 0;
            while (sp > 0)
            {
                BVH.Node node = _tlas[stack[--sp]];
                if (!BVH.RayAABB(o, invD, node.Min, node.Max, 0f, maxDist))
                    continue;
                if (node.Count > 0)
                {
                    int end = node.LeftFirst + node.Count;
                    for (int s = node.LeftFirst; s < end; s++)
                    {
                        int instIdx = _instIdx[s];
                        InstanceRec rec = _inst[instIdx];
                        Vector3 lo = rec.WorldToLocal.MultiplyPoint3x4(o);
                        Vector3 ld = rec.WorldToLocal.MultiplyVector(d);
                        if (_blas[rec.Blas].Occluded(lo, ld, maxDist))
                            return true;
                    }
                }
                else
                {
                    stack[sp++] = node.LeftFirst;
                    stack[sp++] = node.LeftFirst + 1;
                }
            }
            return false;
        }

        public InstancedHit IntersectInstanced(Vector3 o, Vector3 d, float tmin, float tmax)
        {
            InstancedHit best = new InstancedHit() { Valid = false, T = tmax };
            if (_tlasCount == 0) return best;
            Vector3 invD = new Vector3(1f / d.x, 1f / d.y, 1f / d.z);

            Span<int> stack = stackalloc int[64];
            int sp = 0;
            stack[sp++] = 0;
            while (sp > 0)
            {
                BVH.Node node = _tlas[stack[--sp]];
                if (!BVH.RayAABB(o, invD, node.Min, node.Max, tmin, best.T))
                    continue;

                if (node.Count > 0)
                {
                    int end = node.LeftFirst + node.Count;
                    for (int s = node.LeftFirst; s < end; s++)
                    {
                        int instIdx = _instIdx[s];
                        InstanceRec rec = _inst[instIdx];

                        // 월드 레이 -> 인스턴스 로컬 레이
                        // 방향은 MultiplyVector로 변환하여 비정규화
                        Vector3 lo = rec.WorldToLocal.MultiplyPoint3x4(o);
                        Vector3 ld = rec.WorldToLocal.MultiplyVector(d);

                        Hit hit = _blas[rec.Blas].Intersect(lo, ld, tmin, best.T);
                        if (hit.Valid && hit.T < best.T)
                        {
                            best.Valid = true;
                            best.T = hit.T;
                            best.InstanceIndex = instIdx;
                            best.MeshIndex = rec.Blas;
                            best.MeshTriIndex = hit.TriIndex;
                        }
                    }

                }
                else
                {
                    stack[sp++] = node.LeftFirst;       // post-increment: pop 이 stack[--sp] 라 반드시 sp++
                    stack[sp++] = node.LeftFirst + 1;
                }
            }
            return best;
        }

        /// <summary>인스턴스의 로컬 노멀을 월드로 변환(정규화). 역전치 행렬 사용.</summary>
        public Vector3 TransformNormalToWorld(int instanceIndex, Vector3 localNormal)
            => _inst[instanceIndex].NormalMatrix.MultiplyVector(localNormal).normalized;



        public void Dispose()
        {
            if (_blas != null)
                for (int i = 0; i < _blas.Length; i++) _blas[i]?.Dispose();
            _blas = null;
            if (_tlas.IsCreated) _tlas.Dispose();
            if (_instIdx.IsCreated) _instIdx.Dispose();
            if (_inst.IsCreated) _inst.Dispose();
            _tlasCount = 0;
        }


    }

    internal class InstAxisComparer : IComparer<int>
    {
        public Vector3[] Centroid;
        public int Axis;

        public int Compare(int x, int y)
        {
            int c = Centroid[x][Axis].CompareTo(Centroid[y][Axis]);
            return c != 0 ? c : x.CompareTo(y);
        }
    }
}