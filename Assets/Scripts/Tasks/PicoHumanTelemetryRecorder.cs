using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using VRPerception.Infra;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;
#if !PICO_OPENXR_SDK
using Unity.XR.PXR;
#endif

namespace VRPerception.Tasks
{
    /// <summary>
    /// Human 模式下的最小侵入式 PICO 行为数据记录器。
    /// 基于 TrialLifecycle 对齐 trial，按 trial 输出 head pose / gaze / eye openness CSV。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PicoHumanTelemetryRecorder : MonoBehaviour
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        [Header("References")]
        [SerializeField] private TaskRunner taskRunner;
        [SerializeField] private EventBusManager eventBus;
        [SerializeField] private StimulusCapture stimulus;

        [Header("Output")]
        [SerializeField] private string rootFolderName = "VRP_Logs";
        [SerializeField] private string telemetryFolderName = "telemetry";

        [Header("Sampling")]
        [SerializeField] private float sampleIntervalSeconds = 1f / 60f;
        [SerializeField] private int flushEveryNSamples = 30;

        private string _telemetryDir;
        private StreamWriter _writer;
        private bool _isRecording;
        private bool _subscribed;
        private string _activeTaskId;
        private int _activeTrialId = -1;
        private string _activePhase = "trial";
        private string _activeSubphase = "trial";
        private int _sampleIndex;
        private int _samplesSinceFlush;
        private float _nextSampleTime;
        private Coroutine _ensureSubscribeRoutine;
        private bool _eyeTrackingSupportKnown;
        private bool _eyeTrackingSupported;
        private EyeTrackingMode _eyeTrackingStartMode = EyeTrackingMode.PXR_ETM_BOTH;
        private float _nextEyeTrackingStartRetryTime;

#if !PICO_OPENXR_SDK
        private PXR_Manager _pxrManager;
#endif

        private void Awake()
        {
            ResolveDependencies();
            EnsureTelemetryDirectory();
        }

        private void OnEnable()
        {
            ResolveDependencies();
            TrySubscribe();
            if (_ensureSubscribeRoutine == null)
            {
                // 某些场景下 EventBusManager / TrialLifecycle 会晚于当前组件初始化，
                // 这里用短时重试避免因为初始化时序问题而漏掉 trial 事件订阅。
                _ensureSubscribeRoutine = StartCoroutine(EnsureSubscribe());
            }

#if !PICO_OPENXR_SDK
            TryEnsureEyeTrackingStarted(forceRetry: true);
#endif
        }

        private void OnDisable()
        {
            if (_ensureSubscribeRoutine != null)
            {
                StopCoroutine(_ensureSubscribeRoutine);
                _ensureSubscribeRoutine = null;
            }

#if !PICO_OPENXR_SDK
            StopEyeTrackingIfNeeded();
#endif
            Unsubscribe();
            StopRecording();
        }

