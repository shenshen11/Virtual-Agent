using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using VRPerception.Infra.EventBus;
using VRPerception.Tasks;

namespace VRPerception.UI
{
    [RequireComponent(typeof(Canvas))]
    /// <summary>
    /// 世界空间控制台（uGUI 版）
    /// - 目标：在头显运行时提供可交互控制台，替代 IMGUI 的 [C#.ExperimentUI.OnGUI()](Assets/Scripts/UI/ExperimentUI.cs:92)
    /// - 能力：配置任务参数（TaskMode/SubjectMode/Seed/MaxTrials），开始/取消运行，展示 Trial 状态与错误
    /// - 兼容：不修改 [C#.TaskRunner.RunAsync()](Assets/Scripts/Tasks/TaskRunner.cs:66) 主循环，仍通过反射写入私有序列化字段
    /// </summary>
    public class WSConsoleUI : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private TaskRunner runner;
        [SerializeField] private EventBusManager eventBus;

        [Header("UGUI Bindings")]
        [Tooltip("任务模式下拉：0=DistanceCompression, 1=SemanticSizeBias")]
        [SerializeField] private TMP_Dropdown taskModeDropdown;
        [Tooltip("被试模式下拉：0=MLLM, 1=Human")]
        [SerializeField] private TMP_Dropdown subjectModeDropdown;
        [SerializeField] private TMP_InputField seedInput;
        [SerializeField] private TMP_InputField maxTrialsInput;
        [SerializeField] private Button startButton;
        [SerializeField] private Button cancelButton;
        [SerializeField] private TMP_Text statusTaskText;
        [SerializeField] private TMP_Text statusTrialText;
        [SerializeField] private TMP_Text statusStateText;
        [SerializeField] private TMP_Text errorText;
        [Tooltip("提交按钮被点击后是否立刻禁用，直至 RunAsync 结束")]
        [SerializeField] private bool disableControlsWhileRunning = true;

        [Header("Behavior")]
        [SerializeField] private bool autoFindRunner = true;
        [SerializeField] private bool autoFindEventBus = true;

        // runtime state
        private bool _isRunning;
        private string _lastTaskId = "-";
        private int _lastTrialId = -1;
        private string _lastState = "-";
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

