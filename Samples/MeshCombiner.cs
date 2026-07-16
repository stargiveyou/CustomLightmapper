using UnityEngine;


namespace HuskyLibs.CustomLightmapper.Bake
{

    public class MeshCombiner : MonoBehaviour
    {
        public Mesh[] meshes;

        public void CombineMeshes()
        {
            Mesh mesh = new Mesh();
            mesh.name = "Combined";

        }



        public Mesh CombineMesh_Sample_CPU(MeshFilter[] meshFilters)
        {
            // 1. 자식 오브젝트들의 모든 MeshFilter 컴포넌트 수집
            // MeshFilter[] meshFilters = GetComponentsInChildren<MeshFilter>();
            if (meshFilters == null)
                meshFilters = GetComponentsInChildren<MeshFilter>();

            // 병합 구조체 배열 선언 (자신의 MeshFilter는 제외해야 하므로 크기 조절 필요 시 조절)
            CombineInstance[] combine = new CombineInstance[meshFilters.Length];

            for (int i = 0; i < meshFilters.Length; i++)
            {
                // 2. 각 메쉬와 해당 메쉬의 로컬->월드 변환 행렬 매핑
                combine[i].mesh = meshFilters[i].sharedMesh;

                // 부모의 로컬 좌표계를 기준으로 자식들의 위치를 상대적으로 계산
                combine[i].transform = transform.worldToLocalMatrix * meshFilters[i].transform.localToWorldMatrix;

                // 병합 후 기존 오브젝트는 화면에서 숨김 (안 하면 중복 렌더링됨)
                meshFilters[i].gameObject.SetActive(false);
            }

            // 3. 새 메쉬 생성 및 데이터 할당
            Mesh combinedMesh = new Mesh();
            combinedMesh.name = "Combined_Mesh";

            // 4. CombineMeshes(combineInstances, mergeSubMeshes, useMatrices)
            // 두 번째 인자(true): 모든 submesh를 하나의 단일 submesh로 합침 (머티리얼이 같을 때 필수)
            combinedMesh.CombineMeshes(combine, true, true);

            // 5. 자신의 MeshFilter와 MeshRenderer에 할당
            MeshFilter targetFilter = GetComponent<MeshFilter>();
            if (targetFilter == null) targetFilter = gameObject.AddComponent<MeshFilter>();

            MeshRenderer targetRenderer = GetComponent<MeshRenderer>();
            if (targetRenderer == null) targetRenderer = gameObject.AddComponent<MeshRenderer>();

            targetFilter.mesh = combinedMesh;

            // 콜라이더가 필요하다면 가볍게 업데이트 가능
            if (TryGetComponent<MeshCollider>(out var collider))
            {
                collider.sharedMesh = combinedMesh;
            }
            return combinedMesh;
        }
    }
}