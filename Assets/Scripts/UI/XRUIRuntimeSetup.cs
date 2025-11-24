using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace VRPerception.UI
{
    /// <summary>
    /// 运行时 UI 框架兜底：
    /// - 确保场景中存在 EventSystem
    /// - 注入 InputSystemUIInputModule 与 XRUIInputModule 以支持手柄/鼠标/触摸输入
    /// - 结合世界空间 Canvas 上的 TrackedDeviceGraphicRaycaster 完成 XR UI 交互链路
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class XRUIRuntimeSetup : MonoBehaviour
    {
        [Header("Event System 配置")]
        [Tooltip("若场景中不存在 EventSystem，则自动创建一个空节点")]
        [SerializeField] private bool createEventSystemIfMissing = true;
        [Tooltip("为 EventSystem 添加 InputSystemUIInputModule，以兼容鼠标/键盘/触摸")]
        [SerializeField] private bool ensureInputSystemModule = true;
        [Tooltip("为 EventSystem 添加 XRUIInputModule，以支持 XR 控制器射线交互")]
        [SerializeField] private bool ensureXRModule = true;

        private void Awake()
        {
            EnsureEventSystem();
        }

        private void EnsureEventSystem()
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null && createEventSystemIfMissing)
            {
                var go = new GameObject("EventSystem");
                eventSystem = go.AddComponent<EventSystem>();
            }

            if (eventSystem == null)
            {
                Debug.LogWarning("[XRUIRuntimeSetup] 未能找到或创建 EventSystem。世界空间 UI 将无法响应输入。");
                return;
            }

            if (ensureInputSystemModule && eventSystem.GetComponent<InputSystemUIInputModule>() == null)
            {
                eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
            }

            if (ensureXRModule && eventSystem.GetComponent<XRUIInputModule>() == null)
            {
                eventSystem.gameObject.AddComponent<XRUIInputModule>();
            }
        }
    }
}