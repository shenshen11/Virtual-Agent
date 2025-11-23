using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VRPerception.Perception
{
    /// <summary>
    /// Provider 注册表，管理所有可用的 LLM Provider
    /// </summary>
    public class ProviderRegistry : MonoBehaviour
    {
        [Header("Provider Configuration")]
        [SerializeField] private ProviderConfig[] providerConfigs;
        
        [Header("Health Check")]
        [SerializeField] private float healthCheckIntervalSeconds = 60f;
        [SerializeField] private int healthCheckTimeoutMs = 5000;
        
        private readonly Dictionary<string, ILLMProvider> _providers = new Dictionary<string, ILLMProvider>();
        private readonly Dictionary<string, ProviderHealth> _healthStatus = new Dictionary<string, ProviderHealth>();
        private CancellationTokenSource _healthCheckCts;
        
        public event Action<string, bool> ProviderHealthChanged;
        
        public IReadOnlyDictionary<string, ILLMProvider> Providers => _providers;
        public IReadOnlyDictionary<string, ProviderHealth> HealthStatus => _healthStatus;
        
        private void Awake()
        {
            InitializeProviders();
        }
        
        private void Start()
        {
            StartHealthChecking();
            // 立即进行一次健康检查，避免冷启动期间 Router 无可用 Provider
            try
            {
                _ = PerformHealthChecksAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ProviderRegistry] Initial health check error: {ex.Message}");
            }
        }
        
        private void OnDestroy()
        {
            StopHealthChecking();
        }
        
        /// <summary>
        /// 注册 Provider
        /// </summary>
        public void RegisterProvider(string id, ILLMProvider provider)
        {
            if (string.IsNullOrEmpty(id) || provider == null)
            {
                Debug.LogError($"[ProviderRegistry] Invalid provider registration: id={id}, provider={provider}");
                return;
            }
            
            _providers[id] = provider;
            _healthStatus[id] = new ProviderHealth
            {
                IsHealthy = false,
                LastCheckTime = DateTime.UtcNow,
                ErrorMessage = "Not checked yet"
            };
            
            Debug.Log($"[ProviderRegistry] Registered provider: {id} ({provider.ProviderType})");
        }
        
        /// <summary>
        /// 注销 Provider
        /// </summary>
        public void UnregisterProvider(string id)
        {
            if (_providers.Remove(id))
            {
                _healthStatus.Remove(id);
                Debug.Log($"[ProviderRegistry] Unregistered provider: {id}");
            }
        }
        
        /// <summary>
        /// 获取 Provider
        /// </summary>
        public ILLMProvider GetProvider(string id)
        {
            return _providers.TryGetValue(id, out var provider) ? provider : null;
        }
        
        /// <summary>
        /// 获取健康的 Provider 列表
        /// </summary>
        public List<string> GetHealthyProviders()
        {
            return _healthStatus
                .Where(kvp => kvp.Value.IsHealthy)
                .Select(kvp => kvp.Key)
                .ToList();
        }
        
        /// <summary>
        /// 获取指定类型的健康 Provider 列表
        /// </summary>
        public List<string> GetHealthyProvidersByType(string providerType)
        {
            return _providers
                .Where(kvp => kvp.Value.ProviderType == providerType && 
                             _healthStatus.TryGetValue(kvp.Key, out var health) && 
                             health.IsHealthy)
                .Select(kvp => kvp.Key)
                .ToList();
        }
        
        private void InitializeProviders()
        {
            if (providerConfigs == null) return;
            
            foreach (var config in providerConfigs)
            {
                if (!config.enabled) continue;
                
                try
                {
                    var provider = CreateProvider(config);
                    if (provider != null)
                    {
                        RegisterProvider(config.id, provider);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ProviderRegistry] Failed to create provider {config.id}: {ex.Message}");
                }
            }
        }
        
        private ILLMProvider CreateProvider(ProviderConfig config)
        {
            var normalized = NormalizeType(config.type);
            switch (normalized)
            {
                case "cloud_openai":
                    return new OpenAIProvider(config);
                case "cloud_anthropic":
                    return new AnthropicProvider(config);
                case "local_ollama":
                    return new OllamaProvider(config);
                case "local_vllm":
                    return new VLLMProvider(config);
                case "custom_http":
                    return new CustomHttpProvider(config);
                default:
                    Debug.LogWarning($"[ProviderRegistry] Unknown provider type: {config.type} (normalized: {normalized})");
                    return null;
            }
        }

        private static string NormalizeType(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return t;
            t = t.Trim().ToLowerInvariant();
            switch (t)
            {
                // vLLM 常见别名/错拼
                case "loacl_vllm":
                case "local-vllm":
                case "vllm":
                case "vlm_local":
                    return "local_vllm";
                // Ollama
                case "ollama":
                case "local-ollama":
                    return "local_ollama";
                // OpenAI
                case "openai":
                case "gpt":
                case "cloud-openai":
                    return "cloud_openai";
                // Anthropic
                case "anthropic":
                case "claude":
                case "cloud-anthropic":
                    return "cloud_anthropic";
                // 自定义 HTTP
                case "http":
                case "custom-http":
                    return "custom_http";
                default:
                    return t;
            }
        }
        
        private void StartHealthChecking()
        {
            _healthCheckCts = new CancellationTokenSource();
            _ = HealthCheckLoopAsync(_healthCheckCts.Token);
        }
        
        private void StopHealthChecking()
        {
            _healthCheckCts?.Cancel();
            _healthCheckCts?.Dispose();
            _healthCheckCts = null;
        }
        
        private async Task HealthCheckLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(healthCheckIntervalSeconds), cancellationToken);
                    await PerformHealthChecksAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ProviderRegistry] Health check loop error: {ex.Message}");
                }
            }
        }
        
        private async Task PerformHealthChecksAsync(CancellationToken cancellationToken)
        {
            var tasks = _providers.Select(async kvp =>
            {
                var id = kvp.Key;
                var provider = kvp.Value;
                var health = _healthStatus[id];
                
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(healthCheckTimeoutMs);
                    
                    var isHealthy = await provider.HealthCheckAsync(timeoutCts.Token);
                    
                    var wasHealthy = health.IsHealthy;
                    health.IsHealthy = isHealthy;
                    health.LastCheckTime = DateTime.UtcNow;
                    health.ErrorMessage = isHealthy ? null : "Health check failed";
                    
                    if (wasHealthy != isHealthy)
                    {
                        ProviderHealthChanged?.Invoke(id, isHealthy);
                        Debug.Log($"[ProviderRegistry] Provider {id} health changed: {wasHealthy} -> {isHealthy}");
                    }
                }
                catch (Exception ex)
                {
                    var wasHealthy = health.IsHealthy;
                    health.IsHealthy = false;
                    health.LastCheckTime = DateTime.UtcNow;
                    health.ErrorMessage = ex.Message;
                    
                    if (wasHealthy)
                    {
                        ProviderHealthChanged?.Invoke(id, false);
                        Debug.LogWarning($"[ProviderRegistry] Provider {id} health check failed: {ex.Message}");
                    }
                }
            });
            
            await Task.WhenAll(tasks);
        }
    }
    
    /// <summary>
    /// Provider 配置
    /// </summary>
    [Serializable]
    public class ProviderConfig
    {
        public string id;
        public string type;
        public string name;
        public bool enabled = true;
        public string endpoint;
        public string apiKey;
        public string model;
        public Dictionary<string, object> parameters = new Dictionary<string, object>();
    }
    
    /// <summary>
    /// Provider 健康状态
    /// </summary>
    public class ProviderHealth
    {
        public bool IsHealthy { get; set; }
        public DateTime LastCheckTime { get; set; }
        public string ErrorMessage { get; set; }
        public long LastLatencyMs { get; set; }
    }
}
