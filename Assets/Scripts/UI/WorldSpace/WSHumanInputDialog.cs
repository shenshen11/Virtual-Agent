using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using TMPro;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;

namespace VRPerception.UI
{
    /// <summary>
    /// 世界空间人类输入面板（uGUI 版）
    /// - 订阅 TrialLifecycle，当 state=WaitingForInput 时显示弹窗
    /// - 支持 DistanceCompression 与 SemanticSizeBias 两类任务
    /// - 提交后构造 InferenceReceivedEventData（providerId="human" 或 "human_skip"）
    /// - 运行时替代 IMGUI 的 [C#.HumanInputHandler.OnGUI()](Assets/Scripts/UI/HumanInputHandler.cs:109)，Editor 可继续使用旧实现
    /// </summary>
    [RequireComponent(typeof(Canvas))]
    public class WSHumanInputDialog : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private EventBusManager eventBus;
        [SerializeField] private bool autoFindEventBus = true;

        [Header("UI Roots")]
        [Tooltip("弹窗根节点（整体显示/隐藏用）")]
        [SerializeField] private GameObject dialogRoot;
        [Tooltip("遮罩背景，可选")]
        [SerializeField] private GameObject backdrop;

        [Header("Common Widgets")]
        [SerializeField] private TMP_Text taskLabel;
        [SerializeField] private TMP_Text trialLabel;
        [SerializeField] private TMP_Text taskPromptText;
        [SerializeField] private Slider confidenceSlider;
        [SerializeField] private TMP_Text confidenceValueText;
        [SerializeField] private Button submitButton;
        [SerializeField] private Button skipButton;

        [Header("Distance Compression")]
        [SerializeField] private GameObject distanceGroup;
        [SerializeField] private TMP_InputField distanceInput;

        [Header("Semantic Size Bias")]
        [SerializeField] private GameObject sizeBiasGroup;
        [SerializeField] private Toggle optionAToggle;
        [SerializeField] private Toggle optionBToggle;
        [SerializeField] private ToggleGroup sizeToggleGroup;

        [Header("UX Settings")]
        [Tooltip("显示弹窗时是否自动选中第一个输入框")]
        [SerializeField] private bool autoFocusInput = true;
        [Tooltip("提交失败时的提示文本（可选）")]
        [SerializeField] private TMP_Text errorHint;

        [Header("Rendering Settings")]
        [Tooltip("Canvas 排序顺序，数值越大越靠前（建议 100+ 确保在所有 3D 物体前面）")]
        [SerializeField] private int canvasSortingOrder = 100;
        [Tooltip("是否强制 Canvas 始终渲染在最前面（覆盖深度测试）")]
        [SerializeField] private bool alwaysOnTop = true;

        private bool _awaitingInput;
        private string _taskId = string.Empty;
        private int _trialId = -1;
        private float _awaitingInputSinceRealtime;
        private Coroutine _ensureSubscribeRoutine;

        private Canvas _canvas;

        private void Awake()
        {
            _canvas = GetComponent<Canvas>();
            if (_canvas != null)
            {
                _canvas.renderMode = RenderMode.WorldSpace;
                if (_canvas.worldCamera == null && Camera.main != null)
                    _canvas.worldCamera = Camera.main;

                // 设置 Canvas 排序顺序，确保在其他 UI 和 3D 物体前面
                _canvas.sortingOrder = canvasSortingOrder;

                // 如果启用 alwaysOnTop，设置 overrideSorting 确保不被 3D 物体遮挡
                if (alwaysOnTop)
                {
                    _canvas.overrideSorting = true;
                    _canvas.sortingOrder = canvasSortingOrder;
                }

                if (GetComponent<TrackedDeviceGraphicRaycaster>() == null)
                    gameObject.AddComponent<TrackedDeviceGraphicRaycaster>();
            }

            if (autoFindEventBus && eventBus == null)
                eventBus = EventBusManager.Instance;

            HideDialog();
            HookUIEvents(true);
            UpdateConfidenceLabel(confidenceSlider != null ? confidenceSlider.value : 0.9f);
        }

