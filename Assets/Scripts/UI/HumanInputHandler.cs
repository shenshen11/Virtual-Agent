using System;
using UnityEngine;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;

namespace VRPerception.UI
{
    /// <summary>
    /// 人类被试输入处理（IMGUI 版）
    /// - 在 TrialLifecycle=WaitingForInput 时显示输入面板
    /// - 任务 distance_compression：输入距离(m)与置信度
    /// - 任务 semantic_size_bias：选择 A/B 与置信度
    /// - 提交后以 InferenceReceived 事件回传 LLMResponse（providerId="human"）
    /// </summary>
    public class HumanInputHandler : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private EventBusManager eventBus;
        [SerializeField] private bool autoFindEventBus = true;

        [Header("UI")]
        [SerializeField] private Rect panelRect = new Rect(10, 10, 340, 220);

        [Header("Debug/UX")]
        [SerializeField] private bool pinRight = true;
        [SerializeField] private bool showHint = true;
        [SerializeField] private bool verboseLog = false;

        [Header("Rendering")]
        [Tooltip("IMGUI 深度优先级（0 = 最前面，数值越大越靠后）")]
        [SerializeField] private int guiDepth = -1000;
        
        private bool _awaitingInput;
        private string _taskId = "";
        private int _trialId = -1;

        // distance_compression
        private string _distanceText = "10.0";

        // semantic_size_bias
        private int _largerIndex = 0; // 0=A, 1=B

        // material_roughness*
        private float _roughness = 0.5f;

        // change_detection
        private int _changeDetectedIndex = 1; // 0=no, 1=yes
        private int _changeCategoryIndex = 0; // 0=appearance,1=disappearance,2=movement,3=replacement

        // shared
        private float _confidence = 0.9f;
        private Vector2 _scroll;

        private void Awake()
        {
            if (autoFindEventBus && eventBus == null)
            {
                eventBus = EventBusManager.Instance;
            }

            if (verboseLog) Debug.Log($"[HumanInputHandler] Awake. eventBus={(eventBus!=null)}");
            if (eventBus == null)
            {
                Debug.LogWarning("[HumanInputHandler] EventBusManager not found. Human input UI will not show. Ensure EventBusBootstrap/Manager is in the scene.");
            }
        }

        private void OnEnable()
        {
            if (eventBus != null)
            {
                eventBus.TrialLifecycle?.Subscribe(OnTrialLifecycle);
                if (verboseLog) Debug.Log("[HumanInputHandler] Subscribed TrialLifecycle");
                if (eventBus.TrialLifecycle == null)
                {
                    Debug.LogWarning("[HumanInputHandler] TrialLifecycle channel is null. Check EventBusBootstrap/Channels.");
                }
            }
            else
            {
                Debug.LogWarning("[HumanInputHandler] eventBus is null in OnEnable");
            }

            // 防止 EventBusBootstrap 晚于本组件启用：若通道尚未创建，稍后再次尝试订阅
            StartCoroutine(EnsureSubscribe());
        }

        private void OnDisable()
        {
            if (verboseLog) Debug.Log("[HumanInputHandler] OnDisable - Unsubscribe TrialLifecycle");
            eventBus?.TrialLifecycle?.Unsubscribe(OnTrialLifecycle);
        }

