using UnityEngine;
using HuskyLibs.CustomLightmapper.Bake;




namespace HuskyLibs.CustomLightmapper.Bake{

[ExecuteInEditMode]
public class HalfEdgeConversion : MonoBehaviour
    {
        [SerializeField]
         Mesh targetMesh;

        [SerializeField, Tooltip("비워두면 URP Lit 셰이더로 폴백 머티리얼을 자동 생성합니다.")]
         Material drawMaterial;

        public WeldedHalfEdge HalfEdge_Mesh;

        [Header("Origin Mesh")]
        public bool bIsDrawMesh;
        public Color OriginMeshDrawColor = Color.white;

        [Header("Half Edge")]
        public bool bIsDrawHalfEdge;
        public Color HalfEdgeMeshDrawColor = Color.green;
        [Range(0f, 0.45f), Tooltip("하프에지를 면 중심 쪽으로 들이는 정도. 쌍(pair)인 두 반대방향 에지를 분리해 보이게 한다.")]
        public float halfEdgeShrink = 0.15f;
        [Tooltip("화살촉 크기(월드 단위, 에지 길이에 따라 자동 축소).")]
        public float halfEdgeArrowSize = 0.1f;
        [Tooltip("쌍이 없는 경계(boundary) 에지를 별도 색으로 강조.")]
        public bool highlightBoundaryEdge = true;
        public Color boundaryEdgeColor = Color.red;

        // 어떤 Mesh 로 HalfEdge_Mesh 를 빌드했는지 추적 — targetMesh 가 바뀌면 재빌드.
        Mesh builtMesh;

        // Graphics.DrawMesh 로 매 프레임 그릴 때 사용하는 내부 리소스
        Material runtimeMaterial;                       // drawMaterial 미지정 시 자동 생성하는 폴백
        MaterialPropertyBlock propertyBlock;            // 색상을 머티리얼 인스턴스 복제 없이 주입
        static readonly int BaseColorID = Shader.PropertyToID("_BaseColor"); // URP Lit/Unlit
        static readonly int ColorID = Shader.PropertyToID("_Color");         // Built-in 호환


        void OnEnable()
        {
            EnsureResources();
            EnsureHalfEdge();   
        }

        void OnDisable()
        {
            ReleaseRuntimeMaterial();
            DisposeHalfEdge();
        }

        // targetMesh 기준으로 HalfEdge_Mesh 를 보장(없거나 메시가 바뀌었으면 재빌드).
        void EnsureHalfEdge()
        {
            if (targetMesh == null)
            {
                DisposeHalfEdge();
                return;
            }
            // 이미 같은 메시로 빌드되어 있으면 그대로 사용
            if (HalfEdge_Mesh.edges.IsCreated && builtMesh == targetMesh)
                return;

            DisposeHalfEdge();
            HalfEdge_Mesh = new WeldedHalfEdge(targetMesh);
            builtMesh = targetMesh;
        }

        void DisposeHalfEdge()
        {
            // 세 NativeArray 는 항상 함께 생성/해제되므로 edges 로 대표 체크
            if (HalfEdge_Mesh.edges.IsCreated)
                HalfEdge_Mesh.Dispose();
            builtMesh = null;
        }

        [ContextMenu("Rebuild HalfEdge")]
        void RebuildHalfEdge()
        {
            DisposeHalfEdge();
            EnsureHalfEdge();
        }

        // URP 기준 Graphics.DrawMesh 는 매 프레임 등록해야 1프레임 동안 렌더링된다.
        void Update()
        {
            if (!bIsDrawMesh || targetMesh == null)
                return;

            EnsureResources();
            var mat = drawMaterial != null ? drawMaterial : runtimeMaterial;
            if (mat == null)
                return;

            // 머티리얼을 복제하지 않고 색상만 덮어쓰기 (URP/Built-in 양쪽 프로퍼티 모두 세팅)
            propertyBlock.SetColor(BaseColorID, OriginMeshDrawColor);
            propertyBlock.SetColor(ColorID, OriginMeshDrawColor);

            // camera=null → 모든 카메라에서 렌더링. SRP(URP)에서도 동일하게 동작한다.
            Graphics.DrawMesh(
                targetMesh,
                transform.localToWorldMatrix,
                mat,
                gameObject.layer,
                null,
                0,
                propertyBlock);
        }

        void EnsureResources()
        {
            propertyBlock ??= new MaterialPropertyBlock();

            // 사용자가 머티리얼을 지정하지 않았을 때만 URP 폴백 머티리얼을 만든다.
            if (drawMaterial == null && runtimeMaterial == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null)
                    shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null)
                    shader = Shader.Find("Sprites/Default"); // 최후의 폴백

