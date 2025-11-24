using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using VRPerception.Infra.EventBus;

namespace VRPerception.Perception
{
    [Serializable]
    public class RequestOptions
    {
        public Dictionary<string, string> headers;
        public int timeoutMs = 30000;
        public int maxRetries = 2;
        public float initialRetryDelaySeconds = 0.5f;
        public float maxRetryDelaySeconds = 8f;
        public float backoffFactor = 2f;
        public float jitterFraction = 0.2f;
        public string contentType = "application/json";
        public string path = ""; // relative or absolute
    }

    [Serializable]
    public class TransportResponse
    {
        public bool success;
        public long statusCode;
        public string text; // response body text (if any)
        public byte[] data; // raw bytes (if needed)
        public string errorCode;
        public string errorMessage;
        public long latencyMs;
        public string endpoint;
    }

    public interface IConnector
    {
        Task<TransportResponse> PostAsync(object payload, RequestOptions options, CancellationToken cancellationToken = default);
        string ConnectorType { get; }
        string BaseUrl { get; }
    }

    [Serializable]
    public class HttpHeader
    {
        public string key;
        public string value;
    }

    /// <summary>
    /// HTTP API 连接器：统一的 POST JSON 请求/响应、超时与重试、错误上报与状态广播
    /// </summary>
    public class HttpConnector : MonoBehaviour, IConnector
    {
        [Header("Endpoint")]
        [SerializeField] private string baseUrl = "http://127.0.0.1:8000";
        [SerializeField] private string apiKey;
        [Tooltip("可选：在每次请求中自动附带的默认请求头")]
        [SerializeField] private List<HttpHeader> defaultHeaders = new List<HttpHeader>();

        [Header("Defaults")]
        [SerializeField] private int defaultTimeoutMs = 30000;
        [SerializeField] private int defaultMaxRetries = 2;
        [SerializeField] private float defaultInitialRetryDelaySeconds = 0.5f;
        [SerializeField] private float defaultMaxRetryDelaySeconds = 8f;
        [SerializeField] private float defaultBackoffFactor = 2f;
        [SerializeField, Range(0f, 1f)] private float defaultJitterFraction = 0.2f;

        [Header("Event Bus")]
        [SerializeField] private EventBusManager eventBus;

        public string ConnectorType => "http";
        public string BaseUrl => baseUrl;

        private void Awake()
        {
            if (eventBus == null) eventBus = EventBusManager.Instance;
        }

        public async Task<TransportResponse> PostAsync(object payload, RequestOptions options, CancellationToken cancellationToken = default)
        {
            options ??= new RequestOptions();
            if (options.timeoutMs <= 0) options.timeoutMs = defaultTimeoutMs;
            if (options.maxRetries < 0) options.maxRetries = defaultMaxRetries;
            if (options.initialRetryDelaySeconds <= 0) options.initialRetryDelaySeconds = defaultInitialRetryDelaySeconds;
            if (options.maxRetryDelaySeconds <= 0) options.maxRetryDelaySeconds = defaultMaxRetryDelaySeconds;
            if (options.backoffFactor <= 0) options.backoffFactor = defaultBackoffFactor;
            if (options.jitterFraction < 0) options.jitterFraction = defaultJitterFraction;
            if (string.IsNullOrEmpty(options.contentType)) options.contentType = "application/json";

            var url = BuildUrl(options.path);
            var unifiedHeaders = BuildHeaders(options.headers);

            PublishConnectionState(ConnectionState.Connecting, url, "POST");
            var startTime = DateTime.UtcNow;

            try
            {
                for (int attempt = 0; attempt <= options.maxRetries; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
                    byte[] bodyBytes = BuildRequestBody(payload, options.contentType);
                    request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.timeout = Mathf.Max(1, Mathf.CeilToInt(options.timeoutMs / 1000f));

                    // headers
                    request.SetRequestHeader("Content-Type", options.contentType);
                    foreach (var kv in unifiedHeaders)
                    {
                        if (string.IsNullOrEmpty(kv.Key)) continue;
                        request.SetRequestHeader(kv.Key, kv.Value ?? "");
                    }

                    var op = request.SendWebRequest();
                    while (!op.isDone && !cancellationToken.IsCancellationRequested)
                    {
                        await Task.Yield();
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        request.Abort();
                        throw new OperationCanceledException("HTTP request cancelled");
                    }

                    var latency = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;

                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        PublishConnectionState(ConnectionState.Connected, url, "OK");
                        var resp = new TransportResponse
                        {
                            success = true,
                            statusCode = request.responseCode,
                            text = request.downloadHandler?.text,
                            data = request.downloadHandler?.data,
                            latencyMs = latency,
                            endpoint = url
                        };
                        // metric
                        eventBus?.PublishMetric("http_request_latency", "latency", latency, "ms",
                            new { endpoint = url, status = request.responseCode });
                        return resp;
                    }

                    // Failure
                    var shouldRetry = ShouldRetry(request);
                    if (attempt < options.maxRetries && shouldRetry)
                    {
                        var delay = ComputeBackoffDelay(attempt + 1, options.initialRetryDelaySeconds, options.backoffFactor, options.maxRetryDelaySeconds, options.jitterFraction);
                        PublishConnectionState(ConnectionState.Reconnecting, url, $"retry_in={delay:0.00}s status={request.responseCode}");
                        await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                        continue;
                    }

                    // No more retry
                    PublishConnectionState(ConnectionState.Error, url, $"{request.responseCode} {request.error}");
                    var errorResp = new TransportResponse
                    {
                        success = false,
                        statusCode = request.responseCode,
                        text = request.downloadHandler?.text,
                        errorCode = request.responseCode.ToString(),
                        errorMessage = request.error,
                        latencyMs = latency,
                        endpoint = url
                    };
                    // error event
                    eventBus?.PublishError("HttpConnector", ErrorSeverity.Error, errorResp.errorCode, errorResp.errorMessage,
                        new { endpoint = url, status = request.responseCode, attempt = attempt + 1 });
                    return errorResp;
                }

                // should not reach here
                return new TransportResponse
                {
                    success = false,
                    statusCode = 0,
                    errorCode = "UNKNOWN",
                    errorMessage = "Unknown error",
                    latencyMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                    endpoint = url
                };
            }
            catch (OperationCanceledException)
            {
                PublishConnectionState(ConnectionState.Error, url, "CANCELLED");
                return new TransportResponse
                {
                    success = false,
                    statusCode = 0,
                    errorCode = "CANCELLED",
                    errorMessage = "Request cancelled",
                    latencyMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                    endpoint = url
                };
            }
            catch (Exception ex)
            {
                PublishConnectionState(ConnectionState.Error, url, $"EXCEPTION {ex.GetType().Name}");
                eventBus?.PublishError("HttpConnector", ErrorSeverity.Error, "EXCEPTION", ex.Message,
                    new { endpoint = url, exception = ex.GetType().Name });
                return new TransportResponse
                {
                    success = false,
                    statusCode = 0,
                    errorCode = "EXCEPTION",
                    errorMessage = ex.Message,
                    latencyMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                    endpoint = url
                };
            }
        }

