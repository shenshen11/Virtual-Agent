using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRPerception.Tasks
{
    /// <summary>
    /// 任务注册表：维护 taskId 到 ITask 工厂的映射，支持扩展与向后兼容。
    /// </summary>
    public sealed class TaskRegistry
    {
        private static readonly Lazy<TaskRegistry> _instance = new Lazy<TaskRegistry>(() => new TaskRegistry());

        private readonly Dictionary<string, Func<TaskRunnerContext, ITask>> _factories =
            new Dictionary<string, Func<TaskRunnerContext, ITask>>(StringComparer.OrdinalIgnoreCase);

        private readonly object _syncRoot = new object();

        public static TaskRegistry Instance => _instance.Value;

        private TaskRegistry()
        {
            // 默认注册内置任务，确保旧任务在未显式注册时仍可创建
            TryRegisterInternal("distance_compression", ctx => new DistanceCompressionTask(ctx));
            TryRegisterInternal("semantic_size_bias", ctx => new SemanticSizeBiasTask(ctx));
            TryRegisterInternal("relative_depth_order", ctx => new RelativeDepthOrderTask(ctx));
            TryRegisterInternal("change_detection", ctx => new ChangeDetectionTask(ctx));
            TryRegisterInternal("occlusion_reasoning", ctx => new OcclusionReasoningTask(ctx));
            TryRegisterInternal("color_constancy", ctx => new ColorConstancyTask(ctx));
            TryRegisterInternal("material_perception", ctx => new MaterialPerceptionTask(ctx));
            TryRegisterInternal("visual_search", ctx => new VisualSearchTask(ctx));
        }

        /// <summary>
        /// 注册任务工厂。
        /// </summary>
        public bool Register(string taskId, Func<TaskRunnerContext, ITask> factory, bool overwrite = false)
        {
            if (string.IsNullOrWhiteSpace(taskId))
            {
                throw new ArgumentException("taskId cannot be null or whitespace.", nameof(taskId));
            }

            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            lock (_syncRoot)
            {
                if (!overwrite && _factories.ContainsKey(taskId))
                {
                    Debug.LogWarning($"[TaskRegistry] Task '{taskId}' already registered. Use overwrite=true to replace the existing factory.");
                    return false;
                }

                _factories[taskId] = factory;
                return true;
            }
        }

        /// <summary>
        /// 取消注册任务工厂。
        /// </summary>
        public bool Unregister(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return false;
            }

            lock (_syncRoot)
            {
                return _factories.Remove(taskId);
            }
        }

        /// <summary>
        /// 尝试通过 taskId 创建任务实例。
        /// </summary>
        public bool TryCreate(string taskId, TaskRunnerContext context, out ITask task)
        {
            task = null;
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return false;
            }

            Func<TaskRunnerContext, ITask> factory;
            lock (_syncRoot)
            {
                if (!_factories.TryGetValue(taskId, out factory))
                {
                    return false;
                }
            }

            try
            {
                task = factory != null ? factory.Invoke(context) : null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TaskRegistry] Failed to instantiate task '{taskId}': {ex.Message}");
                task = null;
            }

            return task != null;
        }

        /// <summary>
        /// 判断是否已注册指定 taskId。
        /// </summary>
        public bool Contains(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return false;
            }

            lock (_syncRoot)
            {
                return _factories.ContainsKey(taskId);
            }
        }

        /// <summary>
        /// 获取所有已注册的 taskId 列表（返回副本，避免外部修改）。
        /// </summary>
        public IReadOnlyCollection<string> GetRegisteredTaskIds()
        {
            lock (_syncRoot)
            {
                return new List<string>(_factories.Keys);
            }
        }

        private void TryRegisterInternal(string taskId, Func<TaskRunnerContext, ITask> factory)
        {
            if (string.IsNullOrWhiteSpace(taskId) || factory == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (!_factories.ContainsKey(taskId))
                {
                    _factories[taskId] = factory;
                }
            }
        }
    }
}
