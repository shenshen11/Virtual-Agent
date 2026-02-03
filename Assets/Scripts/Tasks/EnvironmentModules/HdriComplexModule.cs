using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRPerception.Tasks.EnvironmentModules
{
    /// <summary>
    /// HDRI-only 复杂环境模块：使用 Skybox HDRI 提供反射线索。
    /// </summary>
    public sealed class HdriComplexModule : EnvironmentModuleBase
    {
        [Header("HDRI Skybox")]
        [Tooltip("HDRI Skybox 材质（建议使用 Skybox/Panoramic）。")]
        [SerializeField] private Material skyboxMaterial;
        [SerializeField] private bool setCameraToSkybox = true;
        [SerializeField] private float skyboxExposure = 1.0f;
        [SerializeField] private float skyboxRotation = 0.0f;
        [SerializeField] private float ambientIntensity = 1.0f;
        [SerializeField] private float reflectionIntensity = 1.0f;
        [SerializeField] private bool disableDirectionalLight = true;

        [Header("Optional Object Visibility")]
        [Tooltip("HDRI-only 时需要隐藏的场景根对象（例如 Room 几何）。")]
        [SerializeField] private GameObject[] disableRoots;

        private readonly Dictionary<GameObject, bool> _disabledSnapshot = new Dictionary<GameObject, bool>();

        public override void Apply(EnvironmentModuleContext context)
        {
            if (skyboxMaterial == null)
            {
                Debug.LogWarning("[HdriComplexModule] skyboxMaterial is not assigned.");
                return;
            }

            RenderSettings.skybox = skyboxMaterial;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = Mathf.Max(0f, ambientIntensity);
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = Mathf.Max(0f, reflectionIntensity);

            if (skyboxMaterial.HasProperty("_Exposure"))
            {
                skyboxMaterial.SetFloat("_Exposure", skyboxExposure);
            }

            if (skyboxMaterial.HasProperty("_Rotation"))
            {
                skyboxMaterial.SetFloat("_Rotation", skyboxRotation);
            }

            if (setCameraToSkybox && context.TargetCamera != null)
            {
                context.TargetCamera.clearFlags = CameraClearFlags.Skybox;
            }

            if (disableDirectionalLight && context.MainDirectionalLight != null)
            {
                context.MainDirectionalLight.enabled = false;
            }

            DisableRoots();

            // Ensure ambient/reflection probes refresh after runtime skybox change (尤其在设备端)
            DynamicGI.UpdateEnvironment();
        }

        public override void Teardown(EnvironmentModuleContext context)
        {
            RestoreDisabledRoots();
        }

        private void DisableRoots()
        {
            _disabledSnapshot.Clear();

            if (disableRoots == null || disableRoots.Length == 0) return;

            foreach (var go in disableRoots)
            {
                if (go == null) continue;
                _disabledSnapshot[go] = go.activeSelf;
                if (go.activeSelf)
                {
                    go.SetActive(false);
                }
            }
        }

        private void RestoreDisabledRoots()
        {
            if (_disabledSnapshot.Count == 0) return;

            foreach (var kv in _disabledSnapshot)
            {
                if (kv.Key == null) continue;
                if (kv.Key.activeSelf != kv.Value)
                {
                    kv.Key.SetActive(kv.Value);
                }
            }

            _disabledSnapshot.Clear();
        }
    }
}
