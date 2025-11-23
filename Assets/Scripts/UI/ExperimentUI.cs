using System;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using VRPerception.Infra.EventBus;
using VRPerception.Tasks;

namespace VRPerception.UI
{
    /// <summary>
    /// 实验控制 UI（IMGUI 版，零依赖 UGUI）
    /// - 配置：任务模式、被试模式、随机种子、最大试次数
    /// - 控制：开始/取消运行
    /// - 状态：展示 Trial 生命周期与错误
    /// 说明：通过反射设置 TaskRunner 的私有序列化字段，避免改动 Runner 代码。
    /// </summary>
    public class ExperimentUI : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private TaskRunner runner;
        [SerializeField] private EventBusManager eventBus;

        [Header("Behavior")]
        [Tooltip("若为空则自动查找场景中的 TaskRunner")]
        [SerializeField] private bool autoFindRunner = true;

        private TaskMode _uiTaskMode = TaskMode.DistanceCompression;
        private SubjectMode _uiSubjectMode = SubjectMode.MLLM;
        private string _seedText = "12345";
        private string _maxTrialsText = "0";
        private bool _isRunning;

        // 状态显示
        private string _lastTaskId = "-";
        private int _lastTrialId = -1;
        private string _lastState = "-";
        private string _lastError = null;
        private Vector2 _scroll;

        private void Awake()
        {
            if (eventBus == null) eventBus = EventBusManager.Instance;
            if (runner == null && autoFindRunner)
            {
                runner = FindObjectOfType<TaskRunner>();
            }

            // 从 Runner 初始化 UI 值
            if (runner != null)
            {
                TryInitFromRunner();
            }
        }

        private void OnEnable()
        {
            if (eventBus != null)
            {
                eventBus.TrialLifecycle?.Subscribe(OnTrialLifecycle);
                eventBus.Error?.Subscribe(OnError);
            }
        }

        private void OnDisable()
        {
            if (eventBus != null)
            {
                eventBus.TrialLifecycle?.Unsubscribe(OnTrialLifecycle);
                eventBus.Error?.Unsubscribe(OnError);
            }
        }

        private void OnTrialLifecycle(TrialLifecycleEventData data)
        {
            _lastTaskId = data.taskId;
            _lastTrialId = data.trialId;
            _lastState = data.state.ToString();
            if (data.state == TrialLifecycleState.Started) _isRunning = true;
            if (data.state == TrialLifecycleState.Completed ||
                data.state == TrialLifecycleState.Failed ||
                data.state == TrialLifecycleState.Cancelled)
            {
                // 运行中也可能还有后续 Trial；此处不自动复位 _isRunning，仅由按钮控制
            }
        }

