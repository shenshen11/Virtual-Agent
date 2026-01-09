using System;
using UnityEngine;
using UnityEngine.XR;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;

namespace VRPerception.UI
{
    /// <summary>
    /// PICO XR 输入桥接（Scenario 12 / depth_jnd_staircase）：
    /// - 订阅 TrialLifecycle，在 WaitingForInput 时启用
    /// - 监听 PICO 左手柄 primary/secondary button（常见为 X/Y；项目文档中记为 A/B）
    /// - primaryButton => 选择 A（左侧物体），secondaryButton => 选择 B（右侧物体）
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
        private float _lastTriggerTime = -999f;

        private readonly System.Collections.Generic.List<InputDevice> _leftHandDevices =
            new System.Collections.Generic.List<InputDevice>(2);

        private bool _lastPrimaryPressed = false;
        private bool _lastSecondaryPressed = false;

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
            eventBus?.TrialLifecycle?.Subscribe(OnTrialLifecycle);
        }

        private void OnDisable()
        {
            eventBus?.TrialLifecycle?.Unsubscribe(OnTrialLifecycle);
            ResetAwaitingState();
        }

        private void Update()
        {
            if (!enabledForTask) return;
            if (!_awaitingInput) return;
            if (eventBus == null) return;
            if (_hasTriggeredForThisTrial) return;
            if (Time.realtimeSinceStartup - _lastTriggerTime < minTriggerIntervalSeconds) return;

            if (!string.Equals(_taskId, TargetTaskId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

#if UNITY_EDITOR
            if (allowKeyboardInEditor)
            {
                if (Input.GetKeyDown(editorChooseAKey))
                {
                    Trigger("A");
                    return;
                }
                if (Input.GetKeyDown(editorChooseBKey))
                {
                    Trigger("B");
                    return;
                }
            }
#endif

            if (useLeftControllerButtons && ShouldTriggerByXR(out var choice))
            {
                Trigger(choice);
            }
        }

        private bool ShouldTriggerByXR(out string choice)
        {
            choice = null;

            _leftHandDevices.Clear();
            InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, _leftHandDevices);

            bool primaryDown = false;
            bool secondaryDown = false;

            for (int i = 0; i < _leftHandDevices.Count; i++)
            {
                var device = _leftHandDevices[i];
                if (!device.isValid) continue;

                if (device.TryGetFeatureValue(CommonUsages.primaryButton, out bool p) && p) primaryDown = true;
                if (device.TryGetFeatureValue(CommonUsages.secondaryButton, out bool s) && s) secondaryDown = true;
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
            if (_hasTriggeredForThisTrial) return;

            _hasTriggeredForThisTrial = true;
            _lastTriggerTime = Time.realtimeSinceStartup;

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
                _lastPrimaryPressed = false;
                _lastSecondaryPressed = false;

                if (verboseLog && string.Equals(_taskId, TargetTaskId, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log("[XRDepthJndHumanInputBridge] WaitingForInput: Left primaryButton=>A(left), secondaryButton=>B(right).");
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
