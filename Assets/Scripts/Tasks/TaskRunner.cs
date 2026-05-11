using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR;
using Unity.XR.CoreUtils;
using VRPerception.Infra;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;
using VRPerception.Tasks;
using VRPerception.UI;

namespace VRPerception.Tasks
{
    /// <summary>
    /// 任务运行器：负责任务生命周期、Trial 执行、结果收集与基础评测编排
    /// </summary>
    public class TaskRunner : MonoBehaviour
    {
        private enum MllmCaptureMode
        {
            Single,
            MultiFrame,
            Video
        }

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
            public string preTaskMessage;
            public string humanInputPrompt;
            // 跨被试均匀采样：被试 ID（用于 TrialBalancer 派生 participantIndex）
            public string experimentId;
            public string participantId;
        }

        [Header("References")]
        [SerializeField] private EventBusManager eventBus;
        [SerializeField] private PerceptionSystem perception;
        [SerializeField] private StimulusCapture stimulus;
        [SerializeField] private HumanReferenceFrameService humanReferenceFrame;
        [SerializeField] private PicoHumanTelemetryRecorder humanTelemetryRecorder;
        [SerializeField] private TrialObjectCsvRecorder trialObjectCsvRecorder;
        [SerializeField] private ObjectPlacer objectPlacer;
        [SerializeField] private XROrigin xrOrigin;

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

        [Header("MLLM Capture")]
        [Tooltip("MLLM 采集模式：单帧、多帧离散采样、连续视频")]
        [SerializeField] private MllmCaptureMode mllmCaptureMode = MllmCaptureMode.Single;
        [Tooltip("MLLM 抓帧数量：多帧模式下使用")]
        [SerializeField] private int mllmFrameCount = 1;
        [Tooltip("MLLM 水平扫描范围（度），实际为 ±该值")]
        [SerializeField] private float mllmScanYawRangeDeg = 4f;
        [Tooltip("MLLM 俯仰扫描范围（度），实际为 ±该值")]
        [SerializeField] private float mllmScanPitchRangeDeg = 2f;
        [Tooltip("MLLM 每次转角后等待毫秒数，减小运动模糊")]
        [SerializeField] private int mllmScanSettleMs = 30;
        [Tooltip("MLLM 扫描随机种子；0 表示自动派生")]
        [SerializeField] private int mllmScanSeed = 0;
        [Tooltip("MLLM 视频采样帧率")]
        [SerializeField] private int mllmVideoFps = 10;
        [Tooltip("MLLM 视频采样时长（毫秒）")]
        [SerializeField] private int mllmVideoDurationMs = 1500;

        [Header("Subject")]
        [Tooltip("当前回合被试模式（Human 模式将由 UI 采集人类答案）")]
        [SerializeField] private SubjectMode subjectMode = SubjectMode.MLLM;

        [Header("Human Fixation Guidance")]
        [SerializeField] private bool showHumanPreFixationPrompt = true;
        [SerializeField] private float humanPreFixationPromptSeconds = 4f;
        [SerializeField] private string humanPreFixationTitle = "注视标定准备";
        [TextArea]
        [SerializeField] private string humanPreFixationMessage = "请看向即将出现的红色\n注视点，并保持头部稳定。";

        [Header("Human Start Confirmation")]
        [SerializeField] private bool waitForHumanStartConfirmationAfterFixation = true;
        [SerializeField] private KeyCode editorStartKey = KeyCode.Space;
        [SerializeField] private string humanStartConfirmationTitle = "准备开始正式实验";
        [TextArea]
        [SerializeField] private string humanStartConfirmationFallbackMessage = "按右手柄 A 键开始实验。";

        private ITask _task;
        private CancellationTokenSource _runCts;
        private string _overrideTaskId;
        private string _preTaskMessage;
        private string _humanInputPrompt;
        private string _runId;
        // 跨被试均匀采样：当前被试 ID（由 ApplyRunConfig 注入；为空时回退到设备 ID）
        private string _experimentId;
        private string _participantId;
 
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

