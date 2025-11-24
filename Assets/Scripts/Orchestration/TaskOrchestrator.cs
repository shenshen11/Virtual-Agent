using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using VRPerception.Infra.EventBus;
using VRPerception.Tasks;

namespace VRPerception.Orchestration
{
    /// <summary>
    /// 负责驱动 TaskRunner 顺序执行 Playlist，并处理暂停/恢复/跳过/断点续跑。
    /// </summary>
    public sealed class TaskOrchestrator : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private TaskRunner taskRunner;
        [SerializeField] private EventBusManager eventBus;
        [SerializeField] private TaskPlaylist defaultPlaylist;
        [SerializeField] private UnityEvent onPlaylistCompleted;

        [Header("Options")]
        [Tooltip("是否在 Awake 时禁用 TaskRunner 的 AutoRun。")]
        [SerializeField] private bool disableRunnerAutoRun = true;

        [Tooltip("启动时自动尝试恢复上次断点。")]
        [SerializeField] private bool autoRestoreCheckpoint = true;

        [Tooltip("Playlist 完成后是否删除断点文件。")]
        [SerializeField] private bool deleteCheckpointOnPlaylistCompleted = true;

        [Tooltip("暂停/错误/条目完成时保存断点。")]
        [SerializeField] private bool saveCheckpointOnPause = true;
        [SerializeField] private bool saveCheckpointOnError = true;
        [SerializeField] private bool saveCheckpointOnEntryCompleted = true;

        [Tooltip("默认参与者ID（优先级：此字段 > Playlist.DefaultParticipantId > 设备ID）。")]
        [SerializeField] private string participantIdOverride;

        [Tooltip("当无法加载 checkpoint 时是否自动重新开始。")]
        [SerializeField] private bool autoRestartIfCheckpointMissing = true;

        private TaskPlaylist _currentPlaylist;
        private int _currentEntryIndex = -1;
        private bool _isRunning;
        private CancellationTokenSource _orchestratorCts;

        // Runtime state
        private OrchestratorLifecycleState _state = OrchestratorLifecycleState.Idle;
        private IPlaylistCheckpointStore _checkpointStore;
        private TaskRunner.TaskRunConfig _lastRunConfig;
        private int _lastObservedTrialIndex = -1;
        private string _currentTaskId;

        private bool _pauseRequested;
        private bool _skipRestRequested;
        private bool _skipEntryRequested;

        public TaskPlaylist CurrentPlaylist => _currentPlaylist;
        public int CurrentEntryIndex => _currentEntryIndex;
        public bool IsRunning => _isRunning;

        private void Awake()
        {
            if (taskRunner == null)
            {
                taskRunner = FindObjectOfType<TaskRunner>();
            }

            if (eventBus == null)
            {
                eventBus = EventBusManager.Instance;
            }

            if (disableRunnerAutoRun && taskRunner != null)
            {
                taskRunner.AutoRun = false;
            }

            _checkpointStore = new FilePlaylistCheckpointStore();
        }

        private void OnEnable()
        {
            TrySubscribeTrialLifecycle();
        }

        private void OnDisable()
        {
            TryUnsubscribeTrialLifecycle();
        }

        private void OnDestroy()
        {
            _orchestratorCts?.Cancel();
            _orchestratorCts?.Dispose();
            _orchestratorCts = null;
            TryUnsubscribeTrialLifecycle();
        }

        /// <summary>
        /// 以指定 Playlist 启动编排（若 null 则使用默认）。
        /// </summary>
        public async Task StartPlaylistAsync(TaskPlaylist playlist = null, CancellationToken externalToken = default)
        {
            if (_isRunning)
            {
                Debug.LogWarning("[TaskOrchestrator] Playlist already running.");
                return;
            }

            _orchestratorCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            _currentPlaylist = playlist ?? defaultPlaylist;

            if (_currentPlaylist == null)
            {
                Debug.LogError("[TaskOrchestrator] Playlist not assigned.");
                return;
            }

            _isRunning = true;

            try
            {
                PublishState(OrchestratorLifecycleState.Preparing, "Preparing to start playlist");

                // Attempt restore
                bool restored = false;
                if (autoRestoreCheckpoint)
                {
                    var cp = await _checkpointStore.LoadLatestAsync(_currentPlaylist.PlaylistId, GetParticipantId(), _orchestratorCts.Token);
                    if (cp != null)
                    {
                        _currentEntryIndex = Mathf.Clamp(cp.currentEntryIndex, 0, _currentPlaylist.Entries.Count - 1);
                        _lastObservedTrialIndex = Mathf.Max(-1, cp.currentTrialIndex);
                        PublishState(OrchestratorLifecycleState.RestoringCheckpoint, $"Restored checkpoint at entry {_currentEntryIndex}", isRestore: true);
                        restored = true;
                    }
                }

                if (!restored)
                {
                    _currentEntryIndex = 0;
                    if (!autoRestartIfCheckpointMissing && autoRestoreCheckpoint)
                    {
                        PublishState(OrchestratorLifecycleState.Error, "Checkpoint missing and autoRestartIfCheckpointMissing=false");
                        _isRunning = false;
                        return;
                    }
                }

                PublishState(OrchestratorLifecycleState.LoadingPlaylist, "Loading playlist");
                await RunLoopAsync(_orchestratorCts.Token);
            }
            catch (OperationCanceledException)
            {
                PublishState(OrchestratorLifecycleState.Cancelled, "Orchestrator cancelled");
            }
            catch (Exception ex)
            {
                PublishState(OrchestratorLifecycleState.Error, $"Unhandled error: {ex.Message}");
                if (saveCheckpointOnError)
                {
                    try { await SaveCheckpointAsync(entryCompleted: false, "Error occurred"); } catch { }
                }
                eventBus?.PublishError("TaskOrchestrator", ErrorSeverity.Critical, "ORCH_ERROR", ex.ToString());
            }
            finally
            {
                _isRunning = false;
                _orchestratorCts?.Dispose();
                _orchestratorCts = null;
            }
        }

