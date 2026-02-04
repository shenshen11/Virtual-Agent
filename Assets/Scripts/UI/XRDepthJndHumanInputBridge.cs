using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;

namespace VRPerception.UI
{
    /// <summary>
    /// PICO XR 输入桥接（Scenario 12 / depth_jnd_staircase）：
    /// - 订阅 TrialLifecycle，在 WaitingForInput 时启用
    /// - 监听左手柄 X/Y 键：X => A（左侧物体），Y => B（右侧物体）
    ///   - 优先使用 XR 的 primary/secondary button（通常分别对应 X/Y）
    ///   - 并尝试使用 xButton/yButton 命名用法做兼容回退（不同 XR 后端映射不一致时）
    /// - 复用 TaskRunner.WaitForInferenceAsync：通过 InferenceReceived 发布 LLMResponse(providerId="human")
    /// </summary>
    public sealed class XRDepthJndHumanInputBridge : MonoBehaviour
    {
        private const string TargetTaskId = "depth_jnd_staircase";

        [Header("References")]
        [SerializeField] private EventBusManager eventBus;
        [SerializeField] private bool autoFindEventBus = true;

        [Header("Behavior")]
        [SerializeField] private bool enabledForTask = true;
        [SerializeField] private bool verboseLog = false;

        [Tooltip("两次触发之间最小间隔（秒），防止长按/抖动导致连跳")]
        [SerializeField] private float minTriggerIntervalSeconds = 0.25f;

        [Header("XR Controller (PICO)")]
        [SerializeField] private bool useLeftControllerButtons = true;

        [Header("Editor Debug")]
        [SerializeField] private bool allowKeyboardInEditor = true;
        [SerializeField] private KeyCode editorChooseAKey = KeyCode.Alpha1;
        [SerializeField] private KeyCode editorChooseBKey = KeyCode.Alpha2;

        [Header("Default Answer")]
        [SerializeField] private float defaultConfidence = 0.9f;

        private bool _awaitingInput;
        private string _taskId = string.Empty;
        private int _trialId = -1;

        private float _waitingStartRealtime = 0f;
        private bool _hasTriggeredForThisTrial = false;
        private float _lastTriggerTime = -999f; // Time.time

        private readonly System.Collections.Generic.List<InputDevice> _leftHandDevices =
            new System.Collections.Generic.List<InputDevice>(2);

        private static readonly InputFeatureUsage<bool> XButtonUsage = new InputFeatureUsage<bool>("xButton");
        private static readonly InputFeatureUsage<bool> YButtonUsage = new InputFeatureUsage<bool>("yButton");

        private bool _lastPrimaryPressed = false;
        private bool _lastSecondaryPressed = false;
        private bool _subscribedTrialLifecycle = false;

        private void SyncHeldButtonState()
        {
            if (!useLeftControllerButtons)
            {
                _lastPrimaryPressed = false;
                _lastSecondaryPressed = false;
                return;
            }

            _leftHandDevices.Clear();
            InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, _leftHandDevices);

            bool primaryDown = false;
            bool secondaryDown = false;

            for (int i = 0; i < _leftHandDevices.Count; i++)
            {
                var device = _leftHandDevices[i];
                if (!device.isValid) continue;

                bool p = false;
                bool s = false;

                if (device.TryGetFeatureValue(CommonUsages.primaryButton, out bool p1) && p1) p = true;
                if (device.TryGetFeatureValue(XButtonUsage, out bool px) && px) p = true;

                if (device.TryGetFeatureValue(CommonUsages.secondaryButton, out bool s1) && s1) s = true;
                if (device.TryGetFeatureValue(YButtonUsage, out bool sy) && sy) s = true;

                if (p) primaryDown = true;
                if (s) secondaryDown = true;
            }

            _lastPrimaryPressed = primaryDown;
            _lastSecondaryPressed = secondaryDown;
        }

        private void Awake()
        {
            if (autoFindEventBus && eventBus == null)
            {
                eventBus = EventBusManager.Instance;
            }

            if (eventBus == null)
            {
                Debug.LogWarning("[XRDepthJndHumanInputBridge] EventBusManager not found. Input bridge will be disabled.");
            }
        }

        private void OnEnable()
        {
            TrySubscribeTrialLifecycle();
            StartCoroutine(EnsureSubscribeTrialLifecycle());
        }

        private void OnDisable()
        {
            TryUnsubscribeTrialLifecycle();
            ResetAwaitingState();
        }

