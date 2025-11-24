using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;
using VRPerception.Tasks;

namespace VRPerception.Tasks
{
    /// <summary>
    /// 任务运行器：负责任务生命周期、Trial 执行、结果收集与基础评测编排
    /// </summary>
    public class TaskRunner : MonoBehaviour
    {
        [Serializable]
        public class TaskRunConfig
        {
            public string taskId;
            public SubjectMode? subjectMode;
            public bool forceHumanInput;
            public int? randomSeed;
            public int? maxTrials;
            public bool? enableActionPlanLoop;
            public int? actionPlanLoopTimeoutMs;
            public int? humanInputTimeoutMs;
            public string humanInputPrompt;
        }

        [Header("References")]
        [SerializeField] private EventBusManager eventBus;
        [SerializeField] private PerceptionSystem perception;
        [SerializeField] private StimulusCapture stimulus;

        [Header("Execution")]
        [SerializeField] private bool autoRun = true;
        [SerializeField] private TaskMode taskMode = TaskMode.DistanceCompression;
        [SerializeField] private int randomSeed = 12345;
        [SerializeField] private int maxTrials = 0; // 0 = use task default
        [Tooltip("当模型返回 action_plan 时，等待一次后续 inference 作为最终答案")]
        [SerializeField] private bool enableActionPlanLoop = true;
        [SerializeField] private int actionPlanLoopTimeoutMs = 20000;
        [Tooltip("Human 模式下等待用户输入的超时时间（毫秒），0 表示无限等待")]
        [SerializeField] private int humanInputTimeoutMs = 0; // 0 = 无限等待

        [Header("Subject")]
        [Tooltip("当前回合被试模式（Human 模式将由 UI 采集人类答案）")]
        [SerializeField] private SubjectMode subjectMode = SubjectMode.MLLM;

        private ITask _task;
        private CancellationTokenSource _runCts;
        private string _overrideTaskId;
        private string _humanInputPrompt;
 
        public TaskRunnerContext Context { get; private set; } = new TaskRunnerContext();

        public bool AutoRun
        {
            get => autoRun;
            set => autoRun = value;
        }

        /// <summary>
        /// 当前用于 BuildTrials 的随机种子（可能由 Playlist/Orchestrator 覆写）。
        /// </summary>
        public int CurrentRandomSeed => randomSeed;

        /// <summary>
        /// 当前最大试次数（0 表示使用任务默认值）。
        /// </summary>
        public int CurrentMaxTrials => maxTrials;

        /// <summary>
        /// 当前配置的任务 ID（如有 Playlist/Orchestrator，则通常为其传入的 taskId）。
        /// </summary>
        public string CurrentConfiguredTaskId =>
            !string.IsNullOrWhiteSpace(_overrideTaskId) ? _overrideTaskId : TaskModeToTaskId(taskMode);

        /// <summary>
        /// 当前配置的 TaskMode（用于调试与导出工具）。
        /// </summary>
        public TaskMode CurrentTaskMode => taskMode;

        public bool IsRunning => _runCts != null;

        private void Awake()
        {
            if (eventBus == null) eventBus = EventBusManager.Instance;
            if (perception == null) perception = GetComponent<PerceptionSystem>();
            if (stimulus == null) stimulus = GetComponent<StimulusCapture>();

            Context.runner = this;
            Context.eventBus = eventBus;
            Context.perception = perception;
            Context.stimulus = stimulus;
        }

        private void Start()
        {
            if (autoRun)
            {
                _ = RunAsync();
            }
        }

        private void OnDestroy()
        {
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = null;
        }

