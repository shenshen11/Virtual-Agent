using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using TMPro;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;
using VRPerception.Tasks;

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
        [SerializeField] private GameObject confidenceRatingGroup;
        [SerializeField] private ToggleGroup confidenceToggleGroup;
        [SerializeField] private Toggle confidence1Toggle;
        [SerializeField] private Toggle confidence2Toggle;
        [SerializeField] private Toggle confidence3Toggle;
        [SerializeField] private Toggle confidence4Toggle;
        [SerializeField] private Toggle confidence5Toggle;
        [SerializeField] private TMP_Text confidenceValueText;
        [SerializeField] private Button submitButton;
        [SerializeField] private Button skipButton;

        [Header("Distance Estimation")]
        [SerializeField] private GameObject distanceGroup;
        [SerializeField] private TMP_InputField distanceInput;

        [Header("Semantic Size Bias")]
        [SerializeField] private GameObject sizeBiasGroup;
        [SerializeField] private Toggle optionAToggle;
        [SerializeField] private Toggle optionBToggle;
        [SerializeField] private ToggleGroup sizeToggleGroup;

        [Header("Visual Weight Judgment")]
        [SerializeField] private GameObject visualWeightGroup;
        [SerializeField] private Toggle visualWeightAToggle;
        [SerializeField] private Toggle visualWeightBToggle;
        [SerializeField] private Toggle visualWeightCToggle;
        [SerializeField] private ToggleGroup visualWeightToggleGroup;
        [SerializeField] private Toggle visualWeightMaterialToggle;
        [SerializeField] private Toggle visualWeightSizeToggle;
        [SerializeField] private Toggle visualWeightLightnessToggle;

        [Header("Visual Crowding")]
        [SerializeField] private GameObject visualCrowdingGroup;
        [SerializeField] private TMP_InputField visualCrowdingLetterInput;

        [Header("Change Detection")]
        [SerializeField] private GameObject changeDetectionGroup;
        [SerializeField] private Toggle changeNoToggle;
        [SerializeField] private Toggle changeAppearanceToggle;
        [SerializeField] private Toggle changeDisappearanceToggle;
        [SerializeField] private Toggle changeReplacementToggle;
        [SerializeField] private Toggle changeMovementToggle;
        [SerializeField] private ToggleGroup changeDetectionToggleGroup;

        [Header("Depth JND")]
        [SerializeField] private GameObject depthJndGroup;
        [SerializeField] private Toggle depthJndLeftToggle;
        [SerializeField] private Toggle depthJndRightToggle;
        [SerializeField] private ToggleGroup depthJndToggleGroup;

        [Header("Material Roughness")]
        [SerializeField] private GameObject roughnessGroup;
        [SerializeField] private Slider roughnessSlider;
        [SerializeField] private TMP_Text roughnessValueText;

        [Header("Color Constancy (Adjustment)")]
        [SerializeField] private GameObject colorGroup;
        [SerializeField] private Slider colorRSlider;
        [SerializeField] private Slider colorGSlider;
        [SerializeField] private Slider colorBSlider;
        [SerializeField] private TMP_Text colorRValueText;
        [SerializeField] private TMP_Text colorGValueText;
        [SerializeField] private TMP_Text colorBValueText;
        [SerializeField] private Image colorPreviewImage;

        [Header("Motion Gate (Roughness)")]
        [Tooltip("当 trial.requireHeadMotion=true 时，是否要求头动达到阈值才允许提交（用于 optic flow 条件）。")]
        [SerializeField] private bool enableHeadMotionGate = true;
        [Tooltip("要求头部 yaw 峰峰值（度），达到后才可提交。")]
        [SerializeField] private float requiredYawRangeDeg = 20f;
        [Tooltip("可选：用于显示门控状态的提示文本。")]
        [SerializeField] private TMP_Text motionGateHint;

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
        private const int NoConfidenceRating = 0;
        private int _confidenceRating = NoConfidenceRating;
        private bool _syncingConfidenceToggles;
        private bool _isDistanceAnchorTrial;
        private float _currentTrueDistanceM;
        private TouchScreenKeyboard _softKeyboard;
        private TMP_InputField _activeSoftKeyboardInput;
        private bool _openingSoftKeyboard;
        private TMP_Text _submitButtonText;
        private string _submitButtonDefaultText;
        private bool _submitButtonDefaultTextCaptured;

        private Canvas _canvas;

        // Motion gate state (roughness)
        private bool _requireHeadMotion;
        private bool _yawInit;
        private float _lastYawDeg;
        private float _unwrappedYawDeg;
        private float _minYawDeg;
        private float _maxYawDeg;

        private ColorAdjustableTarget _colorTarget;

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
            ClearConfidenceRating();
            HookUIEvents(true);
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

                _requireHeadMotion = false;
                _isDistanceAnchorTrial = false;
                _currentTrueDistanceM = 0f;
                if (data.trialConfig is TrialSpec ts)
                {
                    _requireHeadMotion = ts.requireHeadMotion;
                    _isDistanceAnchorTrial = IsDistanceEstimationTask(data.taskId) && ts.isAnchor;
                    _currentTrueDistanceM = ts.trueDistanceM;
                }

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

        private static bool IsDistanceEstimationTask(string taskId)
        {
            return string.Equals(taskId, "distance_compression", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(taskId, "horizon_cue_integration", StringComparison.OrdinalIgnoreCase);
        }

        private void PrepareDialogForTask(string taskId, string customPrompt = null)
        {
            bool isDistance = IsDistanceEstimationTask(taskId);
            bool isHorizonCue = string.Equals(taskId, "horizon_cue_integration", StringComparison.OrdinalIgnoreCase);
            bool isSizeBias = string.Equals(taskId, "semantic_size_bias", StringComparison.OrdinalIgnoreCase);
            bool isRoughness = !string.IsNullOrWhiteSpace(taskId) && taskId.StartsWith("material_roughness", StringComparison.OrdinalIgnoreCase);
            bool isColor = string.Equals(taskId, "color_constancy_adjustment", StringComparison.OrdinalIgnoreCase);
            bool isNumerosity = string.Equals(taskId, "numerosity_comparison", StringComparison.OrdinalIgnoreCase);
            bool isDepthJnd = string.Equals(taskId, "depth_jnd_staircase", StringComparison.OrdinalIgnoreCase);
            bool isVisualWeight = string.Equals(taskId, "visual_weight_judgment", StringComparison.OrdinalIgnoreCase);
            bool isVisualCrowding = string.Equals(taskId, "visual_crowding", StringComparison.OrdinalIgnoreCase);
            bool isChangeDetection = string.Equals(taskId, "change_detection", StringComparison.OrdinalIgnoreCase);

            if (taskLabel != null) taskLabel.text = $"任务: {taskId}";
            if (trialLabel != null) trialLabel.text = $"试次: {_trialId}";
            if (errorHint != null) errorHint.text = string.Empty;
            if (motionGateHint != null) motionGateHint.text = string.Empty;
            if (submitButton != null) submitButton.interactable = true;
            if (skipButton != null) skipButton.gameObject.SetActive(!_isDistanceAnchorTrial && !isDepthJnd);
            SetSubmitButtonText(_isDistanceAnchorTrial ? "继续" : null);

            if (taskPromptText != null)
            {
                if (isDistance && _isDistanceAnchorTrial)
                {
                    taskPromptText.text = isHorizonCue
                        ? $"校准试次：当前红色球体的真实距离为 {_currentTrueDistanceM:0.##} 米。\n请观察并记住该距离感，然后点击继续。"
                        : $"校准试次：当前目标物体的真实距离为 {_currentTrueDistanceM:0.##} 米。\n请观察并记住该距离感，然后点击继续。";
                }
                else if (isDepthJnd)
                {
                    taskPromptText.text = "请选择看起来更近的物体，并设置您的置信度。";
                }
                else if (isVisualWeight)
                {
                    taskPromptText.text = string.Empty;
                }
                else if (isVisualCrowding)
                {
                    taskPromptText.text = !string.IsNullOrWhiteSpace(customPrompt)
                        ? customPrompt
                        : "字母已隐藏。请回忆右侧外周显示的字母；若为 5 字母串，请输入中间目标字母，并设置置信度。";
                }
                else if (isChangeDetection)
                {
                    taskPromptText.text = !string.IsNullOrWhiteSpace(customPrompt)
                        ? customPrompt
                        : "请判断前后两个场景是否发生变化，选择变化类型，并设置置信度。";
                }
                else if (!string.IsNullOrWhiteSpace(customPrompt))
                {
                    taskPromptText.text = customPrompt;
                }
                else if (isDistance)
                {
                    taskPromptText.text = isHorizonCue
                        ? "请估计正前方红色球体与您的距离（单位：米），并设置您的置信度。"
                        : "请估计您与目标物体之间的距离（单位：米），并设置您的置信度。";
                }
                else if (isSizeBias)
                {
                    taskPromptText.text = "请选择您认为更大的对象（A 或 B），并设置您的置信度。";
                }
                else if (isRoughness)
                {
                    taskPromptText.text = "请估计金属球表面的粗糙度 roughness（0=镜面，1=完全哑光），并设置置信度。";
                    if (_requireHeadMotion)
                    {
                        taskPromptText.text += "\n本条件要求左右晃头观察高光变化后再提交。";
                    }
                }
                else if (isColor)
                {
                    taskPromptText.text = "请调节球体颜色至您认为的“视觉灰色”，并提交当前 RGB。";
                }
                else if (isNumerosity)
                {
                    taskPromptText.text = "请选择哪一侧点更多，并设置置信度（A=左侧 Left，B=右侧 Right）。";
                }
                else
                {
                    taskPromptText.text = "请根据任务要求完成输入。";
                }
            }

            if (distanceGroup != null) distanceGroup.SetActive(isDistance && !_isDistanceAnchorTrial);
            if (sizeBiasGroup != null) sizeBiasGroup.SetActive(isSizeBias);
            if (visualWeightGroup != null) visualWeightGroup.SetActive(isVisualWeight);
            if (visualCrowdingGroup != null) visualCrowdingGroup.SetActive(isVisualCrowding);
            if (changeDetectionGroup != null) changeDetectionGroup.SetActive(isChangeDetection);
            if (depthJndGroup != null) depthJndGroup.SetActive(isDepthJnd);
            if (roughnessGroup != null) roughnessGroup.SetActive(isRoughness);
            if (isColor)
            {
                EnsureColorWidgets();
            }
            if (colorGroup != null) colorGroup.SetActive(isColor);
            if (sizeBiasGroup != null) sizeBiasGroup.SetActive(isSizeBias || isNumerosity);

            if (isDistance && !_isDistanceAnchorTrial && distanceInput != null)
            {
                distanceInput.text = "10.0";
                distanceInput.contentType = TMP_InputField.ContentType.DecimalNumber;
                distanceInput.keyboardType = TouchScreenKeyboardType.DecimalPad;
                distanceInput.lineType = TMP_InputField.LineType.SingleLine;
            }

            if (isVisualCrowding && visualCrowdingLetterInput != null)
            {
                visualCrowdingLetterInput.text = string.Empty;
                visualCrowdingLetterInput.characterLimit = 1;
                visualCrowdingLetterInput.contentType = TMP_InputField.ContentType.Alphanumeric;
                visualCrowdingLetterInput.keyboardType = TouchScreenKeyboardType.Default;
                visualCrowdingLetterInput.lineType = TMP_InputField.LineType.SingleLine;
            }

            if (confidenceSlider != null) confidenceSlider.gameObject.SetActive(false);
            if (confidenceRatingGroup != null) confidenceRatingGroup.SetActive(!_isDistanceAnchorTrial);
            ClearConfidenceRating();

            if (isRoughness)
            {
                if (roughnessSlider != null)
                {
                    roughnessSlider.wholeNumbers = false;
                    roughnessSlider.minValue = 0f;
                    roughnessSlider.maxValue = 1f;
                    roughnessSlider.value = 0.5f;
                    UpdateRoughnessLabel(roughnessSlider.value);
                }

                // 先禁用提交，等待 Update() 中门控达标（若 requireHeadMotion=false 则立即放行）
                if (submitButton != null)
                {
                    submitButton.interactable = !_requireHeadMotion || !enableHeadMotionGate;
                }

                ResetHeadMotionGate();
            }

            if (isColor)
            {
                _colorTarget = FindColorTarget();
                var startColor = _colorTarget != null ? _colorTarget.CurrentColor : new Color(0.5f, 0.5f, 0.5f);
                SetColorSliders(startColor);
                UpdateColorPreviewAndTarget();
            }

            if (isSizeBias)
            {
                ConfigureSizeChoiceToggles(true);
            }
            else if (isVisualWeight && visualWeightToggleGroup != null)
            {
                if (visualWeightAToggle != null)
                {
                    visualWeightAToggle.isOn = true;
                    if (visualWeightBToggle != null) visualWeightBToggle.isOn = false;
                    if (visualWeightCToggle != null) visualWeightCToggle.isOn = false;
                }
                if (visualWeightMaterialToggle != null) visualWeightMaterialToggle.isOn = false;
                if (visualWeightSizeToggle != null) visualWeightSizeToggle.isOn = false;
                if (visualWeightLightnessToggle != null) visualWeightLightnessToggle.isOn = false;
            }
            else if (isNumerosity)
            {
                ConfigureSizeChoiceToggles(false);
            }
            else if (isChangeDetection)
            {
                ConfigureChangeDetectionToggles();
            }
            else if (isDepthJnd && depthJndToggleGroup != null)
            {
                if (depthJndLeftToggle != null)
                {
                    depthJndLeftToggle.isOn = true;
                    if (depthJndRightToggle != null) depthJndRightToggle.isOn = false;
                }
            }
        }

        private void ConfigureSizeChoiceToggles(bool selectDefault)
        {
            if (sizeToggleGroup == null && sizeBiasGroup != null)
            {
                sizeToggleGroup = sizeBiasGroup.GetComponentInChildren<ToggleGroup>(true);
                if (sizeToggleGroup == null)
                {
                    sizeToggleGroup = sizeBiasGroup.AddComponent<ToggleGroup>();
                }
            }

            if (sizeToggleGroup != null)
            {
                sizeToggleGroup.allowSwitchOff = !selectDefault;
                if (optionAToggle != null) optionAToggle.group = sizeToggleGroup;
                if (optionBToggle != null) optionBToggle.group = sizeToggleGroup;
            }

            if (optionAToggle != null) optionAToggle.SetIsOnWithoutNotify(selectDefault);
            if (optionBToggle != null) optionBToggle.SetIsOnWithoutNotify(false);
        }

        private void ConfigureChangeDetectionToggles()
        {
            if (changeDetectionToggleGroup == null && changeDetectionGroup != null)
            {
                changeDetectionToggleGroup = changeDetectionGroup.GetComponentInChildren<ToggleGroup>(true);
                if (changeDetectionToggleGroup == null)
                {
                    changeDetectionToggleGroup = changeDetectionGroup.AddComponent<ToggleGroup>();
                }
            }

            if (changeDetectionToggleGroup != null)
            {
                changeDetectionToggleGroup.allowSwitchOff = true;
                if (changeNoToggle != null) changeNoToggle.group = changeDetectionToggleGroup;
                if (changeAppearanceToggle != null) changeAppearanceToggle.group = changeDetectionToggleGroup;
                if (changeDisappearanceToggle != null) changeDisappearanceToggle.group = changeDetectionToggleGroup;
                if (changeReplacementToggle != null) changeReplacementToggle.group = changeDetectionToggleGroup;
                if (changeMovementToggle != null) changeMovementToggle.group = changeDetectionToggleGroup;
            }

            if (changeNoToggle != null) changeNoToggle.SetIsOnWithoutNotify(false);
            if (changeAppearanceToggle != null) changeAppearanceToggle.SetIsOnWithoutNotify(false);
            if (changeDisappearanceToggle != null) changeDisappearanceToggle.SetIsOnWithoutNotify(false);
            if (changeReplacementToggle != null) changeReplacementToggle.SetIsOnWithoutNotify(false);
            if (changeMovementToggle != null) changeMovementToggle.SetIsOnWithoutNotify(false);
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
                    OpenSoftKeyboardForInput(distanceInput, TouchScreenKeyboardType.DecimalPad);
                }
                else if (sizeBiasGroup != null && sizeBiasGroup.activeSelf && optionAToggle != null)
                {
                    optionAToggle.Select();
                }
                else if (visualWeightGroup != null && visualWeightGroup.activeSelf && visualWeightAToggle != null)
                {
                    visualWeightAToggle.Select();
                }
                else if (visualCrowdingGroup != null && visualCrowdingGroup.activeSelf && visualCrowdingLetterInput != null)
                {
                    OpenSoftKeyboardForInput(visualCrowdingLetterInput, TouchScreenKeyboardType.Default);
                }
                else if (changeDetectionGroup != null && changeDetectionGroup.activeSelf && changeNoToggle != null)
                {
                    changeNoToggle.Select();
                }
                else if (depthJndGroup != null && depthJndGroup.activeSelf && depthJndLeftToggle != null)
                {
                    depthJndLeftToggle.Select();
                }
            }
        }

        private void Update()
        {
            UpdateSoftKeyboardInput();

            if (!_awaitingInput) return;

            if (roughnessGroup != null && roughnessGroup.activeSelf)
            {
                // 实时更新 roughness 文本
                if (roughnessSlider != null)
                {
                    UpdateRoughnessLabel(roughnessSlider.value);
                }

                if (!_requireHeadMotion || !enableHeadMotionGate)
                {
                    if (submitButton != null) submitButton.interactable = true;
                    if (motionGateHint != null) motionGateHint.text = string.Empty;
                    return;
                }

                var cam = _canvas != null && _canvas.worldCamera != null ? _canvas.worldCamera : Camera.main;
                if (cam == null) return;

                float yaw = cam.transform.eulerAngles.y;
                if (!_yawInit)
                {
                    _yawInit = true;
                    _lastYawDeg = yaw;
                    _unwrappedYawDeg = 0f;
                    _minYawDeg = 0f;
                    _maxYawDeg = 0f;
                }
                else
                {
                    var delta = Mathf.DeltaAngle(_lastYawDeg, yaw);
                    _unwrappedYawDeg += delta;
                    _minYawDeg = Mathf.Min(_minYawDeg, _unwrappedYawDeg);
                    _maxYawDeg = Mathf.Max(_maxYawDeg, _unwrappedYawDeg);
                    _lastYawDeg = yaw;
                }

                float range = _maxYawDeg - _minYawDeg;
                bool ok = range >= Mathf.Max(1f, requiredYawRangeDeg);

                if (submitButton != null) submitButton.interactable = ok;
                if (motionGateHint != null)
                {
                    motionGateHint.text = ok
                        ? "头动门控已达标，可以提交。"
                        : $"请左右晃头观察高光变化（当前≈{range:0}° / 需≥{requiredYawRangeDeg:0}°）";
                }
            }
        }

        private void HideDialog()
        {
            CloseSoftKeyboard();

            if (dialogRoot != null) dialogRoot.SetActive(false);
            if (backdrop != null) backdrop.SetActive(false);

            // 显式隐藏任务特定的 Group，确保它们不会残留
            if (distanceGroup != null) distanceGroup.SetActive(false);
            if (sizeBiasGroup != null) sizeBiasGroup.SetActive(false);
            if (visualCrowdingGroup != null) visualCrowdingGroup.SetActive(false);
            if (changeDetectionGroup != null) changeDetectionGroup.SetActive(false);
            if (depthJndGroup != null) depthJndGroup.SetActive(false);
            if (roughnessGroup != null) roughnessGroup.SetActive(false);
            if (colorGroup != null) colorGroup.SetActive(false);
            if (skipButton != null) skipButton.gameObject.SetActive(true);
            if (confidenceRatingGroup != null) confidenceRatingGroup.SetActive(true);
            SetSubmitButtonText(null);
        }

        private void HookUIEvents(bool bind)
        {
            if (bind)
            {
                if (confidence1Toggle != null) confidence1Toggle.onValueChanged.AddListener(OnConfidence1Changed);
                if (confidence2Toggle != null) confidence2Toggle.onValueChanged.AddListener(OnConfidence2Changed);
                if (confidence3Toggle != null) confidence3Toggle.onValueChanged.AddListener(OnConfidence3Changed);
                if (confidence4Toggle != null) confidence4Toggle.onValueChanged.AddListener(OnConfidence4Changed);
                if (confidence5Toggle != null) confidence5Toggle.onValueChanged.AddListener(OnConfidence5Changed);
                if (roughnessSlider != null) roughnessSlider.onValueChanged.AddListener(UpdateRoughnessLabel);
                if (colorRSlider != null) colorRSlider.onValueChanged.AddListener(OnColorSliderChanged);
                if (colorGSlider != null) colorGSlider.onValueChanged.AddListener(OnColorSliderChanged);
                if (colorBSlider != null) colorBSlider.onValueChanged.AddListener(OnColorSliderChanged);
                if (distanceInput != null) distanceInput.onSelect.AddListener(OnDistanceInputSelected);
                if (visualCrowdingLetterInput != null) visualCrowdingLetterInput.onSelect.AddListener(OnVisualCrowdingLetterInputSelected);
                if (submitButton != null) submitButton.onClick.AddListener(SubmitCurrent);
                if (skipButton != null) skipButton.onClick.AddListener(SkipCurrent);
            }
            else
            {
                if (confidence1Toggle != null) confidence1Toggle.onValueChanged.RemoveListener(OnConfidence1Changed);
                if (confidence2Toggle != null) confidence2Toggle.onValueChanged.RemoveListener(OnConfidence2Changed);
                if (confidence3Toggle != null) confidence3Toggle.onValueChanged.RemoveListener(OnConfidence3Changed);
                if (confidence4Toggle != null) confidence4Toggle.onValueChanged.RemoveListener(OnConfidence4Changed);
                if (confidence5Toggle != null) confidence5Toggle.onValueChanged.RemoveListener(OnConfidence5Changed);
                if (roughnessSlider != null) roughnessSlider.onValueChanged.RemoveListener(UpdateRoughnessLabel);
                if (colorRSlider != null) colorRSlider.onValueChanged.RemoveListener(OnColorSliderChanged);
                if (colorGSlider != null) colorGSlider.onValueChanged.RemoveListener(OnColorSliderChanged);
                if (colorBSlider != null) colorBSlider.onValueChanged.RemoveListener(OnColorSliderChanged);
                if (distanceInput != null) distanceInput.onSelect.RemoveListener(OnDistanceInputSelected);
                if (visualCrowdingLetterInput != null) visualCrowdingLetterInput.onSelect.RemoveListener(OnVisualCrowdingLetterInputSelected);
                if (submitButton != null) submitButton.onClick.RemoveListener(SubmitCurrent);
                if (skipButton != null) skipButton.onClick.RemoveListener(SkipCurrent);
            }
        }

        private void OnConfidence1Changed(bool isOn) { if (isOn && !_syncingConfidenceToggles) SetConfidenceRating(1); }
        private void OnConfidence2Changed(bool isOn) { if (isOn && !_syncingConfidenceToggles) SetConfidenceRating(2); }
        private void OnConfidence3Changed(bool isOn) { if (isOn && !_syncingConfidenceToggles) SetConfidenceRating(3); }
        private void OnConfidence4Changed(bool isOn) { if (isOn && !_syncingConfidenceToggles) SetConfidenceRating(4); }
        private void OnConfidence5Changed(bool isOn) { if (isOn && !_syncingConfidenceToggles) SetConfidenceRating(5); }

        private void OnDistanceInputSelected(string _)
        {
            if (_isDistanceAnchorTrial) return;
            if (distanceGroup != null && distanceGroup.activeSelf && distanceInput != null)
                OpenSoftKeyboardForInput(distanceInput, TouchScreenKeyboardType.DecimalPad);
        }

        private void OnVisualCrowdingLetterInputSelected(string _)
        {
            if (visualCrowdingGroup != null && visualCrowdingGroup.activeSelf && visualCrowdingLetterInput != null)
                OpenSoftKeyboardForInput(visualCrowdingLetterInput, TouchScreenKeyboardType.Default);
        }

        private void OpenSoftKeyboardForInput(TMP_InputField input, TouchScreenKeyboardType keyboardType)
        {
            if (input == null || _openingSoftKeyboard) return;

            _openingSoftKeyboard = true;
            try
            {
                _activeSoftKeyboardInput = input;
                input.Select();
                input.ActivateInputField();

#if UNITY_ANDROID || UNITY_IOS
                if (TouchScreenKeyboard.isSupported)
                    _softKeyboard = TouchScreenKeyboard.Open(input.text ?? string.Empty, keyboardType, false, false, false, false);
#endif
            }
            finally
            {
                _openingSoftKeyboard = false;
            }
        }

        private void UpdateSoftKeyboardInput()
        {
            if (_softKeyboard == null || _activeSoftKeyboardInput == null) return;

            _activeSoftKeyboardInput.text = _softKeyboard.text;

            if (_softKeyboard.status == TouchScreenKeyboard.Status.Done ||
                _softKeyboard.status == TouchScreenKeyboard.Status.Canceled)
            {
                _activeSoftKeyboardInput.DeactivateInputField();
                _activeSoftKeyboardInput = null;
                _softKeyboard = null;
            }
        }

        private void CloseSoftKeyboard()
        {
            if (_softKeyboard != null)
            {
                _softKeyboard.active = false;
                _softKeyboard = null;
            }

            if (_activeSoftKeyboardInput != null)
            {
                _activeSoftKeyboardInput.DeactivateInputField();
                _activeSoftKeyboardInput = null;
            }
        }

        private void SetSubmitButtonText(string overrideText)
        {
            if (submitButton == null) return;

            if (_submitButtonText == null)
                _submitButtonText = submitButton.GetComponentInChildren<TMP_Text>(true);

            if (_submitButtonText == null) return;

            if (!_submitButtonDefaultTextCaptured)
            {
                _submitButtonDefaultText = _submitButtonText.text;
                _submitButtonDefaultTextCaptured = true;
            }

            _submitButtonText.text = string.IsNullOrEmpty(overrideText) ? _submitButtonDefaultText : overrideText;
        }

        private void SetConfidenceRating(int rating)
        {
            _confidenceRating = Mathf.Clamp(rating, 1, 5);
            SyncConfidenceToggles();
            UpdateConfidenceLabel();
        }

        private void ClearConfidenceRating()
        {
            _confidenceRating = NoConfidenceRating;
            SyncConfidenceToggles();
            UpdateConfidenceLabel();
        }

        private float GetConfidenceValue()
        {
            if (_confidenceRating <= NoConfidenceRating) return 0f;
            return Mathf.Clamp01(_confidenceRating / 5f);
        }

        private void UpdateConfidenceLabel()
        {
            if (confidenceValueText != null)
                confidenceValueText.text = _confidenceRating <= NoConfidenceRating ? "置信度: 未选择" : $"置信度: {_confidenceRating}/5";
        }

        private void SyncConfidenceToggles()
        {
            _syncingConfidenceToggles = true;

            if (confidenceToggleGroup != null)
            {
                confidenceToggleGroup.allowSwitchOff = true;
                if (confidence1Toggle != null) confidence1Toggle.group = confidenceToggleGroup;
                if (confidence2Toggle != null) confidence2Toggle.group = confidenceToggleGroup;
                if (confidence3Toggle != null) confidence3Toggle.group = confidenceToggleGroup;
                if (confidence4Toggle != null) confidence4Toggle.group = confidenceToggleGroup;
                if (confidence5Toggle != null) confidence5Toggle.group = confidenceToggleGroup;
            }

            if (confidence1Toggle != null) confidence1Toggle.SetIsOnWithoutNotify(_confidenceRating == 1);
            if (confidence2Toggle != null) confidence2Toggle.SetIsOnWithoutNotify(_confidenceRating == 2);
            if (confidence3Toggle != null) confidence3Toggle.SetIsOnWithoutNotify(_confidenceRating == 3);
            if (confidence4Toggle != null) confidence4Toggle.SetIsOnWithoutNotify(_confidenceRating == 4);
            if (confidence5Toggle != null) confidence5Toggle.SetIsOnWithoutNotify(_confidenceRating == 5);

            _syncingConfidenceToggles = false;
        }

        private void UpdateRoughnessLabel(float value)
        {
            if (roughnessValueText != null)
                roughnessValueText.text = $"粗糙度: {value:F2}";
        }

        private void OnColorSliderChanged(float _)
        {
            UpdateColorValueTexts();
            UpdateColorPreviewAndTarget();
        }

        private void SetColorSliders(Color color)
        {
            if (colorRSlider != null) colorRSlider.value = Mathf.RoundToInt(color.r * 255f);
            if (colorGSlider != null) colorGSlider.value = Mathf.RoundToInt(color.g * 255f);
            if (colorBSlider != null) colorBSlider.value = Mathf.RoundToInt(color.b * 255f);
            UpdateColorValueTexts();
        }

        private void UpdateColorValueTexts()
        {
            if (colorRValueText != null && colorRSlider != null)
                colorRValueText.text = $"R:{Mathf.RoundToInt(colorRSlider.value)}";
            if (colorGValueText != null && colorGSlider != null)
                colorGValueText.text = $"G:{Mathf.RoundToInt(colorGSlider.value)}";
            if (colorBValueText != null && colorBSlider != null)
                colorBValueText.text = $"B:{Mathf.RoundToInt(colorBSlider.value)}";
        }

        private void UpdateColorPreviewAndTarget()
        {
            var color = GetColorFromSliders();
            if (colorPreviewImage != null)
            {
                colorPreviewImage.color = color;
            }

            if (_colorTarget == null)
            {
                _colorTarget = FindColorTarget();
            }

            if (_colorTarget != null)
            {
                _colorTarget.SetColor(color);
            }
        }

        private Color GetColorFromSliders()
        {
            float r = colorRSlider != null ? colorRSlider.value / 255f : 0.5f;
            float g = colorGSlider != null ? colorGSlider.value / 255f : 0.5f;
            float b = colorBSlider != null ? colorBSlider.value / 255f : 0.5f;
            return new Color(r, g, b, 1f);
        }

        private ColorAdjustableTarget FindColorTarget()
        {
            return FindObjectOfType<ColorAdjustableTarget>();
        }

        private void ResetHeadMotionGate()
        {
            _yawInit = false;
            _lastYawDeg = 0f;
            _unwrappedYawDeg = 0f;
            _minYawDeg = 0f;
            _maxYawDeg = 0f;
        }

        private void EnsureColorWidgets()
        {
            if (colorGroup != null && colorRSlider != null && colorGSlider != null && colorBSlider != null)
            {
                return;
            }

            var root = dialogRoot != null ? dialogRoot.transform : transform;

            if (colorGroup == null)
            {
                colorGroup = new GameObject("ColorConstancy", typeof(RectTransform));
                colorGroup.transform.SetParent(root, false);

                var rt = colorGroup.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.05f, 0.05f);
                rt.anchorMax = new Vector2(0.95f, 0.45f);
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;

                var bg = colorGroup.AddComponent<Image>();
                bg.color = new Color(0f, 0f, 0f, 0.25f);

                var layout = colorGroup.AddComponent<VerticalLayoutGroup>();
                layout.childControlHeight = true;
                layout.childForceExpandHeight = false;
                layout.childControlWidth = true;
                layout.childForceExpandWidth = true;
                layout.spacing = 6f;
                layout.padding = new RectOffset(8, 8, 8, 8);
            }

            if (colorPreviewImage == null)
            {
                var preview = new GameObject("ColorPreview", typeof(RectTransform), typeof(Image));
                preview.transform.SetParent(colorGroup.transform, false);
                colorPreviewImage = preview.GetComponent<Image>();
                colorPreviewImage.color = new Color(0.5f, 0.5f, 0.5f, 1f);
                var le = preview.AddComponent<LayoutElement>();
                le.preferredHeight = 18f;
            }

            TMP_Text textTemplate = taskLabel != null ? taskLabel : GetComponentInChildren<TMP_Text>(true);
            Slider sliderTemplate = colorRSlider != null ? colorRSlider : (confidenceSlider != null ? confidenceSlider : GetComponentInChildren<Slider>(true));

            if (sliderTemplate == null || textTemplate == null)
            {
                return;
            }

            if (colorRSlider == null || colorRValueText == null)
                CreateColorRow("R", textTemplate, sliderTemplate, out colorRSlider, out colorRValueText);
            if (colorGSlider == null || colorGValueText == null)
                CreateColorRow("G", textTemplate, sliderTemplate, out colorGSlider, out colorGValueText);
            if (colorBSlider == null || colorBValueText == null)
                CreateColorRow("B", textTemplate, sliderTemplate, out colorBSlider, out colorBValueText);

            HookUIEvents(false);
            HookUIEvents(true);
        }

        private void CreateColorRow(string label, TMP_Text textTemplate, Slider sliderTemplate, out Slider slider, out TMP_Text valueText)
        {
            slider = null;
            valueText = null;

            var row = new GameObject($"Row_{label}", typeof(RectTransform));
            row.transform.SetParent(colorGroup.transform, false);

            var h = row.AddComponent<HorizontalLayoutGroup>();
            h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = true;
            h.childForceExpandWidth = true;
            h.spacing = 6f;

            var labelObj = Instantiate(textTemplate.gameObject, row.transform);
            labelObj.name = $"Label_{label}";
            var labelText = labelObj.GetComponent<TMP_Text>();
            if (labelText != null)
            {
                labelText.text = label;
                labelText.fontSize = Mathf.Max(12, labelText.fontSize - 4);
                labelText.alignment = TextAlignmentOptions.MidlineLeft;
                labelText.raycastTarget = false;
            }
            var labelLE = labelObj.GetComponent<LayoutElement>() ?? labelObj.AddComponent<LayoutElement>();
            labelLE.preferredWidth = 24f;

            var sliderObj = Instantiate(sliderTemplate.gameObject, row.transform);
            sliderObj.name = $"Slider_{label}";
            sliderObj.SetActive(true);
            slider = sliderObj.GetComponent<Slider>();
            if (slider != null)
            {
                slider.onValueChanged.RemoveAllListeners();
                slider.wholeNumbers = true;
                slider.minValue = 0f;
                slider.maxValue = 255f;
                slider.value = 128f;
            }
            var sliderLE = sliderObj.GetComponent<LayoutElement>() ?? sliderObj.AddComponent<LayoutElement>();
            sliderLE.preferredWidth = 160f;
            sliderLE.flexibleWidth = 1f;

            var valueObj = Instantiate(textTemplate.gameObject, row.transform);
            valueObj.name = $"Value_{label}";
            valueText = valueObj.GetComponent<TMP_Text>();
            if (valueText != null)
            {
                valueText.text = "128";
                valueText.fontSize = Mathf.Max(12, valueText.fontSize - 4);
                valueText.alignment = TextAlignmentOptions.MidlineRight;
                valueText.raycastTarget = false;
            }
            var valueLE = valueObj.GetComponent<LayoutElement>() ?? valueObj.AddComponent<LayoutElement>();
            valueLE.preferredWidth = 56f;
        }

        private void SubmitCurrent()
        {
            if (!_awaitingInput || eventBus == null)
            {
                if (errorHint != null) errorHint.text = "当前无待提交的试次。";
                return;
            }

            float confidence = GetConfidenceValue();
            long reactionMs = 0;
            try
            {
                reactionMs = (long)Mathf.Max(0f, (Time.realtimeSinceStartup - _awaitingInputSinceRealtime) * 1000f);
            }
            catch { }

            if (_isDistanceAnchorTrial)
            {
                PublishDistanceAnchorAcknowledgement(_currentTrueDistanceM, reactionMs);
            }
            else
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
                    string moreSide = string.Empty;
                    if (optionAToggle != null && optionAToggle.isOn) moreSide = "left";
                    else if (optionBToggle != null && optionBToggle.isOn) moreSide = "right";
                    PublishNumerosity(moreSide, confidence, reactionMs);
                }
                else
                {
                    string larger = optionAToggle != null && optionAToggle.isOn ? "A" : "B";
                    PublishSize(larger, confidence, reactionMs);
                }
            }
            else if (visualWeightGroup != null && visualWeightGroup.activeSelf)
            {
                string[] evidenceCues = GetVisualWeightEvidenceCues();
                if (evidenceCues.Length == 0)
                {
                    if (errorHint != null) errorHint.text = "请至少选择一个判断依据。";
                    return;
                }

                PublishVisualWeight(GetVisualWeightChoice(), evidenceCues, confidence, reactionMs);
            }
            else if (visualCrowdingGroup != null && visualCrowdingGroup.activeSelf)
            {
                if (!TryGetVisualCrowdingLetter(out var letter))
                {
                    if (errorHint != null) errorHint.text = "请输入一个英文字母。";
                    return;
                }

                PublishVisualCrowding(letter, confidence, reactionMs);
            }
            else if (changeDetectionGroup != null && changeDetectionGroup.activeSelf)
            {
                if (!TryGetChangeDetectionAnswer(out var changed, out var category))
                {
                    if (errorHint != null) errorHint.text = "请选择变化类型。";
                    return;
                }

                PublishChangeDetection(changed, category, confidence, reactionMs);
            }
            else if (depthJndGroup != null && depthJndGroup.activeSelf)
            {
                string closer = depthJndLeftToggle != null && depthJndLeftToggle.isOn ? "A" : "B";
                PublishDepthJnd(closer, confidence, reactionMs);
            }
            else if (roughnessGroup != null && roughnessGroup.activeSelf)
            {
                if (_requireHeadMotion && enableHeadMotionGate && submitButton != null && !submitButton.interactable)
                {
                    if (errorHint != null) errorHint.text = "请先完成左右晃头观察（门控未达标）。";
                    return;
                }

                if (roughnessSlider == null)
                {
                    if (errorHint != null) errorHint.text = "Roughness Slider 未绑定，请在 Inspector 中绑定 UI。";
                    return;
                }

                PublishRoughness(Mathf.Clamp01(roughnessSlider.value), confidence);
            }
            else if (colorGroup != null && colorGroup.activeSelf)
            {
                if (colorRSlider == null || colorGSlider == null || colorBSlider == null)
                {
                    if (errorHint != null) errorHint.text = "RGB Slider 未绑定，请在 Inspector 中绑定 UI。";
                    return;
                }

                int r = Mathf.RoundToInt(colorRSlider.value);
                int g = Mathf.RoundToInt(colorGSlider.value);
                int b = Mathf.RoundToInt(colorBSlider.value);
                PublishColor(r, g, b, confidence);
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

        private void PublishDistanceAnchorAcknowledgement(float distance, long reactionMs)
        {
            var response = new LLMResponse
            {
                type = "inference",
                taskId = _taskId,
                trialId = _trialId,
                providerId = "human",
                confidence = 1f,
                latencyMs = reactionMs,
                answer = new DistanceAnswer { acknowledged_distance_m = distance, confidence = 1f }
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

        private string GetVisualWeightChoice()
        {
            if (visualWeightBToggle != null && visualWeightBToggle.isOn) return "B";
            if (visualWeightCToggle != null && visualWeightCToggle.isOn) return "C";
            return "A";
        }

        private string[] GetVisualWeightEvidenceCues()
        {
            var cues = new List<string>(3);
            if (visualWeightMaterialToggle != null && visualWeightMaterialToggle.isOn) cues.Add("material");
            if (visualWeightSizeToggle != null && visualWeightSizeToggle.isOn) cues.Add("size");
            if (visualWeightLightnessToggle != null && visualWeightLightnessToggle.isOn) cues.Add("lightness");
            return cues.ToArray();
        }

        private bool TryGetVisualCrowdingLetter(out string letter)
        {
            letter = null;
            if (visualCrowdingLetterInput == null) return false;

            var text = visualCrowdingLetterInput.text;
            if (string.IsNullOrWhiteSpace(text)) return false;

            text = text.Trim();
            if (text.Length != 1 || !char.IsLetter(text[0])) return false;

            letter = char.ToUpperInvariant(text[0]).ToString();
            return true;
        }

        private bool TryGetChangeDetectionAnswer(out bool changed, out string category)
        {
            changed = false;
            category = null;

            if (changeNoToggle != null && changeNoToggle.isOn)
            {
                category = "none";
                return true;
            }

            if (changeAppearanceToggle != null && changeAppearanceToggle.isOn)
            {
                changed = true;
                category = "appearance";
                return true;
            }

            if (changeDisappearanceToggle != null && changeDisappearanceToggle.isOn)
            {
                changed = true;
                category = "disappearance";
                return true;
            }

            if (changeReplacementToggle != null && changeReplacementToggle.isOn)
            {
                changed = true;
                category = "replacement";
                return true;
            }

            if (changeMovementToggle != null && changeMovementToggle.isOn)
            {
                changed = true;
                category = "movement";
                return true;
            }

            return false;
        }

        private void PublishVisualWeight(string heavier, string[] evidenceCues, float confidence, long reactionMs)
        {
            var response = new LLMResponse
            {
                type = "inference",
                taskId = _taskId,
                trialId = _trialId,
                providerId = "human",
                confidence = confidence,
                latencyMs = reactionMs,
                answer = new VisualWeightAnswer { heavier = heavier, evidence_cues = evidenceCues, confidence = confidence }
            };

            PublishResponse(response);
        }

        private void PublishVisualCrowding(string letter, float confidence, long reactionMs)
        {
            var response = new LLMResponse
            {
                type = "inference",
                taskId = _taskId,
                trialId = _trialId,
                providerId = "human",
                confidence = confidence,
                latencyMs = reactionMs,
                answer = new VisualCrowdingAnswer { letter = letter, confidence = confidence }
            };

            PublishResponse(response);
        }

        private void PublishChangeDetection(bool changed, string category, float confidence, long reactionMs)
        {
            var response = new LLMResponse
            {
                type = "inference",
                taskId = _taskId,
                trialId = _trialId,
                providerId = "human",
                confidence = confidence,
                latencyMs = reactionMs,
                answer = new ChangeDetectionAnswer { changed = changed, category = changed ? category : "none", confidence = confidence }
            };

            PublishResponse(response);
        }

        private void PublishDepthJnd(string closer, float confidence, long reactionMs)
        {
            var response = new LLMResponse
            {
                type = "inference",
                taskId = _taskId,
                trialId = _trialId,
                providerId = "human",
                confidence = confidence,
                latencyMs = reactionMs,
                answer = new DepthJndAnswer { closer = closer, confidence = confidence }
            };

            PublishResponse(response);
        }

        private void PublishRoughness(float roughness, float confidence)
        {
            var response = new LLMResponse
            {
                type = "inference",
                taskId = _taskId,
                trialId = _trialId,
                providerId = "human",
                confidence = confidence,
                latencyMs = 0,
                answer = new RoughnessAnswer { roughness = roughness, confidence = confidence }
            };

            PublishResponse(response);
        }

        private void PublishColor(int r, int g, int b, float confidence)
        {
            var response = new LLMResponse
            {
                type = "inference",
                taskId = _taskId,
                trialId = _trialId,
                providerId = "human",
                confidence = confidence,
                latencyMs = 0,
                answer = new ColorAnswer { color_name = "gray", rgb = new[] { r, g, b }, confidence = confidence }
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
                if (graphic == null) continue;

                // TMP_SubMeshUI 的 material getter 会在 shared material 缺失时尝试 new Material(null)，这里先走安全分支。
                if (graphic is TMP_SubMeshUI tmpSubMesh)
                {
                    SetRenderQueueSafe(tmpSubMesh.sharedMaterial);
                    continue;
                }

                if (graphic is TMP_Text tmpText)
                {
                    SetRenderQueueSafe(tmpText.fontSharedMaterial);
                }

                try
                {
                    SetRenderQueueSafe(graphic.material);
                }
                catch (ArgumentNullException)
                {
                    // 某些 UI 组件在运行期尚未准备好材质实例，跳过即可，不应中断 trial 生命周期事件。
                }
            }
        }

        private static void SetRenderQueueSafe(Material material)
        {
            if (material == null) return;

            // 设置渲染队列为 Overlay (3000+)，确保在所有不透明和透明物体之后渲染
            material.renderQueue = 3000;
        }

        [Serializable]
        private class DistanceAnswer
        {
            public float distance_m;
            public float acknowledged_distance_m;
            public float confidence;
        }

        [Serializable]
        private class SizeAnswer
        {
            public string larger;
            public float confidence;
        }

        [Serializable]
        private class VisualWeightAnswer
        {
            public string heavier;
            public string[] evidence_cues;
            public float confidence;
        }

        [Serializable]
        private class VisualCrowdingAnswer
        {
            public string letter;
            public float confidence;
        }

        [Serializable]
        private class ChangeDetectionAnswer
        {
            public bool changed;
            public string category;
            public float confidence;
        }

        [Serializable]
        private class DepthJndAnswer
        {
            public string closer;
            public float confidence;
        }

        [Serializable]
        private class RoughnessAnswer
        {
            public float roughness;
            public float confidence;
        }

        [Serializable]
        private class ColorAnswer
        {
            public string color_name;
            public int[] rgb;
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