        private void OnEnable()
        {
            SubscribeEvents();
            if (_ensureSubscribeRoutine == null)
                _ensureSubscribeRoutine = StartCoroutine(EnsureSubscribe());
        }

        private void OnDisable()
        {
            UnsubscribeEvents();
            if (_ensureSubscribeRoutine != null)
            {
                StopCoroutine(_ensureSubscribeRoutine);
                _ensureSubscribeRoutine = null;
            }
        }

        private void OnDestroy()
        {
            HookUIEvents(false);
        }

        private void SubscribeEvents()
        {
            eventBus?.TrialLifecycle?.Subscribe(OnTrialLifecycle);
        }

        private void UnsubscribeEvents()
        {
            eventBus?.TrialLifecycle?.Unsubscribe(OnTrialLifecycle);
        }

        private IEnumerator EnsureSubscribe()
        {
            const float timeout = 3f;
            float start = Time.realtimeSinceStartup;

            if (eventBus == null && autoFindEventBus)
            {
                while (eventBus == null && Time.realtimeSinceStartup - start < timeout)
                {
                    yield return null;
                    eventBus = EventBusManager.Instance;
                }
            }

            if (eventBus == null)
                yield break;

            while (eventBus.TrialLifecycle == null && Time.realtimeSinceStartup - start < timeout)
            {
                yield return null;
            }

            if (eventBus.TrialLifecycle != null)
            {
                eventBus.TrialLifecycle.Unsubscribe(OnTrialLifecycle); // 避免重复
                eventBus.TrialLifecycle.Subscribe(OnTrialLifecycle);
            }

            _ensureSubscribeRoutine = null;
        }

        private void OnTrialLifecycle(TrialLifecycleEventData data)
        {
            if (data == null) return;

            if (data.state == TrialLifecycleState.WaitingForInput)
            {
                _awaitingInput = true;
                _taskId = data.taskId;
                _trialId = data.trialId;
                _awaitingInputSinceRealtime = Time.realtimeSinceStartup;
                PrepareDialogForTask(_taskId, data.humanInputPrompt);
                ShowDialog();
            }
            else if (data.state == TrialLifecycleState.Completed ||
                     data.state == TrialLifecycleState.Failed ||
                     data.state == TrialLifecycleState.Cancelled)
            {
                _awaitingInput = false;
                HideDialog();
            }
        }

        private void PrepareDialogForTask(string taskId, string customPrompt = null)
        {
            bool isDistance = string.Equals(taskId, "distance_compression", StringComparison.OrdinalIgnoreCase);
            bool isSizeBias = string.Equals(taskId, "semantic_size_bias", StringComparison.OrdinalIgnoreCase);
            bool isNumerosity = string.Equals(taskId, "numerosity_comparison", StringComparison.OrdinalIgnoreCase);

            if (taskLabel != null) taskLabel.text = $"任务: {taskId}";
            if (trialLabel != null) trialLabel.text = $"试次: {_trialId}";
            if (errorHint != null) errorHint.text = string.Empty;

            // 设置任务提示文本：优先使用自定义提示，否则使用默认提示
            if (taskPromptText != null)
            {
                if (!string.IsNullOrWhiteSpace(customPrompt))
                {
                    taskPromptText.text = customPrompt;
                }
                else if (isDistance)
                {
                    taskPromptText.text = "请估计您与目标物体之间的距离（单位：米），并设置您的置信度。";
                }
                else if (isSizeBias)
                {
                    taskPromptText.text = "请选择您认为更大的对象（A 或 B），并设置您的置信度。";
                }
                else if (isNumerosity)
                {
                    taskPromptText.text = "请在黑屏后判断哪一侧点更多，并设置置信度（A=Left，B=Right）。";
                }
                else
                {
                    taskPromptText.text = "请根据任务要求完成输入。";
                }
            }

            if (distanceGroup != null) distanceGroup.SetActive(isDistance);
            if (sizeBiasGroup != null) sizeBiasGroup.SetActive(isSizeBias || isNumerosity);

            if (isDistance && distanceInput != null)
            {
                distanceInput.text = "10.0";
                distanceInput.contentType = TMP_InputField.ContentType.DecimalNumber;
            }

            if (confidenceSlider != null)
            {
                confidenceSlider.value = 0.9f;
                UpdateConfidenceLabel(confidenceSlider.value);
                confidenceSlider.wholeNumbers = false;
            }

            if (isSizeBias && sizeToggleGroup != null)
            {
                // 默认选中 A
                if (optionAToggle != null)
                {
                    optionAToggle.isOn = true;
                    if (optionBToggle != null) optionBToggle.isOn = false;
                }
            }
            else if (isNumerosity && sizeToggleGroup != null)
            {
                // 复用 A/B 两个选项作为 Left/Right
                if (optionAToggle != null)
                {
                    optionAToggle.isOn = true;
                    if (optionBToggle != null) optionBToggle.isOn = false;
                }
            }
        }

