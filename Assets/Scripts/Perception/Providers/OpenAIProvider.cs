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
    /// OpenAI Provider 实现（兼容 OpenAI API 的服务，包括 vLLM 等）
    /// </summary>
    public class OpenAIProvider : ILLMProvider
    {
        private readonly ProviderConfig _config;
        private readonly string _endpoint;
        private readonly string _apiKey;
        private readonly string _model;
        
        public virtual string ProviderType => "cloud_openai";
        public virtual string ProviderName => _config.name ?? "OpenAI";
        public virtual bool IsAvailable => !string.IsNullOrEmpty(_endpoint);
        
        public OpenAIProvider(ProviderConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _endpoint = config.endpoint ?? "https://api.openai.com/v1/chat/completions";
            _apiKey = config.apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            _model = config.model ?? "gpt-4-vision-preview";
            
            if (string.IsNullOrEmpty(_apiKey))
            {
                Debug.Log($"[OpenAIProvider] No API key provided for {ProviderName}; proceeding without Authorization header (OK for local OpenAI-compatible servers like LMDeploy/vLLM).");
            }
        }
        
        public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // 发送一个简单的健康检查请求
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
                var jsonBody = BuildRequestJson(request);
#if UNITY_EDITOR
                var imageCount = GetRequestImages(request).Length;
                Debug.Log($"[OpenAIProvider] POST {_endpoint} model={_model} max_tokens={request.maxTokens} image_count={imageCount} tools={(request.tools?.Length ?? 0)}");
#endif
                using var webRequest = new UnityWebRequest(_endpoint, "POST");
                webRequest.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(_apiKey))
                {
                    webRequest.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
                }
                
                // 设置超时
                webRequest.timeout = request.timeoutMs / 1000;
                
                var operation = webRequest.SendWebRequest();
                
                // 等待请求完成或取消
                while (!operation.isDone && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Yield();
                }
                
                cancellationToken.ThrowIfCancellationRequested();
                
                var latency = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
                
                var responseText = webRequest.downloadHandler.text;
                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    var bodySnippet = responseText;
                    if (!string.IsNullOrEmpty(bodySnippet) && bodySnippet.Length > 512) bodySnippet = bodySnippet.Substring(0, 512) + "...";
#if UNITY_EDITOR
                    Debug.LogWarning($"[OpenAIProvider] HTTP {(int)webRequest.responseCode} {webRequest.result} url={_endpoint} body={bodySnippet}");
