using UnityEngine;
using System.Collections.Generic;

namespace HuskyLibs.CustomLightmapper.Bake
{
    public class ObjectRandomTranslator : MonoBehaviour
    {
        public float range;

        public Transform parent;

        public List<Transform> childs = new List<Transform>();

        [ContextMenu("Random Translate")]
        public void RunTranslate()
        {
            OnChildSetup();
            for(int i =0; i< childs.Count; i++)
            {
                Vector3 randomPosition = UnityEngine.Random.insideUnitSphere * range;
                childs[i].position = this.transform.position + randomPosition;
            }
        }

        private void OnChildSetup()
        {
            childs.Clear();
            if(parent == null)
            return;

            foreach(Transform child in parent)
            {
                if(child == parent)
                continue;
                childs.Add(child);
            }

        }

    }
}
