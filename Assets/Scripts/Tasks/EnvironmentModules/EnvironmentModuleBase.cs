using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRPerception.Tasks.EnvironmentModules
{
    /// <summary>
    /// 便于在场景中配置的 Environment Module 基类（挂在 module Root 上）。
    /// </summary>
    public abstract class EnvironmentModuleBase : MonoBehaviour, IEnvironmentModule
    {
        [Serializable]
        public struct AnchorRef
        {
            public string id;
            public Transform transform;
        }

        [Header("Module Identity")]
        [SerializeField] private string moduleId = "module_id";
        [SerializeField] private string displayName = "Environment Module";
        [SerializeField] private bool allowSetLighting = false;

        [Header("Anchors")]
        [Tooltip("可选：显式注册的锚点列表（如 StimulusAnchor/LightAnchor/PointLightAnchor）。若未配置，将回退按名称在子节点中查找。")]
        [SerializeField] private AnchorRef[] anchors;

        private Dictionary<string, Transform> _anchorsById;
        private Dictionary<string, Transform> _anchorsByName;

        public string Id => moduleId;
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? moduleId : displayName;
        public GameObject Root => gameObject;
        public bool AllowSetLighting => allowSetLighting;

        protected virtual void Awake()
        {
            BuildAnchorMaps();
        }

        protected virtual void OnValidate()
        {
            // Editor 下保证字典更新（避免切换 module 时锚点找不到）
            BuildAnchorMaps();
        }

        private void BuildAnchorMaps()
        {
            _anchorsById ??= new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            _anchorsById.Clear();

            if (anchors != null)
            {
                foreach (var a in anchors)
                {
                    var key = a.id?.Trim();
                    if (string.IsNullOrEmpty(key) || a.transform == null) continue;
                    _anchorsById[key] = a.transform;
                }
            }

            _anchorsByName ??= new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            _anchorsByName.Clear();

            var all = GetComponentsInChildren<Transform>(true);
            foreach (var t in all)
            {
                if (t == null) continue;
                var nameKey = t.name?.Trim();
                if (string.IsNullOrEmpty(nameKey)) continue;
                if (!_anchorsByName.ContainsKey(nameKey))
                {
                    _anchorsByName[nameKey] = t;
                }
            }
        }

        public virtual void Apply(EnvironmentModuleContext context) { }

        public virtual void Teardown(EnvironmentModuleContext context) { }

        public virtual Transform GetAnchor(string anchorId)
        {
            if (string.IsNullOrWhiteSpace(anchorId)) return null;

            var key = anchorId.Trim();
            if (_anchorsById != null && _anchorsById.TryGetValue(key, out var t) && t != null)
            {
                return t;
            }

            if (_anchorsByName != null && _anchorsByName.TryGetValue(key, out t) && t != null)
            {
                return t;
            }

            return null;
        }
    }
}