        private void Update()
        {
            if (!_isRecording) return;
            if (taskRunner != null && taskRunner.CurrentSubjectMode != SubjectMode.Human)
            {
                // 录制期间如果主体模式被切走，立即停止，避免混入非 Human 数据。
                StopRecording();
                return;
            }

            // 采样频率由 unscaledTime 驱动，不受 Time.timeScale 影响，适合实验记录。
            if (Time.unscaledTime + 1e-4f < _nextSampleTime) return;
            _nextSampleTime = Time.unscaledTime + Mathf.Max(0.001f, sampleIntervalSeconds);

            try
            {
                CaptureSample();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PicoHumanTelemetryRecorder] Sampling stopped: {ex.Message}");
                StopRecording();
            }
        }

        private IEnumerator EnsureSubscribe()
        {
            const float timeoutSeconds = 5f;
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;

            // 最多重试 5 秒，尽量等到 TrialLifecycle 可用。
            while (!_subscribed && Time.realtimeSinceStartup < deadline)
            {
                ResolveDependencies();
                TrySubscribe();
                yield return null;
            }

            _ensureSubscribeRoutine = null;
        }

        private void ResolveDependencies()
        {
            if (taskRunner == null) taskRunner = GetComponent<TaskRunner>();
            if (eventBus == null) eventBus = EventBusManager.Instance;
            if (stimulus == null) stimulus = GetComponent<StimulusCapture>();
#if !PICO_OPENXR_SDK
            if (_pxrManager == null) _pxrManager = FindObjectOfType<PXR_Manager>();
#endif
        }

        private void TrySubscribe()
        {
            if (_subscribed || eventBus == null || eventBus.TrialLifecycle == null) return;
            eventBus.TrialLifecycle.Subscribe(OnTrialLifecycle);
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            eventBus?.TrialLifecycle?.Unsubscribe(OnTrialLifecycle);
            _subscribed = false;
        }

        private void OnTrialLifecycle(TrialLifecycleEventData data)
        {
            if (data == null) return;

            if (data.state == TrialLifecycleState.Started || data.state == TrialLifecycleState.WaitingForInput)
            {
                if (taskRunner != null && taskRunner.CurrentSubjectMode != SubjectMode.Human) return;
                // 从 trial 开始或等待用户输入阶段就打开录制，确保首帧输入前的数据也被保留。
                StartRecording(data.taskId, data.trialId, "trial");
                return;
            }

            if (!_isRecording) return;
            // 只响应当前活跃 trial 的结束事件，避免其他 trial 的广播误关当前 writer。
            if (!string.Equals(data.taskId, _activeTaskId, StringComparison.OrdinalIgnoreCase) || data.trialId != _activeTrialId) return;

            if (data.state == TrialLifecycleState.Completed ||
                data.state == TrialLifecycleState.Failed ||
                data.state == TrialLifecycleState.Cancelled)
            {
                StopRecording();
            }
        }

        public void StartCalibrationRecording(string taskId)
        {
            if (taskRunner != null && taskRunner.CurrentSubjectMode != SubjectMode.Human) return;
            StartRecording(taskId, -1, "reference_frame_calibration", "pre_delay");
        }

        public void StopCalibrationRecording()
        {
            if (!_isRecording) return;
            if (!string.Equals(_activePhase, "reference_frame_calibration", StringComparison.OrdinalIgnoreCase)) return;
            StopRecording();
        }

        public void SetCalibrationSubphase(string subphase)
        {
            if (!_isRecording) return;
            if (!string.Equals(_activePhase, "reference_frame_calibration", StringComparison.OrdinalIgnoreCase)) return;
            _activeSubphase = string.IsNullOrWhiteSpace(subphase) ? "reference_frame_calibration" : subphase.Trim();
        }

        private void StartRecording(string taskId, int trialId, string phase, string subphase = null)
        {
            if (_isRecording &&
                string.Equals(taskId, _activeTaskId, StringComparison.OrdinalIgnoreCase) &&
                trialId == _activeTrialId &&
                string.Equals(phase, _activePhase, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(string.IsNullOrWhiteSpace(subphase) ? "trial" : subphase.Trim(), _activeSubphase, StringComparison.OrdinalIgnoreCase))
            {
                // 同一阶段的重复事件不重复开文件。
                return;
            }

            // 新阶段开始前先完整关闭旧 writer，保证一个阶段对应一个独立 CSV。
            StopRecording();
            EnsureTelemetryDirectory();

            _activeTaskId = string.IsNullOrWhiteSpace(taskId) ? "unknown_task" : taskId.Trim();
            _activeTrialId = trialId;
            _activePhase = string.IsNullOrWhiteSpace(phase) ? "trial" : phase.Trim();
            _activeSubphase = string.IsNullOrWhiteSpace(subphase) ? _activePhase : subphase.Trim();
            _sampleIndex = 0;
            _samplesSinceFlush = 0;

            string segmentPart = trialId >= 0 ? $"trial{trialId:D4}" : SanitizeFilePart(_activePhase);
            string fileName = $"{SanitizeFilePart(_activeTaskId)}_{segmentPart}_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.csv";
            string path = Path.Combine(_telemetryDir, fileName);
            _writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
            _writer.WriteLine(
                // 列名固定，便于后续直接用 pandas / Excel / 脚本批处理。
                "sampleIndex,utcIso,realtimeSinceStartup,taskId,trialId,phase,subphase," +
                "headPosX,headPosY,headPosZ,headRotX,headRotY,headRotZ,headRotW," +
                "eyeTrackingEnabled,eyeTrackingStateResult,eyeTrackingActive,trackingMode,trackingStateCode," +
                "combinedEyePoseStatus,leftEyePoseStatus,rightEyePoseStatus," +
                "gazePointValid,gazeVectorValid," +
                "gazePointLocalX,gazePointLocalY,gazePointLocalZ," +
                "gazePointWorldX,gazePointWorldY,gazePointWorldZ," +
                "gazeVectorLocalX,gazeVectorLocalY,gazeVectorLocalZ," +
                "gazeVectorWorldX,gazeVectorWorldY,gazeVectorWorldZ," +
                "leftOpenness,rightOpenness");

            _isRecording = true;
            _nextSampleTime = Time.unscaledTime + Mathf.Max(0.001f, sampleIntervalSeconds);
            // 启动录制后立即补一帧，避免 trial 起点处出现空窗。
            CaptureSample();
        }

        private void StopRecording()
        {
            _isRecording = false;
            _activeTaskId = null;
            _activeTrialId = -1;
            _activePhase = "trial";
            _activeSubphase = "trial";
            _sampleIndex = 0;
            _samplesSinceFlush = 0;
            _nextSampleTime = 0f;

            if (_writer == null) return;

            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch
            {
                // Ignore telemetry flush/close failures so the main experiment loop is unaffected.
            }
            finally
            {
                _writer = null;
            }
        }

        private void CaptureSample()
        {
            if (_writer == null) return;

            // 优先使用 StimulusCapture 维护的头显相机；缺失时退回 Camera.main，降低场景接入成本。
            Camera headCamera = stimulus != null && stimulus.HeadCamera != null ? stimulus.HeadCamera : Camera.main;
            if (headCamera == null) return;

            Transform head = headCamera.transform;
            Vector3 headPos = head.position;
            Quaternion headRot = head.rotation;

            int eyeTrackingStateResult = -1;
            bool eyeTrackingEnabled = false;
            bool eyeTrackingActive = false;
            int trackingMode = -1;
            int trackingStateCode = -1;
            int combinedEyePoseStatus = -1;
            int leftEyePoseStatus = -1;
            int rightEyePoseStatus = -1;
            bool gazePointValid = false;
            bool gazeVectorValid = false;
            Vector3? gazePointLocal = null;
            Vector3? gazePointWorld = null;
            Vector3? gazeVectorLocal = null;
            Vector3? gazeVectorWorld = null;
            float? leftOpenness = null;
            float? rightOpenness = null;

#if !PICO_OPENXR_SDK
            eyeTrackingEnabled = _pxrManager != null && _pxrManager.eyeTracking;
            if (eyeTrackingEnabled)
            {
                bool isTracking = false;
                EyeTrackingState state = default;
                eyeTrackingStateResult = PXR_MotionTracking.GetEyeTrackingState(ref isTracking, ref state);
                if (eyeTrackingStateResult == 0)
                {
                    eyeTrackingActive = isTracking;
                    trackingMode = (int)state.currentTrackingMode;
                    trackingStateCode = (int)state.code;
                    if (!eyeTrackingActive && trackingStateCode == (int)TrackingStateCode.PXR_MT_SERVICE_NEED_START)
                    {
                        // 某些设备会在运行期回到“需要启动”状态，这里按节流策略补启动一次。
                        TryEnsureEyeTrackingStarted(forceRetry: false);
                    }
                }

                if (eyeTrackingStateResult == 0 && eyeTrackingActive)
                {
                    // PICO SDK 给的是相对头部坐标系数据，这里同时记录 local/world 两份，方便后处理。
                    if (PXR_EyeTracking.GetCombinedEyePoseStatus(out uint combinedStatus))
                    {
                        combinedEyePoseStatus = (int)combinedStatus;
                    }

                    if (PXR_EyeTracking.GetLeftEyePoseStatus(out uint leftStatus))
                    {
                        leftEyePoseStatus = (int)leftStatus;
                    }

                    if (PXR_EyeTracking.GetRightEyePoseStatus(out uint rightStatus))
                    {
                        rightEyePoseStatus = (int)rightStatus;
                    }

                    if (PXR_EyeTracking.GetCombineEyeGazePoint(out Vector3 pointLocal))
                    {
                        gazePointValid = true;
                        gazePointLocal = pointLocal;
                        gazePointWorld = head.TransformPoint(pointLocal);
                    }

                    if (PXR_EyeTracking.GetCombineEyeGazeVector(out Vector3 vectorLocal))
                    {
                        gazeVectorValid = true;
                        gazeVectorLocal = vectorLocal.normalized;
                        gazeVectorWorld = head.TransformDirection(gazeVectorLocal.Value).normalized;
                    }

                    if (PXR_EyeTracking.GetLeftEyeGazeOpenness(out float leftOpen))
                    {
                        leftOpenness = leftOpen;
                    }

                    if (PXR_EyeTracking.GetRightEyeGazeOpenness(out float rightOpen))
                    {
                        rightOpenness = rightOpen;
                    }
                }
            }
#endif

            // 统一按 InvariantCulture 输出，避免不同系统区域设置把小数点写成逗号。
            string line = string.Join(",",
                _sampleIndex.ToString(Invariant),
                DateTime.UtcNow.ToString("o", Invariant),
                Time.realtimeSinceStartup.ToString("F6", Invariant),
                EscapeCsv(_activeTaskId),
                _activeTrialId.ToString(Invariant),
                EscapeCsv(_activePhase),
                EscapeCsv(_activeSubphase),
                FormatFloat(headPos.x),
                FormatFloat(headPos.y),
                FormatFloat(headPos.z),
                FormatFloat(headRot.x),
                FormatFloat(headRot.y),
                FormatFloat(headRot.z),
                FormatFloat(headRot.w),
                FormatBool(eyeTrackingEnabled),
                eyeTrackingStateResult.ToString(Invariant),
                FormatBool(eyeTrackingActive),
                trackingMode.ToString(Invariant),
                trackingStateCode.ToString(Invariant),
                combinedEyePoseStatus.ToString(Invariant),
                leftEyePoseStatus.ToString(Invariant),
                rightEyePoseStatus.ToString(Invariant),
                FormatBool(gazePointValid),
                FormatBool(gazeVectorValid),
                FormatNullableVectorComponent(gazePointLocal, 0),
                FormatNullableVectorComponent(gazePointLocal, 1),
                FormatNullableVectorComponent(gazePointLocal, 2),
                FormatNullableVectorComponent(gazePointWorld, 0),
                FormatNullableVectorComponent(gazePointWorld, 1),
                FormatNullableVectorComponent(gazePointWorld, 2),
                FormatNullableVectorComponent(gazeVectorLocal, 0),
                FormatNullableVectorComponent(gazeVectorLocal, 1),
                FormatNullableVectorComponent(gazeVectorLocal, 2),
                FormatNullableVectorComponent(gazeVectorWorld, 0),
                FormatNullableVectorComponent(gazeVectorWorld, 1),
                FormatNullableVectorComponent(gazeVectorWorld, 2),
                FormatNullableFloat(leftOpenness),
                FormatNullableFloat(rightOpenness));

            _writer.WriteLine(line);
            _sampleIndex++;
            _samplesSinceFlush++;

            if (_samplesSinceFlush >= Mathf.Max(1, flushEveryNSamples))
            {
                // 周期性 flush，在数据安全和 IO 开销之间做折中。
                _writer.Flush();
                _samplesSinceFlush = 0;
            }
        }

        private void EnsureTelemetryDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_telemetryDir) && Directory.Exists(_telemetryDir)) return;

            // Telemetry 挂在 session 目录下，和同轮实验的其他日志保持同一根路径。
            string sessionDir = LogSessionPaths.GetOrCreateSessionDirectory(rootFolderName);
            _telemetryDir = Path.Combine(sessionDir, string.IsNullOrWhiteSpace(telemetryFolderName) ? "telemetry" : telemetryFolderName.Trim());
            Directory.CreateDirectory(_telemetryDir);
        }

