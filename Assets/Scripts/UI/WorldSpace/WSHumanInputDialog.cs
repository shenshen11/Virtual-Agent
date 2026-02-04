using System;
using System.Collections;
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

                _requireHeadMotion = false;
                if (data.trialConfig is TrialSpec ts)
                {
                    _requireHeadMotion = ts.requireHeadMotion;
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

        private void PrepareDialogForTask(string taskId, string customPrompt = null)
        {
            bool isDistance = string.Equals(taskId, "distance_compression", StringComparison.OrdinalIgnoreCase);
            bool isSizeBias = string.Equals(taskId, "semantic_size_bias", StringComparison.OrdinalIgnoreCase);
            bool isRoughness = !string.IsNullOrWhiteSpace(taskId) && taskId.StartsWith("material_roughness", StringComparison.OrdinalIgnoreCase);
            bool isColor = string.Equals(taskId, "color_constancy_adjustment", StringComparison.OrdinalIgnoreCase);
            bool isNumerosity = string.Equals(taskId, "numerosity_comparison", StringComparison.OrdinalIgnoreCase);

            if (taskLabel != null) taskLabel.text = $"任务: {taskId}";
            if (trialLabel != null) trialLabel.text = $"试次: {_trialId}";
            if (errorHint != null) errorHint.text = string.Empty;
            if (motionGateHint != null) motionGateHint.text = string.Empty;

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

        private void Update()
        {
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
