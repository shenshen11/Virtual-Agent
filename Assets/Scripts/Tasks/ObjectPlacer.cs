using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRPerception.Tasks
{
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

        private readonly List<GameObject> _spawned = new List<GameObject>();

        private void Awake()
        {
            if (defaultMaterial == null)
            {
                defaultMaterial = new Material(Shader.Find("Standard"))
                {
                    name = "VRP_Default_Object_Mat",
                    color = new Color(0.85f, 0.85f, 0.9f)
                };
            }
        }

        /// <summary>
        /// 生成对象
        /// </summary>
        public GameObject Place(string kind, Vector3 position, float uniformScale = 1f, Material materialOverride = null, string name = null)
        {
            kind = (kind ?? "cube").ToLowerInvariant();
            GameObject go = CreatePrimitive(kind);
            if (go == null) return null;

            if (!string.IsNullOrEmpty(name)) go.name = name;

            go.transform.position = position;
            go.transform.localScale = Vector3.one * Mathf.Max(0.001f, uniformScale);

            var mat = materialOverride ?? defaultMaterial;
            ApplyMaterial(go, mat);

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