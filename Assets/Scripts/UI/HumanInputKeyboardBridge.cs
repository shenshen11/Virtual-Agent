using System;
using UnityEngine;
using UnityEngine.XR;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;

namespace VRPerception.UI
{
    /// <summary>
    /// HumanInput 键盘/手柄桥接组件（最小侵入实现）：
    /// - 订阅 TrialLifecycle，当 state=WaitingForInput 时记录当前 taskId/trialId
    /// - 在 Update 中监听 PICO 右手柄 A 键（XR 右手 primaryButton）或（可选）键盘 A 键
    /// - 触发时构造一个 LLMResponse，并通过 InferenceReceived 事件发送
    /// - 完全复用 TaskRunner.WaitForInferenceAsync / ExperimentLogger 现有流程
    ///
    /// 使用方式（推荐实验场景配置）：
    /// 1. 在场景中任意 GameObject 上添加本组件（如 EventBusManager 所在对象）。
    /// 2. 确保场景中已存在 EventBusManager，或勾选 autoFindEventBus=true。
    /// 3. 将 subjectMode 配置为 Human（通过 Playlist/TaskOrchestrator 或直接在 TaskRunner 上设置）。
    /// 4. 实验中，被试按 PICO 右手柄 A 键，即可结束当前试次并进入下一试次。
    ///
    /// 注意：
    /// - 不会修改或关闭现有 WSHumanInputDialog/HumanInputHandler；是否显示 UI 由你在场景中挂不挂这些组件决定。
    /// - 若你只想用手柄 A 键而不用 VR 弹窗，可以从场景中移除 WSHumanInputDialog prefab。
    /// </summary>
    public class HumanInputKeyboardBridge : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private EventBusManager eventBus;
        [SerializeField] private bool autoFindEventBus = true;

        [Header("Behavior")]
        [Tooltip("是否启用外部输入（PICO 右手柄 A / 键盘 A）来触发人类输入，仅在 TrialLifecycle=WaitingForInput 时有效")]
        [SerializeField] private bool useKeyboardInput = true;

        [Tooltip("是否输出调试日志")]
        [SerializeField] private bool verboseLog = false;

        [Tooltip("当任务类型未知时，是否仍然发送一个通用“继续”响应")]
        [SerializeField] private bool allowGenericAnswer = true;

        [Header("XR Controller (PICO)")]
        [Tooltip("是否使用 XR 右手柄 primaryButton 作为触发（在 PICO 上通常是右手柄 A 键）")]
        [SerializeField] private bool useRightControllerPrimaryButton = true;

        [Tooltip("在 Editor 中是否允许使用键盘 A 作为调试触发")]
        [SerializeField] private bool allowKeyboardAInEditor = true;

        [Header("Default Answers (for Human 模式)")]
        [Tooltip("距离估计任务在使用 A 键跳转时的默认距离（米）")]
        [SerializeField] private float defaultDistanceMeters = 10f;

        [Tooltip("输入触发时的默认置信度 [0,1]")]
        [SerializeField] private float defaultConfidence = 0.9f;

        private bool _awaitingInput;
        private string _taskId = string.Empty;
        private int _trialId = -1;

        // XR 右手设备缓存，避免每帧 GC
        private readonly System.Collections.Generic.List<InputDevice> _rightHandDevices =
            new System.Collections.Generic.List<InputDevice>(2);

        // 用于做 primaryButton 的“边沿触发”
        private bool _lastPrimaryButtonPressed = false;

        // 每个试次只允许触发一次（逻辑防抖）
        private bool _hasTriggeredForThisTrial = false;

        // 时间防抖：两次触发之间的最小间隔（秒），用于避免长按/抖动造成的多次跳转
        [Tooltip("两次试次跳转之间的最小间隔时间（秒），用于防止长按 A 键导致多次连续跳转")]
        [SerializeField] private float minTriggerIntervalSeconds = 1.0f;

        // 上一次成功触发跳转的时间（Time.time）
        private float _lastTriggerTime = -999f;

        private void Awake()
        {
            if (autoFindEventBus && eventBus == null)
            {
                eventBus = EventBusManager.Instance;
            }

            if (eventBus == null)
            {
                Debug.LogWarning("[HumanInputKeyboardBridge] EventBusManager not found. Input bridge will be disabled.");
            }
        }

