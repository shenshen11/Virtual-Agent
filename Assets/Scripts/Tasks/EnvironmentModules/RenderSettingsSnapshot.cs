using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRPerception.Tasks.EnvironmentModules
{
    /// <summary>
    /// RenderSettings/Camera 等全局状态快照（用于 module 切换的可恢复性）。
    /// </summary>
    public sealed class RenderSettingsSnapshot
    {
        private readonly Material _skybox;
        private readonly AmbientMode _ambientMode;
        private readonly Color _ambientLight;
        private readonly float _ambientIntensity;
        private readonly Color _ambientSkyColor;
        private readonly Color _ambientEquatorColor;
        private readonly Color _ambientGroundColor;

        private readonly DefaultReflectionMode _defaultReflectionMode;
        private readonly Cubemap _customReflection;
        private readonly float _reflectionIntensity;
        private readonly int _reflectionBounces;
        private readonly Light _sun;

        private readonly bool _fog;
        private readonly Color _fogColor;
        private readonly FogMode _fogMode;
        private readonly float _fogDensity;
        private readonly float _fogStartDistance;
        private readonly float _fogEndDistance;

        private readonly CameraSnapshot _cameraSnapshot;
        private readonly LightSnapshot _mainDirectionalLightSnapshot;

        private RenderSettingsSnapshot(
            Material skybox,
            AmbientMode ambientMode,
            Color ambientLight,
            float ambientIntensity,
            Color ambientSkyColor,
            Color ambientEquatorColor,
            Color ambientGroundColor,
            DefaultReflectionMode defaultReflectionMode,
            Cubemap customReflection,
            float reflectionIntensity,
            int reflectionBounces,
            Light sun,
            bool fog,
            Color fogColor,
            FogMode fogMode,
            float fogDensity,
            float fogStartDistance,
            float fogEndDistance,
            CameraSnapshot cameraSnapshot,
            LightSnapshot mainDirectionalLightSnapshot)
        {
            _skybox = skybox;
            _ambientMode = ambientMode;
            _ambientLight = ambientLight;
            _ambientIntensity = ambientIntensity;
            _ambientSkyColor = ambientSkyColor;
            _ambientEquatorColor = ambientEquatorColor;
            _ambientGroundColor = ambientGroundColor;
            _defaultReflectionMode = defaultReflectionMode;
            _customReflection = customReflection;
            _reflectionIntensity = reflectionIntensity;
            _reflectionBounces = reflectionBounces;
            _sun = sun;
            _fog = fog;
            _fogColor = fogColor;
            _fogMode = fogMode;
            _fogDensity = fogDensity;
            _fogStartDistance = fogStartDistance;
            _fogEndDistance = fogEndDistance;
            _cameraSnapshot = cameraSnapshot;
            _mainDirectionalLightSnapshot = mainDirectionalLightSnapshot;
        }

        /// <summary>
        /// 捕获当前 RenderSettings/Camera/主方向光状态。
        /// </summary>
        public static RenderSettingsSnapshot Capture(Camera camera, Light mainDirectionalLight)
        {
            var defaultReflectionMode = RenderSettings.defaultReflectionMode;
            Cubemap customReflection = null;
            if (defaultReflectionMode == DefaultReflectionMode.Custom)
            {
                try
                {
                    customReflection = RenderSettings.customReflection;
                }
                catch (ArgumentException)
                {
                    // 有些项目可能把 Reflection Source 设为 Custom 但未指定 cubemap，
                    // Unity 在访问 customReflection 时会抛异常；为保证 module 切换可用，回退为 Skybox。
                    defaultReflectionMode = DefaultReflectionMode.Skybox;
                    customReflection = null;
                }

                if (customReflection == null)
                {
                    defaultReflectionMode = DefaultReflectionMode.Skybox;
                }
            }

            return new RenderSettingsSnapshot(
                RenderSettings.skybox,
                RenderSettings.ambientMode,
                RenderSettings.ambientLight,
                RenderSettings.ambientIntensity,
                RenderSettings.ambientSkyColor,
                RenderSettings.ambientEquatorColor,
                RenderSettings.ambientGroundColor,
                defaultReflectionMode,
                customReflection,
                RenderSettings.reflectionIntensity,
                RenderSettings.reflectionBounces,
                RenderSettings.sun,
                RenderSettings.fog,
                RenderSettings.fogColor,
                RenderSettings.fogMode,
                RenderSettings.fogDensity,
                RenderSettings.fogStartDistance,
                RenderSettings.fogEndDistance,
                CameraSnapshot.Capture(camera),
                LightSnapshot.Capture(mainDirectionalLight));
        }

        /// <summary>
        /// 恢复快照。
        /// </summary>
        public void Restore(Camera camera, Light mainDirectionalLight)
        {
            RenderSettings.skybox = _skybox;
            RenderSettings.ambientMode = _ambientMode;
            RenderSettings.ambientLight = _ambientLight;
            RenderSettings.ambientIntensity = _ambientIntensity;
            RenderSettings.ambientSkyColor = _ambientSkyColor;
            RenderSettings.ambientEquatorColor = _ambientEquatorColor;
            RenderSettings.ambientGroundColor = _ambientGroundColor;

            if (_defaultReflectionMode == DefaultReflectionMode.Custom && _customReflection != null)
            {
                // 先设置 cubemap，再切换模式，避免 Custom 但 cubemap 为空导致异常。
                RenderSettings.customReflection = _customReflection;
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            }
            else
            {
                RenderSettings.defaultReflectionMode = _defaultReflectionMode == DefaultReflectionMode.Custom
                    ? DefaultReflectionMode.Skybox
                    : _defaultReflectionMode;
            }
            RenderSettings.reflectionIntensity = _reflectionIntensity;
            RenderSettings.reflectionBounces = _reflectionBounces;
            RenderSettings.sun = _sun;

            RenderSettings.fog = _fog;
            RenderSettings.fogColor = _fogColor;
            RenderSettings.fogMode = _fogMode;
            RenderSettings.fogDensity = _fogDensity;
            RenderSettings.fogStartDistance = _fogStartDistance;
            RenderSettings.fogEndDistance = _fogEndDistance;

            _cameraSnapshot.Restore(camera);
            _mainDirectionalLightSnapshot.Restore(mainDirectionalLight);
        }

        private readonly struct CameraSnapshot
        {
            private readonly bool _captured;
            private readonly CameraClearFlags _clearFlags;
            private readonly Color _backgroundColor;

            private CameraSnapshot(bool captured, CameraClearFlags clearFlags, Color backgroundColor)
            {
                _captured = captured;
                _clearFlags = clearFlags;
                _backgroundColor = backgroundColor;
            }

            public static CameraSnapshot Capture(Camera camera)
            {
                if (camera == null) return new CameraSnapshot(false, default, default);
                return new CameraSnapshot(true, camera.clearFlags, camera.backgroundColor);
            }

            public void Restore(Camera camera)
            {
                if (!_captured || camera == null) return;
                camera.clearFlags = _clearFlags;
                camera.backgroundColor = _backgroundColor;
            }
        }

        private readonly struct LightSnapshot
        {
            private readonly bool _captured;
            private readonly bool _enabled;
            private readonly Color _color;
            private readonly float _intensity;
            private readonly LightShadows _shadows;
            private readonly float _shadowStrength;

            private LightSnapshot(bool captured, bool enabled, Color color, float intensity, LightShadows shadows, float shadowStrength)
            {
                _captured = captured;
                _enabled = enabled;
                _color = color;
                _intensity = intensity;
                _shadows = shadows;
                _shadowStrength = shadowStrength;
            }

            public static LightSnapshot Capture(Light light)
            {
                if (light == null) return new LightSnapshot(false, false, default, default, default, default);
                return new LightSnapshot(true, light.enabled, light.color, light.intensity, light.shadows, light.shadowStrength);
            }

            public void Restore(Light light)
            {
                if (!_captured || light == null) return;
                light.enabled = _enabled;
                light.color = _color;
                light.intensity = _intensity;
                light.shadows = _shadows;
                light.shadowStrength = _shadowStrength;
            }
        }
    }
}
