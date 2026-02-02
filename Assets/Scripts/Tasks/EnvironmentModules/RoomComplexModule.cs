using UnityEngine;
using UnityEngine.Rendering;

namespace VRPerception.Tasks.EnvironmentModules
{
    /// <summary>
    /// RoomComplexModule（Complex）：通常引用 Task.unity 中的 Room Root。
    /// 该模块默认不强行改写 RenderSettings；复杂度主要来自 Room 几何与 Reflection Probe 等场景资产。
    /// </summary>
    public sealed class RoomComplexModule : EnvironmentModuleBase
    {
        [Header("Lighting Reset")]
        [Tooltip("进入 Complex 时强制回到中性光照，避免从上一任务继承色调。")]
        [SerializeField] private bool forceNeutralLighting = true;
        [SerializeField] private Color neutralAmbient = new Color(0.5f, 0.5f, 0.5f, 1f);
        [SerializeField] private Color neutralDirectionalColor = Color.white;
        [SerializeField] private float neutralDirectionalIntensity = 1.0f;
        [SerializeField] private bool enableDirectionalLight = true;

        public override void Apply(EnvironmentModuleContext context)
        {
            if (!forceNeutralLighting) return;

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = neutralAmbient;

            if (context.MainDirectionalLight != null)
            {
                if (enableDirectionalLight) context.MainDirectionalLight.enabled = true;
                context.MainDirectionalLight.color = neutralDirectionalColor;
                context.MainDirectionalLight.intensity = neutralDirectionalIntensity;
            }
        }
    }
}
