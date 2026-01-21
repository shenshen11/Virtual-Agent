using UnityEngine;

namespace VRPerception.Tasks.EnvironmentModules
{
    /// <summary>
    /// Environment Module（环境模块）接口：
    /// 代表一套可切换、可恢复、可审计的环境子系统（几何/灯光/反射/背景等）。
    /// </summary>
    public interface IEnvironmentModule
    {
        /// <summary>模块唯一 ID（建议使用小写与下划线，如 "room_complex"）。</summary>
        string Id { get; }

        /// <summary>用于 UI/日志展示的名称。</summary>
        string DisplayName { get; }

        /// <summary>该模块的根对象（用于启用/禁用）。</summary>
        GameObject Root { get; }

        /// <summary>
        /// 当模块处于激活状态时，是否允许外部调用 ExperimentSceneManager.SetLighting(...) 改写全局光照。
        /// 对于强隔离的模块（如 BlackSimple），建议返回 false。
        /// </summary>
        bool AllowSetLighting { get; }

        /// <summary>应用模块（可在此改写 RenderSettings/Camera clearFlags 等）。</summary>
        void Apply(EnvironmentModuleContext context);

        /// <summary>退出模块（可选；通常由 RenderSettingsSnapshot 负责恢复全局状态）。</summary>
        void Teardown(EnvironmentModuleContext context);

        /// <summary>获取模块内锚点（如 "StimulusAnchor" / "PointLightAnchor"）。</summary>
        Transform GetAnchor(string anchorId);
    }
}