        private void ShowDialog()
        {
            if (dialogRoot != null) dialogRoot.SetActive(true);
            if (backdrop != null) backdrop.SetActive(true);

            // 强制设置渲染队列，确保 UI 在 3D 物体前面
            if (alwaysOnTop)
            {
                ForceUIRenderQueue();
            }

            if (autoFocusInput)
            {
                if (distanceGroup != null && distanceGroup.activeSelf && distanceInput != null)
                {
                    distanceInput.Select();
                    distanceInput.ActivateInputField();
                }
                else if (sizeBiasGroup != null && sizeBiasGroup.activeSelf && optionAToggle != null)
                {
                    optionAToggle.Select();
                }
            }
        }

        private void HideDialog()
        {
            if (dialogRoot != null) dialogRoot.SetActive(false);
            if (backdrop != null) backdrop.SetActive(false);

            // 显式隐藏任务特定的 Group，确保它们不会残留
            if (distanceGroup != null) distanceGroup.SetActive(false);
            if (sizeBiasGroup != null) sizeBiasGroup.SetActive(false);
        }

        private void HookUIEvents(bool bind)
        {
            if (bind)
            {
                if (confidenceSlider != null) confidenceSlider.onValueChanged.AddListener(UpdateConfidenceLabel);
                if (submitButton != null) submitButton.onClick.AddListener(SubmitCurrent);
                if (skipButton != null) skipButton.onClick.AddListener(SkipCurrent);
            }
            else
            {
                if (confidenceSlider != null) confidenceSlider.onValueChanged.RemoveListener(UpdateConfidenceLabel);
                if (submitButton != null) submitButton.onClick.RemoveListener(SubmitCurrent);
                if (skipButton != null) skipButton.onClick.RemoveListener(SkipCurrent);
            }
        }

        private void UpdateConfidenceLabel(float value)
        {
            if (confidenceValueText != null)
                confidenceValueText.text = $"置信度: {value:F2}";
        }

        private void SubmitCurrent()
        {
            if (!_awaitingInput || eventBus == null)
            {
                if (errorHint != null) errorHint.text = "当前无待提交的试次。";
                return;
            }

            float confidence = confidenceSlider != null ? Mathf.Clamp01(confidenceSlider.value) : 0.9f;
            long reactionMs = 0;
            try
            {
                reactionMs = (long)Mathf.Max(0f, (Time.realtimeSinceStartup - _awaitingInputSinceRealtime) * 1000f);
            }
            catch { }

            if (distanceGroup != null && distanceGroup.activeSelf)
            {
                float distance = 0f;
                if (distanceInput != null)
                {
                    if (!float.TryParse(distanceInput.text, out distance))
                    {
                        if (errorHint != null) errorHint.text = "请输入合法的距离数值。";
                        return;
                    }
                }
                PublishDistance(distance, confidence, reactionMs);
            }
            else if (sizeBiasGroup != null && sizeBiasGroup.activeSelf)
            {
                if (string.Equals(_taskId, "numerosity_comparison", StringComparison.OrdinalIgnoreCase))
                {
                    string moreSide = optionAToggle != null && optionAToggle.isOn ? "left" : "right";
                    PublishNumerosity(moreSide, confidence, reactionMs);
                }
                else
                {
                    string larger = optionAToggle != null && optionAToggle.isOn ? "A" : "B";
                    PublishSize(larger, confidence, reactionMs);
                }
            }
            else
            {
                if (errorHint != null) errorHint.text = "当前任务类型未提供人类输入面板。";
                return;
            }

            _awaitingInput = false;
            HideDialog();
        }

