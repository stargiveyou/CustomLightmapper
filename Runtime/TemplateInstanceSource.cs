using System;
using HuskyLibs.CustomLightmapper.Bake;
using Unity.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace HuskyLibs.CustomLightmapper
{

    /*
    [Mesh + Matrix] ─(어댑터)→ BuildResult ┬→ (BVH/InstancedRadianceScene/BurstScene)  ← 공통, 이번 범위
                                        ├→ Track A: per-instance 아틀라스(소규모 N만)   ← 후속·선택
                                        └→ Track B: 대표점 → SH9 프로브(대량 N)         ← 후속
    */
    /// <summary>
    /// MeshFilter 없는 수집 어댑터: (Mesh 템플릿[] + per-template Matrix4x4[][]) → 베이크 코어 입력.
    /// DrawMeshInstancedIndirect 워크플로용. 코어(TwoLevelBVH/InstancedRadianceScene)는 무수정 재사용.
    ///
    /// 산출: 로컬 Tri[][](메시당) + 메시 알베도 + TwoLevelBVH.Instance[] + 인스턴스 대표점(SH 베이크용).
    /// 대표점 = M_i · anchor_local. anchor 는 기본 템플릿 로컬 AABB 중심이며,
    /// <c>BuildScene(surfaceLift)</c> &gt; 0 이면 윗면(+Y) 표면 + 여유로 올려 프로브 자기차폐
    /// (솔리드 내부 갇힘 → SH=0 검정)를 방지한다. 대표점 위치만 바꾸므로 BVH/순회(Job·Burst)
    /// 핫패스는 무손상 — 대량·GPU 이식에 유리.
    /// 씬은 모드 A(per-mesh 알베도). 2단 BVH 메모리 ∝ 메시 종류 수라 대량 인스턴스에 적합.
    /// </summary>
    public struct MatrixInstanceInput
    {
        public Mesh[] templates;                // Mesh Template...
        public Vector3[] templateAlbedo;        // per-template 알베도(null -> fallback 0.5)
        public Matrix4x4[][] instanceMatrices;  // [template][instance] world L2W
    }

    /// <summary>
    /// per-instance SH 프로브 대표점의 배치 전략. 목적: 대표점이 솔리드 메시 내부(AABB 중심)에
    /// 갇혀 자기차폐로 SH=검정 되는 것을 막고, 표면 근접·솔리드 밖 두 조건을 만족시키는 것.
    ///  - Auto               : surfaceLift&gt;0 → LocalTopLift, 아니면 Center (하위호환 기본).
    ///  - Center             : 로컬 AABB 중심. 솔리드 내부 → 갇힘. 하위호환/디버그용.
    ///  - LocalTopLift       : 로컬 +Y 윗면 중앙 + surfaceLift(로컬 단위, M 스케일에 비례). 얇은/볼록 프롭용.
    ///  - WorldUpLift        : 로컬 윗면 중앙을 변환 후 월드 +Y 로 surfaceLift(월드 단위) 이동. 위→아래 라이팅 지배 씬용.
    ///  - SurfaceNormalOffset: 실제 최상단 삼각형 표면점 + 외향 법선·여백. 임의 메시·회전에 견고(권장, 실템플릿용).
    /// </summary>
    public enum AnchorMode
    {
        Auto,
        Center,
        LocalTopLift,
        WorldUpLift,
        SurfaceNormalOffset,
    }

    public struct MatrixInstanceScene : IDisposable
    {
        public Tri[][] uniqueMeshes;                 // 메시당 로컬 삼각형
        public Vector3[] meshAlbedo;                 // per-mesh
        public TwoLevelBVH.Instance[] instances;     // 평탄(MeshIndex + L2W)
        public Vector3[] instancePoints;             // 대표점 = M·anchor
        public uint[] instanceSeeds;                 // per0instance 시드 (재현성)
        public int[] instanceTemplate;               // 인스턴스 → 템플릿(=MeshIndex)
        public TwoLevelBVH bvh;                      // (씬과 공유; 소유는 이 struct)
        public InstancedRadianceScene scene;         // 모드 A


        //하류 Burst 경로용 BrustScene 구성(bvh + 메시 알베도). 반환 값은 홏루 측 Dispose
        public readonly BurstScene ToBurstScene(Allocator alloc)
        => BurstScene.Create(bvh, meshAlbedo, alloc);
        public void ToBakeArrays(Allocator alloc,
                             out NativeArray<Vector3> points,
                             out NativeArray<Vector3> normals,
                             out NativeArray<bool> valid,
                             out NativeArray<uint> seeds)
        {
            int n = instancePoints.Length;
            points = new NativeArray<Vector3>(n, alloc);
            normals = new NativeArray<Vector3>(n, alloc);
            valid = new NativeArray<bool>(n, alloc);
            seeds = new NativeArray<uint>(n, alloc);

            for (int i = 0; i < n; i++)
            {
                points[i] = instancePoints[i];
                normals[i] = Vector3.up;
                valid[i] = true;
                seeds[i] = instanceSeeds[i];
            }

        }
        public readonly void Dispose()
        {
            scene?.Dispose();  // bvh 미소유(외부 전달) → bvh 는 아래에서 별도 Dispose
            bvh?.Dispose();
        }
    }
    public static class TemplateInstanceSource
    {


        /// <summary>
        /// anchorMode 별 로컬 대표점 산출(BuildScene 내부용 — 상단 프로브).
        /// </summary>
        static void ComputeAnchor(AnchorMode mode, Bounds b, Tri[] tris, float surfaceLift,
                                  out Vector3 localAnchor, out float worldLift)
            => ComputeLocalAnchor(mode, b, tris, surfaceLift, top: true, out localAnchor, out worldLift);

        /// <summary>
        /// anchorMode 별 로컬 대표점 산출(공개 API — 멀티 프로브의 상·하단 대칭 산출용).
        /// <paramref name="top"/>=true 면 윗면(+Y) 기준(BuildScene 과 동일 거동), false 면 아랫면(-Y) 대칭:
        /// LocalTopLift→min.y-lift, WorldUpLift→월드 -Y 여백, SurfaceNormalOffset→최하단 삼각형 표면점+외향 법선.
        /// localAnchor 는 M 으로 변환할 로컬 좌표, worldLift 는 변환 후 월드 +Y 로 더할 **부호 있는** 여백
        /// (WorldUpLift 전용 — top=false 면 음수, 그 외 모드는 0).
        /// </summary>
        public static void ComputeLocalAnchor(AnchorMode mode, Bounds b, Tri[] tris, float surfaceLift, bool top,
                                              out Vector3 localAnchor, out float worldLift)
        {
            worldLift = 0f;
            if (mode == AnchorMode.Auto)
                mode = surfaceLift > 0f ? AnchorMode.LocalTopLift : AnchorMode.Center;
            float sign = top ? 1f : -1f;
            float faceY = top ? b.max.y : b.min.y;

            switch (mode)
            {
                case AnchorMode.Center:
                    localAnchor = b.center; // 솔리드 내부 → 갇힘(하위호환/디버그)
                    return;

                case AnchorMode.LocalTopLift:
                    localAnchor = new Vector3(b.center.x, faceY + sign * surfaceLift, b.center.z);
                    return;

                case AnchorMode.WorldUpLift:
                    localAnchor = new Vector3(b.center.x, faceY, b.center.z); // 로컬 면(실표면), 여백은 월드에서
                    worldLift = sign * surfaceLift;
                    return;

                case AnchorMode.SurfaceNormalOffset:
                {
                    // 최상단/최하단(centroid.y 극값) 삼각형의 표면점 + 외향 법선으로 ε 만큼 밀어 솔리드 밖 표면 근접.
                    // ε 은 ClosestHit tMin(1e-4) 보다 크게(자기히트 방지).
                    float eps = Mathf.Max(surfaceLift, 1e-3f);
                    Vector3 c = b.center, n = sign * Vector3.up;
                    float bestY = top ? float.NegativeInfinity : float.PositiveInfinity;
                    if (tris != null)
                    {
                        for (int i = 0; i < tris.Length; i++)
                        {
                            Vector3 ct = (tris[i].V0 + tris[i].V1 + tris[i].V2) * (1f / 3f);
                            if (top ? ct.y <= bestY : ct.y >= bestY) continue;
                            Vector3 nrm = Vector3.Cross(tris[i].V1 - tris[i].V0, tris[i].V2 - tris[i].V0);
                            float mag = nrm.magnitude;
                            if (mag < 1e-12f) continue; // 퇴화 삼각형 skip
                            nrm /= mag;
                            if (Vector3.Dot(nrm, ct - b.center) < 0f) nrm = -nrm; // 외향 보정
                            bestY = ct.y; c = ct; n = nrm;
                        }
                    }
                    localAnchor = c + n * eps;
                    return;
                }

                default:
                    localAnchor = b.center;
                    return;
            }
        }

        /// <summary>Mesh → 로컬 Tri[] (전 submesh 병합; 모드 A 알베도라 submesh 구분 불필요).</summary>
        public static Tri[] MeshToLocalTris(Mesh m)
        {
            if (m == null) throw new ArgumentNullException(nameof(m));
            if (!m.isReadable)
                throw new InvalidOperationException($"Mesh '{m.name}' 이 Read/Write 불가. 임포트 설정에서 Read/Write Enabled 필요(또는 정점 데이터 직접 공급).");
            var verts = m.vertices;
            var idx = m.triangles;
            var tris = new Tri[idx.Length / 3];
            for (int i = 0, t = 0; i + 2 < idx.Length; i += 3, t++)
                tris[t] = new Tri { V0 = verts[idx[i]], V1 = verts[idx[i + 1]], V2 = verts[idx[i + 2]] };
            return tris;
        }

        /// <summary>
        /// 어댑터 빌드. 코어(BVH/씬) 구성 + 대표점 산출. 결과는 Dispose 필요.
        /// <paramref name="surfaceLift"/>&gt;0 이면 대표점을 로컬 AABB 중심 대신 <b>윗면(+Y) 표면 + 여유</b>로 올려
        /// 솔리드 내부 자기차폐(갇힌 프로브 → SH=0 검정)를 피한다(로컬 단위 여유; M 의 스케일에 비례).
        /// 0(기본)이면 기존과 동일하게 M·bounds.center.
        /// </summary>
        public static MatrixInstanceScene BuildScene(MatrixInstanceInput input, Allocator allocator = Allocator.Persistent, BVH.BuildQuality q = BVH.BuildQuality.SAH, float surfaceLift = 0f, AnchorMode anchorMode = AnchorMode.Auto)
        {
            if (input.templates == null || input.templates.Length == 0)
                throw new ArgumentException("templates 비어 있음");
            if (input.instanceMatrices == null || input.instanceMatrices.Length != input.templates.Length)
                throw new ArgumentException("instanceMatrices 길이가 templates 와 불일치");

            int T = input.templates.Length;
            var tris = new Tri[T][];
            var anchor = new Vector3[T];      // M 으로 변환할 로컬 대표점
            var worldLift = new float[T];     // 변환 후 월드 +Y 여백(WorldUpLift 전용, 그 외 0)
            var albedo = new Vector3[T];
            for (int t = 0; t < T; t++)
            {
                tris[t] = MeshToLocalTris(input.templates[t]);
                var b = input.templates[t].bounds;
                // anchorMode 별 대표점 배치(솔리드 내부 자기차폐 회피). 대표점 위치만 바꾸므로 BVH/순회 핫패스 무손상.
                ComputeAnchor(anchorMode, b, tris[t], surfaceLift, out anchor[t], out worldLift[t]);
                albedo[t] = (input.templateAlbedo != null && t < input.templateAlbedo.Length)
                    ? input.templateAlbedo[t] : new Vector3(0.5f, 0.5f, 0.5f);
            }

            // 인스턴스 평탄화 + 대표점 계산
            var instList = new List<TwoLevelBVH.Instance>();
            var ptList = new List<Vector3>();
            var tmpList = new List<int>();

            for (int t = 0; t < T; t++)
            {
                var mats = input.instanceMatrices[t];
                if (mats == null) continue;
                for (int i = 0; i < mats.Length; i++)
                {
                    instList.Add(new TwoLevelBVH.Instance { MeshIndex = t, LocalToWorld = mats[i] });
                    Vector3 pw = mats[i].MultiplyPoint3x4(anchor[t]);
                    if (worldLift[t] != 0f) pw += Vector3.up * worldLift[t]; // WorldUpLift: 여백은 월드 +Y
                    ptList.Add(pw);
                    tmpList.Add(t);
                }
            }
            var instances = instList.ToArray();

            // 코어 구성(bvh 를 직접 만들어 씬에 전달 → 소유 명확)
            var bvh = new TwoLevelBVH(tris, instances, allocator, q);
            var scene = new InstancedRadianceScene(tris, albedo, instances, bvh);

            return new MatrixInstanceScene
            {
                uniqueMeshes = tris,
                meshAlbedo = albedo,
                instances = instances,
                instancePoints = ptList.ToArray(),
                instanceTemplate = tmpList.ToArray(),
                bvh = bvh,
                scene = scene,
            };
        }

        /// <summary>로깅용 요약.</summary>
        public static string Summary(in MatrixInstanceScene s)
        {
            long triTotal = 0;
            for (int t = 0; t < s.uniqueMeshes.Length; t++) triTotal += s.uniqueMeshes[t].Length;
            return $"templates={s.uniqueMeshes.Length}, instances={s.instances.Length}, triTemplates≈{triTotal}, points={s.instancePoints.Length}";
        }
    }
}