        public async Task RunAsync()
        {
            if (_runCts != null)
            {
                Debug.LogWarning("[TaskRunner] Run already in progress");
                return;
            }

            string resolvedTaskId = !string.IsNullOrWhiteSpace(_overrideTaskId)
                ? _overrideTaskId
                : TaskModeToTaskId(taskMode);

            if (!string.IsNullOrWhiteSpace(resolvedTaskId) && TryCreateTaskById(resolvedTaskId, out _task))
            {
                resolvedTaskId = _task.TaskId;
            }
            else if (TryCreateTask(taskMode, out _task))
            {
                resolvedTaskId = _task.TaskId;
            }
            else
            {
                Debug.LogError($"[TaskRunner] Unable to create task. mode={taskMode}, overrideId={_overrideTaskId}");
                return;
            }

            _task.Initialize(this, eventBus);

            var trials = _task.BuildTrials(randomSeed) ?? Array.Empty<TrialSpec>();
            if (maxTrials > 0 && trials.Length > maxTrials)
            {
                Array.Resize(ref trials, maxTrials);
            }

            _runCts = new CancellationTokenSource();

            // Wait briefly for EventBus channels to be created by EventBusBootstrap (handles late initialization)
            var ebDeadline = DateTime.UtcNow.AddMilliseconds(2000);
            while ((eventBus == null || eventBus.TrialLifecycle == null || eventBus.InferenceReceived == null) && DateTime.UtcNow < ebDeadline)
            {
                await Task.Yield();
                if (_runCts.IsCancellationRequested) throw new OperationCanceledException();
            }

            for (int i = 0; i < trials.Length; i++)
            {
                var trial = trials[i];
                trial.trialId = i;       // 规范化编号
                trial.taskId = _task.TaskId;

                // 记录 trial 起始时间并在 catch 中可见
                DateTime trialStartUtc = DateTime.UtcNow;
                double trialElapsedMs = 0;

                try
                {
                    PublishTrialState(trial, TrialLifecycleState.Initialized, trialConfig: trial);

                    // 前置布置
                    PublishTrialState(trial, TrialLifecycleState.SceneSetup, trialConfig: trial);
                    await _task.OnBeforeTrialAsync(trial, _runCts.Token);

                    PublishTrialState(trial, TrialLifecycleState.Started, trialConfig: trial);

                    trialStartUtc = DateTime.UtcNow;

                    LLMResponse finalResponse = null;

                    if (subjectMode == SubjectMode.MLLM)
                    {
                        // 一次推理
                        var captureOptions = new FrameCaptureOptions
                        {
                            fov = trial.fovDeg > 0 ? trial.fovDeg : 60f,
                            width = 1280,
                            height = 720,
                            format = "jpeg",
                            quality = 75,
                            includeMetadata = true
                        };

                        // 进入处理阶段（模型推理中）
                        PublishTrialState(trial, TrialLifecycleState.Processing, trialConfig: trial);

                        finalResponse = await perception.RequestInferenceAsync(
                            trial.taskId,
                            trial.trialId,
                            _task.GetSystemPrompt(),
                            _task.BuildTaskPrompt(trial),
                            _task.GetTools(),
                            captureOptions,
                            _runCts.Token
                        );

                        // 若返回动作计划，进入一次闭环等待：等待下一次 inference
                        if (enableActionPlanLoop && finalResponse != null && finalResponse.type == "action_plan")
                        {
                            var followUp = await WaitForInferenceAsync(trial.taskId, trial.trialId, actionPlanLoopTimeoutMs, _runCts.Token);
                            if (followUp != null) finalResponse = followUp;
                        }
                    }
                    else
                    {
                        // Human 模式：此处仅发布 WaitingForInput，UI 负责收集答案与生成 LLMResponse 的等价结构
                        PublishTrialState(trial, TrialLifecycleState.WaitingForInput, trialConfig: trial);
                        // 使用专门的 humanInputTimeoutMs，0 表示无限等待
                        int timeout = humanInputTimeoutMs > 0 ? humanInputTimeoutMs : int.MaxValue;
                        finalResponse = await WaitForInferenceAsync(trial.taskId, trial.trialId, timeout, _runCts.Token);
                    }

                    // 如果未收到任何响应（超时或通道不可用），标记失败
                    // ⚠️ 重要：即使超时，也要执行清理逻辑，避免物体残留
                    if (finalResponse == null)
                    {
                        PublishTrialState(trial, TrialLifecycleState.Failed, trialConfig: trial, error: "No inference received within timeout or channel unavailable");

                        // 执行清理逻辑（即使没有响应）
                        await _task.OnAfterTrialAsync(trial, null, _runCts.Token);

                        // 采样指标：trial 耗时（超时/无响应）
                        trialElapsedMs = (DateTime.UtcNow - trialStartUtc).TotalMilliseconds;
                        eventBus?.PublishMetric("trial_duration_ms", "trial", trialElapsedMs, "ms",
                            new { taskId = trial.taskId, trialId = trial.trialId, subject = subjectMode.ToString(), status = "timeout" });
                        continue;
                    }

                    // 后置清理 / 记录
                    await _task.OnAfterTrialAsync(trial, finalResponse, _runCts.Token);

                    // 评测
                    var eval = _task.Evaluate(trial, finalResponse);
                    PublishTrialState(trial, TrialLifecycleState.Completed, trialConfig: trial, results: eval);
                    // 采样指标：trial 耗时（完成）
                    trialElapsedMs = (DateTime.UtcNow - trialStartUtc).TotalMilliseconds;
                    eventBus?.PublishMetric("trial_duration_ms", "trial", trialElapsedMs, "ms",
                        new { taskId = trial.taskId, trialId = trial.trialId, subject = subjectMode.ToString(), status = "completed", actionPlanLoop = enableActionPlanLoop });
                }
                catch (OperationCanceledException)
                {
                    PublishTrialState(trial, TrialLifecycleState.Cancelled, trialConfig: trial, error: "Cancelled");
                    // 采样指标：trial 耗时（取消）
                    trialElapsedMs = (DateTime.UtcNow - trialStartUtc).TotalMilliseconds;
                    eventBus?.PublishMetric("trial_duration_ms", "trial", trialElapsedMs, "ms",
                        new { taskId = trial.taskId, trialId = trial.trialId, subject = subjectMode.ToString(), status = "cancelled" });
                    break;
                }
                catch (Exception ex)
                {
                    //Debug.LogError($"[TaskRunner] Trial failed: {ex.Message}");
                    PublishTrialState(trial, TrialLifecycleState.Failed, trialConfig: trial, error: ex.Message);
                    // 采样指标：trial 耗时（失败）
                    trialElapsedMs = (DateTime.UtcNow - trialStartUtc).TotalMilliseconds;
                    eventBus?.PublishMetric("trial_duration_ms", "trial", trialElapsedMs, "ms",
                        new { taskId = trial.taskId, trialId = trial.trialId, subject = subjectMode.ToString(), status = "failed" });
                }
            }

            // 运行结束后请求一次日志刷盘（与 Orchestrator 的 checkpoint 刷盘相互独立，二者兼容）
            try
            {
                eventBus?.LogFlush?.Publish(new LogFlushEventData
                {
                    requestId = Guid.NewGuid().ToString("N"),
                    timestamp = DateTime.UtcNow,
                    logType = "run",
                    forceFlush = true
                });
            }
            catch { }

            _runCts.Dispose();
            _runCts = null;
        }