        public void Cancel()
        {
            if (!_isRunning) return;

            try
            {
                _orchestratorCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            taskRunner?.CancelRun();
        }

        public void Pause()
        {
            if (!_isRunning) return;
            if (_pauseRequested) return;
            _pauseRequested = true;
            PublishState(OrchestratorLifecycleState.Paused, "Paused by user");
            taskRunner?.CancelRun();
            if (saveCheckpointOnPause)
            {
                _ = SaveCheckpointAsync(false, "Paused");
            }
        }

        public void Resume()
        {
            if (!_isRunning) return;
            if (!_pauseRequested) return;
            _pauseRequested = false;
            PublishState(OrchestratorLifecycleState.Resumed, "Resumed by user");
        }

        public void SkipRest()
        {
            _skipRestRequested = true;
        }

        public void SkipEntry()
        {
            _skipEntryRequested = true;
            taskRunner?.CancelRun();
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            for (; _currentEntryIndex < _currentPlaylist.Entries.Count; _currentEntryIndex++)
            {
                ct.ThrowIfCancellationRequested();

                // Pause gate before starting entry
                await WaitIfPausedAsync(ct);

                var entry = _currentPlaylist.Entries[_currentEntryIndex];
                _currentTaskId = entry.ResolveTaskId();
                PublishState(OrchestratorLifecycleState.RunningEntry, $"Start entry #{_currentEntryIndex}: {_currentTaskId}");

                await ConfigureRunnerAsync(entry, ct);

                // Runner execution
                if (_skipEntryRequested)
                {
                    _skipEntryRequested = false;
                    PublishState(OrchestratorLifecycleState.SkippedEntry, $"Skipped entry #{_currentEntryIndex}");
                }
                else
                {
                    await taskRunner.RunAsync();
                }

                // If paused during entry, wait here and do not mark entry completed
                if (_pauseRequested)
                {
                    await SaveCheckpointAsync(false, "Paused during entry");
                    await WaitIfPausedAsync(ct);
                }

                // If user explicitly skipped entry, just continue to next
                if (_skipEntryRequested)
                {
                    _skipEntryRequested = false;
                    PublishState(OrchestratorLifecycleState.SkippedEntry, $"Skipped entry #{_currentEntryIndex} after run");
                }
                else
                {
                    if (saveCheckpointOnEntryCompleted)
                    {
                        await SaveCheckpointAsync(true, "Entry completed");
                    }
                }

                // Rest phase
                await RestIfNeededAsync(entry, ct);
            }

            PublishState(OrchestratorLifecycleState.Completed, "Playlist completed");
            onPlaylistCompleted?.Invoke();

            if (deleteCheckpointOnPlaylistCompleted)
            {
                try { await _checkpointStore.DeleteAsync(_currentPlaylist.PlaylistId, GetParticipantId(), default); } catch { }
            }
        }

        private async Task RestIfNeededAsync(TaskPlaylistEntry entry, CancellationToken ct)
        {
            var seconds = entry.ResolveRestSeconds(_currentPlaylist.DefaultRestSeconds);
            if (seconds <= 0.01f) return;

            PublishState(OrchestratorLifecycleState.WaitingForRest, $"Resting {seconds:0.0}s");

            var start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < seconds)
            {
                ct.ThrowIfCancellationRequested();
                if (_pauseRequested)
                {
                    await SaveCheckpointAsync(false, "Paused during rest");
                    await WaitIfPausedAsync(ct);
                    // After resume, continue counting remaining rest time
                    var elapsed = Time.realtimeSinceStartup - start;
                    start = Time.realtimeSinceStartup - Mathf.Min(elapsed, seconds);
                }
                if (_skipRestRequested)
                {
                    _skipRestRequested = false;
                    break;
                }
                await Task.Yield();
            }
        }

