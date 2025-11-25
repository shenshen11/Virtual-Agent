using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRPerception.Tasks
{
    [Serializable]
    public class KindPrefab
    {
        /// <summary>
        /// 语义类型名（如 "chair" / "apple" / "toy_car"），区分大小写不敏感。
        /// 建议与 TrialSpec 中使用的对象标签保持一致。
        /// </summary>
        public string kind;

        /// <summary>
        /// 对应的 Prefab。若为空则忽略该条目。
        /// </summary>
        public GameObject prefab;
    }

    /// <summary>
    /// 3D对象动态生成与管理：
    /// - 支持基本原语：cube / sphere / capsule (human 近似) / quad
    /// - 位置与尺寸控制
    /// - 可选材质应用与分组清理
    /// </summary>
    public class ObjectPlacer : MonoBehaviour
    {
        [Header("Defaults")]
        [SerializeField] private Material defaultMaterial;
        [SerializeField] private bool useSharedMaterial = true;

        [Header("Prefab Overrides")]
        [SerializeField] private List<KindPrefab> prefabOverrides = new List<KindPrefab>();

        private readonly List<GameObject> _spawned = new List<GameObject>();
        private Dictionary<string, GameObject> _prefabMap;

        private void Awake()
        {
            BuildPrefabMap();

            if (defaultMaterial == null)
            {
                defaultMaterial = new Material(Shader.Find("Standard"))
                {
                    name = "VRP_Default_Object_Mat",
                    color = new Color(0.85f, 0.85f, 0.9f)
                };
            }
        }

        private void OnValidate()
        {
            BuildPrefabMap();
        }

        /// <summary>
        /// 基于 prefabOverrides 构建快速查找表，按 kind → Prefab 映射。
        /// 不存在映射时保持 _prefabMap=null，以便 Place 走原有 Primitive 逻辑。
        /// </summary>
        private void BuildPrefabMap()
        {
            if (prefabOverrides == null || prefabOverrides.Count == 0)
            {
                _prefabMap = null;
                return;
            }

            if (_prefabMap == null)
                _prefabMap = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            else
                _prefabMap.Clear();

            foreach (var entry in prefabOverrides)
            {
                if (entry == null) continue;
                if (string.IsNullOrWhiteSpace(entry.kind)) continue;
                if (entry.prefab == null) continue;

                var key = entry.kind.Trim();
                if (key.Length == 0) continue;

                _prefabMap[key] = entry.prefab;
            }
        }

        /// <summary>
        /// 生成对象
        /// </summary>
        public GameObject Place(string kind, Vector3 position, float uniformScale = 1f, Material materialOverride = null, string name = null)
        {
            kind = (kind ?? "cube").ToLowerInvariant();

            bool usedPrefab = false;
            GameObject go = null;

            // 优先使用配置的 Prefab；若未配置则退回原生 Primitive（保持旧行为不变）
            if (_prefabMap != null && _prefabMap.TryGetValue(kind, out var prefab) && prefab != null)
            {
                go = GameObject.Instantiate(prefab);
                usedPrefab = true;
            }
            else
            {
                go = CreatePrimitive(kind);
            }

            if (go == null) return null;

            if (!string.IsNullOrEmpty(name)) go.name = name;

            go.transform.position = position;
            go.transform.localScale = Vector3.one * Mathf.Max(0.001f, uniformScale);

            // 对于 Prefab：默认保留其自带材质；只有显式传入 materialOverride 时才覆盖。
            // 对于 Primitive：沿用原有行为，总是应用 defaultMaterial（或覆写材质）。
            if (materialOverride != null)
            {
                ApplyMaterial(go, materialOverride);
            }
            else if (!usedPrefab)
            {
                var mat = defaultMaterial;
                ApplyMaterial(go, mat);
            }

            _spawned.Add(go);
            return go;
        }

        /// <summary>
        /// 按名称销毁已放置对象（完全匹配）
        /// </summary>
        public bool DestroyByName(string objectName)
        {
            for (int i = _spawned.Count - 1; i >= 0; i--)
            {
                var go = _spawned[i];
                if (go == null) { _spawned.RemoveAt(i); continue; }
                if (go.name.Equals(objectName, StringComparison.Ordinal))
                {
#if UNITY_EDITOR
                    GameObject.DestroyImmediate(go);
#else
                    GameObject.Destroy(go);
#endif
                    _spawned.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 清理所有放置对象
        /// </summary>
        public void ClearAll()
        {
            for (int i = _spawned.Count - 1; i >= 0; i--)
            {
                var go = _spawned[i];
                if (go != null)
                {
#if UNITY_EDITOR
                    GameObject.DestroyImmediate(go);
#else
                    GameObject.Destroy(go);
#endif
                }
            }
            _spawned.Clear();
        }

        private static GameObject CreatePrimitive(string kind)
        {
            switch (kind)
            {
                case "cube":      return GameObject.CreatePrimitive(PrimitiveType.Cube);
                case "sphere":    return GameObject.CreatePrimitive(PrimitiveType.Sphere);
                case "capsule":
                case "human":     return GameObject.CreatePrimitive(PrimitiveType.Capsule);
                case "quad":      return GameObject.CreatePrimitive(PrimitiveType.Quad);
                case "cylinder":  return GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                case "plane":     return GameObject.CreatePrimitive(PrimitiveType.Plane);
                default:          return GameObject.CreatePrimitive(PrimitiveType.Cube);
            }
        }

        private void ApplyMaterial(GameObject go, Material mat)
        {
            if (go == null || mat == null) return;
            var r = go.GetComponent<Renderer>();
            if (r == null) return;

            if (useSharedMaterial)
                r.sharedMaterial = mat;
            else
                r.material = mat;
        }
    }
}
