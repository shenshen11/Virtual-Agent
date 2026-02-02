using UnityEngine;
using UnityEngine.Rendering;

namespace VRPerception.Tasks.EnvironmentModules
{
    /// <summary>
    /// BlackSimpleModule（Simple）：纯黑背景 + 极少反射信息的歧义环境。
    /// 建议该模块 Root 下只放置必要的点光源、探针与参照物（如需）。
    /// </summary>
    public sealed class BlackSimpleModule : EnvironmentModuleBase
    {
        [Header("Camera")]
        [SerializeField] private bool setCameraToSolidBlack = true;

        [Header("RenderSettings")]
        [SerializeField] private bool disableFog = true;
        [SerializeField] private bool forceFlatAmbient = true;
        [SerializeField] private Color ambientColor = Color.black;
        [SerializeField] private float ambientIntensity = 0f;
        [SerializeField] private bool disableEnvironmentReflections = true;
        [SerializeField] private Cubemap blackCubemap;

        [Header("Lighting")]
        [Tooltip("若启用，将暂时禁用主方向光（通常位于场景常驻物体中），避免对 Simple 条件产生额外线索。")]
        [SerializeField] private bool disableMainDirectionalLight = true;

        [Header("Point Light (Simple Key Light)")]
        [Tooltip("确保 Simple 环境中有一个点光源，避免完全黑屏。")]
        [SerializeField] private bool ensurePointLight = true;
        [SerializeField] private string pointLightName = "BlackSimple_PointLight";
        [SerializeField] private string pointLightAnchorId = "PointLightAnchor";
        [SerializeField] private bool overridePointLightSettings = true;
        [SerializeField] private Color pointLightColor = Color.white;
        [SerializeField] private float pointLightIntensity = 1.2f;
        [SerializeField] private float pointLightRange = 4.0f;
        [SerializeField] private LightShadows pointLightShadows = LightShadows.None;
        [Tooltip("若无锚点，则使用相机本地偏移（相机坐标系）。")]
        [SerializeField] private Vector3 pointLightCameraOffset = new Vector3(0.3f, 0.2f, 0.5f);
        [SerializeField] private Vector3 pointLightFallbackLocalOffset = new Vector3(0.0f, 1.0f, 1.5f);

        private Light _pointLight;

        public override void Apply(EnvironmentModuleContext context)
        {
            if (setCameraToSolidBlack && context.TargetCamera != null)
            {
                context.TargetCamera.clearFlags = CameraClearFlags.SolidColor;
                context.TargetCamera.backgroundColor = Color.black;
            }

            if (disableFog)
            {
                RenderSettings.fog = false;
            }

            if (forceFlatAmbient)
            {
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = ambientColor;
                RenderSettings.ambientIntensity = Mathf.Max(0f, ambientIntensity);
            }

            if (disableEnvironmentReflections)
            {
                RenderSettings.reflectionIntensity = 0f;

                if (blackCubemap != null)
                {
                    // 先设置 cubemap，再切换到 Custom，避免 Custom 但 cubemap 为空导致异常
                    RenderSettings.customReflection = blackCubemap;
                    RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                }
                else
                {
                    // 若项目当前处于 Custom 但未配置 cubemap，会触发 Unity 的 ArgumentException
                    if (RenderSettings.defaultReflectionMode == DefaultReflectionMode.Custom)
                    {
                        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
                    }
                }
            }

            if (disableMainDirectionalLight && context.MainDirectionalLight != null)
            {
                context.MainDirectionalLight.enabled = false;
            }

            EnsurePointLight(context);
        }

        public override void Teardown(EnvironmentModuleContext context)
        {
            if (_pointLight != null)
            {
                _pointLight.enabled = false;
            }
        }

        private void EnsurePointLight(EnvironmentModuleContext context)
        {
            if (!ensurePointLight) return;

            if (_pointLight == null)
            {
                _pointLight = FindExistingPointLight();
                if (_pointLight == null)
                {
                    var name = string.IsNullOrWhiteSpace(pointLightName) ? "BlackSimple_PointLight" : pointLightName;
                    var go = new GameObject(name);
                    if (Root != null)
                    {
                        go.transform.SetParent(Root.transform, false);
                    }
                    _pointLight = go.AddComponent<Light>();
                    _pointLight.type = LightType.Point;
                }
            }

            if (_pointLight == null) return;

            if (overridePointLightSettings)
            {
                _pointLight.type = LightType.Point;
                _pointLight.color = pointLightColor;
                _pointLight.intensity = Mathf.Max(0f, pointLightIntensity);
                _pointLight.range = Mathf.Max(0.1f, pointLightRange);
                _pointLight.shadows = pointLightShadows;
            }

            PositionPointLight(context);
            _pointLight.enabled = true;
        }

        private Light FindExistingPointLight()
        {
            if (Root == null) return null;

            if (!string.IsNullOrWhiteSpace(pointLightName))
            {
                var byName = Root.transform.Find(pointLightName);
                if (byName != null)
                {
                    var light = byName.GetComponent<Light>();
                    if (light != null && light.type == LightType.Point) return light;
                }
            }

            if (string.IsNullOrWhiteSpace(pointLightName))
            {
                var lights = Root.GetComponentsInChildren<Light>(true);
                foreach (var l in lights)
                {
                    if (l != null && l.type == LightType.Point) return l;
                }
            }

            return null;
        }

        private void PositionPointLight(EnvironmentModuleContext context)
        {
            if (_pointLight == null) return;

            var anchor = GetAnchor(pointLightAnchorId);
            if (anchor != null)
            {
                _pointLight.transform.position = anchor.position;
                _pointLight.transform.rotation = anchor.rotation;
                return;
            }

            if (context.TargetCamera != null)
            {
                var cam = context.TargetCamera.transform;
                _pointLight.transform.position = cam.TransformPoint(pointLightCameraOffset);
                _pointLight.transform.rotation = Quaternion.identity;
                return;
            }

            if (_pointLight.transform.parent != null)
            {
                _pointLight.transform.localPosition = pointLightFallbackLocalOffset;
            }
            else
            {
                _pointLight.transform.position = pointLightFallbackLocalOffset;
            }
        }
    }
}