        private void Update()
        {
            if (!enabledForTask) return;
            if (!_awaitingInput) return;
            if (eventBus == null) return;
            if (_hasTriggeredForThisTrial) return;

            if (!string.Equals(_taskId, TargetTaskId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Time.time - _lastTriggerTime < minTriggerIntervalSeconds) return;

            if (ShouldTriggerByInput(out var choice))
            {
                _hasTriggeredForThisTrial = true;
                _lastTriggerTime = Time.time;
                Trigger(choice);
            }
        }

        private bool ShouldTriggerByInput(out string choice)
        {
            choice = null;

#if UNITY_EDITOR
            if (allowKeyboardInEditor)
            {
                if (Input.GetKeyDown(editorChooseAKey)) { choice = "A"; return true; }
                if (Input.GetKeyDown(editorChooseBKey)) { choice = "B"; return true; }
            }
#endif

            if (!useLeftControllerButtons) return false;

            _leftHandDevices.Clear();
            InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, _leftHandDevices);

            bool primaryDown = false;
            bool secondaryDown = false;

            for (int i = 0; i < _leftHandDevices.Count; i++)
            {
                var device = _leftHandDevices[i];
                if (!device.isValid) continue;

                bool p = false;
                bool s = false;

                if (device.TryGetFeatureValue(CommonUsages.primaryButton, out bool p1) && p1) p = true;
                if (device.TryGetFeatureValue(XButtonUsage, out bool px) && px) p = true;

                if (device.TryGetFeatureValue(CommonUsages.secondaryButton, out bool s1) && s1) s = true;
                if (device.TryGetFeatureValue(YButtonUsage, out bool sy) && sy) s = true;

                if (p) primaryDown = true;
                if (s) secondaryDown = true;
            }

            bool primaryEdge = primaryDown && !_lastPrimaryPressed;
            bool secondaryEdge = secondaryDown && !_lastSecondaryPressed;

            _lastPrimaryPressed = primaryDown;
            _lastSecondaryPressed = secondaryDown;

            if (primaryEdge)
            {
                choice = "A";
                return true;
            }
            if (secondaryEdge)
            {
                choice = "B";
                return true;
            }

            return false;
        }

        private void Trigger(string closer)
        {
            if (string.IsNullOrEmpty(closer)) return;

            long rtMs = 0;
            try
            {
                rtMs = (long)Mathf.Max(0f, (Time.realtimeSinceStartup - _waitingStartRealtime) * 1000f);
            }
            catch { }

            var conf = Mathf.Clamp01(defaultConfidence);
            var response = new LLMResponse
            {
                type = "inference",
                taskId = string.IsNullOrEmpty(_taskId) ? TargetTaskId : _taskId,
                trialId = _trialId,
                providerId = "human",
                confidence = conf,
                latencyMs = rtMs,
                answer = new DepthAnswer { closer = closer, confidence = conf }
            };

            var data = new InferenceReceivedEventData
            {
                requestId = "human_xr_djnd_" + Guid.NewGuid().ToString("N"),
                taskId = response.taskId,
                trialId = response.trialId,
                timestamp = DateTime.UtcNow,
                providerId = response.providerId,
                response = response
            };

            try
            {
                eventBus.InferenceReceived?.Publish(data);
                if (verboseLog)
                {
                    Debug.Log($"[XRDepthJndHumanInputBridge] Published choice='{closer}' for {response.taskId}/{response.trialId} rtMs={rtMs}.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[XRDepthJndHumanInputBridge] Failed to publish inference: {ex.Message}");
            }

            _awaitingInput = false;
        }

        private void OnTrialLifecycle(TrialLifecycleEventData data)
        {
            if (data == null) return;

            if (data.state == TrialLifecycleState.WaitingForInput)
            {
                _taskId = data.taskId;
                _trialId = data.trialId;
                _awaitingInput = true;
                _waitingStartRealtime = Time.realtimeSinceStartup;

                _hasTriggeredForThisTrial = false;

                // Avoid treating a still-held button (from previous trial) as a fresh "edge" on the new trial.
                SyncHeldButtonState();

                if (verboseLog && string.Equals(_taskId, TargetTaskId, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log("[XRDepthJndHumanInputBridge] WaitingForInput: Left X(primary)=>A(left), Y(secondary)=>B(right).");
                }
            }
            else if (data.state == TrialLifecycleState.Completed ||
                     data.state == TrialLifecycleState.Failed ||
                     data.state == TrialLifecycleState.Cancelled)
            {
                ResetAwaitingState();
            }
            else
            {
                // 任意非 WaitingForInput 状态都应停止监听，避免在其他输入源已提交后重复触发
                _awaitingInput = false;
                _lastPrimaryPressed = false;
                _lastSecondaryPressed = false;
            }
        }

        private void TrySubscribeTrialLifecycle()
        {
            if (eventBus == null) return;
            if (eventBus.TrialLifecycle == null) return;
            if (_subscribedTrialLifecycle) return;

            eventBus.TrialLifecycle.Subscribe(OnTrialLifecycle);
            _subscribedTrialLifecycle = true;
        }

        private void TryUnsubscribeTrialLifecycle()
        {
            if (eventBus == null) return;
            if (eventBus.TrialLifecycle == null) return;
            if (!_subscribedTrialLifecycle) return;

            eventBus.TrialLifecycle.Unsubscribe(OnTrialLifecycle);
            _subscribedTrialLifecycle = false;
        }

        private IEnumerator EnsureSubscribeTrialLifecycle()
        {
            float start = Time.realtimeSinceStartup;
            float timeoutSeconds = 3f;

            while (eventBus == null && Time.realtimeSinceStartup - start < timeoutSeconds)
            {
                eventBus = EventBusManager.Instance;
                yield return null;
            }

            if (eventBus == null) yield break;

            while (eventBus.TrialLifecycle == null && Time.realtimeSinceStartup - start < timeoutSeconds)
            {
                yield return null;
            }

            TrySubscribeTrialLifecycle();
        }

        private void ResetAwaitingState()
        {
            _awaitingInput = false;
            _hasTriggeredForThisTrial = false;
            _lastPrimaryPressed = false;
            _lastSecondaryPressed = false;
        }

        [Serializable]
        private class DepthAnswer
        {
            public string closer; // "A"|"B"
            public float confidence;
        }
    }
}
