using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace VRPerception.Perception
{
    /// <summary>
    /// Anthropic Claude Provider 实现
    /// </summary>
    public class AnthropicProvider : ILLMProvider
    {
        private readonly ProviderConfig _config;
        private readonly string _endpoint;
        private readonly string _apiKey;
        private readonly string _model;
        
        public string ProviderType => "cloud_anthropic";
        public string ProviderName => _config.name ?? "Anthropic Claude";
        public bool IsAvailable => !string.IsNullOrEmpty(_apiKey) && !string.IsNullOrEmpty(_endpoint);
        
        public AnthropicProvider(ProviderConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _endpoint = config.endpoint ?? "https://api.anthropic.com/v1/messages";
            _apiKey = config.apiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            _model = config.model ?? "claude-3-sonnet-20240229";
            
            if (string.IsNullOrEmpty(_apiKey))
            {
                Debug.LogWarning($"[AnthropicProvider] No API key provided for {ProviderName}");
            }
        }
        
        public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var healthRequest = new LLMRequest
                {
                    taskId = "health_check",
                    trialId = 0,
                    systemPrompt = "You are a helpful assistant.",
                    taskPrompt = "Say 'OK' if you can see this message.",
                    maxTokens = 10,
                    timeoutMs = 5000
                };
                
                var response = await InferAsync(healthRequest, cancellationToken);
                return response.type != "error";
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
                webRequest.SetRequestHeader("x-api-key", _apiKey);
                webRequest.SetRequestHeader("anthropic-version", "2023-06-01");
                
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
        
        private AnthropicRequestBody BuildRequestBody(LLMRequest request)
        {
            var messages = new List<AnthropicMessage>();
            
            // 构建用户消息内容
            var content = new List<AnthropicContent>();
            
            if (!string.IsNullOrEmpty(request.taskPrompt))
            {
                content.Add(new AnthropicContent
                {
                    type = "text",
                    text = request.taskPrompt
                });
            }
            
            var images = GetRequestImages(request);
            for (int i = 0; i < images.Length; i++)
            {
                content.Add(new AnthropicContent
                {
                    type = "image",
                    source = new AnthropicImageSource
                    {
                        type = "base64",
                        media_type = "image/jpeg",
                        data = images[i]
                    }
                });
            }
            
            messages.Add(new AnthropicMessage
            {
                role = "user",
                content = content.ToArray()
            });
            
            var requestBody = new AnthropicRequestBody
            {
                model = _model,
                max_tokens = request.maxTokens,
                temperature = request.temperature,
                top_p = request.topP,
                messages = messages.ToArray()
            };
            
            // 添加系统提示
            if (!string.IsNullOrEmpty(request.systemPrompt))
            {
                requestBody.system = request.systemPrompt;
            }
            
            // 添加工具定义
            if (request.tools != null && request.tools.Length > 0)
            {
                requestBody.tools = ConvertTools(request.tools);
            }
            
            return requestBody;
        }
        
        private AnthropicTool[] ConvertTools(ToolSpec[] tools)
        {
            var anthropicTools = new AnthropicTool[tools.Length];
            for (int i = 0; i < tools.Length; i++)
            {
                anthropicTools[i] = new AnthropicTool
                {
                    name = tools[i].name,
                    description = tools[i].description,
                    input_schema = tools[i].parameters
                };
            }
            return anthropicTools;
        }
        
        private LLMResponse ParseResponse(string responseText, LLMRequest request, long latencyMs)
        {
            try
            {
                var anthropicResponse = JsonUtility.FromJson<AnthropicResponse>(responseText);
                
                if (anthropicResponse.content == null || anthropicResponse.content.Length == 0)
                {
                    return new LLMResponse
                    {
                        type = "error",
                        taskId = request.taskId,
                        trialId = request.trialId,
                        errorCode = "NO_CONTENT",
                        errorMessage = "No content in response",
                        latencyMs = latencyMs
                    };
                }
                
                // 检查是否有工具使用
                var toolUseContent = Array.Find(anthropicResponse.content, c => c.type == "tool_use");
                if (toolUseContent != null)
                {
                    // 解析为动作计划
                    var actions = ParseToolUse(anthropicResponse.content);
                    return new LLMResponse
                    {
                        type = "action_plan",
                        taskId = request.taskId,
                        trialId = request.trialId,
                        actions = actions,
                        latencyMs = latencyMs
                    };
                }
                
                // 查找文本内容
                var textContent = Array.Find(anthropicResponse.content, c => c.type == "text");
                if (textContent != null && !string.IsNullOrEmpty(textContent.text))
                {
                    return ParseInferenceResponse(textContent.text, request, latencyMs);
                }
                
                return new LLMResponse
                {
                    type = "error",
                    taskId = request.taskId,
                    trialId = request.trialId,
                    errorCode = "EMPTY_RESPONSE",
                    errorMessage = "Empty response content",
                    latencyMs = latencyMs
                };
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

        private static string[] GetRequestImages(LLMRequest request)
        {
            if (request?.imagesBase64 != null && request.imagesBase64.Length > 0)
            {
                var nonEmpty = new List<string>(request.imagesBase64.Length);
                for (int i = 0; i < request.imagesBase64.Length; i++)
                {
                    if (!string.IsNullOrEmpty(request.imagesBase64[i]))
                    {
                        nonEmpty.Add(request.imagesBase64[i]);
                    }
                }

                if (nonEmpty.Count > 0)
                {
                    return nonEmpty.ToArray();
                }
            }

            if (!string.IsNullOrEmpty(request?.imageBase64))
            {
                return new[] { request.imageBase64 };
            }

            return Array.Empty<string>();
        }
        
        private ActionCommand[] ParseToolUse(AnthropicContent[] contents)
        {
            var actions = new List<ActionCommand>();
            
            foreach (var content in contents)
            {
                if (content.type == "tool_use")
                {
                    actions.Add(new ActionCommand
                    {
                        id = content.id,
                        name = content.name,
                        parameters = content.input
                    });
                }
            }
            
            return actions.ToArray();
        }
        
        private LLMResponse ParseInferenceResponse(string content, LLMRequest request, long latencyMs)
        {
            // 尝试从内容中提取JSON
            var jsonStart = content.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonContent = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                try
                {
                    // 仅支持新格式: {task, trial_id, response: {...}, confidence, valid}
                    var newFormat = JsonUtility.FromJson<NewFormatResponse>(jsonContent);
                    if (newFormat != null && newFormat.response != null)
                    {
                        var resolvedConfidence = newFormat.confidence;
                        if (resolvedConfidence <= 0f && newFormat.response.confidence > 0f)
                        {
                            resolvedConfidence = newFormat.response.confidence;
                            Debug.LogWarning($"[AnthropicProvider] task={request.taskId} trial={request.trialId}: top-level confidence missing/zero, fallback to response.confidence={resolvedConfidence}");
                        }

                        if (!newFormat.valid && newFormat.response.valid)
                        {
                            Debug.LogWarning($"[AnthropicProvider] task={request.taskId} trial={request.trialId}: top-level valid missing/false, detected response.valid=true");
                        }

                        // 保留模型输出的 JSON 原文，便于排查字段映射
                        newFormat.response.raw_json = jsonContent;
                        return new LLMResponse
                        {
                            type = "inference",
                            taskId = request.taskId,
                            trialId = request.trialId,
                            answer = newFormat.response,
                            confidence = resolvedConfidence,
                            latencyMs = latencyMs
                        };
                    }
                }
                catch
                {
                    // JSON解析失败，返回原始内容
                }
            }

            Debug.LogWarning($"[AnthropicProvider] task={request.taskId} trial={request.trialId}: inference JSON parse failed, falling back to raw_content");
            return new LLMResponse
            {
                type = "inference",
                taskId = request.taskId,
                trialId = request.trialId,
                answer = new RawContentAnswer { raw_content = content },
                latencyMs = latencyMs
            };
        }

        [Serializable]
        private class RawContentAnswer
        {
            public string raw_content;
        }
    }
    
    // Anthropic API 数据结构
    [Serializable]
    public class AnthropicRequestBody
    {
        public string model;
        public int max_tokens;
        public float temperature;
        public float top_p;
        public string system;
        public AnthropicMessage[] messages;
        public AnthropicTool[] tools;
    }
    
    [Serializable]
    public class AnthropicMessage
    {
        public string role;
        public AnthropicContent[] content;
    }
    
    [Serializable]
    public class AnthropicContent
    {
        public string type;
        public string text;
        public AnthropicImageSource source;
        public string id;
        public string name;
        public object input;
    }
    
    [Serializable]
    public class AnthropicImageSource
    {
        public string type;
        public string media_type;
        public string data;
    }
    
    [Serializable]
    public class AnthropicTool
    {
        public string name;
        public string description;
        public ParameterSpec input_schema;
    }
    
    [Serializable]
    public class AnthropicResponse
    {
        public AnthropicContent[] content;
    }
}
