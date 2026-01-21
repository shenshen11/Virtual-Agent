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
        }
    }
}