        private void OnEnable()
        {
            if (eventBus != null)
            {
                eventBus.TrialLifecycle?.Subscribe(OnTrialLifecycle);
                if (verboseLog)
                {
                    Debug.Log("[HumanInputKeyboardBridge] Subscribed TrialLifecycle.");
                }
            }
            else
            {
                Debug.LogWarning("[HumanInputKeyboardBridge] eventBus is null in OnEnable");
            }
        }

        private void OnDisable()
        {
            eventBus?.TrialLifecycle?.Unsubscribe(OnTrialLifecycle);
            _awaitingInput = false;
            _lastPrimaryButtonPressed = false;
        }

        private void Update()
        {
            // 总开关：完全关闭桥接
            if (!useKeyboardInput) return;

            // 仅在等待人类输入时响应（由 TrialLifecycle=WaitingForInput 控制）
            if (!_awaitingInput) return;

            if (eventBus == null) return;

            // 如果本 trial 已经触发过一次，直接忽略后续所有输入（防长按连跳）
            if (_hasTriggeredForThisTrial) return;

            // 时间防抖：两次触发之间的最小间隔限制
            if (Time.time - _lastTriggerTime < minTriggerIntervalSeconds)
            {
                // 上一次触发离现在太近，丢弃当前输入
                return;
            }

            // Editor 下先确认键盘 A 是否工作，便于快速排查问题
#if UNITY_EDITOR
            if (allowKeyboardAInEditor && Input.GetKeyDown(KeyCode.A))
            {
                if (verboseLog)
                {
                    Debug.Log("[HumanInputKeyboardBridge] Editor keyboard 'A' pressed, sending answer.");
                }

                // 标记本 trial 已经触发过一次
                _hasTriggeredForThisTrial = true;
                _lastTriggerTime = Time.time; // 记录触发时间
                SendKeyboardAnswer();
                return;
            }
#endif

            // 运行在设备上的主要路径：XR 右手柄 primaryButton（PICO 右手柄 A）
            if (ShouldTriggerByInput())
            {
                if (verboseLog)
                {
                    Debug.Log("[HumanInputKeyboardBridge] XR right primaryButton edge detected, sending answer.");
                }

                // 标记本 trial 已经触发过一次，防止长按在多个帧/多个 WaitingForInput 中连锁触发
                _hasTriggeredForThisTrial = true;
                _lastTriggerTime = Time.time; // 记录触发时间
                SendKeyboardAnswer();
            }
        }

        /// <summary>
        /// 统一判断是否有“按键触发”：
        /// - 优先检测 XR 右手柄 primaryButton（在 PICO 上即右手柄 A 键）
        /// - 在 Editor 环境下，可选允许键盘 A 做调试
        /// </summary>
        private bool ShouldTriggerByInput()
        {
            bool triggered = false;

            // 1. XR 右手柄 primaryButton（PICO 右手柄 A）
            if (useRightControllerPrimaryButton)
            {
                _rightHandDevices.Clear();
                InputDevices.GetDevicesAtXRNode(XRNode.RightHand, _rightHandDevices);

                bool primaryDownThisFrame = false;

                for (int i = 0; i < _rightHandDevices.Count; i++)
                {
                    var device = _rightHandDevices[i];

                    if (!device.isValid)
                        continue;

                    // 使用 TryGetFeatureCommonUsages.primaryButton
                    if (device.TryGetFeatureValue(CommonUsages.primaryButton, out bool pressed) && pressed)
                    {
                        primaryDownThisFrame = true;
                        break;
                    }
                }

                // 边沿触发：上一帧未按，这一帧按下
                if (primaryDownThisFrame && !_lastPrimaryButtonPressed)
                {
                    triggered = true;
                }

                // 重要：一旦检测到触发，就立即将状态视为“已按下”，
                // 避免在本帧/后续帧中被再次作为“新按下”识别
                _lastPrimaryButtonPressed = primaryDownThisFrame;
            }

#if UNITY_EDITOR
            // 2. Editor 下可选的键盘 A 调试入口
            if (!triggered && allowKeyboardAInEditor)
            {
                if (Input.GetKeyDown(KeyCode.A))
                {
                    triggered = true;
                }
            }
#endif

            return triggered;
        }

