using System;
using System.Collections.Generic;
using UnityEngine;
using VRPerception.Infra.EventBus;

namespace VRPerception.Tasks
{
    /// <summary>
    /// 实验场景管理器：
    /// - 动态场景切换（开阔地/走廊）
    /// - 光照预设管理（bright/dim/hdr 简化）
    /// - 纹理密度控制（通过材质 Tiling）
    /// - 遮挡物控制（简化为若干可开关的柱/墙）
    /// </summary>
    public class ExperimentSceneManager : MonoBehaviour
    {
        [Header("Event Bus")]
        [SerializeField] private EventBusManager eventBus;

        [Header("Materials")]
        [Tooltip("地面材质（可为空，将自动创建一个简单材质）")]
        [SerializeField] private Material groundMaterial;
        [Tooltip("墙体材质（可为空，将自动创建一个简单材质）")]
        [SerializeField] private Material wallMaterial;

        [Header("Lighting Presets")]
        [SerializeField] private Color brightAmbient = new Color(0.8f, 0.8f, 0.8f);
        [SerializeField] private Color dimAmbient = new Color(0.25f, 0.25f, 0.25f);
        [SerializeField] private Color hdrAmbient = new Color(1.2f, 1.2f, 1.2f);

        [Header("Corridor Settings")]
        [SerializeField] private float corridorHalfWidth = 2.0f;
        [SerializeField] private float corridorWallHeight = 3.0f;
        [SerializeField] private float corridorLength = 200f;
        [SerializeField] private float corridorWallThickness = 0.2f;

        [Header("Open Field Settings")]
        [SerializeField] private Vector2 openFieldSize = new Vector2(50f, 50f); // XZ

        [Header("Occluders")]
        [SerializeField] private bool createDefaultOccluders = true;
        [SerializeField] private Vector3[] occluderPositions = new[]
        {
            new Vector3(1.5f, 1.0f, 5f),
            new Vector3(-1.0f, 1.0f, 8f),
            new Vector3(0.5f, 1.0f, 12f)
        };
        [SerializeField] private Vector3 occluderSize = new Vector3(0.5f, 2.0f, 0.5f);

        [Header("Lighting Runtime")]
        [Tooltip("主方向光（可选）。如未指定，将在运行时尝试查找场景中的第一个 Directional Light。")]
        [SerializeField] private Light mainDirectionalLight;

        private readonly List<GameObject> _spawned = new List<GameObject>();
        private readonly List<GameObject> _occluders = new List<GameObject>();
        private string _currentEnvironment = null;
        private string _currentLightingPreset = "default";
        private float _currentTextureDensity = 1f;
        private bool _currentOcclusionEnabled = false;
        private bool _currentShadowEnabled = false;

        // 公开走廊参数，供其他脚本查询
        public float CorridorHalfWidth => corridorHalfWidth;
        public float CorridorLength => corridorLength;
        public float CorridorWallHeight => corridorWallHeight;
        public string CurrentEnvironment => _currentEnvironment;
        public string CurrentLightingPreset => _currentLightingPreset;
        public float CurrentTextureDensity => _currentTextureDensity;
        public bool CurrentOcclusionEnabled => _currentOcclusionEnabled;
        public bool CurrentShadowEnabled => _currentShadowEnabled;

        private void Awake()
        {
            if (eventBus == null) eventBus = EventBusManager.Instance;
            EnsureMaterials();
        }

        private void EnsureMaterials()
        {
            if (groundMaterial == null)
            {
                groundMaterial = new Material(Shader.Find("Standard"));
                groundMaterial.name = "VRP_Ground_Mat";
                groundMaterial.color = new Color(0.65f, 0.7f, 0.65f);
            }
            if (wallMaterial == null)
            {
                wallMaterial = new Material(Shader.Find("Standard"));
                wallMaterial.name = "VRP_Wall_Mat";
                wallMaterial.color = new Color(0.7f, 0.7f, 0.75f);
            }
        }

        // ============== Public API ==============

        public void SetupEnvironment(string environment, float textureDensity = 1f, string lightingPreset = "default", bool occlusion = false)
        {
            ClearCurrent();

            switch ((environment ?? "open_field").ToLower())
            {
                case "corridor":
                    BuildCorridor();
                    _currentEnvironment = "corridor";
                    break;
                case "open_field":
                default:
                    BuildOpenField();
                    _currentEnvironment = "open_field";
                    break;
            }

            _currentTextureDensity = Mathf.Max(0.1f, textureDensity);
            _currentLightingPreset = (lightingPreset ?? "default").ToLower();
            _currentOcclusionEnabled = occlusion;

            SetTextureDensity(_currentTextureDensity);
            SetLighting(_currentLightingPreset);
            SetOcclusion(_currentOcclusionEnabled);

            PublishSceneEvent("environment_setup");
        }

