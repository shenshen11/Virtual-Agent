using UnityEngine;

namespace VRPerception.Tasks.EnvironmentModules
{
    /// <summary>
    /// 用于在 Unity PlayMode 下快速验证 module 切换与恢复的调试组件。
    /// 默认不会影响任何任务，只有当你把它挂到场景里时才会生效。
    /// </summary>
    public sealed class EnvironmentModuleDebugSwitcher : MonoBehaviour
    {
        [SerializeField] private ExperimentSceneManager sceneManager;
        [SerializeField] private bool switchOnStart = false;
        [SerializeField] private string moduleId = "black_simple";
        [SerializeField] private bool clearOnDisable = true;

        [Header("Toggle (Optional)")]
        [SerializeField] private bool enableToggleKey = true;
        [SerializeField] private KeyCode toggleKey = KeyCode.M;
        [SerializeField] private string fallbackEnvironment = "open_field";

        private bool _active;

        private void Awake()
        {
            if (sceneManager == null) sceneManager = FindObjectOfType<ExperimentSceneManager>();
        }

        private void Start()
        {
            if (switchOnStart)
            {
                SwitchToModule();
            }
        }

        private void Update()
        {
            if (!enableToggleKey) return;
            if (!Input.GetKeyDown(toggleKey)) return;

            if (_active)
            {
                SwitchToFallback();
            }
            else
            {
                SwitchToModule();
            }
        }

        private void OnDisable()
        {
            if (clearOnDisable && _active)
            {
                TryClearModule();
            }
        }

        private void SwitchToModule()
        {
            if (sceneManager == null)
            {
                Debug.LogWarning("[EnvironmentModuleDebugSwitcher] No ExperimentSceneManager found.");
                return;
            }

            if (string.IsNullOrWhiteSpace(moduleId))
            {
                Debug.LogWarning("[EnvironmentModuleDebugSwitcher] moduleId is empty.");
                return;
            }

            sceneManager.SetupEnvironment($"module:{moduleId.Trim()}", textureDensity: 1f, lightingPreset: "module", occlusion: false);
            _active = true;
        }

        private void SwitchToFallback()
        {
            if (sceneManager == null) return;
            sceneManager.SetupEnvironment(string.IsNullOrWhiteSpace(fallbackEnvironment) ? "open_field" : fallbackEnvironment.Trim(),
                textureDensity: 1f, lightingPreset: "default", occlusion: false);
            _active = false;
        }

        private void TryClearModule()
        {
            if (sceneManager == null) return;
            sceneManager.SwitchModule(null);
            _active = false;
        }
    }
}

