using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VRPerception.Perception
{
    public class VLLMQuickTest : MonoBehaviour
    {
        [Header("Refs (optional)")]
        public ProviderRouter providerRouter;
        public ProviderRegistry providerRegistry;

        [Header("Config")]
        public string systemPrompt = "You are a helpful assistant.";
        public string taskPrompt = "Respond with a single word: OK";
        public int timeoutMs = 15000;
        public int maxTokens = 16;
        public float temperature = 0.0f;
        public float topP = 1.0f;
        public bool autoRunOnStart = true;

        private CancellationTokenSource _cts;

        private async void Start()
        {
            if (providerRouter == null) providerRouter = FindObjectOfType<ProviderRouter>();
            if (providerRegistry == null) providerRegistry = FindObjectOfType<ProviderRegistry>();

            if (autoRunOnStart)
            {
                await RunTestAsync();
            }
        }

        [ContextMenu("Run VLLM Quick Test")]
        public async void RunFromContextMenu()
        {
            await RunTestAsync();
        }

        public async Task RunTestAsync()
        {
            try
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource(timeoutMs);

                var request = new LLMRequest
                {
                    taskId = "connectivity_test",
                    trialId = 0,
                    systemPrompt = systemPrompt,
                    taskPrompt = taskPrompt,
                    maxTokens = maxTokens,
                    temperature = temperature,
                    topP = topP,
                    timeoutMs = timeoutMs
                };

                if (providerRouter == null)
                {
                    Debug.LogError("[VLLMQuickTest] ProviderRouter not found in scene.");
                    return;
                }

                var response = await providerRouter.RouteRequestAsync(request, _cts.Token);
                if (response.type == "error")
                {
                    Debug.LogError($"[VLLMQuickTest] Error: code={response.errorCode}, msg={response.errorMessage}, latency={response.latencyMs}ms, provider={response.providerId}");
                }
                else
                {
                    var content = response.answer != null ? JsonUtility.ToJson(response.answer) : "(no answer)";
                    Debug.Log($"[VLLMQuickTest] Success. Provider={response.providerId}, latency={response.latencyMs}ms, content={content}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VLLMQuickTest] Exception: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}