        public SubjectMode CurrentSubjectMode => subjectMode;

        public string CurrentRunId => _runId;

        public bool IsRunning => _runCts != null;

        private void Awake()
        {
            if (eventBus == null) eventBus = EventBusManager.Instance;
            if (perception == null) perception = GetComponent<PerceptionSystem>();
            if (stimulus == null) stimulus = GetComponent<StimulusCapture>();
            if (GetComponent<PicoHumanTelemetryRecorder>() == null)
            {
                gameObject.AddComponent<PicoHumanTelemetryRecorder>();
            }
            if (humanTelemetryRecorder == null)
            {
                humanTelemetryRecorder = GetComponent<PicoHumanTelemetryRecorder>();
            }
            if (trialObjectCsvRecorder == null)
            {
                if (GetComponent<TrialObjectCsvRecorder>() == null)
                {
                    gameObject.AddComponent<TrialObjectCsvRecorder>();
                }
                trialObjectCsvRecorder = GetComponent<TrialObjectCsvRecorder>();
            }
            if (objectPlacer == null)
            {
                objectPlacer = GetComponent<ObjectPlacer>();
            }
            if (objectPlacer == null)
            {
                objectPlacer = FindObjectOfType<ObjectPlacer>();
            }
            if (humanReferenceFrame == null)
            {
                humanReferenceFrame = GetComponent<HumanReferenceFrameService>();
            }
            if (humanReferenceFrame == null)
            {
                humanReferenceFrame = gameObject.AddComponent<HumanReferenceFrameService>();
            }
            if (xrOrigin == null)
            {
                xrOrigin = FindObjectOfType<XROrigin>();
            }

            Context.runner = this;
            Context.eventBus = eventBus;
            Context.perception = perception;
            Context.stimulus = stimulus;
            Context.humanReferenceFrame = humanReferenceFrame;
            Context.trialObjectCsvRecorder = trialObjectCsvRecorder;
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
            _runId = LogSessionPaths.GetOrCreateSessionId("VRP_Logs");

            var trials = _task.BuildTrials(randomSeed) ?? Array.Empty<TrialSpec>();
            for (int i = 0; i < trials.Length; i++)
            {
                if (trials[i] != null)
                {
                    trials[i].originalTrialIndex = i;
                }
            }

            // 跨被试均匀采样：仅当显式给出 maxTrials > 0 且 trials 多于目标数时启用，
            // 否则保持原行为（全量执行 BuildTrials 返回的全部 trials）。
            if (subjectMode == SubjectMode.Human && maxTrials > 0 && trials.Length > maxTrials)
            {
                string effectiveParticipantId = !string.IsNullOrWhiteSpace(_participantId)
                    ? _participantId
                    : SystemInfo.deviceUniqueIdentifier;
                if (!TryResolveExperimentParticipantIndex(_experimentId, out var experimentNumber, out var participantIndex))
                {
                    Debug.LogError("[TaskRunner] Human trial sampling requires a positive numeric experimentId.");
                    return;
                }

                Func<TrialSpec, string> stratumKeyFn;
                string taskSalt;
                if (_task is IStratifiableTask stratifiable)
                {
                    stratumKeyFn = stratifiable.GetStratumKey;
                    taskSalt = stratifiable.GetTaskBalancerSalt() ?? _task.TaskId ?? "default";
                }
                else
                {
                    // 未实现分层接口的任务回退为单层均匀采样
                    stratumKeyFn = _ => "_default";
                    taskSalt = _task.TaskId ?? "default";
                }

                int orderSeed = unchecked(randomSeed
                    ^ TrialBalancer.StableHash(_experimentId ?? "")
                    ^ TrialBalancer.StableHash(taskSalt));

                trials = TrialBalancer.BalanceAndSample(
                    trials,
                    maxTrials,
                    participantIndex,
                    taskSalt,
                    stratumKeyFn,
                    orderSeed,
                    out var balanceReport);

                eventBus?.PublishMetric("trial_balancer_sample", "trial", trials.Length, "count", new
                {
                    taskId = _task.TaskId,
                    experimentId = _experimentId,
                    experimentNumber,
                    participantId = effectiveParticipantId,
                    participantIndex,
                    target = maxTrials,
                    poolSize = balanceReport.totalPoolSize,
                    protectedCount = balanceReport.totalProtected,
                    sampled = balanceReport.totalSampled,
                    quotas = balanceReport.stratumQuota,
                    offsets = balanceReport.stratumOffset,
                    sizes = balanceReport.stratumSize
                });
            }

            _runCts = new CancellationTokenSource();
            bool runLifecycleBegun = false;

            try
            {
                // Wait briefly for EventBus channels to be created by EventBusBootstrap (handles late initialization)
                var ebDeadline = DateTime.UtcNow.AddMilliseconds(2000);
                while ((eventBus == null || eventBus.TrialLifecycle == null || eventBus.InferenceReceived == null) && DateTime.UtcNow < ebDeadline)
                {
                    await Task.Yield();
                    if (_runCts.IsCancellationRequested) throw new OperationCanceledException();
                }

                bool didHumanCalibration = await RunHumanReferenceCalibrationIfNeededAsync(_task, _runCts.Token);
                await WaitForHumanStartConfirmationIfNeededAsync(_task, didHumanCalibration, _runCts.Token);

                if (_task is ITaskRunLifecycle lifecycle)
                {
                    await lifecycle.OnRunBeginAsync(_runCts.Token);
                    runLifecycleBegun = true;
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
                        RecordTrialObjects(trial, i);

                        PublishTrialState(trial, TrialLifecycleState.Started, trialConfig: trial);

                        trialStartUtc = DateTime.UtcNow;

                        LLMResponse finalResponse = null;

                        if (subjectMode == SubjectMode.MLLM)
                        {
                            PublishTrialState(trial, TrialLifecycleState.Processing, trialConfig: trial);

                            if (_task is ITemporalInferenceTask temporalTask)
                            {
                                finalResponse = await temporalTask.RunTemporalMllmInferenceAsync(trial, _runCts.Token);
                            }
                            else
                            {
                                var captureMode = ResolveCaptureMode();
                                var trajectoryMode = ResolveTrajectoryMode(captureMode);

                                var captureOptions = new FrameCaptureOptions
                                {
                                    captureMode = captureMode,
                                    trajectoryMode = trajectoryMode,
                                    fov = trial.fovDeg > 0 ? trial.fovDeg : 60f,
                                    width = 1280,
                                    height = 720,
                                    format = "jpeg",
                                    quality = 75,
                                    includeMetadata = true,
                                    frameCount = captureMode == CaptureMode.SingleImage ? 1 : Mathf.Max(1, mllmFrameCount),
                                    scanYawRangeDeg = Mathf.Max(0f, mllmScanYawRangeDeg),
                                    scanPitchRangeDeg = Mathf.Max(0f, mllmScanPitchRangeDeg),
                                    scanSettleMs = Mathf.Max(0, mllmScanSettleMs),
                                    scanSeed = mllmScanSeed,
                                    videoFps = Mathf.Max(1, mllmVideoFps),
                                    videoDurationMs = Mathf.Max(200, mllmVideoDurationMs),
                                    videoFormat = "mp4"
                                };

                                finalResponse = await perception.RequestInferenceAsync(
                                    trial.taskId,
                                    trial.trialId,
                                    _task.GetSystemPrompt(),
                                    _task.BuildTaskPrompt(trial),
                                    null,
                                    captureOptions,
                                    _runCts.Token
                                );
                            }

                            // 若返回动作计划，进入一次闭环等待：等待下一次 inference
                            if (enableActionPlanLoop && finalResponse != null && finalResponse.type == "action_plan")
                            {
                                var followUp = await WaitForInferenceAsync(trial.taskId, trial.trialId, actionPlanLoopTimeoutMs, _runCts.Token);
                                if (followUp != null) finalResponse = followUp;
                            }
                        }
                        else
                        {
                            // Human 模式：等待必要的曝光/遮罩时序后，发布 WaitingForInput，UI 负责收集答案
                            if (_task is ITemporalInferenceTask temporalTask)
                            {
                                await temporalTask.RunTemporalHumanPresentationAsync(trial, _runCts.Token);
                            }
                            else if (_task is NumerosityComparisonTask numerosityTask)
                            {
                                await numerosityTask.RunFixedExposureHumanPresentationAsync(trial, _runCts.Token);
                            }
                            else if (_task is VisualCrowdingTask visualCrowdingTask)
                            {
                                await visualCrowdingTask.RunFixedExposureHumanPresentationAsync(trial, _runCts.Token);
                            }
                            else if (RequiresManualHumanAnswerPhase(_task.TaskId))
                            {
                                await WaitForHumanAnswerPhaseConfirmationAsync(trial, _runCts.Token);
                            }

                            PublishTrialState(trial, TrialLifecycleState.WaitingForInput, trialConfig: trial, error: "Waiting for input");

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
                        // 确保取消时也执行清理，移除已生成的场景物体
                        try
                        {
                            if (_task != null)
                            {
                                await _task.OnAfterTrialAsync(trial, null, CancellationToken.None);
                            }
                        }
                        catch (Exception cleanupEx)
                        {
                            Debug.LogWarning($"[TaskRunner] Cleanup after cancellation failed: {cleanupEx.Message}");
                        }
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
                        try
                        {
                            if (_task != null)
                            {
                                await _task.OnAfterTrialAsync(trial, null, CancellationToken.None);
                            }
                        }
                        catch (Exception cleanupEx)
                        {
                            Debug.LogWarning($"[TaskRunner] Cleanup after failure failed: {cleanupEx.Message}");
                        }
                    }
                }
            }
            finally
            {
                if (runLifecycleBegun && _task is ITaskRunLifecycle lifecycleEnd)
                {
                    try { await lifecycleEnd.OnRunEndAsync(CancellationToken.None); } catch { }
                }

                humanReferenceFrame?.Clear();

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
        }

        public void CancelRun()
        {
            _runCts?.Cancel();
        }

        public void SetHumanTelemetryTrialSubphase(string subphase)
        {
            humanTelemetryRecorder?.SetTrialSubphase(subphase);
        }

        private CaptureMode ResolveCaptureMode()
        {
            return mllmCaptureMode switch
            {
                MllmCaptureMode.MultiFrame => CaptureMode.MultiImage,
                MllmCaptureMode.Video => CaptureMode.Video,
                _ => CaptureMode.SingleImage
            };
        }

        private static CaptureTrajectoryMode ResolveTrajectoryMode(CaptureMode captureMode)
        {
            return captureMode switch
            {
                CaptureMode.MultiImage => CaptureTrajectoryMode.RandomJitter,
                CaptureMode.Video => CaptureTrajectoryMode.Sweep,
                _ => CaptureTrajectoryMode.Fixed
            };
        }

        private async Task<bool> RunHumanReferenceCalibrationIfNeededAsync(ITask task, CancellationToken ct)
        {
            if (subjectMode != SubjectMode.Human) return false;
            if (task == null) return false;
            bool requiresCalibration =
                string.Equals(task.TaskId, "distance_compression", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(task.TaskId, "change_detection", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(task.TaskId, "color_constancy_adjustment", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(task.TaskId, "depth_jnd_staircase", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(task.TaskId, "horizon_cue_integration", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(task.TaskId, "material_roughness", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(task.TaskId, "material_roughness_motion", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(task.TaskId, "numerosity_comparison", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(task.TaskId, "visual_crowding", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(task.TaskId, "visual_weight_judgment", StringComparison.OrdinalIgnoreCase);
            if (!requiresCalibration) return false;
            if (humanReferenceFrame == null) return false;

            var headCamera = stimulus != null ? stimulus.HeadCamera : Camera.main;
            if (headCamera == null)
            {
                Debug.LogWarning("[TaskRunner] Human fixation calibration skipped: HeadCamera is missing. Falling back to task-local reference.");
                return false;
            }

            TryResetTrialBlackoutOverlay();
            await ShowHumanPreFixationPromptIfNeededAsync(ct);

            Transform xrRigTransform = ResolveHumanCalibrationRigTransform();
            humanTelemetryRecorder?.StartCalibrationRecording(task.TaskId);
            bool calibrated;
            try
            {
                calibrated = await humanReferenceFrame.CalibrateAsync(
                    headCamera,
                    xrRigTransform,
                    ct,
                    subphase => humanTelemetryRecorder?.SetCalibrationSubphase(subphase));
            }
            finally
            {
                humanTelemetryRecorder?.StopCalibrationRecording();
            }

            return calibrated;
        }

        private async Task ShowHumanPreFixationPromptIfNeededAsync(CancellationToken ct)
        {
            if (!showHumanPreFixationPrompt) return;
            if (humanPreFixationPromptSeconds <= 0f) return;

            var panel = FindTaskMessagePanel();
            if (panel == null)
            {
                Debug.LogWarning("[TaskRunner] WSTaskMessagePanel not found; human pre-fixation prompt will not be shown.");
                return;
            }

            panel.ShowManualMessage(humanPreFixationTitle, humanPreFixationMessage);
            int delayMs = Mathf.RoundToInt(humanPreFixationPromptSeconds * 1000f);
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, ct);
            }
            panel.HideManualMessage();
        }

        private async Task WaitForHumanStartConfirmationIfNeededAsync(ITask task, bool didHumanCalibration, CancellationToken ct)
        {
            if (!waitForHumanStartConfirmationAfterFixation) return;
            if (!didHumanCalibration) return;
            if (subjectMode != SubjectMode.Human) return;

            var panel = FindTaskMessagePanel();
            if (panel != null)
            {
                panel.ShowManualMessage(humanStartConfirmationTitle, BuildHumanStartConfirmationMessage());
            }
            else
            {
                Debug.LogWarning("[TaskRunner] WSTaskMessagePanel not found; waiting for human start confirmation without VR prompt.");
            }

            Debug.Log($"[TaskRunner] Human fixation completed for {task?.TaskId}. Press right controller A / primaryButton to start formal trials.");

            bool lastPressed = IsHumanStartConfirmationPressed();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                bool pressed = IsHumanStartConfirmationPressed();
                if (pressed && !lastPressed)
                {
                    break;
                }

                lastPressed = pressed;
                await Task.Yield();
            }

            while (IsHumanStartConfirmationPressed())
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            await Task.Delay(250, ct);

            panel?.HideManualMessage();
        }

        private string BuildHumanStartConfirmationMessage()
        {
            string taskMessage = string.IsNullOrWhiteSpace(_preTaskMessage)
                ? humanStartConfirmationFallbackMessage
                : _preTaskMessage.Trim() + "\n\n" + (humanStartConfirmationFallbackMessage ?? string.Empty);

            return taskMessage ?? string.Empty;
        }

        private bool IsHumanStartConfirmationPressed()
        {
#if UNITY_EDITOR
            if (Input.GetKey(editorStartKey)) return true;
#endif
            var rightHandDevices = new List<InputDevice>(2);
            InputDevices.GetDevicesAtXRNode(XRNode.RightHand, rightHandDevices);
            for (int i = 0; i < rightHandDevices.Count; i++)
            {
                var device = rightHandDevices[i];
                if (!device.isValid) continue;
                if (device.TryGetFeatureValue(CommonUsages.primaryButton, out bool pressed) && pressed)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool RequiresManualHumanAnswerPhase(string taskId)
        {
            return string.Equals(taskId, "distance_compression", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(taskId, "depth_jnd_staircase", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(taskId, "horizon_cue_integration", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(taskId, "visual_weight_judgment", StringComparison.OrdinalIgnoreCase);
        }

        private async Task WaitForHumanAnswerPhaseConfirmationAsync(TrialSpec trial, CancellationToken ct)
        {
            string taskId = trial != null ? trial.taskId : _task?.TaskId;
            Debug.Log($"[TaskRunner] Waiting for left controller X / primaryButton to enter answer phase. task={taskId}, trial={trial?.trialId ?? -1}");

            bool lastPressed = IsHumanAnswerPhaseConfirmationPressed();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                bool pressed = IsHumanAnswerPhaseConfirmationPressed();
                if (pressed && !lastPressed)
                {
                    break;
                }

                lastPressed = pressed;
                await Task.Yield();
            }

            while (IsHumanAnswerPhaseConfirmationPressed())
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            await Task.Delay(250, ct);
        }

        private static bool IsHumanAnswerPhaseConfirmationPressed()
        {
            var leftHandDevices = new List<InputDevice>(2);
            InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, leftHandDevices);
            for (int i = 0; i < leftHandDevices.Count; i++)
            {
                var device = leftHandDevices[i];
                if (!device.isValid) continue;
                if (device.TryGetFeatureValue(CommonUsages.primaryButton, out bool pressed) && pressed)
                {
                    return true;
                }
            }

            return false;
        }

        private static WSTaskMessagePanel FindTaskMessagePanel()
        {
            var panel = UnityEngine.Object.FindObjectOfType<WSTaskMessagePanel>();
            if (panel != null) return panel;

            var all = Resources.FindObjectsOfTypeAll<WSTaskMessagePanel>();
            if (all == null) return null;

            for (int i = 0; i < all.Length; i++)
            {
                var candidate = all[i];
                if (candidate == null) continue;
                var go = candidate.gameObject;
                if (go == null) continue;
                if (!go.scene.IsValid()) continue;
                return candidate;
            }

            return null;
        }

        private Transform ResolveHumanCalibrationRigTransform()
        {
            if (xrOrigin == null)
            {
                xrOrigin = FindObjectOfType<XROrigin>();
            }

            if (xrOrigin != null)
            {
                return xrOrigin.transform;
            }

            Debug.LogWarning("[TaskRunner] XROrigin not found. Human fixation calibration will fall back to HeadCamera forward.");
            return null;
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

            _preTaskMessage = config.preTaskMessage;

            if (!string.IsNullOrWhiteSpace(config.humanInputPrompt))
            {
                _humanInputPrompt = config.humanInputPrompt;
            }

            _experimentId = string.IsNullOrWhiteSpace(config.experimentId) ? null : config.experimentId.Trim();

            if (!string.IsNullOrWhiteSpace(config.participantId))
            {
                _participantId = config.participantId;
            }

            var resolvedTaskId = config.taskId;

            if (!string.IsNullOrWhiteSpace(resolvedTaskId))
            {
                _overrideTaskId = resolvedTaskId;

                // 尝试同步 TaskMode 以便 UI / 导出工具展示（仅对已知任务生效）
                // 尝试根据 taskId 对齐枚举，便于 Inspector 下拉框保持同步
                bool matched = false;
                foreach (TaskMode mode in Enum.GetValues(typeof(TaskMode)))
                {
                    var mapped = TaskModeToTaskId(mode);
                    if (!string.IsNullOrEmpty(mapped) && string.Equals(resolvedTaskId, mapped, StringComparison.OrdinalIgnoreCase))
                    {
                        taskMode = mode;
                        matched = true;
                        break;
                    }
                }

                if (!matched && Enum.TryParse(resolvedTaskId, true, out TaskMode parsedMode))
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

        private static bool TryResolveExperimentParticipantIndex(string experimentId, out int experimentNumber, out int participantIndex)
        {
            experimentNumber = 0;
            participantIndex = 0;

            if (string.IsNullOrWhiteSpace(experimentId))
            {
                return false;
            }

            var value = experimentId.Trim();
            for (int i = 0; i < value.Length; i++)
            {
                if (!char.IsDigit(value[i])) return false;
            }

            if (!int.TryParse(value, out experimentNumber) || experimentNumber < 1)
            {
                return false;
            }

            participantIndex = experimentNumber - 1;
            return true;
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
                TaskMode.RelativeDepthOrder => "relative_depth_order",
                TaskMode.ChangeDetection => "change_detection",
                TaskMode.OcclusionReasoning => "occlusion_reasoning",
                TaskMode.ColorConstancy => "color_constancy",
                TaskMode.MaterialPerception => "material_perception",
                TaskMode.NumerosityComparison => "numerosity_comparison",
                TaskMode.VisualSearch => "visual_search",
                TaskMode.ObjectCounting => "object_counting",
                TaskMode.DepthJndStaircase => "depth_jnd_staircase",
                TaskMode.HorizonCueIntegration => "horizon_cue_integration",
                TaskMode.VisualCrowding => "visual_crowding",
                TaskMode.VisualWeightJudgment => "visual_weight_judgment",
                TaskMode.ColorConstancyAdjustment => "color_constancy_adjustment",
                TaskMode.MaterialRoughnessAmbiguity => "material_roughness",
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

        private void RecordTrialObjects(TrialSpec trial, int trialExecutionIndex)
        {
            if (trialObjectCsvRecorder == null || trial == null) return;
            if (string.Equals(trial.taskId, "change_detection", StringComparison.OrdinalIgnoreCase)) return;

            trialObjectCsvRecorder.RecordTrialObjects(
                _runId,
                subjectMode,
                randomSeed,
                trialExecutionIndex,
                trial);
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

        private static bool TryArmTrialBlackoutOverlay(int delayMs)
        {
            try
            {
                var overlay = FindTrialBlackoutOverlay();
                if (overlay == null) return false;

                if (!overlay.enabled) overlay.enabled = true;
                if (!overlay.gameObject.activeInHierarchy) overlay.gameObject.SetActive(true);

                overlay.BeginBlackoutAfterMs(delayMs);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TryResetTrialBlackoutOverlay()
        {
            try
            {
                var overlay = FindTrialBlackoutOverlay();
                if (overlay == null) return;

                if (!overlay.enabled) overlay.enabled = true;
                if (!overlay.gameObject.activeInHierarchy) overlay.gameObject.SetActive(true);
                overlay.ResetBlackout();
            }
            catch { }
        }

        private static TrialBlackoutOverlay FindTrialBlackoutOverlay()
        {
            var overlay = TrialBlackoutOverlay.Instance;
            if (overlay != null) return overlay;

            overlay = UnityEngine.Object.FindObjectOfType<TrialBlackoutOverlay>();
            if (overlay != null) return overlay;

            var all = Resources.FindObjectsOfTypeAll<TrialBlackoutOverlay>();
            if (all == null) return null;

            for (int i = 0; i < all.Length; i++)
            {
                var candidate = all[i];
                if (candidate == null) continue;
                var go = candidate.gameObject;
                if (go == null) continue;
                if (!go.scene.IsValid()) continue;
                return candidate;
            }

            return null;
        }
    }

    public enum TaskMode
    {
        DistanceCompression,
        SemanticSizeBias,
        RelativeDepthOrder,
        ChangeDetection,
        OcclusionReasoning,
        ColorConstancy,
        MaterialPerception,
        NumerosityComparison,
        VisualSearch,
        ObjectCounting,
        DepthJndStaircase,
        HorizonCueIntegration,
        VisualCrowding,
        ColorConstancyAdjustment,
        MaterialRoughnessAmbiguity,
        VisualWeightJudgment
    }

    public enum SubjectMode
    {
        MLLM,
        Human
    }
}
