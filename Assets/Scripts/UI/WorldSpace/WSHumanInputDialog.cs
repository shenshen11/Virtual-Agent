using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
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
        [SerializeField] private TMP_Text confidenceValueText;
        [SerializeField] private Button submitButton;
        [SerializeField] private Button skipButton;

        [Header("Distance Compression")]
        [SerializeField] private GameObject distanceGroup;
        [SerializeField] private TMP_InputField distanceInput;
        [Tooltip("距离压缩任务专用：置信度数字输入框（0-1），替代滑块")]
        [SerializeField] private TMP_InputField confidenceNumericInput;

        [Header("Distance Compression - Slider Input (替代 TMP_InputField，避免 VR 软键盘)")]
        [Tooltip("距离 Slider，由 PICO 右手柄摇杆 Y 轴推动调整。若未在 Inspector 绑定，运行时会自动在 distanceGroup 内创建。")]
        [SerializeField] private Slider distanceSlider;
        [Tooltip("距离 Slider 当前值的只读显示文本。")]
        [SerializeField] private TMP_Text distanceValueText;
        [Tooltip("Slider 最小距离（米）。")]
        [SerializeField] private float distanceMin = 0.5f;
        [Tooltip("Slider 最大距离（米）。")]
        [SerializeField] private float distanceMax = 25f;
        [Tooltip("新 trial 开始时 Slider 的默认距离（米）。")]
        [SerializeField] private float distanceDefault = 5f;
        [Tooltip("摇杆 Y 轴推动时距离变化速率 (m/s)。")]
        [SerializeField] private float distanceStickRateMps = 5f;
        [Tooltip("摇杆死区，绝对值小于该值视为静止。")]
        [SerializeField] private float stickDeadzone = 0.2f;

        [Header("Confidence (1-5 离散评分，替代 0-1 数字/滑块)")]
        [Tooltip("置信度 5 个 Toggle 的容器；若未绑定将运行时创建。")]
        [SerializeField] private GameObject confidenceLevelGroup;
        [Tooltip("5 个 Toggle，索引 0-4 对应 level 1-5。若长度不为 5 将按需创建。")]
        [SerializeField] private Toggle[] confidenceLevelToggles;
        [Tooltip("ToggleGroup，确保 5 个 Toggle 互斥。")]
        [SerializeField] private ToggleGroup confidenceLevelToggleGroup;
        [Tooltip("当前选中等级的提示文本（可选）。")]
        [SerializeField] private TMP_Text confidenceLevelLabel;
        [Tooltip("默认选中等级 (1..5)。")]
        [SerializeField, Range(1, 5)] private int defaultConfidenceLevel = 3;

        [Header("Anchor Trial Hint (前 N 次锚定/预实验试次)")]
        [Tooltip("锚定试次提示卡片容器；若未绑定将运行时创建。")]
        [SerializeField] private GameObject anchorHintGroup;
        [Tooltip("锚定试次提示文本（显示真实距离与按 A 键继续提示）。")]
        [SerializeField] private TMP_Text anchorHintText;

        [Header("Panel Appearance")]
        [Tooltip("面板整体透明度（0=全透明，1=不透明）")]
        [SerializeField, Range(0f, 1f)] private float panelAlpha = 0.6f;
        [Tooltip("面板位置偏移（世界空间），用于避免遮挡实验中心内容")]
        [SerializeField] private Vector3 panelPositionOffset = new Vector3(0.5f, 0.2f, 0f);
        [Tooltip("是否对距离压缩任务使用数字输入置信度（而非滑块）")]
        [SerializeField] private bool useNumericConfidenceForDistance = true;

        [Header("Data Export")]
        [Tooltip("是否自动记录人类被试数据并在任务结束时导出CSV")]
        [SerializeField] private bool enableHumanDataExport = true;
        [SerializeField] private string exportFolderName = "VRP_HumanData";

        [Header("Semantic Size Bias")]
        [SerializeField] private GameObject sizeBiasGroup;
        [SerializeField] private Toggle optionAToggle;
        [SerializeField] private Toggle optionBToggle;
        [SerializeField] private ToggleGroup sizeToggleGroup;

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
        [Tooltip("勾选后，仅在 color_constancy_adjustment 任务中显示该面板。")]
        [SerializeField] private bool onlyShowForColorConstancyAdjustment = false;

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

        // Motion gate state (roughness)
        private bool _requireHeadMotion;
        private bool _yawInit;
        private float _lastYawDeg;
        private float _unwrappedYawDeg;
        private float _minYawDeg;
        private float _maxYawDeg;

        private ColorAdjustableTarget _colorTarget;

        // Human data export
        private readonly List<HumanTrialRecord> _humanRecords = new List<HumanTrialRecord>();
        private string _exportDir;
        private TrialSpec _currentTrialSpec;
        private Vector3 _dialogOriginalLocalPos;
        private bool _originalPosRecorded;

        // 右手柄摇杆/A键监听（仅在 distance_compression 普通试次时启用）
        private readonly List<InputDevice> _rightHandDevicesForDialog = new List<InputDevice>(2);
        private bool _lastDialogPrimaryButton;
        private float _dialogLastSubmitTime = -999f;
        private const float DialogAKeyDebounceSeconds = 1f;
        private bool _isAnchorTrial;

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

            if (enableHumanDataExport)
            {
                _exportDir = Path.Combine(Application.persistentDataPath, exportFolderName,
                    DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
            }

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
            ExportHumanDataCsv();
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
                if (!ShouldShowForTask(data.taskId))
                {
                    _awaitingInput = false;
                    HideDialog();
                    return;
                }

                _awaitingInput = true;
                _taskId = data.taskId;
                _trialId = data.trialId;

                _requireHeadMotion = false;
                _currentTrialSpec = null;
                if (data.trialConfig is TrialSpec ts)
                {
                    _requireHeadMotion = ts.requireHeadMotion;
                    _currentTrialSpec = ts;
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

        private bool ShouldShowForTask(string taskId)
        {
            if (!onlyShowForColorConstancyAdjustment) return true;

            return string.Equals(taskId, "color_constancy_adjustment", StringComparison.OrdinalIgnoreCase);
        }

        private void PrepareDialogForTask(string taskId, string customPrompt = null)
        {
            bool isDistance = string.Equals(taskId, "distance_compression", StringComparison.OrdinalIgnoreCase);
            bool isSizeBias = string.Equals(taskId, "semantic_size_bias", StringComparison.OrdinalIgnoreCase);
            bool isRoughness = !string.IsNullOrWhiteSpace(taskId) && taskId.StartsWith("material_roughness", StringComparison.OrdinalIgnoreCase);
            bool isColor = string.Equals(taskId, "color_constancy_adjustment", StringComparison.OrdinalIgnoreCase);
            bool isNumerosity = string.Equals(taskId, "numerosity_comparison", StringComparison.OrdinalIgnoreCase);

            // 判定是否为锚定/预实验试次（仅 distance_compression 任务有此概念）
            _isAnchorTrial = isDistance && _currentTrialSpec != null && _currentTrialSpec.isAnchor;

            if (taskLabel != null) taskLabel.text = $"任务: {taskId}";
            if (trialLabel != null) trialLabel.text = $"试次: {_trialId}";
            if (errorHint != null) errorHint.text = string.Empty;
            if (motionGateHint != null) motionGateHint.text = string.Empty;

            // ============ 锚定试次（前 N 次预实验）：仅显示提示卡片 + A 键跳过 ============
            if (_isAnchorTrial)
            {
                EnsureAnchorHintWidgets();

                if (taskPromptText != null)
                {
                    taskPromptText.text = "预实验适应阶段，无需输入数值，请观察后按右手柄 A 键继续。";
                }

                // 隐藏所有输入控件与提交按钮
                if (distanceGroup != null) distanceGroup.SetActive(false);
                if (sizeBiasGroup != null) sizeBiasGroup.SetActive(false);
                if (roughnessGroup != null) roughnessGroup.SetActive(false);
                if (colorGroup != null) colorGroup.SetActive(false);
                if (confidenceLevelGroup != null) confidenceLevelGroup.SetActive(false);
                if (confidenceSlider != null) confidenceSlider.gameObject.SetActive(false);
                if (confidenceValueText != null) confidenceValueText.gameObject.SetActive(false);
                if (confidenceNumericInput != null) confidenceNumericInput.gameObject.SetActive(false);
                if (submitButton != null) submitButton.gameObject.SetActive(false);

                // 显示锚定提示卡片
                if (anchorHintGroup != null) anchorHintGroup.SetActive(true);
                if (anchorHintText != null && _currentTrialSpec != null)
                {
                    anchorHintText.text =
                        $"预实验试次 {_trialId + 1} / 3\n" +
                        $"目标距离：{_currentTrialSpec.trueDistanceM:F1} 米\n" +
                        $"请仔细观察并记住该距离感，\n" +
                        $"准备好后按右手柄 A 键继续";
                }
                return;
            }

            // 普通试次：隐藏锚定提示卡片
            if (anchorHintGroup != null) anchorHintGroup.SetActive(false);
            if (submitButton != null) submitButton.gameObject.SetActive(true);

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
                    taskPromptText.text = "请在黑屏后判断哪一侧点更多，并设置置信度（A=Left，B=Right）。";
                }
                else
                {
                    taskPromptText.text = "请根据任务要求完成输入。";
                }
            }

            if (distanceGroup != null) distanceGroup.SetActive(isDistance);
            if (sizeBiasGroup != null) sizeBiasGroup.SetActive(isSizeBias);
            if (roughnessGroup != null) roughnessGroup.SetActive(isRoughness);
            if (isColor)
            {
                EnsureColorWidgets();
            }
            if (colorGroup != null) colorGroup.SetActive(isColor);
            if (sizeBiasGroup != null) sizeBiasGroup.SetActive(isSizeBias || isNumerosity);

            // ============ 距离输入：Slider + 摇杆控制（替代 TMP_InputField 避免 VR 软键盘）============
            if (isDistance)
            {
                EnsureDistanceSliderWidgets();

                // 强制隐藏旧的 TMP_InputField，避免触发系统软键盘
                if (distanceInput != null) distanceInput.gameObject.SetActive(false);

                if (distanceSlider != null)
                {
                    distanceSlider.gameObject.SetActive(true);
                    distanceSlider.wholeNumbers = false;
                    distanceSlider.minValue = distanceMin;
                    distanceSlider.maxValue = distanceMax;
                    distanceSlider.value = Mathf.Clamp(distanceDefault, distanceMin, distanceMax);
                    UpdateDistanceValueLabel(distanceSlider.value);
                }
                if (distanceValueText != null) distanceValueText.gameObject.SetActive(true);
            }
            else
            {
                if (distanceSlider != null) distanceSlider.gameObject.SetActive(false);
                if (distanceValueText != null) distanceValueText.gameObject.SetActive(false);
            }

            // ============ 置信度输入：5-级 Toggle（距离任务使用），其他任务沿用旧滑块 ============
            bool useLevelToggles = isDistance;

            if (useLevelToggles)
            {
                EnsureConfidenceLevelWidgets();
                if (confidenceLevelGroup != null) confidenceLevelGroup.SetActive(true);
                SetSelectedConfidenceLevel(defaultConfidenceLevel);
                UpdateConfidenceLevelLabel();

                // 隐藏旧 numeric/slider 输入
                if (confidenceNumericInput != null) confidenceNumericInput.gameObject.SetActive(false);
                if (confidenceSlider != null) confidenceSlider.gameObject.SetActive(false);
                if (confidenceValueText != null) confidenceValueText.gameObject.SetActive(false);
            }
            else
            {
                if (confidenceLevelGroup != null) confidenceLevelGroup.SetActive(false);

                // 其他任务回退到原 slider 路径（保持兼容）
                bool showNumericConfidence = false; // 非距离任务不再使用数字输入
                if (confidenceNumericInput != null)
                {
                    confidenceNumericInput.gameObject.SetActive(showNumericConfidence);
                }

                if (confidenceSlider != null)
                {
                    confidenceSlider.gameObject.SetActive(!showNumericConfidence);
                    confidenceSlider.value = 0.9f;
                    UpdateConfidenceLabel(confidenceSlider.value);
                    confidenceSlider.wholeNumbers = false;
                }
                if (confidenceValueText != null)
                {
                    confidenceValueText.gameObject.SetActive(!showNumericConfidence);
                }
            }

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

            ApplyPanelTransparency();
            ApplyPositionOffset();

            if (alwaysOnTop)
            {
                ForceUIRenderQueue();
            }

            if (autoFocusInput)
            {
                // 避免对已隐藏的 distanceInput 调 ActivateInputField 触发 VR 软键盘；
                // distance_compression 现使用 Slider+Toggle 输入，不需要 focus
                if (distanceGroup != null && distanceGroup.activeSelf
                    && distanceInput != null && distanceInput.gameObject.activeSelf)
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

        private void Update()
        {
            if (!_awaitingInput) return;

            // ============ 距离任务（非 anchor）：右手柄摇杆 Y 调整距离 + primaryButton 边沿提交 ============
            if (!_isAnchorTrial && distanceSlider != null && distanceSlider.gameObject.activeSelf)
            {
                // 摇杆 Y 推动 → 增减距离
                float stickY = ReadRightStickY();
                if (Mathf.Abs(stickY) > stickDeadzone)
                {
                    float delta = stickY * distanceStickRateMps * Time.deltaTime;
                    distanceSlider.value = Mathf.Clamp(distanceSlider.value + delta, distanceMin, distanceMax);
                    UpdateDistanceValueLabel(distanceSlider.value);
                }

                // primaryButton 边沿触发 → 提交（带 1s 防抖）
                if (ReadRightPrimaryButtonEdge())
                {
                    if (Time.time - _dialogLastSubmitTime >= DialogAKeyDebounceSeconds)
                    {
                        _dialogLastSubmitTime = Time.time;
                        SubmitCurrent();
                        return;
                    }
                }
            }

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
            if (dialogRoot != null) dialogRoot.SetActive(false);
            if (backdrop != null) backdrop.SetActive(false);

            // 显式隐藏任务特定的 Group，确保它们不会残留
            if (distanceGroup != null) distanceGroup.SetActive(false);
            if (sizeBiasGroup != null) sizeBiasGroup.SetActive(false);
            if (roughnessGroup != null) roughnessGroup.SetActive(false);
            if (colorGroup != null) colorGroup.SetActive(false);
        }

        private void HookUIEvents(bool bind)
        {
            if (bind)
            {
                if (confidenceSlider != null) confidenceSlider.onValueChanged.AddListener(UpdateConfidenceLabel);
                if (roughnessSlider != null) roughnessSlider.onValueChanged.AddListener(UpdateRoughnessLabel);
                if (colorRSlider != null) colorRSlider.onValueChanged.AddListener(OnColorSliderChanged);
                if (colorGSlider != null) colorGSlider.onValueChanged.AddListener(OnColorSliderChanged);
                if (colorBSlider != null) colorBSlider.onValueChanged.AddListener(OnColorSliderChanged);
                if (submitButton != null) submitButton.onClick.AddListener(SubmitCurrent);
                if (skipButton != null) skipButton.onClick.AddListener(SkipCurrent);
            }
            else
            {
                if (confidenceSlider != null) confidenceSlider.onValueChanged.RemoveListener(UpdateConfidenceLabel);
                if (roughnessSlider != null) roughnessSlider.onValueChanged.RemoveListener(UpdateRoughnessLabel);
                if (colorRSlider != null) colorRSlider.onValueChanged.RemoveListener(OnColorSliderChanged);
                if (colorGSlider != null) colorGSlider.onValueChanged.RemoveListener(OnColorSliderChanged);
                if (colorBSlider != null) colorBSlider.onValueChanged.RemoveListener(OnColorSliderChanged);
                if (submitButton != null) submitButton.onClick.RemoveListener(SubmitCurrent);
                if (skipButton != null) skipButton.onClick.RemoveListener(SkipCurrent);
            }
        }

        private void UpdateConfidenceLabel(float value)
        {
            if (confidenceValueText != null)
                confidenceValueText.text = $"置信度: {value:F2}";
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

            float confidence = ReadConfidenceValue();
            long reactionMs = 0;
            try
            {
                reactionMs = (long)Mathf.Max(0f, (Time.realtimeSinceStartup - _awaitingInputSinceRealtime) * 1000f);
            }
            catch { }

            if (distanceGroup != null && distanceGroup.activeSelf)
            {
                float distance = 0f;

                // 优先从新的 Slider 读取（VR 场景下 distanceInput 已隐藏，避免软键盘）
                if (distanceSlider != null && distanceSlider.gameObject.activeSelf)
                {
                    distance = distanceSlider.value;
                }
                else if (distanceInput != null && !string.IsNullOrEmpty(distanceInput.text))
                {
                    if (!float.TryParse(distanceInput.text, out distance))
                    {
                        if (errorHint != null) errorHint.text = "请输入合法的距离数值。";
                        return;
                    }
                }

                if (confidence < 0f)
                {
                    if (errorHint != null) errorHint.text = "请选择置信度等级（1-5）。";
                    return;
                }
                RecordHumanTrial(distance, confidence, reactionMs);
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
            material.renderQueue = 3000;
        }

        // ============ Confidence Helpers ============

        private float ReadConfidenceValue()
        {
            // 优先：5-级 Toggle（距离任务使用）
            if (confidenceLevelGroup != null && confidenceLevelGroup.activeSelf
                && confidenceLevelToggles != null && confidenceLevelToggles.Length == 5)
            {
                int level = GetSelectedConfidenceLevel();
                if (level >= 1 && level <= 5)
                {
                    // 1->0.2, 2->0.4, 3->0.6, 4->0.8, 5->1.0
                    return level / 5f;
                }
                return -1f;
            }

            // 兼容回退：旧 numeric input
            bool useNumeric = useNumericConfidenceForDistance &&
                              string.Equals(_taskId, "distance_compression", StringComparison.OrdinalIgnoreCase);

            if (useNumeric && confidenceNumericInput != null && confidenceNumericInput.gameObject.activeSelf)
            {
                if (float.TryParse(confidenceNumericInput.text, out var val))
                    return Mathf.Clamp01(val);
                return -1f;
            }

            return confidenceSlider != null ? Mathf.Clamp01(confidenceSlider.value) : 0.9f;
        }

        // ============ Panel Appearance ============

        private void ApplyPanelTransparency()
        {
            if (dialogRoot == null) return;

            var images = dialogRoot.GetComponentsInChildren<Image>(true);
            foreach (var img in images)
            {
                if (img == null) continue;
                var c = img.color;
                c.a = Mathf.Min(c.a, panelAlpha);
                img.color = c;
            }

            if (backdrop != null)
            {
                var bgImage = backdrop.GetComponent<Image>();
                if (bgImage != null)
                {
                    var c = bgImage.color;
                    c.a = Mathf.Min(c.a, panelAlpha * 0.5f);
                    bgImage.color = c;
                }
            }
        }

        private void ApplyPositionOffset()
        {
            if (dialogRoot == null || panelPositionOffset == Vector3.zero) return;

            var rt = dialogRoot.GetComponent<RectTransform>();
            if (rt != null)
            {
                if (!_originalPosRecorded)
                {
                    _dialogOriginalLocalPos = rt.localPosition;
                    _originalPosRecorded = true;
                }
                rt.localPosition = _dialogOriginalLocalPos + panelPositionOffset;
            }
        }

        // ============ Human Data Recording & Export ============

        private void RecordHumanTrial(float estimatedDistance, float confidence, long reactionMs)
        {
            if (!enableHumanDataExport) return;

            var record = new HumanTrialRecord
            {
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                taskId = _taskId,
                trialId = _trialId,
                estimatedDistanceM = estimatedDistance,
                confidence = confidence,
                reactionTimeMs = reactionMs,
                trueDistanceM = _currentTrialSpec?.trueDistanceM ?? 0f,
                environment = _currentTrialSpec?.environment ?? "unknown",
                targetKind = _currentTrialSpec?.targetKind ?? "unknown",
                fovDeg = _currentTrialSpec?.fovDeg ?? 0f,
                isAnchor = _currentTrialSpec?.isAnchor ?? false
            };

            float trueD = record.trueDistanceM;
            record.absError = Mathf.Abs(estimatedDistance - trueD);
            record.relError = trueD > 0.001f ? record.absError / trueD : 0f;

            _humanRecords.Add(record);
            Debug.Log($"[WSHumanInputDialog] Recorded trial {_trialId}: est={estimatedDistance:F2}m, " +
                      $"true={trueD:F2}m, conf={confidence:F2}, rt={reactionMs}ms");
        }

        private void ExportHumanDataCsv()
        {
            if (!enableHumanDataExport || _humanRecords.Count == 0) return;

            try
            {
                if (!Directory.Exists(_exportDir))
                    Directory.CreateDirectory(_exportDir);

                var path = Path.Combine(_exportDir, "human_distance_compression_data.csv");
                using (var sw = new StreamWriter(path, false, Encoding.UTF8))
                {
                    sw.WriteLine("timestamp,taskId,trialId,estimatedDistanceM,trueDistanceM," +
                                 "absError,relError,confidence,reactionTimeMs," +
                                 "environment,targetKind,fovDeg,isAnchor");

                    foreach (var r in _humanRecords)
                    {
                        sw.WriteLine(string.Join(",",
                            CsvEscape(r.timestamp),
                            CsvEscape(r.taskId),
                            r.trialId,
                            r.estimatedDistanceM.ToString("F3"),
                            r.trueDistanceM.ToString("F3"),
                            r.absError.ToString("F3"),
                            r.relError.ToString("F3"),
                            r.confidence.ToString("F3"),
                            r.reactionTimeMs,
                            CsvEscape(r.environment),
                            CsvEscape(r.targetKind),
                            r.fovDeg.ToString("F0"),
                            r.isAnchor ? "true" : "false"
                        ));
                    }
                }

                Debug.Log($"[WSHumanInputDialog] Exported {_humanRecords.Count} human trial records to: {path}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WSHumanInputDialog] CSV export failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 手动触发导出（可从 Inspector 右键 ContextMenu 调用）
        /// </summary>
        [ContextMenu("Export Human Data CSV Now")]
        public void ForceExportCsv()
        {
            ExportHumanDataCsv();
        }

        private static string CsvEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        // ============ Distance Slider / Confidence Level / Anchor Hint Helpers ============

        private void UpdateDistanceValueLabel(float value)
        {
            if (distanceValueText != null)
            {
                distanceValueText.text = $"距离: {value:F1} m";
            }
        }

        private int GetSelectedConfidenceLevel()
        {
            if (confidenceLevelToggles == null || confidenceLevelToggles.Length != 5)
            {
                return Mathf.Clamp(defaultConfidenceLevel, 1, 5);
            }
            for (int i = 0; i < 5; i++)
            {
                if (confidenceLevelToggles[i] != null && confidenceLevelToggles[i].isOn)
                {
                    return i + 1;
                }
            }
            return Mathf.Clamp(defaultConfidenceLevel, 1, 5);
        }

        private void SetSelectedConfidenceLevel(int level)
        {
            int clamped = Mathf.Clamp(level, 1, 5);
            if (confidenceLevelToggles == null || confidenceLevelToggles.Length != 5)
            {
                return;
            }
            for (int i = 0; i < 5; i++)
            {
                if (confidenceLevelToggles[i] == null) continue;
                bool shouldBeOn = (i + 1) == clamped;
                if (confidenceLevelToggles[i].isOn != shouldBeOn)
                {
                    confidenceLevelToggles[i].isOn = shouldBeOn;
                }
            }
            UpdateConfidenceLevelLabel();
        }

        private void UpdateConfidenceLevelLabel()
        {
            if (confidenceLevelLabel != null)
            {
                int level = GetSelectedConfidenceLevel();
                confidenceLevelLabel.text = $"置信度等级: {level} / 5";
            }
        }

        private float ReadRightStickY()
        {
            _rightHandDevicesForDialog.Clear();
            InputDevices.GetDevicesAtXRNode(XRNode.RightHand, _rightHandDevicesForDialog);

            for (int i = 0; i < _rightHandDevicesForDialog.Count; i++)
            {
                var device = _rightHandDevicesForDialog[i];
                if (!device.isValid) continue;
                if (device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 axis))
                {
                    return axis.y;
                }
            }

#if UNITY_EDITOR
            // Editor 调试：上下方向键模拟摇杆 Y
            if (Input.GetKey(KeyCode.UpArrow)) return 1f;
            if (Input.GetKey(KeyCode.DownArrow)) return -1f;
#endif
            return 0f;
        }

        private bool ReadRightPrimaryButtonEdge()
        {
            _rightHandDevicesForDialog.Clear();
            InputDevices.GetDevicesAtXRNode(XRNode.RightHand, _rightHandDevicesForDialog);

            bool pressedThisFrame = false;
            for (int i = 0; i < _rightHandDevicesForDialog.Count; i++)
            {
                var device = _rightHandDevicesForDialog[i];
                if (!device.isValid) continue;
                if (device.TryGetFeatureValue(CommonUsages.primaryButton, out bool pressed) && pressed)
                {
                    pressedThisFrame = true;
                    break;
                }
            }

#if UNITY_EDITOR
            // Editor 调试：S 键模拟 A 键提交（避免与 HumanInputKeyboardBridge 的 A 键调试冲突）
            if (Input.GetKeyDown(KeyCode.S)) return true;
#endif

            bool edge = pressedThisFrame && !_lastDialogPrimaryButton;
            _lastDialogPrimaryButton = pressedThisFrame;
            return edge;
        }

        private void EnsureDistanceSliderWidgets()
        {
            if (distanceSlider != null && distanceValueText != null)
            {
                return;
            }

            // 优先使用现有 distanceGroup 作为父节点，否则使用 dialogRoot
            Transform parent = (distanceGroup != null) ? distanceGroup.transform
                              : (dialogRoot != null ? dialogRoot.transform : transform);
            if (parent == null) return;

            TMP_Text textTemplate = taskLabel != null ? taskLabel : GetComponentInChildren<TMP_Text>(true);
            Slider sliderTemplate = confidenceSlider != null ? confidenceSlider : GetComponentInChildren<Slider>(true);

            if (sliderTemplate == null || textTemplate == null)
            {
                return;
            }

            if (distanceValueText == null)
            {
                var valueObj = Instantiate(textTemplate.gameObject, parent);
                valueObj.name = "DistanceValueText";
                valueObj.SetActive(true);
                distanceValueText = valueObj.GetComponent<TMP_Text>();
                if (distanceValueText != null)
                {
                    distanceValueText.text = $"距离: {distanceDefault:F1} m";
                    distanceValueText.alignment = TextAlignmentOptions.MidlineLeft;
                    distanceValueText.raycastTarget = false;
                }
                var le = valueObj.GetComponent<LayoutElement>() ?? valueObj.AddComponent<LayoutElement>();
                le.preferredHeight = 24f;
            }

            if (distanceSlider == null)
            {
                var sliderObj = Instantiate(sliderTemplate.gameObject, parent);
                sliderObj.name = "DistanceSlider";
                sliderObj.SetActive(true);
                distanceSlider = sliderObj.GetComponent<Slider>();
                if (distanceSlider != null)
                {
                    distanceSlider.onValueChanged.RemoveAllListeners();
                    distanceSlider.wholeNumbers = false;
                    distanceSlider.minValue = distanceMin;
                    distanceSlider.maxValue = distanceMax;
                    distanceSlider.value = Mathf.Clamp(distanceDefault, distanceMin, distanceMax);
                    distanceSlider.onValueChanged.AddListener(UpdateDistanceValueLabel);
                }
                var le = sliderObj.GetComponent<LayoutElement>() ?? sliderObj.AddComponent<LayoutElement>();
                le.preferredHeight = 28f;
                le.flexibleWidth = 1f;
            }
        }

        private void EnsureConfidenceLevelWidgets()
        {
            if (confidenceLevelGroup != null
                && confidenceLevelToggles != null && confidenceLevelToggles.Length == 5
                && confidenceLevelToggles[0] != null && confidenceLevelToggles[4] != null)
            {
                return;
            }

            Transform root = dialogRoot != null ? dialogRoot.transform : transform;
            if (root == null) return;

            // 创建容器
            if (confidenceLevelGroup == null)
            {
                confidenceLevelGroup = new GameObject("ConfidenceLevelGroup", typeof(RectTransform));
                confidenceLevelGroup.transform.SetParent(root, false);

                var bg = confidenceLevelGroup.AddComponent<Image>();
                bg.color = new Color(0f, 0f, 0f, 0.25f);
                bg.raycastTarget = false;

                var layout = confidenceLevelGroup.AddComponent<HorizontalLayoutGroup>();
                layout.childAlignment = TextAnchor.MiddleCenter;
                layout.childControlWidth = true;
                layout.childForceExpandWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandHeight = false;
                layout.spacing = 6f;
                layout.padding = new RectOffset(8, 8, 4, 4);

                var le = confidenceLevelGroup.AddComponent<LayoutElement>();
                le.preferredHeight = 36f;
            }

            if (confidenceLevelToggleGroup == null)
            {
                confidenceLevelToggleGroup = confidenceLevelGroup.GetComponent<ToggleGroup>()
                                          ?? confidenceLevelGroup.AddComponent<ToggleGroup>();
                confidenceLevelToggleGroup.allowSwitchOff = false;
            }

            // 准备 toggle 模板：优先使用现有 optionAToggle/optionBToggle 之一
            Toggle toggleTemplate = optionAToggle != null ? optionAToggle
                                  : (optionBToggle != null ? optionBToggle : GetComponentInChildren<Toggle>(true));

            if (toggleTemplate == null)
            {
                Debug.LogWarning("[WSHumanInputDialog] EnsureConfidenceLevelWidgets: 无法找到 Toggle 模板，请在 Inspector 绑定 confidenceLevelToggles 或确保场景中存在至少一个 Toggle。");
                return;
            }

            if (confidenceLevelToggles == null || confidenceLevelToggles.Length != 5)
            {
                confidenceLevelToggles = new Toggle[5];
            }

            for (int i = 0; i < 5; i++)
            {
                if (confidenceLevelToggles[i] != null) continue;

                var toggleObj = Instantiate(toggleTemplate.gameObject, confidenceLevelGroup.transform);
                toggleObj.name = $"ConfidenceLevelToggle_{i + 1}";
                toggleObj.SetActive(true);

                var t = toggleObj.GetComponent<Toggle>();
                if (t != null)
                {
                    t.onValueChanged.RemoveAllListeners();
                    t.group = confidenceLevelToggleGroup;
                    t.isOn = (i + 1) == defaultConfidenceLevel;
                    t.onValueChanged.AddListener(_ => UpdateConfidenceLevelLabel());
                }

                // 设置子文本为 "1".."5"
                var label = toggleObj.GetComponentInChildren<TMP_Text>(true);
                if (label != null)
                {
                    label.text = (i + 1).ToString();
                    label.alignment = TextAlignmentOptions.Center;
                    label.raycastTarget = false;
                }

                var le = toggleObj.GetComponent<LayoutElement>() ?? toggleObj.AddComponent<LayoutElement>();
                le.preferredWidth = 48f;
                le.flexibleWidth = 1f;

                confidenceLevelToggles[i] = t;
            }
        }

        private void EnsureAnchorHintWidgets()
        {
            if (anchorHintGroup != null && anchorHintText != null)
            {
                return;
            }

            Transform root = dialogRoot != null ? dialogRoot.transform : transform;
            if (root == null) return;

            if (anchorHintGroup == null)
            {
                anchorHintGroup = new GameObject("AnchorHintGroup", typeof(RectTransform));
                anchorHintGroup.transform.SetParent(root, false);

                var rt = anchorHintGroup.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.05f, 0.2f);
                rt.anchorMax = new Vector2(0.95f, 0.8f);
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;

                var bg = anchorHintGroup.AddComponent<Image>();
                bg.color = new Color(0f, 0f, 0f, 0.4f);
                bg.raycastTarget = false;

                var layout = anchorHintGroup.AddComponent<VerticalLayoutGroup>();
                layout.childAlignment = TextAnchor.MiddleCenter;
                layout.childControlHeight = true;
                layout.childControlWidth = true;
                layout.padding = new RectOffset(16, 16, 16, 16);
            }

            if (anchorHintText == null)
            {
                TMP_Text textTemplate = taskLabel != null ? taskLabel : GetComponentInChildren<TMP_Text>(true);
                if (textTemplate != null)
                {
                    var hintObj = Instantiate(textTemplate.gameObject, anchorHintGroup.transform);
                    hintObj.name = "AnchorHintText";
                    hintObj.SetActive(true);
                    anchorHintText = hintObj.GetComponent<TMP_Text>();
                    if (anchorHintText != null)
                    {
                        anchorHintText.text = string.Empty;
                        anchorHintText.alignment = TextAlignmentOptions.Center;
                        anchorHintText.fontSize = Mathf.Max(16f, textTemplate.fontSize);
                        anchorHintText.raycastTarget = false;
                    }
                }
                else
                {
                    var hintObj = new GameObject("AnchorHintText", typeof(RectTransform));
                    hintObj.transform.SetParent(anchorHintGroup.transform, false);
                    anchorHintText = hintObj.AddComponent<TextMeshProUGUI>();
                    anchorHintText.alignment = TextAlignmentOptions.Center;
                    anchorHintText.fontSize = 24f;
                    anchorHintText.color = Color.white;
                    anchorHintText.raycastTarget = false;
                }
            }
        }

        // ============ Data Models ============

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

        private class HumanTrialRecord
        {
            public string timestamp;
            public string taskId;
            public int trialId;
            public float estimatedDistanceM;
            public float trueDistanceM;
            public float absError;
            public float relError;
            public float confidence;
            public long reactionTimeMs;
            public string environment;
            public string targetKind;
            public float fovDeg;
            public bool isAnchor;
        }
    }
}
