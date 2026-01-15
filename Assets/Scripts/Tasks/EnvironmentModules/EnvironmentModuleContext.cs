using UnityEngine;

namespace VRPerception.Tasks.EnvironmentModules
{
    /// <summary>
    /// Environment Module 在 Apply/Teardown 期间可用的上下文信息。
    /// </summary>
    public readonly struct EnvironmentModuleContext
    {
        public EnvironmentModuleContext(Camera targetCamera, Light mainDirectionalLight)
        {
            TargetCamera = targetCamera;
            MainDirectionalLight = mainDirectionalLight;
        }

        /// <summary>模块允许改写的目标相机（用于 clearFlags/backgroundColor）。</summary>
        public Camera TargetCamera { get; }

        /// <summary>实验场景的主方向光（可为空）。</summary>
        public Light MainDirectionalLight { get; }
    }
}