        public void SetLighting(string preset)
        {
            var p = (preset ?? "default").ToLower();
            _currentLightingPreset = p;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;

            // 默认值
            Color ambient;
            Color? dirColor = null;
            float? dirIntensity = null;

            switch (p)
            {
                case "bright":
                    ambient = brightAmbient;
                    dirColor = Color.white;
                    dirIntensity = 1.2f;
                    break;
                case "dim":
                    ambient = dimAmbient;
                    dirColor = Color.white;
                    dirIntensity = 0.6f;
                    break;
                case "hdr":
                    ambient = hdrAmbient;
                    dirColor = Color.white;
                    dirIntensity = 1.5f;
                    break;
                // 颜色恒常实验用：色温 + 强度组合
                case "bright_neutral":
                    ambient = new Color(0.80f, 0.80f, 0.80f);
                    dirColor = Color.white;
                    dirIntensity = 1.3f;
                    break;
                case "dim_neutral":
                    ambient = new Color(0.35f, 0.35f, 0.35f);
                    dirColor = Color.white;
                    dirIntensity = 0.7f;
                    break;
                case "bright_warm":
                    ambient = new Color(0.90f, 0.80f, 0.70f);
                    dirColor = new Color(1.00f, 0.90f, 0.80f);
                    dirIntensity = 1.3f;
                    break;
                case "dim_warm":
                    ambient = new Color(0.45f, 0.35f, 0.30f);
                    dirColor = new Color(0.95f, 0.80f, 0.70f);
                    dirIntensity = 0.7f;
                    break;
                case "bright_cool":
                    ambient = new Color(0.70f, 0.80f, 0.95f);
                    dirColor = new Color(0.80f, 0.90f, 1.00f);
                    dirIntensity = 1.3f;
                    break;
                case "dim_cool":
                    ambient = new Color(0.30f, 0.35f, 0.50f);
                    dirColor = new Color(0.65f, 0.75f, 0.95f);
                    dirIntensity = 0.7f;
                    break;
                default:
                    // default 中性灰
                    ambient = new Color(0.5f, 0.5f, 0.5f);
                    dirColor = Color.white;
                    dirIntensity = 1.0f;
                    break;
            }

            RenderSettings.ambientLight = ambient;

            // 尝试同步主方向光的颜色与强度（若存在）
            EnsureMainDirectionalLight();
            if (mainDirectionalLight != null)
            {
                if (dirColor.HasValue) mainDirectionalLight.color = dirColor.Value;
                if (dirIntensity.HasValue) mainDirectionalLight.intensity = dirIntensity.Value;
            }

            PublishSceneEvent("lighting_changed", new { preset = p });
        }

        public void SetTextureDensity(float scale)
        {
            scale = Mathf.Max(0.1f, scale);
            _currentTextureDensity = scale;

            foreach (var go in _spawned)
            {
                if (go == null) continue;
                var r = go.GetComponent<Renderer>();
                if (r != null && r.sharedMaterial != null)
                {
                    var tiling = new Vector2(scale, scale);
                    if (r.sharedMaterial.HasProperty("_MainTex"))
                    {
                        var mainTex = r.sharedMaterial.mainTextureScale;
                        r.sharedMaterial.mainTextureScale = tiling;
                    }
                }
            }

            PublishSceneEvent("texture_density_changed", new { scale = scale });
        }

        public void SetOcclusion(bool enable)
        {
            _currentOcclusionEnabled = enable;

            if (enable && _occluders.Count == 0 && createDefaultOccluders)
            {
                CreateOccluders();
            }

            foreach (var oc in _occluders)
            {
                if (oc != null) oc.SetActive(enable);
            }

            PublishSceneEvent("occlusion_changed", new { enabled = enable });
        }

        /// <summary>
        /// 控制主方向光阴影开关（供颜色恒常等任务使用）。
        /// </summary>
        public void SetShadowMode(bool enable)
        {
            _currentShadowEnabled = enable;

            EnsureMainDirectionalLight();
            if (mainDirectionalLight != null)
            {
                mainDirectionalLight.shadows = enable
                    ? LightShadows.Soft
                    : LightShadows.None;
            }

            PublishSceneEvent("shadow_changed", new { enabled = enable });
        }

        public void ClearCurrent()
        {
            foreach (var go in _spawned)
            {
#if UNITY_EDITOR
                if (go != null) DestroyImmediate(go);
#else
                if (go != null) Destroy(go);
#endif
            }
            _spawned.Clear();

            foreach (var oc in _occluders)
            {
#if UNITY_EDITOR
                if (oc != null) DestroyImmediate(oc);
#else
                if (oc != null) Destroy(oc);
#endif
            }
            _occluders.Clear();

            _currentEnvironment = null;
            PublishSceneEvent("environment_cleared");
        }

        // ============== Builders ==============

        private void BuildOpenField()
        {
            // Use Plane
            var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            plane.name = "env_open_field_ground";
            plane.transform.position = Vector3.zero;
            // Plane is 10x10 units. Scale to match openFieldSize (XZ)
            var sx = Mathf.Max(1f, openFieldSize.x / 10f);
            var sz = Mathf.Max(1f, openFieldSize.y / 10f);
            plane.transform.localScale = new Vector3(sx, 1f, sz);
            ApplyMat(plane, groundMaterial);
            _spawned.Add(plane);
        }

