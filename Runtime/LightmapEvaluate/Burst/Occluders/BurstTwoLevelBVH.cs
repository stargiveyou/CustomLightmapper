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

        // ── α 트랙: 알파 컷아웃 any-hit (IntersectBlas/OccludedBlas 복제 + 알파 판정) ────
        // 호출측이 s.alpha.MeshCutout(mesh) 일 때만 분기하므로, 컷아웃 없는 씬은 위의 원본이
        // 그대로 실행된다(α 결정 ⑥). matBase = 인스턴스의 머티리얼 슬롯 시작(α 결정 ③).

        static bool IntersectBlasAlpha(in BurstScene s, int mesh, int matBase, Vector3 o, Vector3 d,
                                       float tmin, float t, out float hT, out int hTri)
        {
            hT = t; hTri = 0;
            bool valid = false;
            if (s.blasNodeCount[mesh] == 0)
                return false;
            int triIdxBase = s.blasTriIdxStart[mesh];
            int triBase = s.blasTriStart[mesh];
            int nodeBase = s.blasNodeStart[mesh];
            Vector3 invD = new Vector3(1.0f / d.x, 1f / d.y, 1f / d.z);

            Span<int> stack = stackalloc int[64];
            int sp = 0; stack[sp++] = 0;

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
                        if (RayGeometry.RayTriUV(o, d, s.blasTris[triBase + orig], tmin, hT, out float h, out float bu, out float bv))
                        {
                            // 투명이면 채택 안 함 → hT 도 조이지 않아 뒤쪽 삼각형이 후보로 남는다.
                            if (!s.alpha.HitOpaque(matBase, mesh, orig, bu, bv)) continue;
                            valid = true;
                            hT = h;
                            hTri = orig;
                        }
                    }
                }
                else
                {
                    int leftNode = node.LeftFirst;
                    stack[sp++] = leftNode;
                    stack[sp++] = leftNode + 1;
                }
            }
            return valid;
        }

        static bool OccludedBlasAlpha(in BurstScene s, int mesh, int matBase, Vector3 o, Vector3 d, float maxDist)
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
                    {
                        int orig = s.blasTriIdx[triIdxBase + sIdx];
                        if (RayGeometry.RayTriUV(o, d, s.blasTris[triBase + orig], 0f, maxDist, out _, out float bu, out float bv)
                            && s.alpha.HitOpaque(matBase, mesh, orig, bu, bv))
                            return true;   // 불투명 히트만 차폐로 인정
                    }
                }
                else { stack[sp++] = node.LeftFirst; stack[sp++] = node.LeftFirst + 1; }
            }
            return false;
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

                        // out 변수는 분기 밖에서 선언한다(삼항 안 인라인 선언은 확정대입 규칙이 모호해짐).
                        float hT; int hTri;
                        bool hitBlas;
                        if (s.alpha.MeshCutout(mesh))
                            hitBlas = IntersectBlasAlpha(s, mesh, s.alpha.instMatBase[instIdx], lo, ld, tmin, best.T, out hT, out hTri);
                        else
                            hitBlas = IntersectBlas(s, mesh, lo, ld, tmin, best.T, out hT, out hTri);

                        if (hitBlas && hT < best.T)
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

                        // BLAS 가 '불투명 히트'만 true 로 돌려주므로 TLAS 층 조기 반환은 그대로 유효.
                        bool blocked = s.alpha.MeshCutout(mesh)
                            ? OccludedBlasAlpha(s, mesh, s.alpha.instMatBase[instIdx], lo, ld, maxDist)
                            : OccludedBlas(s, mesh, lo, ld, maxDist);
                        if (blocked)
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
