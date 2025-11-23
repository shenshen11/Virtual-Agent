using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace VRPerception.Perception
{
    /// <summary>
    /// Ollama Provider 实现（本地部署）
    /// </summary>
    public class OllamaProvider : ILLMProvider
    {
        private readonly ProviderConfig _config;
        private readonly string _endpoint;
        private readonly string _model;
        
        public string ProviderType => "local_ollama";
        public string ProviderName => _config.name ?? "Ollama";
        public bool IsAvailable => !string.IsNullOrEmpty(_endpoint);
        
        public OllamaProvider(ProviderConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _endpoint = config.endpoint ?? "http://localhost:11434/api/generate";
            _model = config.model ?? "llava";
        }
        
        public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // 检查Ollama服务是否可用
                var healthEndpoint = _endpoint.Replace("/api/generate", "/api/tags");
                
                using var webRequest = UnityWebRequest.Get(healthEndpoint);
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
        
        private OllamaRequestBody BuildRequestBody(LLMRequest request)
        {
            // 构建提示词
            var promptBuilder = new StringBuilder();
            
            if (!string.IsNullOrEmpty(request.systemPrompt))
            {
                promptBuilder.AppendLine($"System: {request.systemPrompt}");
            }
            
            if (!string.IsNullOrEmpty(request.taskPrompt))
            {
                promptBuilder.AppendLine($"User: {request.taskPrompt}");
            }
            
            // 添加工具定义
            if (request.tools != null && request.tools.Length > 0)
            {
                promptBuilder.AppendLine("\nAvailable tools:");
                foreach (var tool in request.tools)
                {
                    promptBuilder.AppendLine($"- {tool.name}: {tool.description}");
                }
                promptBuilder.AppendLine("\nRespond with ONLY JSON in the format: {\"type\": \"inference\", \"answer\": {...}} or {\"type\": \"action_plan\", \"actions\": [...]}");
            }
            else
            {
                promptBuilder.AppendLine("\nRespond with ONLY JSON in the format: {\"type\": \"inference\", \"answer\": {...}, \"confidence\": 0.0-1.0}");
            }
            
            var requestBody = new OllamaRequestBody
            {
                model = _model,
                prompt = promptBuilder.ToString(),
                stream = false,
                options = new OllamaOptions
                {
                    temperature = request.temperature,
                    top_p = request.topP,
                    num_predict = request.maxTokens
                }
            };
            
            // 添加图像
            if (!string.IsNullOrEmpty(request.imageBase64))
            {
                requestBody.images = new string[] { request.imageBase64 };
            }
            
            return requestBody;
        }
        
        private LLMResponse ParseResponse(string responseText, LLMRequest request, long latencyMs)
        {
            try
            {
                var ollamaResponse = JsonUtility.FromJson<OllamaResponse>(responseText);
                
                if (string.IsNullOrEmpty(ollamaResponse.response))
                {
                    return new LLMResponse
                    {
                        type = "error",
                        taskId = request.taskId,
                        trialId = request.trialId,
                        errorCode = "EMPTY_RESPONSE",
                        errorMessage = "Empty response from Ollama",
                        latencyMs = latencyMs
                    };
                }
                
                return ParseInferenceResponse(ollamaResponse.response, request, latencyMs);
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
        
        private LLMResponse ParseInferenceResponse(string content, LLMRequest request, long latencyMs)
        {
            // 清理响应内容，移除可能的前缀/后缀
            content = content.Trim();
            
            // 查找JSON内容
            var jsonStart = content.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}');
            
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonContent = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                
                try
                {
                    // 尝试解析为通用响应
                    var genericResponse = JsonUtility.FromJson<OllamaGenericResponse>(jsonContent);
                    
                    if (genericResponse.type == "action_plan" && genericResponse.actions != null)
                    {
                        return new LLMResponse
                        {
                            type = "action_plan",
                            taskId = request.taskId,
                            trialId = request.trialId,
                            actions = genericResponse.actions,
                            latencyMs = latencyMs
                        };
                    }
                    else if (genericResponse.type == "inference" && genericResponse.answer != null)
                    {
                        return new LLMResponse
                        {
                            type = "inference",
                            taskId = request.taskId,
                            trialId = request.trialId,
                            answer = genericResponse.answer,
                            confidence = genericResponse.confidence,
                            explanation = genericResponse.explanation,
                            latencyMs = latencyMs
                        };
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[OllamaProvider] JSON parse failed: {ex.Message}, content: {jsonContent}");
                }
            }
            
            // 如果JSON解析失败，返回原始内容
            return new LLMResponse
            {
                type = "inference",
                taskId = request.taskId,
                trialId = request.trialId,
                answer = new { raw_content = content },
                latencyMs = latencyMs
            };
        }
    }
    
    // Ollama API 数据结构
    [Serializable]
    public class OllamaRequestBody
    {
        public string model;
        public string prompt;
        public bool stream;
        public string[] images;
        public OllamaOptions options;
    }
    
    [Serializable]
    public class OllamaOptions
    {
        public float temperature;
        public float top_p;
        public int num_predict;
    }
    
    [Serializable]
    public class OllamaResponse
    {
        public string model;
        public string response;
        public bool done;
    }
    
    [Serializable]
    public class OllamaGenericResponse
    {
        public string type;
        public object answer;
        public ActionCommand[] actions;
        public float confidence;
        public string explanation;
    }
}