        private void OnError(ErrorEventData data)
        {
            _lastError = $"[{data.severity}] {data.errorCode}: {data.message}";
        }

#if UNITY_EDITOR
        private void OnGUI()
        {
            const int panelWidth = 380;
            GUILayout.BeginArea(new Rect(10, 10, panelWidth, Screen.height - 20), GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);

            GUILayout.Label("VR Perception – 实验控制", EditorTitle());
            if (runner == null)
            {
                EditorHelpBox("未找到 TaskRunner。请在场景中添加 TaskRunner 脚本或手动赋值。", MessageType.Warning);
                if (GUILayout.Button("尝试自动查找 TaskRunner"))
                {
                    runner = FindObjectOfType<TaskRunner>();
                    if (runner != null) TryInitFromRunner();
                }
                GUILayout.EndScrollView();
                GUILayout.EndArea();
                return;
            }

            // 任务模式
            GUILayout.Space(6);
            GUILayout.Label("任务模式 (TaskMode):");
            var newTaskMode = ToolbarTaskMode(_uiTaskMode);
            if (newTaskMode != _uiTaskMode)
            {
                _uiTaskMode = newTaskMode;
                TrySetPrivateField(runner, "taskMode", _uiTaskMode);
            }

            // 被试模式
            GUILayout.Space(6);
            GUILayout.Label("被试模式 (SubjectMode):");
            var newSubject = ToolbarSubjectMode(_uiSubjectMode);
            if (newSubject != _uiSubjectMode)
            {
                _uiSubjectMode = newSubject;
                TrySetPrivateField(runner, "subjectMode", _uiSubjectMode);
            }

            // 参数
            GUILayout.Space(6);
            GUILayout.Label("参数配置:");
            GUILayout.BeginHorizontal();
            GUILayout.Label("随机种子:", GUILayout.Width(70));
            _seedText = GUILayout.TextField(_seedText, GUILayout.Width(100));
            if (GUILayout.Button("应用", GUILayout.Width(60)))
            {
                if (int.TryParse(_seedText, out var seed))
                    TrySetPrivateField(runner, "randomSeed", seed);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("最大试次数:", GUILayout.Width(70));
            _maxTrialsText = GUILayout.TextField(_maxTrialsText, GUILayout.Width(100));
            if (GUILayout.Button("应用", GUILayout.Width(60)))
            {
                if (int.TryParse(_maxTrialsText, out var mt))
                    TrySetPrivateField(runner, "maxTrials", mt);
            }
            GUILayout.EndHorizontal();

            // 控制
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            GUI.enabled = !_isRunning;
            if (GUILayout.Button("开始运行", GUILayout.Height(28)))
            {
                _ = StartRunSafe();
            }
            GUI.enabled = true;

            if (GUILayout.Button("取消运行", GUILayout.Height(28)))
            {
                runner.CancelRun();
                _isRunning = false;
            }
            GUILayout.EndHorizontal();

            // 状态
            GUILayout.Space(10);
            GUILayout.Label("最近 Trial 状态:", EditorBold());
            GUILayout.Label($"Task: {_lastTaskId}");
            GUILayout.Label($"Trial: {_lastTrialId}");
            GUILayout.Label($"State: {_lastState}");
            if (!string.IsNullOrEmpty(_lastError))
            {
                EditorHelpBox(_lastError, MessageType.Error);
            }

            GUILayout.Space(8);
            EditorHelpBox("提示:\n- Human 模式下，等待 HumanInputHandler 界面输入答案。\n- DistanceCompression 输出 {distance_m}；SemanticSizeBias 输出 {A|B}。", MessageType.Info);

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
#endif

        private async Task StartRunSafe()
        {
            try
            {
                _isRunning = true;
                await runner.RunAsync();
            }
            catch (Exception ex)
            {
                _isRunning = false;
                _lastError = $"Run failed: {ex.Message}";
            }
        }

        private void TryInitFromRunner()
        {
            // 读取私有序列化字段用于 UI 显示
            TryGetPrivateField(runner, "taskMode", ref _uiTaskMode);
            TryGetPrivateField(runner, "subjectMode", ref _uiSubjectMode);
            int seed = 12345;
            if (TryGetPrivateField(runner, "randomSeed", ref seed)) _seedText = seed.ToString();
            int mt = 0;
            if (TryGetPrivateField(runner, "maxTrials", ref mt)) _maxTrialsText = mt.ToString();
        }

        // ===== 工具：反射读/写私有序列化字段 =====
        private static bool TrySetPrivateField<T>(object obj, string fieldName, T value)
        {
            if (obj == null || string.IsNullOrEmpty(fieldName)) return false;
            var fi = obj.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi == null) return false;
            if (!fi.FieldType.IsAssignableFrom(typeof(T))) return false;
            try { fi.SetValue(obj, value); return true; }
            catch { return false; }
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

        // ===== 辅助：IMGUI 样式/控件 =====
        private static GUIStyle EditorTitle()
        {
            var s = new GUIStyle(GUI.skin.label);
            s.fontSize = 16;
            s.fontStyle = FontStyle.Bold;
            return s;
        }

        private static GUIStyle EditorBold()
        {
            var s = new GUIStyle(GUI.skin.label);
            s.fontStyle = FontStyle.Bold;
            return s;
        }

        private static void EditorHelpBox(string msg, MessageType type)
        {
            // 统一使用运行时安全的绘制方式，避免依赖 UnityEditor 命名空间
            var color = GUI.color;
            switch (type)
            {
                case MessageType.Info: GUI.color = new Color(0.8f, 0.9f, 1f); break;
                case MessageType.Warning: GUI.color = new Color(1f, 0.95f, 0.8f); break;
                case MessageType.Error: GUI.color = new Color(1f, 0.8f, 0.8f); break;
            }
            GUILayout.Label(msg, GUI.skin.box);
            GUI.color = color;
        }

        private static TaskMode ToolbarTaskMode(TaskMode current)
        {
            var labels = new[] { "DistanceCompression", "SemanticSizeBias" };
            int index = current == TaskMode.SemanticSizeBias ? 1 : 0;
            int newIndex = GUILayout.Toolbar(index, labels);
            return newIndex == 1 ? TaskMode.SemanticSizeBias : TaskMode.DistanceCompression;
        }

        private static SubjectMode ToolbarSubjectMode(SubjectMode current)
        {
            var labels = new[] { "MLLM", "Human" };
            int index = current == SubjectMode.Human ? 1 : 0;
            int newIndex = GUILayout.Toolbar(index, labels);
            return newIndex == 1 ? SubjectMode.Human : SubjectMode.MLLM;
        }

        private enum MessageType { Info, Warning, Error }
    }
}