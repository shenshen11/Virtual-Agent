using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VRPerception.Perception
{
    public class PerceptionE2ETest : MonoBehaviour
    {
        [Header("Refs (optional)")]
        public PerceptionSystem perceptionSystem;

        [Header("Test Options")]
        public string taskId = "semantic_size_bias";
        public int trialId = 1;
        [Tooltip("为空则使用 PromptTemplates 的默认系统提示")]
        [TextArea] public string overrideSystemPrompt = "";
        [Tooltip("为空则使用 PromptTemplates 的默认任务提示")]
        [TextArea] public string overrideTaskPrompt = "";
        public int timeoutMs = 20000;
        public bool autoRunOnStart = true;

        private CancellationTokenSource _cts;

        private async void Start()
        {
            if (perceptionSystem == null) perceptionSystem = FindObjectOfType<PerceptionSystem>();
            if (autoRunOnStart)
            {
                // 等待一小段时间，确保 ProviderRegistry 已完成首次健康检查
                await Task.Delay(1500);
                await RunOnceAsync();
            }
        }

        [ContextMenu("Run E2E Test")]
        public async void RunFromContextMenu()
        {
            await RunOnceAsync();
        }

        public async Task RunOnceAsync()
        {
            if (perceptionSystem == null)
            {
                Debug.LogError("[PerceptionE2ETest] PerceptionSystem not found in scene.");
                return;
            }

            // 构造工具：为确保兼容，我们传 null（当前 JSON 构造未包含 tools）
            ToolSpec[] tools = null;

            _cts?.Cancel();
            _cts = new CancellationTokenSource(timeoutMs);

            try
            {
                var resp = await perceptionSystem.RequestInferenceAsync(
                    taskId: taskId,
                    trialId: trialId,
                    systemPrompt: string.IsNullOrWhiteSpace(overrideSystemPrompt) ? null : overrideSystemPrompt,
                    taskPrompt: string.IsNullOrWhiteSpace(overrideTaskPrompt) ? null : overrideTaskPrompt,
                    tools: tools,
                    cancellationToken: _cts.Token
                );

                if (resp == null)
                {
                    Debug.LogError("[PerceptionE2ETest] Null response.");
                    return;
                }

                if (resp.type == "error")
                {
                    Debug.LogError($"[PerceptionE2ETest] Error: code={resp.errorCode}, msg={resp.errorMessage}, latency={resp.latencyMs}ms, provider={resp.providerId}");
                }
                else if (resp.type == "action_plan")
                {
                    var actionsJson = resp.actions != null ? JsonUtility.ToJson(new Wrapper<ActionCommand>(resp.actions)) : "[]";
                    Debug.Log($"[PerceptionE2ETest] ActionPlan ok. provider={resp.providerId}, latency={resp.latencyMs}ms, actions={actionsJson}");
                }
                else // inference
                {
                    var content = resp.answer != null ? JsonUtility.ToJson(resp.answer) : "(no answer)";
                    Debug.Log($"[PerceptionE2ETest] Inference ok. provider={resp.providerId}, latency={resp.latencyMs}ms, content={content}");
                }
            }
            catch (OperationCanceledException)
            {
                Debug.LogError("[PerceptionE2ETest] Cancelled.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PerceptionE2ETest] Exception: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        [Serializable]
        private class Wrapper<T>
        {
            public T[] items;
            public Wrapper(T[] data) { items = data; }
        }
    }
}