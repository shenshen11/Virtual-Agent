using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 事件总线管理器，提供统一的事件通道访问和管理
    /// </summary>
    public class EventBusManager : MonoBehaviour
    {
        [Header("Event Channels")]
        [SerializeField] private FrameRequestedEventChannel frameRequestedChannel;
        [SerializeField] private FrameCapturedEventChannel frameCapturedChannel;
        [SerializeField] private InferenceReceivedEventChannel inferenceReceivedChannel;
        [SerializeField] private ActionPlanReceivedEventChannel actionPlanReceivedChannel;
        [SerializeField] private ExecutorStateEventChannel executorStateChannel;
        [SerializeField] private CommandLifecycleEventChannel commandLifecycleChannel;
        [SerializeField] private ConnectionStateEventChannel connectionStateChannel;
        [SerializeField] private ErrorEventChannel errorChannel;
        [SerializeField] private TrialLifecycleEventChannel trialLifecycleChannel;
        [SerializeField] private OrchestratorStateEventChannel orchestratorStateChannel;
        [SerializeField] private LogFlushEventChannel logFlushChannel;
        [SerializeField] private PerformanceMetricEventChannel performanceMetricChannel;
        [SerializeField] private SceneObjectEventChannel sceneObjectChannel;
        [SerializeField] private ApplicationQuitEventChannel applicationQuitChannel;
        [SerializeField] private PauseResumeEventChannel pauseResumeChannel;

        [Header("Settings")]
        [SerializeField] private bool dontDestroyOnLoad = true;
        [SerializeField] private bool enableGlobalErrorHandling = true;
        [SerializeField] private bool logChannelActivity = false;

        private static EventBusManager _instance;
        private readonly Dictionary<Type, ScriptableObject> _channelCache = new Dictionary<Type, ScriptableObject>();

        public static EventBusManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindObjectOfType<EventBusManager>();
                    if (_instance == null)
                    {
                        Debug.LogError("[EventBusManager] No EventBusManager found in scene!");
                    }
                }
                return _instance;
            }
        }

        // 公共访问器
        public FrameRequestedEventChannel FrameRequested => frameRequestedChannel;
        public FrameCapturedEventChannel FrameCaptured => frameCapturedChannel;
        public InferenceReceivedEventChannel InferenceReceived => inferenceReceivedChannel;
        public ActionPlanReceivedEventChannel ActionPlanReceived => actionPlanReceivedChannel;
        public ExecutorStateEventChannel ExecutorState => executorStateChannel;
        public CommandLifecycleEventChannel CommandLifecycle => commandLifecycleChannel;
        public ConnectionStateEventChannel ConnectionState => connectionStateChannel;
        public ErrorEventChannel Error => errorChannel;
        public TrialLifecycleEventChannel TrialLifecycle => trialLifecycleChannel;
        public OrchestratorStateEventChannel OrchestratorState => orchestratorStateChannel;
        public LogFlushEventChannel LogFlush => logFlushChannel;
        public PerformanceMetricEventChannel PerformanceMetric => performanceMetricChannel;
        public SceneObjectEventChannel SceneObject => sceneObjectChannel;
        public ApplicationQuitEventChannel ApplicationQuit => applicationQuitChannel;
        public PauseResumeEventChannel PauseResume => pauseResumeChannel;

        private void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
                if (dontDestroyOnLoad)
                {
                    DontDestroyOnLoad(gameObject);
                }
                InitializeChannels();
            }
            else if (_instance != this)
            {
                Debug.LogWarning("[EventBusManager] Multiple EventBusManager instances detected. Destroying duplicate.");
                Destroy(gameObject);
            }
        }

        private void Start()
        {
            if (enableGlobalErrorHandling)
            {
                SetupGlobalErrorHandling();
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                CleanupChannels();
                _instance = null;
            }
        }

        private void InitializeChannels()
        {
            // 兜底：若未在 Inspector/Resources 指定，运行时即时创建实例，避免“未指派”告警
            frameRequestedChannel ??= ScriptableObject.CreateInstance<FrameRequestedEventChannel>();
            frameCapturedChannel ??= ScriptableObject.CreateInstance<FrameCapturedEventChannel>();
            inferenceReceivedChannel ??= ScriptableObject.CreateInstance<InferenceReceivedEventChannel>();
            actionPlanReceivedChannel ??= ScriptableObject.CreateInstance<ActionPlanReceivedEventChannel>();
            executorStateChannel ??= ScriptableObject.CreateInstance<ExecutorStateEventChannel>();
            commandLifecycleChannel ??= ScriptableObject.CreateInstance<CommandLifecycleEventChannel>();
            connectionStateChannel ??= ScriptableObject.CreateInstance<ConnectionStateEventChannel>();
            errorChannel ??= ScriptableObject.CreateInstance<ErrorEventChannel>();
            trialLifecycleChannel ??= ScriptableObject.CreateInstance<TrialLifecycleEventChannel>();
            orchestratorStateChannel ??= ScriptableObject.CreateInstance<OrchestratorStateEventChannel>();
            logFlushChannel ??= ScriptableObject.CreateInstance<LogFlushEventChannel>();
            performanceMetricChannel ??= ScriptableObject.CreateInstance<PerformanceMetricEventChannel>();
            sceneObjectChannel ??= ScriptableObject.CreateInstance<SceneObjectEventChannel>();
            applicationQuitChannel ??= ScriptableObject.CreateInstance<ApplicationQuitEventChannel>();
            pauseResumeChannel ??= ScriptableObject.CreateInstance<PauseResumeEventChannel>();

            // 缓存所有通道以便快速访问
            CacheChannel<FrameRequestedEventChannel>(frameRequestedChannel);
            CacheChannel<FrameCapturedEventChannel>(frameCapturedChannel);
            CacheChannel<InferenceReceivedEventChannel>(inferenceReceivedChannel);
            CacheChannel<ActionPlanReceivedEventChannel>(actionPlanReceivedChannel);
            CacheChannel<ExecutorStateEventChannel>(executorStateChannel);
            CacheChannel<CommandLifecycleEventChannel>(commandLifecycleChannel);
            CacheChannel<ConnectionStateEventChannel>(connectionStateChannel);
            CacheChannel<ErrorEventChannel>(errorChannel);
            CacheChannel<TrialLifecycleEventChannel>(trialLifecycleChannel);
            CacheChannel<OrchestratorStateEventChannel>(orchestratorStateChannel);
            CacheChannel<LogFlushEventChannel>(logFlushChannel);
            CacheChannel<PerformanceMetricEventChannel>(performanceMetricChannel);
            CacheChannel<SceneObjectEventChannel>(sceneObjectChannel);
            CacheChannel<ApplicationQuitEventChannel>(applicationQuitChannel);
            CacheChannel<PauseResumeEventChannel>(pauseResumeChannel);

            Debug.Log($"[EventBusManager] Initialized with {_channelCache.Count} event channels");
        }

        private void CacheChannel<T>(T channel) where T : ScriptableObject
        {
            if (channel != null)
            {
                _channelCache[typeof(T)] = channel;
                if (logChannelActivity)
                {
                    Debug.Log($"[EventBusManager] Cached channel: {typeof(T).Name}");
                }
            }
            else
            {
                Debug.LogWarning($"[EventBusManager] Channel not assigned: {typeof(T).Name}");
            }
        }

        private void CleanupChannels()
        {
            // 清理所有通道的监听器
            foreach (var channel in _channelCache.Values)
            {
                if (channel is EventChannel<FrameRequestedEventData> frameReq)
                    frameReq.Clear();
                else if (channel is EventChannel<FrameCapturedEventData> frameCap)
                    frameCap.Clear();
                else if (channel is EventChannel<InferenceReceivedEventData> inference)
                    inference.Clear();
                else if (channel is EventChannel<ActionPlanReceivedEventData> actionPlan)
                    actionPlan.Clear();
                else if (channel is EventChannel<ExecutorStateEventData> executorState)
                    executorState.Clear();
                else if (channel is EventChannel<CommandLifecycleEventData> commandLifecycle)
                    commandLifecycle.Clear();
                else if (channel is EventChannel<ConnectionStateEventData> connectionState)
                    connectionState.Clear();
                else if (channel is EventChannel<ErrorEventData> error)
                    error.Clear();
                else if (channel is EventChannel<TrialLifecycleEventData> trialLifecycle)
                    trialLifecycle.Clear();
                else if (channel is EventChannel<LogFlushEventData> logFlush)
                    logFlush.Clear();
                else if (channel is EventChannel<PerformanceMetricEventData> performanceMetric)
                    performanceMetric.Clear();
                else if (channel is EventChannel<SceneObjectEventData> sceneObject)
                    sceneObject.Clear();
                else if (channel is VoidEventChannel voidChannel)
                    voidChannel.Clear();
            }

            _channelCache.Clear();
            Debug.Log("[EventBusManager] Cleaned up all event channels");
        }

        private void SetupGlobalErrorHandling()
        {
            // 订阅Unity的日志回调，将错误转发到错误事件通道
            Application.logMessageReceived += OnLogMessageReceived;
        }

        private void OnLogMessageReceived(string logString, string stackTrace, LogType type)
        {
            if (errorChannel == null) return;

            ErrorSeverity severity = type switch
            {
                LogType.Error => ErrorSeverity.Error,
                LogType.Exception => ErrorSeverity.Critical,
                LogType.Warning => ErrorSeverity.Warning,
                LogType.Log => ErrorSeverity.Info,
                LogType.Assert => ErrorSeverity.Error,
                _ => ErrorSeverity.Info
            };

            // 避免无限循环（错误事件本身产生的日志）
            if (logString.Contains("[EventBusManager]") || logString.Contains("[EventChannel]"))
                return;

            var errorData = new ErrorEventData
            {
                errorId = Guid.NewGuid().ToString(),
                source = "Unity",
                severity = severity,
                errorCode = type.ToString(),
                message = logString,
                stackTrace = stackTrace,
                timestamp = DateTime.UtcNow
            };

            try
            {
                errorChannel.Publish(errorData);
            }
            catch
            {
                // 静默处理，避免无限循环
            }
        }

        /// <summary>
        /// 获取指定类型的事件通道
        /// </summary>
        public T GetChannel<T>() where T : ScriptableObject
        {
            return _channelCache.TryGetValue(typeof(T), out var channel) ? channel as T : null;
        }

        /// <summary>
        /// 发布错误事件的便捷方法
        /// </summary>
        public void PublishError(string source, ErrorSeverity severity, string errorCode, string message, object context = null)
        {
            if (errorChannel == null) return;

            var errorData = new ErrorEventData
            {
                errorId = Guid.NewGuid().ToString(),
                source = source,
                severity = severity,
                errorCode = errorCode,
                message = message,
                timestamp = DateTime.UtcNow,
                context = context
            };

            errorChannel.Publish(errorData);
        }

        /// <summary>
        /// 发布性能指标的便捷方法
        /// </summary>
        public void PublishMetric(string metricName, string category, double value, string unit, object tags = null)
        {
            if (performanceMetricChannel == null) return;

            var metricData = new PerformanceMetricEventData
            {
                metricName = metricName,
                category = category,
                value = value,
                unit = unit,
                timestamp = DateTime.UtcNow,
                tags = tags
            };

            performanceMetricChannel.Publish(metricData);
        }
    }
}
