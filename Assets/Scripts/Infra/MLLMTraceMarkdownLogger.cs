using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;

namespace VRPerception.Infra
{
    /// <summary>
    /// Writes an MLLM communication trace to a Markdown file per session.
    /// </summary>
    public class MLLMTraceMarkdownLogger : MonoBehaviour
    {
        [Header("Output")]
        [SerializeField] private string rootFolderName = "VRP_Logs";
        [SerializeField] private string fileName = "mllm_trace.md";
        [SerializeField] private bool enableLogging = true;
        [SerializeField] private bool includePrompts = false;
        [SerializeField] private int maxPromptChars = 400;
        [SerializeField] private bool includeMetadataJson = false;
        [SerializeField] private int maxMetadataChars = 600;
        [SerializeField] private bool includeAnswerJson = false;
        [SerializeField] private int maxAnswerChars = 600;
        [SerializeField] private bool includeImageHash = true;

        [Header("References")]
        [SerializeField] private EventBusManager eventBus;
        [SerializeField] private ProviderRouter providerRouter;

        private string _sessionDir;
        private string _tracePath;
        private readonly object _lock = new object();
        private readonly Dictionary<string, RequestInfo> _requests = new Dictionary<string, RequestInfo>();

        // 每个 requestId 的聚合信息，用于生成 Markdown 小节头
        private class RequestInfo
        {
            public string taskId;
            public int? trialId;
            public bool headerWritten;
        }

        private void Awake()
        {
            if (eventBus == null) eventBus = EventBusManager.Instance;
            if (providerRouter == null) providerRouter = FindObjectOfType<ProviderRouter>();

            // 每次会话独立目录，避免不同运行混杂
            var root = Path.Combine(Application.persistentDataPath, rootFolderName);
            Directory.CreateDirectory(root);

            var session = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            _sessionDir = Path.Combine(root, session);
            Directory.CreateDirectory(_sessionDir);

            _tracePath = Path.Combine(_sessionDir, fileName);
            AppendLines(
                "# MLLM Trace",
                $"- session: {session}",
                $"- startedUtc: {DateTime.UtcNow:O}",
                string.Empty
            );
        }

        private void OnEnable()
        {
            if (eventBus == null) return;

            // 订阅抓帧与推理链路事件
            eventBus.FrameRequested?.Subscribe(OnFrameRequested);
            eventBus.FrameCaptured?.Subscribe(OnFrameCaptured);
            eventBus.InferenceReceived?.Subscribe(OnInferenceReceived);
            eventBus.ActionPlanReceived?.Subscribe(OnActionPlanReceived);
            eventBus.Error?.Subscribe(OnError);

            if (providerRouter != null)
            {
                providerRouter.RequestCompleted += OnProviderRequestCompleted;
                providerRouter.RequestFailed += OnProviderRequestFailed;
            }
        }

        private void OnDisable()
        {
            if (eventBus != null)
            {
                eventBus.FrameRequested?.Unsubscribe(OnFrameRequested);
                eventBus.FrameCaptured?.Unsubscribe(OnFrameCaptured);
                eventBus.InferenceReceived?.Unsubscribe(OnInferenceReceived);
                eventBus.ActionPlanReceived?.Unsubscribe(OnActionPlanReceived);
                eventBus.Error?.Unsubscribe(OnError);
            }

            if (providerRouter != null)
            {
                providerRouter.RequestCompleted -= OnProviderRequestCompleted;
                providerRouter.RequestFailed -= OnProviderRequestFailed;
            }
        }

        private void OnFrameRequested(FrameRequestedEventData data)
        {
            if (!enableLogging || data == null) return;
            var details = $"taskId={data.taskId} trialId={data.trialId} options={FormatOptions(data.options)}";
            LogEvent(data.requestId, data.taskId, data.trialId, "frame_requested", data.timestamp, details);
        }

        private void OnFrameCaptured(FrameCapturedEventData data)
        {
            if (!enableLogging || data == null) return;
            var base64Len = string.IsNullOrEmpty(data.imageBase64) ? 0 : data.imageBase64.Length;
            var details = $"success={data.success} base64Len={base64Len}";
            if (!data.success)
            {
                details += $" error={data.errorMessage}";
            }
            if (includeMetadataJson && data.metadata != null)
            {
                details += $" metadata={Truncate(TryToJson(data.metadata), maxMetadataChars)}";
            }
            LogEvent(data.requestId, data.taskId, data.trialId, "frame_captured", data.timestamp, details);
        }

        private void OnInferenceReceived(InferenceReceivedEventData data)
        {
            if (!enableLogging || data == null) return;
            var response = data.response;
            var details = $"providerId={data.providerId} latencyMs={response?.latencyMs ?? 0} confidence={response?.confidence ?? 0f}";
            if (includeAnswerJson && response?.answer != null)
            {
                details += $" answer={Truncate(TryToJson(response.answer), maxAnswerChars)}";
            }
            if (!string.IsNullOrEmpty(response?.explanation))
            {
                details += $" explanation={Truncate(response.explanation, maxAnswerChars)}";
            }
            LogEvent(data.requestId, data.taskId, data.trialId, "inference_received", data.timestamp, details);
        }

        private void OnActionPlanReceived(ActionPlanReceivedEventData data)
        {
            if (!enableLogging || data == null) return;
            var actionNames = FormatActionNames(data.actions);
            var details = $"providerId={data.providerId} actionsCount={data.actions?.Length ?? 0} actions={actionNames}";
            LogEvent(data.requestId, data.taskId, data.trialId, "action_plan_received", data.timestamp, details);
        }

