using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace VRPerception.Perception
{
    /// <summary>
    /// 自定义 HTTP Provider 实现，用于对接自建微服务
    /// </summary>
    public class CustomHttpProvider : ILLMProvider
    {
        private readonly ProviderConfig _config;
        private readonly string _endpoint;
        private readonly string _apiKey;
        
        public string ProviderType => "custom_http";
        public string ProviderName => _config.name ?? "Custom HTTP";
        public bool IsAvailable => !string.IsNullOrEmpty(_endpoint);
        
        public CustomHttpProvider(ProviderConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _endpoint = config.endpoint ?? throw new ArgumentException("Endpoint is required for CustomHttpProvider");
            _apiKey = config.apiKey;
        }
        
        public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // 尝试发送健康检查请求
                var healthEndpoint = _endpoint.TrimEnd('/') + "/health";
                
                using var webRequest = UnityWebRequest.Get(healthEndpoint);
                if (!string.IsNullOrEmpty(_apiKey))
                {
                    webRequest.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
                }
                webRequest.timeout = 5;
                
                var operation = webRequest.SendWebRequest();
                
                while (!operation.isDone && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Yield();
                }
                
                return webRequest.result == UnityWebRequest.Result.Success;
            }
            catch
            {
                return false;
            }
        }
        
        public async Task<LLMResponse> InferAsync(LLMRequest request, CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            
            try
            {
                var requestBody = BuildRequestBody(request);
                var jsonBody = JsonUtility.ToJson(requestBody);
                
                using var webRequest = new UnityWebRequest(_endpoint, "POST");
                webRequest.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.SetRequestHeader("Content-Type", "application/json");
                
                if (!string.IsNullOrEmpty(_apiKey))
                {
                    webRequest.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
                }
                
                webRequest.timeout = request.timeoutMs / 1000;
                
                var operation = webRequest.SendWebRequest();
                
                while (!operation.isDone && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Yield();
                }
                
                cancellationToken.ThrowIfCancellationRequested();
                
                var latency = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
                
                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    return new LLMResponse
                    {
                        type = "error",
                        taskId = request.taskId,
                        trialId = request.trialId,
                        errorCode = webRequest.responseCode.ToString(),
                        errorMessage = webRequest.error,
                        latencyMs = latency
                    };
                }
                
                var responseText = webRequest.downloadHandler.text;
                return ParseResponse(responseText, request, latency);
            }
            catch (OperationCanceledException)
            {
                return new LLMResponse
                {
                    type = "error",
                    taskId = request.taskId,
                    trialId = request.trialId,
                    errorCode = "CANCELLED",
                    errorMessage = "Request was cancelled",
                    latencyMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds
                };
            }
            catch (Exception ex)
            {
                return new LLMResponse
                {
                    type = "error",
                    taskId = request.taskId,
                    trialId = request.trialId,
                    errorCode = "EXCEPTION",
                    errorMessage = ex.Message,
                    latencyMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds
                };
            }
        }
        
        private CustomRequestBody BuildRequestBody(LLMRequest request)
        {
            return new CustomRequestBody
            {
                taskId = request.taskId,
                trialId = request.trialId,
                systemPrompt = request.systemPrompt,
                taskPrompt = request.taskPrompt,
                payloadMode = request.payloadMode.ToString(),
                imageBase64 = request.imageBase64,
                imagesBase64 = request.imagesBase64,
                videoBase64 = request.videoBase64,
                videoMimeType = request.videoMimeType,
                videoFps = request.videoFps,
                videoDurationMs = request.videoDurationMs,
                metadata = request.metadata,
                metadataList = request.metadataList,
                tools = request.tools,
                parameters = new CustomParameters
                {
                    temperature = request.temperature,
                    topP = request.topP,
                    maxTokens = request.maxTokens,
                    stopSequences = request.stopSequences
                }
            };
        }
        
        private LLMResponse ParseResponse(string responseText, LLMRequest request, long latencyMs)
        {
            try
            {
                // 尝试直接解析为 LLMResponse
                var response = JsonUtility.FromJson<LLMResponse>(responseText);
                
                // 确保基本字段正确
                response.taskId = request.taskId;
                response.trialId = request.trialId;
                response.latencyMs = latencyMs;
                
                return response;
            }
            catch (Exception ex)
            {
                return new LLMResponse
                {
                    type = "error",
                    taskId = request.taskId,
                    trialId = request.trialId,
                    errorCode = "PARSE_ERROR",
                    errorMessage = $"Failed to parse response: {ex.Message}",
                    latencyMs = latencyMs
                };
            }
        }
    }
    
    // Custom HTTP API 数据结构
    [Serializable]
    public class CustomRequestBody
    {
        public string taskId;
        public int trialId;
        public string systemPrompt;
        public string taskPrompt;
        public string payloadMode;
        public string imageBase64;
        public string[] imagesBase64;
        public string videoBase64;
        public string videoMimeType;
        public int videoFps;
        public int videoDurationMs;
        public FrameMetadata metadata;
        public FrameMetadata[] metadataList;
        public ToolSpec[] tools;
        public CustomParameters parameters;
    }
    
    [Serializable]
    public class CustomParameters
    {
        public float temperature;
        public float topP;
        public int maxTokens;
        public string[] stopSequences;
    }
}