#if !PICO_OPENXR_SDK
        private void TryEnsureEyeTrackingStarted(bool forceRetry)
        {
            if (_pxrManager == null || !_pxrManager.eyeTracking) return;
            if (!forceRetry && Time.unscaledTime < _nextEyeTrackingStartRetryTime) return;

            // 启动失败时最多每秒重试一次，避免每帧调用底层 SDK。
            _nextEyeTrackingStartRetryTime = Time.unscaledTime + 1f;

            if (!_eyeTrackingSupportKnown)
            {
                bool supported = false;
                int supportedModesCount = 0;
                EyeTrackingMode[] supportedModes = new EyeTrackingMode[4];
                int supportRet = PXR_MotionTracking.GetEyeTrackingSupported(ref supported, ref supportedModesCount, ref supportedModes);

                _eyeTrackingSupportKnown = true;
                _eyeTrackingSupported = supportRet == 0 && supported;
                if (_eyeTrackingSupported && supportedModesCount > 0 && supportedModes != null && supportedModes.Length > 0)
                {
                    // 直接采用设备返回的首个支持模式，保持和底层能力声明一致。
                    _eyeTrackingStartMode = supportedModes[0];
                }

                Debug.Log(
                    $"[PicoHumanTelemetryRecorder] EyeTrackingSupport supportRet={supportRet} " +
                    $"supported={supported} supportedModesCount={supportedModesCount} mode={_eyeTrackingStartMode}");

                if (!_eyeTrackingSupported) return;
            }

            if (!_eyeTrackingSupported) return;

            EyeTrackingStartInfo startInfo = new EyeTrackingStartInfo
            {
                // 允许 SDK 在需要时触发校准流程，避免拿到未校准的眼动数据。
                needCalibration = 1,
                mode = _eyeTrackingStartMode
            };

            int startRet = PXR_MotionTracking.StartEyeTracking(ref startInfo);
            Debug.Log(
                $"[PicoHumanTelemetryRecorder] StartEyeTracking startRet={startRet} " +
                $"needCalibration={startInfo.needCalibration} mode={startInfo.mode}");
        }

        private void StopEyeTrackingIfNeeded()
        {
            if (_pxrManager == null || !_pxrManager.eyeTracking || !_eyeTrackingSupportKnown || !_eyeTrackingSupported) return;

            EyeTrackingStopInfo stopInfo = default;
            int stopRet = PXR_MotionTracking.StopEyeTracking(ref stopInfo);
            Debug.Log($"[PicoHumanTelemetryRecorder] StopEyeTracking stopRet={stopRet}");
        }
#endif

        private static string SanitizeFilePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown";

            string sanitized = value.Trim();
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                sanitized = sanitized.Replace(c, '_');
            }

            return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
        }

        private static string EscapeCsv(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (!value.Contains(",") && !value.Contains("\"")) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string FormatBool(bool value)
        {
            return value ? "1" : "0";
        }

        private static string FormatFloat(float value)
        {
            return value.ToString("F6", Invariant);
        }

        private static string FormatNullableFloat(float? value)
        {
            return value.HasValue ? value.Value.ToString("F6", Invariant) : string.Empty;
        }

        private static string FormatNullableVectorComponent(Vector3? value, int index)
        {
            if (!value.HasValue) return string.Empty;
            Vector3 vector = value.Value;
            return index switch
            {
                0 => vector.x.ToString("F6", Invariant),
                1 => vector.y.ToString("F6", Invariant),
                _ => vector.z.ToString("F6", Invariant)
            };
        }
    }
}