        private void OnTrialLifecycle(TrialLifecycleEventData data)
        {
            if (data == null) return;

            if (verboseLog) Debug.Log($"[HumanInputHandler] TrialLifecycle received: {data.state} {data.taskId}/{data.trialId}");

            if (data.state == TrialLifecycleState.WaitingForInput)
            {
                _awaitingInput = true;
                _taskId = data.taskId;
                _trialId = data.trialId;

                // reset UI defaults
                _distanceText = "10.0";
                _largerIndex = 0;
                _roughness = 0.5f;
                _changeDetectedIndex = 1;
                _changeCategoryIndex = 0;
                _confidence = 0.9f;
            }
            else if (data.state == TrialLifecycleState.Completed ||
                     data.state == TrialLifecycleState.Failed ||
                     data.state == TrialLifecycleState.Cancelled)
            {
                _awaitingInput = false;
            }
        }

#if UNITY_EDITOR
        private void OnGUI()
        {
            if (!_awaitingInput) return;

            // 设置 GUI 深度，确保在最前面渲染（负数 = 更靠前）
            GUI.depth = guiDepth;

            // 右侧停靠，避免与左侧 ExperimentUI 重叠
            var r = panelRect;
            if (pinRight)
            {
                r = new Rect(Screen.width - panelRect.width - 10f, panelRect.y, panelRect.width, panelRect.height);
            }

            GUILayout.BeginArea(r, GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);

            GUILayout.Label("人类被试输入 HumanInput", Title());
            GUILayout.Label($"任务: {_taskId}");
            GUILayout.Label($"试次: {_trialId}");

            if (showHint)
            {
                GUILayout.Space(4);
                GUILayout.Label("提示: 若按钮/输入无响应，请将 Player Settings → Active Input Handling 设为 Both，并确保 Game 视图获得焦点。", GUI.skin.box);
            }

            GUILayout.Space(6);

            if (string.Equals(_taskId, "distance_compression", StringComparison.OrdinalIgnoreCase))
            {
                GUILayout.Label("估计距离 (米):");
                _distanceText = GUILayout.TextField(_distanceText);
                GUILayout.Space(4);
                DrawConfidence();
                GUILayout.Space(6);

                if (GUILayout.Button("提交答案", GUILayout.Height(26)))
                {
                    if (!float.TryParse(_distanceText, out var dMeters))
                        dMeters = 0f;
                    SubmitDistance(dMeters, Mathf.Clamp01(_confidence));
                }
            }
            else if (string.Equals(_taskId, "semantic_size_bias", StringComparison.OrdinalIgnoreCase))
            {
                GUILayout.Label("选择更大的对象:");
                _largerIndex = GUILayout.Toolbar(_largerIndex, new[] { "A", "B" });
                GUILayout.Space(4);
                DrawConfidence();
                GUILayout.Space(6);

                if (GUILayout.Button("提交答案", GUILayout.Height(26)))
                {
                    var larger = _largerIndex == 0 ? "A" : "B";
                    SubmitSize(larger, Mathf.Clamp01(_confidence));
                }
            }
            else if (_taskId != null && _taskId.StartsWith("material_roughness", StringComparison.OrdinalIgnoreCase))
            {
                GUILayout.Label("估计粗糙度 roughness (0..1):");
                GUILayout.Label($"roughness: {_roughness:F2}");
                _roughness = GUILayout.HorizontalSlider(_roughness, 0f, 1f);
                GUILayout.Space(4);
                DrawConfidence();
                GUILayout.Space(6);

                if (GUILayout.Button("提交答案", GUILayout.Height(26)))
                {
                    SubmitRoughness(Mathf.Clamp01(_roughness), Mathf.Clamp01(_confidence));
                }
            }
            else if (string.Equals(_taskId, "change_detection", StringComparison.OrdinalIgnoreCase))
            {
                GUILayout.Label("是否发生变化:");
                _changeDetectedIndex = GUILayout.Toolbar(_changeDetectedIndex, new[] { "否", "是" });
                GUILayout.Space(4);

                if (_changeDetectedIndex == 1)
                {
                    GUILayout.Label("变化类别:");
                    _changeCategoryIndex = GUILayout.Toolbar(_changeCategoryIndex, new[] { "appearance", "disappearance", "movement", "replacement" });
                    GUILayout.Space(4);
                }
                else
                {
                    GUILayout.Label("变化类别: none");
                    GUILayout.Space(4);
                }

                DrawConfidence();
                GUILayout.Space(6);

                if (GUILayout.Button("提交答案", GUILayout.Height(26)))
                {
                    var changed = _changeDetectedIndex == 1;
                    var category = changed
                        ? new[] { "appearance", "disappearance", "movement", "replacement" }[_changeCategoryIndex]
                        : "none";
                    SubmitChangeDetection(changed, category, Mathf.Clamp01(_confidence));
                }
            }
            else
            {
                GUILayout.Label("该组件仅支持 distance_compression / semantic_size_bias / material_roughness* / change_detection。");
                if (GUILayout.Button("关闭", GUILayout.Height(24)))
                {
                    _awaitingInput = false;
                }
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
#endif

        private void DrawConfidence()
        {
            GUILayout.Label($"置信度: {_confidence:F2}");
            _confidence = GUILayout.HorizontalSlider(_confidence, 0f, 1f);
        }

        private void SubmitDistance(float distanceM, float conf)
        {
            var response = new LLMResponse
            {
                type = "inference",
                taskId = _taskId,
                trialId = _trialId,
                providerId = "human",
                confidence = conf,
                latencyMs = 0,
                answer = new DistanceAnswer { distance_m = distanceM, confidence = conf }
            };

            PublishInference(response);
        }

        private void SubmitSize(string larger, float conf)
        {
            var response = new LLMResponse
            {
                type = "inference",
                taskId = _taskId,
                trialId = _trialId,
                providerId = "human",
                confidence = conf,
                latencyMs = 0,
                answer = new SizeAnswer { larger = larger, confidence = conf }
            };

            PublishInference(response);
        }

        private void SubmitRoughness(float roughness, float conf)
        {
            var response = new LLMResponse
            {
                type = "inference",
                taskId = _taskId,
                trialId = _trialId,
                providerId = "human",
                confidence = conf,
                latencyMs = 0,
                answer = new RoughnessAnswer { roughness = roughness, confidence = conf }
            };

            PublishInference(response);
        }

        private void SubmitChangeDetection(bool changed, string category, float conf)
        {
            var response = new LLMResponse
            {
                type = "inference",
                taskId = _taskId,
                trialId = _trialId,
                providerId = "human",
                confidence = conf,
                latencyMs = 0,
                answer = new ChangeDetectionAnswer
                {
                    changed = changed,
                    category = changed ? category : "none",
                    confidence = conf
                }
            };

            PublishInference(response);
        }

        private void PublishInference(LLMResponse response)
        {
            var data = new InferenceReceivedEventData
            {
                requestId = "human_" + Guid.NewGuid().ToString("N"),
                taskId = response.taskId,
                trialId = response.trialId,
                timestamp = DateTime.UtcNow,
                response = response,
                providerId = response.providerId
            };

            try { eventBus?.InferenceReceived?.Publish(data); } catch { }
            _awaitingInput = false;
        }

        // 延迟订阅以适配 EventBusBootstrap 在本组件之后初始化通道的情况
        private System.Collections.IEnumerator EnsureSubscribe()
        {
            // 最长等待 3 秒以等待通道创建完成
            float start = Time.realtimeSinceStartup;
            float timeout = 3f;

            if (eventBus == null)
                eventBus = EventBusManager.Instance;

            while (eventBus == null && Time.realtimeSinceStartup - start < timeout)
            {
                yield return null;
                eventBus = EventBusManager.Instance;
            }
            if (eventBus == null)
                yield break;

            while (eventBus.TrialLifecycle == null && Time.realtimeSinceStartup - start < timeout)
            {
                yield return null;
            }

            if (eventBus.TrialLifecycle != null)
            {
                eventBus.TrialLifecycle.Subscribe(OnTrialLifecycle);
                if (verboseLog) Debug.Log("[HumanInputHandler] Late subscribed TrialLifecycle");
            }
        }

        private static GUIStyle Title()
        {
            var s = new GUIStyle(GUI.skin.label);
            s.fontSize = 14;
            s.fontStyle = FontStyle.Bold;
            return s;
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
        private class RoughnessAnswer
        {
            public float roughness;
            public float confidence;
        }

        [Serializable]
        private class ChangeDetectionAnswer
        {
            public bool changed;
            public string category;
            public float confidence;
        }
    }
}