        private void OnError(ErrorEventData data)
        {
            if (!enableLogging || data == null) return;
            var requestId = TryGetContextString(data.context, "RequestId", "requestId");
            var taskId = TryGetContextString(data.context, "TaskId", "taskId");
            var trialId = TryGetContextInt(data.context, "TrialId", "trialId");
            var details = $"source={data.source} severity={data.severity} code={data.errorCode} message={data.message}";
            LogEvent(requestId, taskId, trialId, "error", data.timestamp, details);
        }

        private void OnProviderRequestCompleted(string providerId, LLMRequest request, LLMResponse response)
        {
            if (!enableLogging || request == null) return;
            var details = new StringBuilder();
            details.Append("providerId=").Append(providerId);
            details.Append(" responseType=").Append(response?.type ?? "null");
            details.Append(" latencyMs=").Append(response?.latencyMs ?? 0);
            details.Append(" tools=").Append(request.tools?.Length ?? 0);
            details.Append(" imageBase64Len=").Append(string.IsNullOrEmpty(request.imageBase64) ? 0 : request.imageBase64.Length);
            if (includeImageHash && !string.IsNullOrEmpty(request.imageBase64))
            {
                details.Append(" imageHash=").Append(HashString(request.imageBase64));
            }
            if (includePrompts)
            {
                details.Append(" systemPrompt=").Append(Truncate(request.systemPrompt, maxPromptChars));
                details.Append(" taskPrompt=").Append(Truncate(request.taskPrompt, maxPromptChars));
            }
            if (includeMetadataJson && request.metadata != null)
            {
                details.Append(" metadata=").Append(Truncate(TryToJson(request.metadata), maxMetadataChars));
            }
            LogEvent(request.requestId, request.taskId, request.trialId, "provider_completed", DateTime.UtcNow, details.ToString());
        }

        private void OnProviderRequestFailed(string providerId, LLMRequest request, Exception ex)
        {
            if (!enableLogging || request == null) return;
            var details = $"providerId={providerId} error={ex?.Message}";
            LogEvent(request.requestId, request.taskId, request.trialId, "provider_failed", DateTime.UtcNow, details);
        }

        private void LogEvent(string requestId, string taskId, int? trialId, string eventType, DateTime timestamp, string details)
        {
            if (string.IsNullOrEmpty(requestId)) requestId = "unknown";

            lock (_lock)
            {
                // 以 requestId 为主键写入 Markdown 小节
                EnsureHeader(requestId, taskId, trialId, timestamp);
                AppendLines($"- [{timestamp:O}] {eventType}: {details}");
            }
        }

        private void EnsureHeader(string requestId, string taskId, int? trialId, DateTime firstSeen)
        {
            if (!_requests.TryGetValue(requestId, out var info))
            {
                info = new RequestInfo
                {
                    taskId = taskId,
                    trialId = trialId,
                    headerWritten = false
                };
                _requests[requestId] = info;
            }

            if (!info.headerWritten)
            {
                AppendLines(
                    $"## Request {requestId}",
                    string.IsNullOrEmpty(taskId) ? null : $"- taskId: {taskId}",
                    trialId.HasValue ? $"- trialId: {trialId.Value}" : null,
                    $"- firstSeenUtc: {firstSeen:O}"
                );
                info.headerWritten = true;
            }
        }

        private void AppendLines(params string[] lines)
        {
            lock (_lock)
            {
                using (var sw = new StreamWriter(_tracePath, append: true, Encoding.UTF8))
                {
                    foreach (var line in lines)
                    {
                        if (line == null) continue;
                        sw.WriteLine(line);
                    }
                }
            }
        }

        private static string FormatOptions(FrameCaptureOptions options)
        {
            if (options == null) return "default";
            return $"w={options.width} h={options.height} fov={options.fov} fmt={options.format} q={options.quality} label={options.label}";
        }

        private static string FormatActionNames(ActionCommand[] actions)
        {
            if (actions == null || actions.Length == 0) return "none";
            var names = new List<string>(actions.Length);
            foreach (var a in actions)
            {
                if (!string.IsNullOrEmpty(a?.name)) names.Add(a.name);
            }
            return names.Count > 0 ? string.Join("|", names) : "unknown";
        }

        private static string TryToJson(object obj)
        {
            try
            {
                return JsonUtility.ToJson(obj);
            }
            catch
            {
                // 回退为 ToString，避免序列化异常导致日志中断
                return obj?.ToString();
            }
        }

        private static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            var trimmed = (maxChars <= 0 || value.Length <= maxChars) ? value : value.Substring(0, maxChars) + "...";
            return trimmed.Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static string HashString(string value)
        {
            try
            {
                using var sha = SHA256.Create();
                var bytes = Encoding.UTF8.GetBytes(value);
                var hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8 && i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
            catch
            {
                return "hash_error";
            }
        }

        private static string TryGetContextString(object context, params string[] keys)
        {
            if (context == null || keys == null) return null;
            var type = context.GetType();
            foreach (var key in keys)
            {
                // 兼容匿名对象/普通对象的字段与属性
                var prop = type.GetProperty(key);
                if (prop != null)
                {
                    var value = prop.GetValue(context);
                    if (value != null) return value.ToString();
                }
                var field = type.GetField(key);
                if (field != null)
                {
                    var value = field.GetValue(context);
                    if (value != null) return value.ToString();
                }
            }
            return null;
        }

        private static int? TryGetContextInt(object context, params string[] keys)
        {
            var str = TryGetContextString(context, keys);
            return int.TryParse(str, out var value) ? value : (int?)null;
        }
    }
}
