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
        private int _sampleIndex;
        private int _samplesSinceFlush;
        private float _nextSampleTime;
        private Coroutine _ensureSubscribeRoutine;

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
                _ensureSubscribeRoutine = StartCoroutine(EnsureSubscribe());
            }
        }

        private void OnDisable()
        {
            if (_ensureSubscribeRoutine != null)
            {
                StopCoroutine(_ensureSubscribeRoutine);
                _ensureSubscribeRoutine = null;
            }

            Unsubscribe();
            StopRecording();
        }

        private void Update()
        {
            if (!_isRecording) return;
            if (taskRunner != null && taskRunner.CurrentSubjectMode != SubjectMode.Human)
            {
                StopRecording();
                return;
            }

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
                StartRecording(data.taskId, data.trialId);
                return;
            }

            if (!_isRecording) return;
            if (!string.Equals(data.taskId, _activeTaskId, StringComparison.OrdinalIgnoreCase) || data.trialId != _activeTrialId) return;

            if (data.state == TrialLifecycleState.Completed ||
                data.state == TrialLifecycleState.Failed ||
                data.state == TrialLifecycleState.Cancelled)
            {
                StopRecording();
            }
        }

        private void StartRecording(string taskId, int trialId)
        {
            if (_isRecording &&
                string.Equals(taskId, _activeTaskId, StringComparison.OrdinalIgnoreCase) &&
                trialId == _activeTrialId)
            {
                return;
            }

            StopRecording();
            EnsureTelemetryDirectory();

            _activeTaskId = string.IsNullOrWhiteSpace(taskId) ? "unknown_task" : taskId.Trim();
            _activeTrialId = trialId;
            _sampleIndex = 0;
            _samplesSinceFlush = 0;

            string fileName = $"{SanitizeFilePart(_activeTaskId)}_trial{trialId:D4}_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.csv";
            string path = Path.Combine(_telemetryDir, fileName);
            _writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
            _writer.WriteLine(
                "sampleIndex,utcIso,realtimeSinceStartup,taskId,trialId," +
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
            CaptureSample();
        }

        private void StopRecording()
        {
            _isRecording = false;
            _activeTaskId = null;
            _activeTrialId = -1;
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
                }

                if (eyeTrackingStateResult == 0 && eyeTrackingActive)
                {
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

            string line = string.Join(",",
                _sampleIndex.ToString(Invariant),
                DateTime.UtcNow.ToString("o", Invariant),
                Time.realtimeSinceStartup.ToString("F6", Invariant),
                EscapeCsv(_activeTaskId),
                _activeTrialId.ToString(Invariant),
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
                _writer.Flush();
                _samplesSinceFlush = 0;
            }
        }

        private void EnsureTelemetryDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_telemetryDir) && Directory.Exists(_telemetryDir)) return;

            string sessionDir = LogSessionPaths.GetOrCreateSessionDirectory(rootFolderName);
            _telemetryDir = Path.Combine(sessionDir, string.IsNullOrWhiteSpace(telemetryFolderName) ? "telemetry" : telemetryFolderName.Trim());
            Directory.CreateDirectory(_telemetryDir);
        }

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
