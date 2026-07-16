using System;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper.Bake
{
    /// <summary>
    /// 2단 순회 static 함수. TwoLevelBVH와 동일한 로직.
    /// </summary>
    public class BurstTwoLevelBVH
    {

        /// <summary>
        /// BLAS(로컬) 최근접 - BVH.Intersect 와 동일, 오프셋만 적용
        /// </summary>

        private static bool IntersectBlas(BurstScene s, int mesh, Vector3 o, Vector3 d, float tmin, float t, out float hT, out int hTri)
        {
            hT = t; hTri = 0;   // 들어온 best.T 를 초기 경계로(=managed BVH.Intersect). 0 으로 두면 모든 교차가 [tmin,0] 밖→전부 기각
            bool valid = false;
            if (s.blasNodeCount[mesh] == 0)
                return false;
            int triIdxBase = s.blasTriIdxStart[mesh];
            int triBase = s.blasTriStart[mesh];
            int nodeBase = s.blasNodeStart[mesh];
            Vector3 invD = new Vector3(1.0f / d.x, 1f / d.y, 1f / d.z);

            Span<int> stack = stackalloc int[64];
            int sp = 0; stack[sp++] = 0; // BLAS-상대 노드 인덱스


            while (sp > 0)
            {
                BVH.Node node = s.blasNodes[nodeBase + stack[--sp]];
                if (!BVH.RayAABB(o, invD, node.Min, node.Max, tmin, hT)) continue;
                if (node.Count > 0)
                {
                    int end = node.LeftFirst + node.Count;
                    for (int slot = node.LeftFirst; slot < end; slot++)
                    {
                        int orig = s.blasTriIdx[triIdxBase + slot];
                        if (RayGeometry.RayTri(o, d, s.blasTris[triBase + orig], tmin, hT, out float h))
                        {
                            valid = true;
                            hT = h;
                            hTri = orig;
                        }
                    }
                }
                else
                {
                    int leftNode = node.LeftFirst;
                    int rightNode = leftNode + 1;
                    stack[sp++] = leftNode;
                    stack[sp++] = rightNode;
                }
            }
            return valid;
        }

        public static TwoLevelBVH.InstancedHit IntersectInstanced(in BurstScene s, Vector3 o, Vector3 d, float tmin, float tmax)
        {
            var best = new TwoLevelBVH.InstancedHit() { Valid = false, T = tmax };

            if (s.tlasCount == 0)
                return best;
            Vector3 invD = new Vector3(1f / d.x, 1f / d.y, 1f / d.z);

            Span<int> stack = stackalloc int[64];
            int sp = 0;
            stack[sp++] = 0;
            while (sp > 0)
            {
                BVH.Node node = s.tlasNodes[stack[--sp]];
                if (!BVH.RayAABB(o, invD, node.Min, node.Max, tmin, best.T)) continue;
                if (node.Count > 0)
                {
                    int end = node.LeftFirst + node.Count;
                    for (int slot = node.LeftFirst; slot < end; slot++)
                    {
                        int instIdx = s.instIdx[slot];
                        Matrix4x4 w2l = s.instWorldToLocal[instIdx];
                        int mesh = s.instBlas[instIdx];
                        Vector3 lo = w2l.MultiplyPoint3x4(o);
                        Vector3 ld = w2l.MultiplyVector(d);

                        if (IntersectBlas(s, mesh, lo, ld, tmin, best.T, out float hT, out int hTri) && hT < best.T)
                        {
                            best.Valid = true;
                            best.T = hT;
                            best.InstanceIndex = instIdx;
                            best.MeshIndex = mesh;
                            best.MeshTriIndex = hTri;

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

        static bool OccludedBlas(in BurstScene s, int mesh, Vector3 o, Vector3 d, float maxDist)
        {
            int nodeBase = s.blasNodeStart[mesh];
            if (s.blasNodeCount[mesh] == 0) return false;
            int triIdxBase = s.blasTriIdxStart[mesh];
            int triBase = s.blasTriStart[mesh];
            Vector3 invD = new Vector3(1f / d.x, 1f / d.y, 1f / d.z);

            Span<int> stack = stackalloc int[64];
            int sp = 0; stack[sp++] = 0;
            while (sp > 0)
            {
                BVH.Node node = s.blasNodes[nodeBase + stack[--sp]];
                if (!BVH.RayAABB(o, invD, node.Min, node.Max, 0f, maxDist)) continue;
                if (node.Count > 0)
                {
                    int end = node.LeftFirst + node.Count;
                    for (int sIdx = node.LeftFirst; sIdx < end; sIdx++)
                        if (RayGeometry.RayTri(o, d, s.blasTris[triBase + s.blasTriIdx[triIdxBase + sIdx]], 0f, maxDist, out _)) return true;
                }
                else { stack[sp++] = node.LeftFirst; stack[sp++] = node.LeftFirst + 1; }
            }
            return false;
        }


        public static bool Occluded(in BurstScene s, Vector3 o, Vector3 d, float maxDist)
        {

            if (s.tlasCount == 0)
                return false;
            Vector3 invD = new Vector3(1f / d.x, 1f / d.y, 1f / d.z);

            Span<int> stack = stackalloc int[64];
            int sp = 0;
            stack[sp++] = 0;
            while (sp > 0)
            {
                BVH.Node node = s.tlasNodes[stack[--sp]];
                if (!BVH.RayAABB(o, invD, node.Min, node.Max, 0f, maxDist)) continue;
                if (node.Count > 0)
                {
                    int end = node.LeftFirst + node.Count;
                    for (int slot = node.LeftFirst; slot < end; slot++)
                    {
                        int instIdx = s.instIdx[slot];
                        Matrix4x4 w2l = s.instWorldToLocal[instIdx];
                        int mesh = s.instBlas[instIdx];
                        Vector3 lo = w2l.MultiplyPoint3x4(o);
                        Vector3 ld = w2l.MultiplyVector(d);

                        if (OccludedBlas(s, mesh, lo, ld, maxDist))
                        {
                            return true;
                        }

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


        public static Vector3 TransformNormalToWorld(in BurstScene s, int instanceIndex, Vector3 localNormal)
            => s.instNormalMatrix[instanceIndex].MultiplyVector(localNormal).normalized;

        /// <summary>InstancedRadianceScene.ClosestHit(모드 A) 미러: 위치·역전치 월드노멀(레이 향함)·메시 알베도.</summary>
        public static bool ClosestHit(in BurstScene s, Vector3 o, Vector3 d, float tmin, float tmax, out Vector3 pos, out Vector3 nrm, out Vector3 albedo)
        {
            var h = IntersectInstanced(s, o, d, tmin, tmax);
            if (!h.Valid) { pos = default; nrm = default; albedo = default; return false; }

            pos = o + d * h.T;

            //로컬 면 노멀 (즉석) - BuildFaceNormals 와 동일 : cross(v1-v0, v2-v0).normalized
            Tri tri = s.blasTris[s.blasTriStart[h.MeshIndex] + h.MeshTriIndex];
            Vector3 localN = Vector3.Cross(tri.V1 - tri.V0, tri.V2 - tri.V0).normalized;

            Vector3 wn = TransformNormalToWorld(s, h.InstanceIndex, localN);
            if (Vector3.Dot(wn, d) > 0f) wn = -wn;//레이 향함
            nrm = wn;

            albedo = (s.meshAlbedo.IsCreated && (uint)h.MeshIndex < (uint)s.meshAlbedo.Length)
            ? s.meshAlbedo[h.MeshIndex] : new Vector3(0.5f, 0.5f, 0.5f);


            return true;

        }

    }





}