        private void BuildCorridor()
        {
            // Ground (long plane)
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "env_corridor_ground";
            ground.transform.position = new Vector3(0f, 0f, 0f);
            ground.transform.localScale = new Vector3(corridorHalfWidth * 2f, 0.2f, corridorLength);
            ApplyMat(ground, groundMaterial);
            _spawned.Add(ground);

            // Left wall
            var left = GameObject.CreatePrimitive(PrimitiveType.Cube);
            left.name = "env_corridor_wall_left";
            left.transform.position = new Vector3(-corridorHalfWidth, corridorWallHeight * 0.5f, 0f);
            left.transform.localScale = new Vector3(corridorWallThickness, corridorWallHeight, corridorLength);
            ApplyMat(left, wallMaterial);
            _spawned.Add(left);

            // Right wall
            var right = GameObject.CreatePrimitive(PrimitiveType.Cube);
            right.name = "env_corridor_wall_right";
            right.transform.position = new Vector3(corridorHalfWidth, corridorWallHeight * 0.5f, 0f);
            right.transform.localScale = new Vector3(corridorWallThickness, corridorWallHeight, corridorLength);
            ApplyMat(right, wallMaterial);
            _spawned.Add(right);
        }

        private void CreateOccluders()
        {
            foreach (var pos in occluderPositions)
            {
                var oc = GameObject.CreatePrimitive(PrimitiveType.Cube);
                oc.name = "env_occluder";
                oc.transform.position = pos;
                oc.transform.localScale = occluderSize;
                ApplyMat(oc, wallMaterial);
                _occluders.Add(oc);
            }
        }

        private static void ApplyMat(GameObject go, Material mat)
        {
            var r = go.GetComponent<Renderer>();
            if (r != null && mat != null)
            {
                r.sharedMaterial = mat;
            }
        }

        /// <summary>
        /// 确保 mainDirectionalLight 已绑定；如未显式指定则尝试在场景中查找一个 Directional Light。
        /// </summary>
        private void EnsureMainDirectionalLight()
        {
            if (mainDirectionalLight != null) return;

            var lights = FindObjectsOfType<Light>();
            foreach (var light in lights)
            {
                if (light != null && light.type == LightType.Directional)
                {
                    mainDirectionalLight = light;
                    break;
                }
            }
        }

        // ============== Event Bus Helper ==============

        private void PublishSceneEvent(string action, object props = null)
        {
            if (eventBus?.SceneObject == null) return;

            var data = new SceneObjectEventData
            {
                objectId = Guid.NewGuid().ToString(),
                objectName = _currentEnvironment ?? "environment",
                action = SceneObjectAction.PropertyChanged,
                timestamp = DateTime.UtcNow,
                position = Vector3.zero,
                rotation = Vector3.zero,
                scale = Vector3.one,
                properties = new { action, props }
            };

            try { eventBus.SceneObject.Publish(data); } catch { /* ignore */ }
        }

        /// <summary>
        /// 检查位置是否在走廊边界内（仅在走廊环境下生效）
        /// </summary>
        /// <param name="position">要检查的世界坐标位置</param>
        /// <param name="margin">边界安全边距（米），默认 0.5m</param>
        /// <returns>如果在边界内或非走廊环境返回 true，否则返回 false</returns>
        public bool IsPositionInBounds(Vector3 position, float margin = 0.5f)
        {
            // 如果不是走廊环境，不做限制
            if (_currentEnvironment != "corridor")
                return true;

            // 检查 X 轴边界（走廊宽度）
            float maxX = corridorHalfWidth - margin;
            if (Mathf.Abs(position.x) > maxX)
                return false;

            // 检查 Z 轴边界（走廊长度）
            float maxZ = corridorLength * 0.5f - margin;
            if (Mathf.Abs(position.z) > maxZ)
                return false;

            // 检查 Y 轴边界（走廊高度）
            if (position.y < 0f || position.y > corridorWallHeight - margin)
                return false;

            return true;
        }

        /// <summary>
        /// 将位置限制在走廊边界内（仅在走廊环境下生效）
        /// </summary>
        /// <param name="position">原始位置</param>
        /// <param name="margin">边界安全边距（米），默认 0.5m</param>
        /// <returns>限制后的位置</returns>
        public Vector3 ClampPositionToBounds(Vector3 position, float margin = 0.5f)
        {
            // 如果不是走廊环境，不做限制
            if (_currentEnvironment != "corridor")
                return position;

            Vector3 clamped = position;

            // 限制 X 轴（走廊宽度）
            float maxX = corridorHalfWidth - margin;
            clamped.x = Mathf.Clamp(position.x, -maxX, maxX);

            // 限制 Z 轴（走廊长度）
            float maxZ = corridorLength * 0.5f - margin;
            clamped.z = Mathf.Clamp(position.z, -maxZ, maxZ);

            // 限制 Y 轴（走廊高度）
            clamped.y = Mathf.Clamp(position.y, 0f, corridorWallHeight - margin);

            return clamped;
        }
    }
}
