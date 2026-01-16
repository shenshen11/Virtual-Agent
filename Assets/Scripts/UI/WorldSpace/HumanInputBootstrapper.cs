using UnityEngine;

namespace VRPerception.UI
{
    /// <summary>
    /// 运行时实例化 HumanInput 预制体（WorldSpace），用于 VR 输入。
    /// </summary>
    public sealed class HumanInputBootstrapper : MonoBehaviour
    {
        [SerializeField] private GameObject humanInputPrefab;
        [SerializeField] private Transform parentOverride;
        [SerializeField] private Vector3 localPosition = new Vector3(0f, -0.1f, 1.2f);
        [SerializeField] private Vector3 localEulerAngles = Vector3.zero;
        [SerializeField] private Vector3 localScale = new Vector3(0.1f, 0.1f, 0.1f);
        [SerializeField] private bool activateOnSpawn = true;

        private void Awake()
        {
            if (humanInputPrefab == null)
            {
                Debug.LogWarning("[HumanInputBootstrapper] humanInputPrefab is not assigned.");
                return;
            }

            var existing = FindObjectOfType<WSHumanInputDialog>(true);
            if (existing != null)
            {
                if (activateOnSpawn && !existing.gameObject.activeSelf)
                {
                    existing.gameObject.SetActive(true);
                }
                return;
            }

            Transform parent = parentOverride;
            if (parent == null)
            {
                var cam = Camera.main;
                if (cam != null) parent = cam.transform;
            }

            var instance = Instantiate(humanInputPrefab, parent != null ? parent : transform);
            instance.transform.localPosition = localPosition;
            instance.transform.localEulerAngles = localEulerAngles;
            instance.transform.localScale = localScale;

            if (activateOnSpawn && !instance.activeSelf)
            {
                instance.SetActive(true);
            }
        }
    }
}