        private string BuildUrl(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return baseUrl?.TrimEnd('/') ?? "";
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return path;
            return $"{baseUrl?.TrimEnd('/')}/{path.TrimStart('/')}";
        }

        private Dictionary<string, string> BuildHeaders(Dictionary<string, string> overrides)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // defaults from inspector
            if (defaultHeaders != null)
            {
                foreach (var h in defaultHeaders)
                {
                    if (!string.IsNullOrEmpty(h?.key))
                        headers[h.key] = h.value ?? "";
                }
            }
            // api key
            if (!string.IsNullOrEmpty(apiKey))
            {
                if (!headers.ContainsKey("Authorization"))
                    headers["Authorization"] = $"Bearer {apiKey}";
            }
            // overrides
            if (overrides != null)
            {
                foreach (var kv in overrides)
                {
                    headers[kv.Key] = kv.Value;
                }
            }
            return headers;
        }

        private static byte[] BuildRequestBody(object payload, string contentType)
        {
            if (payload == null)
                return Array.Empty<byte>();

            if (payload is string s)
            {
                return Encoding.UTF8.GetBytes(s);
            }

            if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                // Use Unity JsonUtility; payload must be [Serializable] or POCO
                var json = JsonUtility.ToJson(payload);
                return Encoding.UTF8.GetBytes(json);
            }

            // default to JSON
            var fallback = JsonUtility.ToJson(payload);
            return Encoding.UTF8.GetBytes(fallback);
        }

        private static bool ShouldRetry(UnityWebRequest req)
        {
            // Retry on 408, 429, 5xx, network errors
            if (req.result == UnityWebRequest.Result.ConnectionError || req.result == UnityWebRequest.Result.ProtocolError)
            {
                var code = (int)req.responseCode;
                if (code == 408 || code == 429) return true;
                if (code >= 500 && code <= 599) return true;
            }
            return false;
        }

        private static float ComputeBackoffDelay(int attempt, float initial, float factor, float max, float jitter)
        {
            double delay = initial * Math.Pow(factor, Math.Max(0, attempt - 1));
            delay = Math.Min(delay, max);
            var rnd = UnityEngine.Random.Range(-jitter, jitter);
            delay = Math.Max(0.05, delay * (1.0 + rnd));
            return (float)delay;
        }

        private void PublishConnectionState(ConnectionState state, string endpoint, string reason)
        {
            if (eventBus?.ConnectionState == null) return;
            var data = new ConnectionStateEventData
            {
                connectionId = $"http:{GetInstanceID()}",
                providerId = null,
                previousState = ConnectionState.Disconnected, // not tracked finely for HTTP; set for completeness
                currentState = state,
                timestamp = DateTime.UtcNow,
                reason = reason,
                endpoint = endpoint
            };
            try { eventBus.ConnectionState.Publish(data); } catch { }
        }
    }
}