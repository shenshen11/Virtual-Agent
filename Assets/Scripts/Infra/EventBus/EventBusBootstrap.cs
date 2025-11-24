using System;
using System.Reflection;
using UnityEngine;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 在运行时为 EventBusManager 自动创建缺失的事件通道 ScriptableObject 实例（非持久化）。
    /// 目的：即便未在 Resources/Events 配置资产，也能让事件总线工作，便于开箱即用与测试。
    /// </summary>
    public class EventBusBootstrap : MonoBehaviour
    {
        [Header("Options")]
        [Tooltip("为避免重复注入，仅在第一次 Awake 执行。")]
        [SerializeField] private bool runOnce = true;

        private static bool _bootstrapped;

        private void Awake()
        {
            if (runOnce && _bootstrapped) return;

            var bus = EventBusManager.Instance;
            if (bus == null)
            {
                Debug.LogError("[EventBusBootstrap] EventBusManager.Instance not found.");
                return;
            }

            int created = 0;
            created += EnsureChannel<FrameRequestedEventChannel>(bus, "frameRequestedChannel");
            created += EnsureChannel<FrameCapturedEventChannel>(bus, "frameCapturedChannel");
            created += EnsureChannel<InferenceReceivedEventChannel>(bus, "inferenceReceivedChannel");
            created += EnsureChannel<ActionPlanReceivedEventChannel>(bus, "actionPlanReceivedChannel");
            created += EnsureChannel<ExecutorStateEventChannel>(bus, "executorStateChannel");
            created += EnsureChannel<CommandLifecycleEventChannel>(bus, "commandLifecycleChannel");
            created += EnsureChannel<ConnectionStateEventChannel>(bus, "connectionStateChannel");
            created += EnsureChannel<ErrorEventChannel>(bus, "errorChannel");
            created += EnsureChannel<TrialLifecycleEventChannel>(bus, "trialLifecycleChannel");
            created += EnsureChannel<OrchestratorStateEventChannel>(bus, "orchestratorStateChannel");
            created += EnsureChannel<LogFlushEventChannel>(bus, "logFlushChannel");
            created += EnsureChannel<PerformanceMetricEventChannel>(bus, "performanceMetricChannel");
            created += EnsureChannel<SceneObjectEventChannel>(bus, "sceneObjectChannel");
            created += EnsureChannel<ApplicationQuitEventChannel>(bus, "applicationQuitChannel");
            created += EnsureChannel<PauseResumeEventChannel>(bus, "pauseResumeChannel");

            Debug.Log($"[EventBusBootstrap] Ensured channels, created={created}");

            // 注意：EventBusManager 的内部缓存(_channelCache)是在 Awake 中建立，此处未刷新缓存。
            // 当前工程主要通过属性访问通道，故无需刷新缓存即可使用。
            _bootstrapped = true;
        }

        private int EnsureChannel<T>(EventBusManager bus, string fieldName) where T : ScriptableObject
        {
            var type = typeof(EventBusManager);
            var fi = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi == null)
            {
                Debug.LogWarning($"[EventBusBootstrap] Field not found: {fieldName}");
                return 0;
            }

            var existing = fi.GetValue(bus) as ScriptableObject;
            if (existing != null) return 0;

            var created = ScriptableObject.CreateInstance<T>();
            created.name = typeof(T).Name + "_Runtime";
            fi.SetValue(bus, created);
            return 1;
        }
    }
}