        private void SkipCurrent()
        {
            if (!_awaitingInput || eventBus == null)
                return;

            var data = new InferenceReceivedEventData
            {
                requestId = "human_skip_" + Guid.NewGuid().ToString("N"),
                taskId = _taskId,
                trialId = _trialId,
                timestamp = DateTime.UtcNow,
                providerId = "human_skip",
                response = new LLMResponse
                {
                    taskId = _taskId,
                    trialId = _trialId,
                    providerId = "human_skip",
                    type = "inference",
                    confidence = 0f,
                    latencyMs = 0,
                    answer = null
                }
            };

            try { eventBus.InferenceReceived?.Publish(data); } catch { }

            _awaitingInput = false;
            HideDialog();
        }

        private void PublishDistance(float distance, float confidence, long reactionMs)
        {
            var response = new LLMResponse
            {
                type = "inference",
                taskId = _taskId,
                trialId = _trialId,
                providerId = "human",
                confidence = confidence,
                latencyMs = reactionMs,
                answer = new DistanceAnswer { distance_m = distance, confidence = confidence }
            };

            PublishResponse(response);
        }

        private void PublishSize(string larger, float confidence, long reactionMs)
        {
            var response = new LLMResponse
            {
                type = "inference",
                taskId = _taskId,
                trialId = _trialId,
                providerId = "human",
                confidence = confidence,
                latencyMs = reactionMs,
                answer = new SizeAnswer { larger = larger, confidence = confidence }
            };

            PublishResponse(response);
        }

        private void PublishNumerosity(string moreSide, float confidence, long reactionMs)
        {
            var response = new LLMResponse
            {
                type = "inference",
                taskId = _taskId,
                trialId = _trialId,
                providerId = "human",
                confidence = confidence,
                latencyMs = reactionMs,
                answer = new NumerosityAnswer { more_side = moreSide, confidence = confidence }
            };

            PublishResponse(response);
        }

        private void PublishResponse(LLMResponse response)
        {
            var data = new InferenceReceivedEventData
            {
                requestId = "human_" + Guid.NewGuid().ToString("N"),
                taskId = response.taskId,
                trialId = response.trialId,
                timestamp = DateTime.UtcNow,
                providerId = response.providerId,
                response = response
            };

            try { eventBus?.InferenceReceived?.Publish(data); } catch { }
        }

        /// <summary>
        /// 强制设置 Canvas 及其所有子 UI 元素的渲染队列，确保在 3D 物体前面渲染
        /// </summary>
        private void ForceUIRenderQueue()
        {
            if (_canvas == null) return;

            // 获取所有 Graphic 组件（Image, Text, etc.）
            var graphics = _canvas.GetComponentsInChildren<UnityEngine.UI.Graphic>(true);
            foreach (var graphic in graphics)
            {
                if (graphic.material != null)
                {
                    // 设置渲染队列为 Overlay (3000+)，确保在所有不透明和透明物体之后渲染
                    graphic.material.renderQueue = 3000;
                }
            }
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
            public string larger;
            public float confidence;
        }

        [Serializable]
        private class NumerosityAnswer
        {
            public string more_side;
            public float confidence;
        }
    }
}
