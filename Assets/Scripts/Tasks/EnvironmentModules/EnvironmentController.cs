using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRPerception.Tasks.EnvironmentModules
{
    /// <summary>
    /// Environment Module 控制器：
    /// - 发现并索引场景中的模块
    /// - 在切换时做全局状态快照与恢复
    /// - 支持锚点查询
    /// </summary>
    public sealed class EnvironmentController
    {
        private readonly Dictionary<string, EnvironmentModuleBase> _modules =
            new Dictionary<string, EnvironmentModuleBase>(StringComparer.OrdinalIgnoreCase);

        private bool _modulesInitialized;
        private EnvironmentModuleBase _activeModule;
        private RenderSettingsSnapshot _snapshotBeforeActive;
        private Dictionary<EnvironmentModuleBase, bool> _activeSelfSnapshotBeforeActive;

        /// <summary>当前激活的 moduleId（为空表示未使用 module）。</summary>
        public string ActiveModuleId => _activeModule != null ? _activeModule.Id : null;

        /// <summary>当前是否有激活的 module。</summary>
        public bool HasActiveModule => _activeModule != null;

        /// <summary>当前模块是否允许外部 SetLighting。</summary>
        public bool ActiveModuleAllowsSetLighting => _activeModule == null || _activeModule.AllowSetLighting;

        /// <summary>强制刷新模块索引（通常不需要）。</summary>
        public void RefreshModules()
        {
            _modules.Clear();

            var found = UnityEngine.Object.FindObjectsOfType<EnvironmentModuleBase>(includeInactive: true);
            foreach (var m in found)
            {
                if (m == null) continue;
                var id = m.Id?.Trim();
                if (string.IsNullOrEmpty(id)) continue;

                if (_modules.ContainsKey(id))
                {
                    Debug.LogWarning($"[EnvironmentController] Duplicate module id '{id}' found. Ignoring '{m.name}'.");
                    continue;
                }

                _modules[id] = m;
            }

            _modulesInitialized = true;
        }

        public bool HasModule(string moduleId)
        {
            if (string.IsNullOrWhiteSpace(moduleId)) return false;
            EnsureModules();
            return _modules.ContainsKey(moduleId.Trim());
        }

        public Transform GetAnchor(string anchorId)
        {
            if (_activeModule == null) return null;
            return _activeModule.GetAnchor(anchorId);
        }

        /// <summary>
        /// 切换到指定 module。moduleId 为空表示退出 module 模式并恢复状态。
        /// </summary>
        public bool SwitchTo(string moduleId, EnvironmentModuleContext context)
        {
            EnsureModules();

            if (string.IsNullOrWhiteSpace(moduleId))
            {
                ClearActive(context);
                return true;
            }

            var id = moduleId.Trim();
            if (!_modules.TryGetValue(id, out var next) || next == null)
            {
                Debug.LogWarning($"[EnvironmentController] Module '{id}' not found.");
                return false;
            }

            if (_activeModule != null && string.Equals(_activeModule.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                // 同一个模块：确保激活即可
                if (_activeModule.Root != null && !_activeModule.Root.activeSelf)
                {
                    _activeModule.Root.SetActive(true);
                }
                return true;
            }

            // 先退出当前模块（恢复到切换前状态），再进入新模块
            ClearActive(context);

            _snapshotBeforeActive = RenderSettingsSnapshot.Capture(context.TargetCamera, context.MainDirectionalLight);
            _activeSelfSnapshotBeforeActive = CaptureModuleActiveSelfStates();

            // 独占：进入新模块时禁用其他模块 root
            foreach (var kv in _modules)
            {
                var m = kv.Value;
                if (m == null || m.Root == null) continue;
                bool shouldBeActive = string.Equals(kv.Key, id, StringComparison.OrdinalIgnoreCase);
                if (m.Root.activeSelf != shouldBeActive)
                {
                    m.Root.SetActive(shouldBeActive);
                }
            }

            _activeModule = next;
            try
            {
                _activeModule.Apply(context);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[EnvironmentController] Apply failed for module '{id}': {ex.Message}");
            }

            return true;
        }

        /// <summary>退出 module 并恢复到切换前状态。</summary>
        public void ClearActive(EnvironmentModuleContext context)
        {
            if (_activeModule != null)
            {
                try
                {
                    _activeModule.Teardown(context);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[EnvironmentController] Teardown failed for module '{_activeModule.Id}': {ex.Message}");
                }
            }

            // 先恢复全局，再恢复 root activeSelf
            if (_snapshotBeforeActive != null)
            {
                _snapshotBeforeActive.Restore(context.TargetCamera, context.MainDirectionalLight);
            }

            if (_activeSelfSnapshotBeforeActive != null)
            {
                foreach (var kv in _activeSelfSnapshotBeforeActive)
                {
                    if (kv.Key == null || kv.Key.Root == null) continue;
                    if (kv.Key.Root.activeSelf != kv.Value)
                    {
                        kv.Key.Root.SetActive(kv.Value);
                    }
                }
            }

            _activeModule = null;
            _snapshotBeforeActive = null;
            _activeSelfSnapshotBeforeActive = null;
        }

        private void EnsureModules()
        {
            if (_modulesInitialized) return;
            RefreshModules();
        }

        private Dictionary<EnvironmentModuleBase, bool> CaptureModuleActiveSelfStates()
        {
            var snapshot = new Dictionary<EnvironmentModuleBase, bool>();
            foreach (var kv in _modules)
            {
                var m = kv.Value;
                if (m == null || m.Root == null) continue;
                snapshot[m] = m.Root.activeSelf;
            }
            return snapshot;
        }
    }
}