        private void OnTrialLifecycle(TrialLifecycleEventData data)
        {
            if (data == null) return;

            if (data.state == TrialLifecycleState.WaitingForInput)
            {
                _awaitingInput = true;
                _taskId = data.taskId;
                _trialId = data.trialId;

                // 新 trial 开始等待输入时，重置每试次触发标记
                _hasTriggeredForThisTrial = false;

                if (verboseLog)
                {
                    Debug.Log($"[HumanInputKeyboardBridge] WaitingForInput {data.taskId}/{data.trialId}. Press PICO Right A / Keyboard 'A' (Editor) to continue.");
                }
            }
            else if (data.state == TrialLifecycleState.Completed ||
                     data.state == TrialLifecycleState.Failed ||
                     data.state == TrialLifecycleState.Cancelled)
            {
                // 当前试次结束时，停止等待输入
                _awaitingInput = false;
                _lastPrimaryButtonPressed = false;
                _hasTriggeredForThisTrial = false;
            }
        }

        private void SendKeyboardAnswer()
        {
            // 这里不再依赖 _awaitingInput / _hasTriggeredForThisTrial 做早期 return，
            // 因为在 Update() 里已经做了严格的状态和防抖控制。
            // 这样可以避免状态不同步时“第一次也发不出去”的问题。

            var taskId = _taskId;
            var trialId = _trialId;

            if (string.IsNullOrEmpty(taskId) && !allowGenericAnswer)
            {
                if (verboseLog)
                {
                    Debug.LogWarning("[HumanInputKeyboardBridge] taskId is empty and generic answers are disabled.");
                }
                return;
            }

            var response = BuildResponseForTask(taskId, trialId);
            if (response == null)
            {
                if (verboseLog)
                {
                    Debug.LogWarning($"[HumanInputKeyboardBridge] No response built for taskId='{taskId}', genericAnswer={allowGenericAnswer}");
                }
                return;
            }

            var data = new InferenceReceivedEventData
            {
                requestId = "human_keyboard_" + Guid.NewGuid().ToString("N"),
                taskId = response.taskId,
                trialId = response.trialId,
                timestamp = DateTime.UtcNow,
                providerId = response.providerId,
                response = response
            };

            try
            {
                eventBus?.InferenceReceived?.Publish(data);
                if (verboseLog)
                {
                    Debug.Log($"[HumanInputKeyboardBridge] Published input inference for {response.taskId}/{response.trialId} via provider='{response.providerId}'.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[HumanInputKeyboardBridge] Failed to publish inference: {ex.Message}");
            }

            _awaitingInput = false;
        }

        private LLMResponse BuildResponseForTask(string taskId, int trialId)
        {
            // 复用与 HumanInputHandler / WSHumanInputDialog 一致的编码方式：
            // - providerId = "human"
            // - answer 为任务特定结构体
            float conf = Mathf.Clamp01(defaultConfidence);

            if (string.Equals(taskId, "distance_compression", StringComparison.OrdinalIgnoreCase))
            {
                var answer = new DistanceAnswer
                {
                    distance_m = defaultDistanceMeters,
                    confidence = conf
                };

                return new LLMResponse
                {
                    type = "inference",
                    taskId = taskId,
                    trialId = trialId,
                    providerId = "human",
                    confidence = conf,
                    latencyMs = 0,
                    answer = answer
                };
            }

            if (string.Equals(taskId, "semantic_size_bias", StringComparison.OrdinalIgnoreCase))
            {
                // 默认选择 A（与 WSHumanInputDialog 中默认项一致）
                var answer = new SizeAnswer
                {
                    larger = "A",
                    confidence = conf
                };

                return new LLMResponse
                {
                    type = "inference",
                    taskId = taskId,
                    trialId = trialId,
                    providerId = "human",
                    confidence = conf,
                    latencyMs = 0,
                    answer = answer
                };
            }

            if (!allowGenericAnswer)
            {
                return null;
            }

            // 通用占位响应：用在“只需要按 A 继续，不关心具体答案”的场景
            var generic = new GenericContinueAnswer
            {
                note = "keyboard_or_controller_continue",
                confidence = conf
            };

            return new LLMResponse
            {
                type = "inference",
                taskId = string.IsNullOrEmpty(taskId) ? "unknown" : taskId,
                trialId = trialId,
                providerId = "human",
                confidence = conf,
                latencyMs = 0,
                answer = generic
            };
        }

        [Serializable]
        private class DistanceAnswer
        {
            public float distance_m;
            public float confidence;
        }

        [Serializable]
        private class SizeAnswer
        {
            public string larger; // "A"|"B"
            public float confidence;
        }

        [Serializable]
        private class GenericContinueAnswer
        {
            public string note;
            public float confidence;
        }
    }
}