                runtimeMaterial = new Material(shader)
                {
                    // 씬/에셋에 저장되거나 누수되지 않도록
                    hideFlags = HideFlags.HideAndDontSave
                };
            }
        }

        void ReleaseRuntimeMaterial()
        {
            if (runtimeMaterial == null)
                return;

            if (Application.isPlaying)
                Destroy(runtimeMaterial);
            else
                DestroyImmediate(runtimeMaterial);

            runtimeMaterial = null;
        }


#if UNITY_EDITOR

        void OnDrawGizmos()
        {
            if (bIsDrawHalfEdge)
                DrawHalfEdgeGizmo();
        }

        void DrawHalfEdgeGizmo()
        {
            Debug.Log("HalfEdge-Gizmo-On");
            EnsureHalfEdge();
            if (!HalfEdge_Mesh.edges.IsCreated)
            {
                Debug.Log("HalfEdge-Edge-Create Failures....");
                return;
            }

            var verts = HalfEdge_Mesh.vertices;
            var edges = HalfEdge_Mesh.edges;
            var faces = HalfEdge_Mesh.faces;
            Matrix4x4 m = transform.localToWorldMatrix;

            // 정점 인덱스 → 월드 좌표 (float3 → Vector3 암시적 변환)
            Vector3 WorldPos(int vi) => m.MultiplyPoint3x4(verts[vi].position);

            for (int f = 0; f < faces.Length; f++)
            {
                int e0 = faces[f].edgeIndex;
                if (e0 < 0)
                    continue;

                // 삼각형 면: next 로 두 번 따라가 세 하프에지를 얻는다.
                int e1 = edges[e0].nextIndex;
                if (e1 < 0) continue;
                int e2 = edges[e1].nextIndex;
                if (e2 < 0) continue;

                // 각 에지의 '도착' 정점 (vertexIndex). 시작 정점은 직전 에지의 도착 정점.
                int d0 = edges[e0].vertexIndex; // e0 도착 = v1
                int d1 = edges[e1].vertexIndex; // e1 도착 = v2
                int d2 = edges[e2].vertexIndex; // e2 도착 = v0
                if (d0 < 0 || d1 < 0 || d2 < 0)
                    continue;

                Vector3 p0 = WorldPos(d0);
                Vector3 p1 = WorldPos(d1);
                Vector3 p2 = WorldPos(d2);
                Vector3 centroid = (p0 + p1 + p2) / 3f;

                // e0: v0->v1 = (d2 -> d0), e1: v1->v2 = (d0 -> d1), e2: v2->v0 = (d1 -> d2)
                DrawHalfEdgeArrow(e0, p2, p0, centroid);
                DrawHalfEdgeArrow(e1, p0, p1, centroid);
                DrawHalfEdgeArrow(e2, p1, p2, centroid);
            }
        }

        // 하프에지 한 개를 면 중심 쪽으로 들여 방향 화살표로 그린다.
        void DrawHalfEdgeArrow(int edgeIndex, Vector3 start, Vector3 end, Vector3 centroid)
        {
            float t = Mathf.Clamp01(halfEdgeShrink);
            start = Vector3.Lerp(start, centroid, t);
            end = Vector3.Lerp(end, centroid, t);

            bool boundary = HalfEdge_Mesh.edges[edgeIndex].pairIndex < 0;
            Gizmos.color = (boundary && highlightBoundaryEdge) ? boundaryEdgeColor : HalfEdgeMeshDrawColor;
            Gizmos.DrawLine(start, end);

            Vector3 dir = end - start;
            float len = dir.magnitude;
            if (len < 1e-5f)
                return;
            dir /= len;

            float head = Mathf.Min(halfEdgeArrowSize, len * 0.4f);
            // dir 과 평행하지 않은 기준 축을 골라 수직 벡터 2개 생성 (3D 화살촉)
            Vector3 axis = Mathf.Abs(dir.y) < 0.99f ? Vector3.up : Vector3.right;
            Vector3 side = Vector3.Cross(dir, axis).normalized;
            Vector3 side2 = Vector3.Cross(dir, side).normalized;

            Vector3 baseP = end - dir * head;
            float halfHead = head * 0.5f;
            Gizmos.DrawLine(end, baseP + side * halfHead);
            Gizmos.DrawLine(end, baseP - side * halfHead);
            Gizmos.DrawLine(end, baseP + side2 * halfHead);
            Gizmos.DrawLine(end, baseP - side2 * halfHead);
        }

#endif


    }
}