        public void CancelRun()
        {
            _runCts?.Cancel();
        }
        public void ApplyRunConfig(TaskRunConfig config)
        {
            if (config == null) return;

            _overrideTaskId = null;

            if (config.subjectMode.HasValue)
            {
                subjectMode = config.subjectMode.Value;
            }

            if (config.forceHumanInput)
            {
                subjectMode = SubjectMode.Human;
            }

            if (config.randomSeed.HasValue)
            {
                randomSeed = config.randomSeed.Value;
            }

            if (config.maxTrials.HasValue)
            {
                maxTrials = Mathf.Max(0, config.maxTrials.Value);
            }

            if (config.enableActionPlanLoop.HasValue)
            {
                enableActionPlanLoop = config.enableActionPlanLoop.Value;
            }

            if (config.actionPlanLoopTimeoutMs.HasValue)
            {
                actionPlanLoopTimeoutMs = Mathf.Max(1000, config.actionPlanLoopTimeoutMs.Value);
            }

            if (config.humanInputTimeoutMs.HasValue)
            {
                humanInputTimeoutMs = Mathf.Max(0, config.humanInputTimeoutMs.Value);
            }

            if (!string.IsNullOrWhiteSpace(config.humanInputPrompt))
            {
                _humanInputPrompt = config.humanInputPrompt;
            }

            var resolvedTaskId = config.taskId;

            if (!string.IsNullOrWhiteSpace(resolvedTaskId))
            {
                _overrideTaskId = resolvedTaskId;

                // 尝试同步 TaskMode 以便 UI / 导出工具展示（仅对已知任务生效）
                if (string.Equals(resolvedTaskId, "distance_compression", StringComparison.OrdinalIgnoreCase))
                {
                    taskMode = TaskMode.DistanceCompression;
                }
                else if (string.Equals(resolvedTaskId, "semantic_size_bias", StringComparison.OrdinalIgnoreCase))
                {
                    taskMode = TaskMode.SemanticSizeBias;
                }
                else if (Enum.TryParse(resolvedTaskId, true, out TaskMode parsedMode))
                {
                    taskMode = parsedMode;
                }
            }
            else
            {
                // 未传入 taskId 时，不修改当前 taskMode/_overrideTaskId，由场景内配置决定
                _overrideTaskId = null;
            }
        }

