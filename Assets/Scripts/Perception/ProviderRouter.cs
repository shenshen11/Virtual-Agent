using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VRPerception.Perception
{
    /// <summary>
    /// Provider 路由器，负责选择最佳的 Provider 并处理回退
    /// </summary>
    public class ProviderRouter : MonoBehaviour
    {
        [Header("Routing Strategy")]
        [SerializeField] private RoutingStrategy strategy = RoutingStrategy.HealthyRoundRobin;
        [SerializeField] private string[] preferredProviders;
        [SerializeField] private string[] fallbackProviders;
        
        [Header("Retry & Fallback")]
        [SerializeField] private int maxRetries = 2;
        [SerializeField] private float retryDelaySeconds = 1f;
        [SerializeField] private bool enableFallback = true;
        
        [Header("Rate Limiting")]
        [SerializeField] private bool enableRateLimit = true;
        [SerializeField] private int requestsPerMinute = 60;
        
        private ProviderRegistry _registry;
        private readonly Dictionary<string, RateLimiter> _rateLimiters = new Dictionary<string, RateLimiter>();
        private int _roundRobinIndex = 0;
        
        public event Action<string, LLMRequest, LLMResponse> RequestCompleted;
        public event Action<string, LLMRequest, Exception> RequestFailed;
        
        private void Awake()
        {
            _registry = GetComponent<ProviderRegistry>();
            if (_registry == null)
            {
                Debug.LogError("[ProviderRouter] ProviderRegistry not found!");
                enabled = false;
            }
        }
        
        /// <summary>
        /// 路由请求到最佳 Provider
        /// </summary>
        public async Task<LLMResponse> RouteRequestAsync(LLMRequest request, CancellationToken cancellationToken = default)
        {
            var providers = SelectProviders(request);
            
            if (providers.Count == 0)
            {
                throw new InvalidOperationException("No available providers for request");
            }
            
            Exception lastException = null;
            
            foreach (var providerId in providers)
            {
                var provider = _registry.GetProvider(providerId);
                if (provider == null) continue;
                
                // 检查速率限制
                if (enableRateLimit && !CheckRateLimit(providerId))
                {
                    Debug.LogWarning($"[ProviderRouter] Rate limit exceeded for provider: {providerId}");
                    continue;
                }
                
                try
                {
                    var response = await ExecuteWithRetry(provider, request, cancellationToken);
                    response.providerId = providerId;
                    
                    RequestCompleted?.Invoke(providerId, request, response);
                    return response;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    Debug.LogWarning($"[ProviderRouter] Provider {providerId} failed: {ex.Message}");
                    RequestFailed?.Invoke(providerId, request, ex);
                    
                    // 如果不是最后一个provider，继续尝试下一个
                    if (providerId != providers.Last())
                    {
                        await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), cancellationToken);
                    }
                }
            }
            
            // 所有provider都失败了
            throw new AggregateException("All providers failed", lastException);
        }
        
        private List<string> SelectProviders(LLMRequest request)
        {
            var healthyProviders = _registry.GetHealthyProviders();
            var selectedProviders = new List<string>();
            
            switch (strategy)
            {
                case RoutingStrategy.Preferred:
                    // 优先使用指定的providers
                    if (preferredProviders != null)
                    {
                        selectedProviders.AddRange(preferredProviders.Where(healthyProviders.Contains));
                    }
                    break;
                    
                case RoutingStrategy.HealthyRoundRobin:
                    // 健康的providers轮询
                    if (healthyProviders.Count > 0)
                    {
                        var index = _roundRobinIndex % healthyProviders.Count;
                        selectedProviders.Add(healthyProviders[index]);
                        _roundRobinIndex = (_roundRobinIndex + 1) % healthyProviders.Count;
                    }
                    break;
                    
                case RoutingStrategy.Random:
                    // 随机选择健康的provider
                    if (healthyProviders.Count > 0)
                    {
                        var randomIndex = UnityEngine.Random.Range(0, healthyProviders.Count);
                        selectedProviders.Add(healthyProviders[randomIndex]);
                    }
                    break;
                    
                case RoutingStrategy.TaskSpecific:
                    // 根据任务类型选择provider
                    selectedProviders.AddRange(SelectByTaskType(request.taskId, healthyProviders));
                    break;
            }
            
            // 添加回退providers
            if (enableFallback && fallbackProviders != null)
            {
                foreach (var fallback in fallbackProviders)
                {
                    if (healthyProviders.Contains(fallback) && !selectedProviders.Contains(fallback))
                    {
                        selectedProviders.Add(fallback);
                    }
                }
            }
            
            // 如果没有选中任何provider，使用所有健康的providers
            if (selectedProviders.Count == 0)
            {
                selectedProviders.AddRange(healthyProviders);
            }
            
            return selectedProviders;
        }
        
        private List<string> SelectByTaskType(string taskId, List<string> healthyProviders)
        {
            // 根据任务类型选择最适合的provider
            // 这里可以根据具体需求实现更复杂的逻辑
            switch (taskId)
            {
                case "distance_compression":
                    // 距离压缩任务可能更适合视觉能力强的模型
                    return healthyProviders.Where(p => p.Contains("gpt-4") || p.Contains("claude")).ToList();
                    
                case "semantic_size_bias":
                    // 语义大小偏差任务可能需要更好的推理能力
                    return healthyProviders.Where(p => p.Contains("claude") || p.Contains("gpt-4")).ToList();
                    
                default:
                    return healthyProviders;
            }
        }
        
        private bool CheckRateLimit(string providerId)
        {
            if (!_rateLimiters.TryGetValue(providerId, out var limiter))
            {
                limiter = new RateLimiter(requestsPerMinute, TimeSpan.FromMinutes(1));
                _rateLimiters[providerId] = limiter;
            }
            
            return limiter.TryAcquire();
        }
        
        private async Task<LLMResponse> ExecuteWithRetry(ILLMProvider provider, LLMRequest request, CancellationToken cancellationToken)
        {
            Exception lastException = null;
            
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        var delay = TimeSpan.FromSeconds(retryDelaySeconds * Math.Pow(2, attempt - 1));
                        await Task.Delay(delay, cancellationToken);
                    }
                    
                    return await provider.InferAsync(request, cancellationToken);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    
                    // 某些错误不应该重试
                    if (IsNonRetryableError(ex))
                    {
                        throw;
                    }
                    
                    if (attempt == maxRetries)
                    {
                        throw;
                    }
                    
                    Debug.LogWarning($"[ProviderRouter] Attempt {attempt + 1} failed for {provider.ProviderName}: {ex.Message}");
                }
            }
            
            throw lastException;
        }
        
        private bool IsNonRetryableError(Exception ex)
        {
            // 定义不应该重试的错误类型
            var message = ex.Message.ToLower();
            return message.Contains("unauthorized") ||
                   message.Contains("forbidden") ||
                   message.Contains("invalid api key") ||
                   message.Contains("quota exceeded");
        }
    }
    
    /// <summary>
    /// 路由策略
    /// </summary>
    public enum RoutingStrategy
    {
        Preferred,          // 优先使用指定的providers
        HealthyRoundRobin,  // 健康providers轮询
        Random,             // 随机选择
        TaskSpecific        // 根据任务类型选择
    }
    
    /// <summary>
    /// 简单的速率限制器
    /// </summary>
    public class RateLimiter
    {
        private readonly int _maxRequests;
        private readonly TimeSpan _timeWindow;
        private readonly Queue<DateTime> _requestTimes = new Queue<DateTime>();
        private readonly object _lock = new object();
        
        public RateLimiter(int maxRequests, TimeSpan timeWindow)
        {
            _maxRequests = maxRequests;
            _timeWindow = timeWindow;
        }
        
        public bool TryAcquire()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var cutoff = now - _timeWindow;
                
                // 移除过期的请求记录
                while (_requestTimes.Count > 0 && _requestTimes.Peek() < cutoff)
                {
                    _requestTimes.Dequeue();
                }
                
                // 检查是否超过限制
                if (_requestTimes.Count >= _maxRequests)
                {
                    return false;
                }
                
                // 记录新请求
                _requestTimes.Enqueue(now);
                return true;
            }
        }
    }
}