#endif
                    return new LLMResponse
                    {
                        type = "error",
                        taskId = request.taskId,
                        trialId = request.trialId,
                        errorCode = webRequest.responseCode.ToString(),
                        errorMessage = string.IsNullOrEmpty(responseText) ? webRequest.error : $"{webRequest.error} | {bodySnippet}",
                        latencyMs = latency
                    };
                }
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
        
        private OpenAIRequestBody BuildRequestBody(LLMRequest request)
        {
            var messages = new List<OpenAIRequestMessage>();

            // 系统消息
            if (!string.IsNullOrEmpty(request.systemPrompt))
            {
                messages.Add(new OpenAIRequestMessage
                {
                    role = "system",
                    content = new []
                    {
                        new OpenAIContent
                        {
                            type = "text",
                            text = request.systemPrompt
                        }
                    }
                });
            }

            // 用户消息（包含图像和任务提示）
            var userContent = new List<OpenAIContent>();

            if (!string.IsNullOrEmpty(request.taskPrompt))
            {
                userContent.Add(new OpenAIContent
                {
                    type = "text",
                    text = request.taskPrompt
                });
            }

            var images = GetRequestImages(request);
            if (images.Length > 0)
            {
                userContent.Add(new OpenAIContent
                {
                    type = "image_url",
                    image_url = new OpenAIImageUrl
                    {
                        url = $"data:image/jpeg;base64,{images[0]}"
                    }
                });
            }

            messages.Add(new OpenAIRequestMessage
            {
                role = "user",
                content = userContent.ToArray()
            });

            var requestBody = new OpenAIRequestBody
            {
                model = _model,
                messages = messages.ToArray(),
                max_tokens = request.maxTokens,
                temperature = request.temperature,
                top_p = request.topP
            };

            // 添加工具定义
            if (request.tools != null && request.tools.Length > 0)
            {
                requestBody.tools = ConvertTools(request.tools);
                requestBody.tool_choice = "auto";
            }

            return requestBody;
        }

        // 手工构造请求 JSON：文本走 content 字符串，多模态带图时走 content 数组
        private string BuildRequestJson(LLMRequest request)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder(1024);
            sb.Append('{');

            // model
            sb.Append("\"model\":\"").Append(JsonEscape(_model)).Append("\",");

            // messages
            sb.Append("\"messages\":[");
            bool first = true;

            // system
            if (!string.IsNullOrEmpty(request.systemPrompt))
            {
                sb.Append("{\"role\":\"system\",\"content\":\"")
                  .Append(JsonEscape(request.systemPrompt))
                  .Append("\"}");
                first = false;
            }

            // user
            var images = GetRequestImages(request);
            if (images.Length == 0)
            {
                if (!first) sb.Append(',');
                sb.Append("{\"role\":\"user\",\"content\":\"")
                  .Append(JsonEscape(request.taskPrompt ?? string.Empty))
                  .Append("\"}");
            }
            else
            {
                if (!first) sb.Append(',');
                sb.Append("{\"role\":\"user\",\"content\":[");
                bool needComma = false;
                if (!string.IsNullOrEmpty(request.taskPrompt))
                {
                    sb.Append("{\"type\":\"text\",\"text\":\"")
                      .Append(JsonEscape(request.taskPrompt))
                      .Append("\"}");
                    needComma = true;
                }
                for (int i = 0; i < images.Length; i++)
                {
                    if (needComma) sb.Append(',');
                    sb.Append("{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/jpeg;base64,")
                      .Append(JsonEscape(images[i] ?? string.Empty))
                      .Append("\"}}");
                    needComma = true;
                }
                sb.Append("]}");
            }
            sb.Append("]");

            // params
            if (request.maxTokens > 0)
                sb.Append(",\"max_tokens\":").Append(request.maxTokens);
            if (request.temperature >= 0f)
                sb.Append(",\"temperature\":").Append(request.temperature.ToString(culture));
            if (request.topP >= 0f)
                sb.Append(",\"top_p\":").Append(request.topP.ToString(culture));

            sb.Append('}');
            return sb.ToString();
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            var sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
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
        
        private OpenAITool[] ConvertTools(ToolSpec[] tools)
        {
            var openaiTools = new OpenAITool[tools.Length];
            for (int i = 0; i < tools.Length; i++)
            {
                openaiTools[i] = new OpenAITool
                {
                    type = "function",
                    function = new OpenAIFunction
                    {
                        name = tools[i].name,
                        description = tools[i].description,
                        parameters = tools[i].parameters
                    }
                };
            }
            return openaiTools;
        }
        
        private LLMResponse ParseResponse(string responseText, LLMRequest request, long latencyMs)
        {
            try
            {
                var openaiResponse = JsonUtility.FromJson<OpenAIResponse>(responseText);
                
                if (openaiResponse.choices == null || openaiResponse.choices.Length == 0)
                {
                    return new LLMResponse
                    {
                        type = "error",
                        taskId = request.taskId,
                        trialId = request.trialId,
                        errorCode = "NO_CHOICES",
                        errorMessage = "No choices in response",
                        latencyMs = latencyMs
                    };
                }
                
                var choice = openaiResponse.choices[0];
                var message = choice.message;
                
                // 检查是否有工具调用
                if (message.tool_calls != null && message.tool_calls.Length > 0)
                {
                    // 解析为动作计划
                    var actions = ParseToolCalls(message.tool_calls);
                    return new LLMResponse
                    {
                        type = "action_plan",
                        taskId = request.taskId,
                        trialId = request.trialId,
                        actions = actions,
                        latencyMs = latencyMs
                    };
                }
                else if (!string.IsNullOrEmpty(message.content))
                {
                    // 尝试解析为JSON推理结果
                    return ParseInferenceResponse(message.content, request, latencyMs);
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
        
        private ActionCommand[] ParseToolCalls(OpenAIToolCall[] toolCalls)
        {
            var actions = new ActionCommand[toolCalls.Length];
            for (int i = 0; i < toolCalls.Length; i++)
            {
                var toolCall = toolCalls[i];
                object parsedParams = null;

                var args = toolCall?.function != null ? toolCall.function.arguments : null;
                if (!string.IsNullOrEmpty(args))
                {
                    try
                    {
                        parsedParams = JsonUtility.FromJson<object>(args);
                    }
                    catch
                    {
                        // 回退为原始字符串，交由上层自行解析
                        parsedParams = args;
                    }
                }

                actions[i] = new ActionCommand
                {
                    id = toolCall?.id,
                    name = toolCall?.function != null ? toolCall.function.name : null,
                    parameters = parsedParams
                };
            }
            return actions;
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
                    // 对应 PromptTemplates.cs 中的新标准格式
                    var newFormat = JsonUtility.FromJson<NewFormatResponse>(jsonContent);
                    if (newFormat != null && newFormat.response != null)
                    {
                        // 保留模型输出的 JSON 原文，便于排查字段映射
                        newFormat.response.raw_json = jsonContent;
                        return new LLMResponse
                        {
                            type = "inference",
                            taskId = request.taskId,
                            trialId = request.trialId,
                            answer = newFormat.response,
                            confidence = newFormat.confidence,
                            latencyMs = latencyMs
                        };
                    }
                }
                catch
                {
                    // JSON解析失败，返回原始内容
                }
            }

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
    
    // OpenAI API 数据结构
    [Serializable]
    public class OpenAIRequestBody
    {
        public string model;
        public OpenAIRequestMessage[] messages;
        public int max_tokens;
        public float temperature;
        public float top_p;
        public OpenAITool[] tools;
        public string tool_choice;
    }
    
    [Serializable]
    public class OpenAIRequestMessage
    {
        public string role;
        public OpenAIContent[] content;
    }

    [Serializable]
    public class OpenAIResponseMessage
    {
        public string role;
        public string content;
        public OpenAIToolCall[] tool_calls;
    }
    
    [Serializable]
    public class OpenAIContent
    {
        public string type;
        public string text;
        public OpenAIImageUrl image_url;
    }
    
    [Serializable]
    public class OpenAIImageUrl
    {
        public string url;
    }
    
    [Serializable]
    public class OpenAITool
    {
        public string type;
        public OpenAIFunction function;
    }
    
    [Serializable]
    public class OpenAIFunction
    {
        public string name;
        public string description;
        public ParameterSpec parameters;

        // 注意：在响应中的 tool_calls.function.arguments 为 JSON 字符串
        // 请求体中的 function 无该字段，保留为空即可（序列化时会被忽略）
        public string arguments;
    }
    
    [Serializable]
    public class OpenAIResponse
    {
        public OpenAIChoice[] choices;
    }
    
    [Serializable]
    public class OpenAIChoice
    {
        public OpenAIResponseMessage message;
    }
    
    [Serializable]
    public class OpenAIToolCall
    {
        public string id;
        public string type;
        public OpenAIFunction function;
    }
    
    // 新格式响应结构：{task, trial_id, response: {...}, confidence, valid}
    // 对应 PromptTemplates.cs 中新定义的标准输出格式
    [Serializable]
    public class NewFormatResponse
    {
        public string task;
        public string trial_id;
        public NewFormatInnerResponse response;
        public float confidence;
        public bool valid;
    }

    [Serializable]
    public class NewFormatInnerResponse
    {
        // Distance Compression / Horizon Cue Integration
        public float distance_m;

        // Visual Crowding
        public string letter;

        // Change Detection
        public bool changed;
        public string category;

        // Numerosity Comparison
        public string more_side;

        // Material Roughness
        public float roughness;

        // Depth JND Staircase
        public string closer;

        // Color Constancy Adjustment
        public string choice;
        // RGB 数组（JsonUtility 对数组支持有限，使用字段映射）
        public int rgb_r;
        public int rgb_g;
        public int rgb_b;

        // 从模型输出中截取的 { ... } 原文（用于日志排查）
        public string raw_json;
    }
}