        private bool TryCreateTask(TaskMode mode, out ITask task)
        {
            task = null;

            var taskId = TaskModeToTaskId(mode);
            if (!string.IsNullOrEmpty(taskId) && global::VRPerception.Tasks.TaskRegistry.Instance.TryCreate(taskId, Context, out task))
            {
                return true;
            }

            switch (mode)
            {
                case TaskMode.DistanceCompression:
                    task = new DistanceCompressionTask(Context);
                    return true;
                case TaskMode.SemanticSizeBias:
                    task = new SemanticSizeBiasTask(Context);
                    return true;
                default:
                    task = null;
                    return false;
            }
        }

        public bool TryCreateTaskById(string taskId, out ITask task)
        {
            if (global::VRPerception.Tasks.TaskRegistry.Instance.TryCreate(taskId, Context, out task))
            {
                return true;
            }

            if (Enum.TryParse<TaskMode>(taskId, true, out var mode) && TryCreateTask(mode, out task))
            {
                return true;
            }

            task = null;
            return false;
        }

        private static string TaskModeToTaskId(TaskMode mode)
        {
            return mode switch
            {
                TaskMode.DistanceCompression => "distance_compression",
                TaskMode.SemanticSizeBias => "semantic_size_bias",
                _ => null
            };
        }

        private async Task<LLMResponse> WaitForInferenceAsync(string taskId, int trialId, int timeoutMs, CancellationToken externalToken)
        {
            // Ensure InferenceReceived channel exists (handle late EventBusBootstrap)
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while ((eventBus == null || eventBus.InferenceReceived == null) && DateTime.UtcNow < deadline)
            {
                await Task.Yield();
                if (externalToken.IsCancellationRequested) throw new OperationCanceledException();
            }
            if (eventBus?.InferenceReceived == null) return null;

            var tcs = new TaskCompletionSource<LLMResponse>();
            var localCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            localCts.CancelAfter(timeoutMs);

            void Handler(InferenceReceivedEventData data)
            {
                if (data.taskId == taskId && data.trialId == trialId)
                {
                    try { eventBus?.InferenceReceived?.Unsubscribe(Handler); } catch { }
                    tcs.TrySetResult(data.response);
                }
            }

            eventBus.InferenceReceived.Subscribe(Handler);

            try
            {
                using (localCts)
                {
                    using (localCts.Token.Register(() =>
                    {
                        try { eventBus?.InferenceReceived?.Unsubscribe(Handler); } catch { }
                        tcs.TrySetCanceled();
                    }))
                    {
                        return await tcs.Task;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        private void PublishTrialState(TrialSpec trial, TrialLifecycleState state, object trialConfig = null, object results = null, string error = null)
        {
            var data = new TrialLifecycleEventData
            {
                taskId = trial.taskId,
                trialId = trial.trialId,
                state = state,
                timestamp = DateTime.UtcNow,
                trialConfig = trialConfig,
                results = results,
                errorMessage = error,
                humanInputPrompt = _humanInputPrompt
            };
            eventBus?.TrialLifecycle?.Publish(data);

            if (!string.IsNullOrEmpty(error))
            {
                eventBus?.PublishError("TaskRunner", ErrorSeverity.Error, "TRIAL_STATE_ERROR", error,
                    new { trial.taskId, trial.trialId, state });
            }
        }
    }

    public enum TaskMode
    {
        DistanceCompression,
        SemanticSizeBias
    }

    public enum SubjectMode
    {
        MLLM,
        Human
    }
}