                if (GetComponent<TrackedDeviceGraphicRaycaster>() == null)
                    gameObject.AddComponent<TrackedDeviceGraphicRaycaster>();
            }

            if (autoFindEventBus && eventBus == null)
                eventBus = EventBusManager.Instance;

            if (autoFindRunner && runner == null)
                runner = FindObjectOfType<TaskRunner>();

            // Ensure dropdowns have options if left empty in prefab
            InitDropdownOptions();

            // Hook buttons
            if (startButton != null) startButton.onClick.AddListener(OnStartClicked);
            if (cancelButton != null) cancelButton.onClick.AddListener(OnCancelClicked);

            // Initialize UI from current runner fields
            InitUIFromRunner();
            RefreshStatusTexts();
            SetInteractableState(true);
            if (errorText != null) errorText.text = string.Empty;
        }

        private void OnEnable()
        {
            eventBus?.TrialLifecycle?.Subscribe(OnTrialLifecycle);
            eventBus?.Error?.Subscribe(OnError);

            if (_ensureSubscribeRoutine == null)
                _ensureSubscribeRoutine = StartCoroutine(EnsureEventBusReady());
        }

        private void OnDisable()
        {
            eventBus?.TrialLifecycle?.Unsubscribe(OnTrialLifecycle);
            eventBus?.Error?.Unsubscribe(OnError);

            if (_ensureSubscribeRoutine != null)
            {
                StopCoroutine(_ensureSubscribeRoutine);
                _ensureSubscribeRoutine = null;
            }
        }

        // ===== Event Handlers =====

        private void OnTrialLifecycle(TrialLifecycleEventData data)
        {
            if (data == null) return;

            _lastTaskId = data.taskId;
            _lastTrialId = data.trialId;
            _lastState = data.state.ToString();

            if (data.state == TrialLifecycleState.Started)
                _isRunning = true;

            if (data.state == TrialLifecycleState.Completed ||
                data.state == TrialLifecycleState.Failed ||
                data.state == TrialLifecycleState.Cancelled)
            {
                // 不强制复位运行中标记，交由按钮逻辑控制
            }

            RefreshStatusTexts();
        }

        private void OnError(ErrorEventData err)
        {
            if (errorText != null)
                errorText.text = $"[{err.severity}] {err.errorCode}: {err.message}";
        }

        // ===== UI Callbacks =====

        private async void OnStartClicked()
        {
            if (runner == null)
            {
                if (errorText != null) errorText.text = "未找到 TaskRunner。";
                return;
            }

            ApplyRunnerConfigFromUI();

            _isRunning = true;
            if (disableControlsWhileRunning)
                SetInteractableState(false);
            try
            {
                await runner.RunAsync();
            }
            catch (Exception ex)
            {
                _isRunning = false;
                if (errorText != null) errorText.text = $"Run failed: {ex.Message}";
            }
            finally
            {
                _isRunning = false;
                if (disableControlsWhileRunning)
                    SetInteractableState(true);
            }
        }

        private void OnCancelClicked()
        {
            try { runner?.CancelRun(); }
            catch { /* ignore */ }

            _isRunning = false;
            if (disableControlsWhileRunning)
                SetInteractableState(true);
        }

        // ===== Helpers: UI -> Runner config =====

        private void ApplyRunnerConfigFromUI()
        {
            if (runner == null) return;

            // TaskMode
            var tm = TaskMode.DistanceCompression;
            if (taskModeDropdown != null)
                tm = (taskModeDropdown.value == 1) ? TaskMode.SemanticSizeBias : TaskMode.DistanceCompression;
            TrySetPrivateField(runner, "taskMode", tm);

            // SubjectMode
            var sm = SubjectMode.MLLM;
            if (subjectModeDropdown != null)
                sm = (subjectModeDropdown.value == 1) ? SubjectMode.Human : SubjectMode.MLLM;
            TrySetPrivateField(runner, "subjectMode", sm);

            // Seed
            if (seedInput != null && int.TryParse(seedInput.text, out var seed))
                TrySetPrivateField(runner, "randomSeed", seed);

            // MaxTrials
            if (maxTrialsInput != null && int.TryParse(maxTrialsInput.text, out var mt))
                TrySetPrivateField(runner, "maxTrials", mt);
        }

        private void InitUIFromRunner()
        {
            if (runner == null) return;

            // Read private serialized fields for display
            var tm = TaskMode.DistanceCompression;
            if (TryGetPrivateField(runner, "taskMode", ref tm) && taskModeDropdown != null)
                taskModeDropdown.value = (tm == TaskMode.SemanticSizeBias) ? 1 : 0;

            var sm = SubjectMode.MLLM;
            if (TryGetPrivateField(runner, "subjectMode", ref sm) && subjectModeDropdown != null)
                subjectModeDropdown.value = (sm == SubjectMode.Human) ? 1 : 0;

            int seed = 12345;
            if (TryGetPrivateField(runner, "randomSeed", ref seed) && seedInput != null)
                seedInput.text = seed.ToString();

            int mt = 0;
            if (TryGetPrivateField(runner, "maxTrials", ref mt) && maxTrialsInput != null)
                maxTrialsInput.text = mt.ToString();
        }

        // ===== UI helpers =====

        private void RefreshStatusTexts()
        {
            if (statusTaskText != null) statusTaskText.text = $"Task: {_lastTaskId}";
            if (statusTrialText != null) statusTrialText.text = $"Trial: {_lastTrialId}";
            if (statusStateText != null) statusStateText.text = $"State: {_lastState}";
        }

        private void SetInteractableState(bool idle)
        {
            if (startButton != null) startButton.interactable = idle && !_isRunning;
            if (cancelButton != null) cancelButton.interactable = true; // 允许随时取消
            if (taskModeDropdown != null) taskModeDropdown.interactable = idle;
            if (subjectModeDropdown != null) subjectModeDropdown.interactable = idle;
            if (seedInput != null) seedInput.interactable = idle;
            if (maxTrialsInput != null) maxTrialsInput.interactable = idle;
        }

        private void InitDropdownOptions()
        {
            if (taskModeDropdown != null && taskModeDropdown.options.Count == 0)
            {
                taskModeDropdown.options.Add(new TMP_Dropdown.OptionData("DistanceCompression"));
                taskModeDropdown.options.Add(new TMP_Dropdown.OptionData("SemanticSizeBias"));
                taskModeDropdown.value = 0;
            }
            if (subjectModeDropdown != null && subjectModeDropdown.options.Count == 0)
            {
                subjectModeDropdown.options.Add(new TMP_Dropdown.OptionData("MLLM"));
                subjectModeDropdown.options.Add(new TMP_Dropdown.OptionData("Human"));
                subjectModeDropdown.value = 0;
            }
        }

        // ===== Reflection utils (keep minimal, no UnityEditor dependency) =====

        private static bool TrySetPrivateField<T>(object obj, string fieldName, T value)
        {
            if (obj == null || string.IsNullOrEmpty(fieldName)) return false;
            var fi = obj.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi == null) return false;
            if (!fi.FieldType.IsAssignableFrom(typeof(T))) return false;
            try { fi.SetValue(obj, value); return true; } catch { return false; }
        }

        private static bool TryGetPrivateField<T>(object obj, string fieldName, ref T outValue)
        {
            if (obj == null || string.IsNullOrEmpty(fieldName)) return false;
            var fi = obj.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi == null) return false;
            try
            {
                var v = fi.GetValue(obj);
                if (v is T tv) { outValue = tv; return true; }
                return false;
            }
            catch { return false; }
        }

        private IEnumerator EnsureEventBusReady()
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
            {
                if (errorText != null) errorText.text = "未找到 EventBusManager。";
                _ensureSubscribeRoutine = null;
                yield break;
            }

            while (eventBus.TrialLifecycle == null && Time.realtimeSinceStartup - start < timeout)
            {
                yield return null;
            }

            eventBus.TrialLifecycle?.Unsubscribe(OnTrialLifecycle);
            eventBus.TrialLifecycle?.Subscribe(OnTrialLifecycle);

            while (eventBus.Error == null && Time.realtimeSinceStartup - start < timeout)
            {
                yield return null;
            }

            eventBus.Error?.Unsubscribe(OnError);
            eventBus.Error?.Subscribe(OnError);

            _ensureSubscribeRoutine = null;
        }
    }
}