        private async Task WaitIfPausedAsync(CancellationToken ct)
        {
            while (_pauseRequested)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        private async Task ConfigureRunnerAsync(TaskPlaylistEntry entry, CancellationToken ct)
        {
            var config = new TaskRunner.TaskRunConfig
            {
                taskId = entry.ResolveTaskId(),
                subjectMode = entry.subjectMode,
                forceHumanInput = entry.requireHumanInput,
                randomSeed = entry.ResolveRandomSeed(_currentPlaylist.DefaultRandomSeed),
                maxTrials = entry.maxTrials,
                enableActionPlanLoop = entry.enableActionPlanLoop,
                actionPlanLoopTimeoutMs = entry.actionPlanLoopTimeoutMs,
                humanInputPrompt = entry.humanInputPrompt
            };

            _lastRunConfig = config;
            taskRunner?.ApplyRunConfig(config);
            await Task.Yield();
        }

        private void TrySubscribeTrialLifecycle()
        {
            if (eventBus?.TrialLifecycle == null) return;
            eventBus.TrialLifecycle.Subscribe(OnTrialLifecycleEvent);
        }

        private void TryUnsubscribeTrialLifecycle()
        {
            try { eventBus?.TrialLifecycle?.Unsubscribe(OnTrialLifecycleEvent); } catch { }
        }

        private void OnTrialLifecycleEvent(TrialLifecycleEventData data)
        {
            if (data == null) return;
            if (!string.IsNullOrEmpty(_currentTaskId) && !string.Equals(data.taskId, _currentTaskId, StringComparison.OrdinalIgnoreCase))
                return;
            _lastObservedTrialIndex = Mathf.Max(_lastObservedTrialIndex, data.trialId);
        }

        private async Task SaveCheckpointAsync(bool entryCompleted, string message)
        {
            if (_currentPlaylist == null) return;

            // Request a log flush before saving
            try
            {
                eventBus?.LogFlush?.Publish(new LogFlushEventData
                {
                    requestId = Guid.NewGuid().ToString("N"),
                    timestamp = DateTime.UtcNow,
                    logType = "trial",
                    forceFlush = true
                });
            }
            catch { }

            PublishState(OrchestratorLifecycleState.SavingCheckpoint, message);

            var cp = new PlaylistCheckpoint
            {
                checkpointId = Guid.NewGuid().ToString("N"),
                playlistId = _currentPlaylist.PlaylistId,
                playlistName = _currentPlaylist.DisplayName,
                sessionId = SystemInfo.deviceUniqueIdentifier,
                participantId = GetParticipantId(),
                deviceId = SystemInfo.deviceUniqueIdentifier,
                currentEntryIndex = _currentEntryIndex,
                currentTrialIndex = _lastObservedTrialIndex,
                entryCompleted = entryCompleted,
                runner = new TaskRunnerSnapshot
                {
                    taskId = _lastRunConfig?.taskId,
                    subjectMode = _lastRunConfig?.subjectMode,
                    forceHumanInput = _lastRunConfig?.forceHumanInput ?? false,
                    randomSeed = _lastRunConfig?.randomSeed ?? 0,
                    maxTrials = _lastRunConfig?.maxTrials ?? 0,
                    enableActionPlanLoop = _lastRunConfig?.enableActionPlanLoop ?? false,
                    actionPlanLoopTimeoutMs = _lastRunConfig?.actionPlanLoopTimeoutMs ?? 0,
                    humanInputPrompt = _lastRunConfig?.humanInputPrompt
                },
                lastSaveTimeUtc = DateTime.UtcNow,
                lastState = _state,
                lastMessage = message
            };

            try
            {
                await _checkpointStore.SaveAsync(cp, default);
            }
            catch (Exception ex)
            {
                eventBus?.PublishError("TaskOrchestrator", ErrorSeverity.Error, "CHECKPOINT_SAVE_FAILED", ex.Message);
            }
        }

        private void PublishState(OrchestratorLifecycleState state, string message = null, object payload = null, bool isRestore = false)
        {
            _state = state;
            try
            {
                var data = new OrchestratorStateEventData
                {
                    playlistId = _currentPlaylist ? _currentPlaylist.PlaylistId : null,
                    playlistDisplayName = _currentPlaylist ? _currentPlaylist.DisplayName : null,
                    currentEntryIndex = _currentEntryIndex,
                    taskId = _currentTaskId,
                    state = state,
                    timestamp = DateTime.UtcNow,
                    message = message,
                    payload = payload,
                    isCheckpointRestore = isRestore
                };
                eventBus?.OrchestratorState?.Publish(data);
            }
            catch { }
        }

        private string GetParticipantId()
        {
            if (!string.IsNullOrWhiteSpace(participantIdOverride)) return participantIdOverride;
            if (_currentPlaylist != null && !string.IsNullOrWhiteSpace(_currentPlaylist.DefaultParticipantId))
                return _currentPlaylist.DefaultParticipantId;
            return SystemInfo.deviceUniqueIdentifier;
        }
    }
}
