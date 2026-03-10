using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VRPerception.Infra.EventBus;

namespace VRPerception.Perception
{
    /// <summary>
    /// 感知系统，桥接 Stimulus 与 LLM Provider，负责抓帧、推理和动作规划的编排
    /// </summary>
    public class PerceptionSystem : MonoBehaviour
    {
        [Header("Components")]
        [SerializeField] private StimulusCapture stimulusCapture;
        [SerializeField] private ProviderRegistry providerRegistry;
        [SerializeField] private ProviderRouter providerRouter;
        
        [Header("Settings")]
        [SerializeField] private float maxFPS = 10f;
        [SerializeField] private int maxConcurrentRequests = 3;
        [SerializeField] private bool enableThrottling = true;
        [SerializeField] private int requestTimeoutMs = 30000;
        
        [Header("Event Bus")]
        [SerializeField] private EventBusManager eventBus;
        
        private readonly Dictionary<string, PerceptionRequest> _activeRequests = new Dictionary<string, PerceptionRequest>();
        private readonly Queue<PerceptionRequest> _requestQueue = new Queue<PerceptionRequest>();
        private SemaphoreSlim _concurrencySemaphore;
        
        private float _lastCaptureTime = 0f;
        private int _requestCounter = 0;
        
        public bool IsProcessing => _activeRequests.Count > 0;
        public int QueuedRequests => _requestQueue.Count;
        public int ActiveRequests => _activeRequests.Count;
        
        private void Awake()
        {
            if (eventBus == null)
                eventBus = EventBusManager.Instance;
            
            if (stimulusCapture == null)
                stimulusCapture = GetComponent<StimulusCapture>();
            
            if (providerRegistry == null)
                providerRegistry = GetComponent<ProviderRegistry>();
            
            if (providerRouter == null)
                providerRouter = GetComponent<ProviderRouter>();
        }
        
        private void Start()
        {
            SubscribeToEvents();
            _concurrencySemaphore = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
        }
        
        private void OnDestroy()
        {
            UnsubscribeFromEvents();
            CancelAllRequests();
            _concurrencySemaphore?.Dispose();
        }
        
        private void SubscribeToEvents()
        {
            if (eventBus?.FrameRequested != null)
            {
                eventBus.FrameRequested.Subscribe(OnFrameRequested);
            }
        }
        
        private void UnsubscribeFromEvents()
        {
            if (eventBus?.FrameRequested != null)
            {
                eventBus.FrameRequested.Unsubscribe(OnFrameRequested);
            }
        }
        
        /// <summary>
        /// 处理帧请求事件
        /// </summary>
        private void OnFrameRequested(FrameRequestedEventData eventData)
        {
            var request = new PerceptionRequest
            {
                RequestId = eventData.requestId,
                TaskId = eventData.taskId,
                TrialId = eventData.trialId,
                Requester = eventData.requester,
                CaptureOptions = eventData.options,
                Timestamp = eventData.timestamp
            };
            
            EnqueueRequest(request);
        }
        
        /// <summary>
        /// 直接请求推理（不通过事件总线）
        /// </summary>
        public async Task<LLMResponse> RequestInferenceAsync(string taskId, int trialId, string systemPrompt, string taskPrompt, ToolSpec[] tools = null, CancellationToken cancellationToken = default)
        {
            // 使用默认捕获参数调用重载
            var captureOptions = new FrameCaptureOptions();
            return await RequestInferenceAsync(taskId, trialId, systemPrompt, taskPrompt, tools, captureOptions, cancellationToken);
        }

        /// <summary>
        /// 直接请求推理（带自定义捕获参数，如 FOV/分辨率）
        /// </summary>
        public async Task<LLMResponse> RequestInferenceAsync(string taskId, int trialId, string systemPrompt, string taskPrompt, ToolSpec[] tools, FrameCaptureOptions captureOptions, CancellationToken cancellationToken = default)
        {
            var requestId = GenerateRequestId();

            var request = new PerceptionRequest
            {
                RequestId = requestId,
                TaskId = taskId,
                TrialId = trialId,
                Requester = "Direct",
                CaptureOptions = captureOptions ?? new FrameCaptureOptions(),
                Timestamp = DateTime.UtcNow,
                SystemPrompt = systemPrompt,
                TaskPrompt = taskPrompt,
                Tools = tools
            };

            return await ProcessRequestAsync(request, cancellationToken);
        }
        
        /// <summary>
        /// 将请求加入队列
        /// </summary>
        private void EnqueueRequest(PerceptionRequest request)
        {
            // 检查FPS限制
            if (enableThrottling && maxFPS > 0)
            {
                var timeSinceLastCapture = Time.time - _lastCaptureTime;
                var minInterval = 1f / maxFPS;
                
                if (timeSinceLastCapture < minInterval)
                {
                    Debug.LogWarning($"[PerceptionSystem] Request throttled: {request.RequestId}");
                    return;
                }
            }
            
            _requestQueue.Enqueue(request);
            _ = ProcessQueueAsync();
        }
        
        /// <summary>
        /// 处理请求队列
        /// </summary>
        private async Task ProcessQueueAsync()
        {
            while (_requestQueue.Count > 0)
            {
                if (!await _concurrencySemaphore.WaitAsync(100))
                {
                    break; // 并发限制，稍后再试
                }
                
                var request = _requestQueue.Dequeue();
                _ = ProcessRequestWithSemaphore(request);
            }
        }
        
        /// <summary>
        /// 带信号量的请求处理
        /// </summary>
        private async Task ProcessRequestWithSemaphore(PerceptionRequest request)
        {
            try
            {
                using var cts = new CancellationTokenSource(requestTimeoutMs);
                await ProcessRequestAsync(request, cts.Token);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PerceptionSystem] Request failed: {request.RequestId}, Error: {ex.Message}");
                PublishError(request, ex);
            }
            finally
            {
                _concurrencySemaphore.Release();
            }
        }
        
        /// <summary>
        /// 处理单个请求
        /// </summary>
        private async Task<LLMResponse> ProcessRequestAsync(PerceptionRequest request, CancellationToken cancellationToken)
        {
            _activeRequests[request.RequestId] = request;
            
            try
            {
                // 1. 抓帧
                var frames = await CaptureFramesAsync(request, cancellationToken);
                if (frames == null || frames.Count == 0)
                {
                    throw new InvalidOperationException("Frame capture failed");
                }
                
                // 2. 构建LLM请求
                var llmRequest = BuildLLMRequest(request, frames);
                
                // 3. 调用LLM Provider
                var response = await providerRouter.RouteRequestAsync(llmRequest, cancellationToken);
                
                // 4. 发布结果事件
                PublishResponse(request, response);
                
                return response;
            }
            finally
            {
                _activeRequests.Remove(request.RequestId);
            }
        }
        
        /// <summary>
        /// 抓帧
        /// </summary>
        private async Task<List<FrameCapturedEventData>> CaptureFramesAsync(PerceptionRequest request, CancellationToken cancellationToken)
        {
            if (stimulusCapture == null)
            {
                throw new InvalidOperationException("StimulusCapture not available");
            }

            var options = request.CaptureOptions ?? new FrameCaptureOptions();
            var frameCount = Mathf.Max(1, options.frameCount);
            var settleMs = Mathf.Max(0, options.scanSettleMs);
            var perFrameTimeoutMs = Math.Max(5000, 5000 + settleMs);
            var totalTimeoutMs = Math.Max(perFrameTimeoutMs, perFrameTimeoutMs * frameCount);
            var captured = new List<FrameCapturedEventData>(frameCount);
            var camera = stimulusCapture.HeadCamera;
            var cameraTransform = camera != null ? camera.transform : null;
            // In XR, head tracking continuously overwrites the HMD camera pose.
            // Apply scan jitter on parent transform (camera offset/pivot) to keep offsets effective.
            var scanRoot = cameraTransform != null
                ? (cameraTransform.parent != null ? cameraTransform.parent : cameraTransform)
                : null;
            var originalScanLocalRotation = scanRoot != null ? scanRoot.localRotation : Quaternion.identity;
            var rng = CreateScanRandom(request, options);

            using var totalTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            totalTimeoutCts.CancelAfter(totalTimeoutMs);

            try
            {
                for (int i = 0; i < frameCount; i++)
                {
                    if (scanRoot != null && frameCount > 1)
                    {
                        if (i == 0)
                        {
                            // Keep the first frame as the forward baseline view.
                            scanRoot.localRotation = originalScanLocalRotation;
                        }
                        else
                        {
                            var yaw = SampleRange(rng, Mathf.Max(0f, options.scanYawRangeDeg));
                            var pitch = SampleRange(rng, Mathf.Max(0f, options.scanPitchRangeDeg));
                            scanRoot.localRotation = originalScanLocalRotation * Quaternion.Euler(pitch, yaw, 0f);
                        }

                        // Let transform updates settle for at least one frame before capture.
                        await Task.Yield();

                        if (settleMs > 0)
                        {
                            await Task.Delay(settleMs, totalTimeoutCts.Token);
                        }
                    }

                    _lastCaptureTime = Time.time;
                    var frameData = await CaptureSingleFrameAsync(request, totalTimeoutCts.Token, perFrameTimeoutMs);
                    captured.Add(frameData);
                }
            }
            finally
            {
                if (scanRoot != null && frameCount > 1)
                {
                    scanRoot.localRotation = originalScanLocalRotation;
                }
            }

            return captured;
        }

        private async Task<FrameCapturedEventData> CaptureSingleFrameAsync(PerceptionRequest request, CancellationToken cancellationToken, int timeoutMs)
        {
            var tcs = new TaskCompletionSource<FrameCapturedEventData>();

            void OnFrameCaptured(FrameCapturedEventData data)
            {
                if (data.requestId == request.RequestId)
                {
                    try { eventBus.FrameCaptured.Unsubscribe(OnFrameCaptured); } catch { }
                    tcs.TrySetResult(data);
                }
            }

            eventBus.FrameCaptured.Subscribe(OnFrameCaptured);
            stimulusCapture.CaptureFrame(request.RequestId, request.TaskId, request.TrialId, request.CaptureOptions);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = Task.Delay(timeoutMs, timeoutCts.Token);
            var completed = await Task.WhenAny(tcs.Task, delayTask);
            if (completed == tcs.Task)
            {
                timeoutCts.Cancel();
                return tcs.Task.Result;
            }

            try { eventBus.FrameCaptured.Unsubscribe(OnFrameCaptured); } catch { }
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            throw new TimeoutException($"Frame capture timeout: {request.RequestId}");
        }

        private static float SampleRange(System.Random rng, float halfRange)
        {
            if (halfRange <= 0f) return 0f;
            return (float)(rng.NextDouble() * 2.0 - 1.0) * halfRange;
        }

        private static System.Random CreateScanRandom(PerceptionRequest request, FrameCaptureOptions options)
        {
            if (options.scanSeed != 0)
            {
                return new System.Random(options.scanSeed);
            }

            unchecked
            {
                int hash = 17;
                hash = hash * 31 + request.TrialId;
                hash = hash * 31 + (request.TaskId?.GetHashCode() ?? 0);
                hash = hash * 31 + (request.RequestId?.GetHashCode() ?? 0);
                return new System.Random(hash);
            }
        }
        
        /// <summary>
        /// 构建LLM请求
        /// </summary>
        private LLMRequest BuildLLMRequest(PerceptionRequest request, List<FrameCapturedEventData> frames)
        {
            var first = frames[0];
            var images = new string[frames.Count];
            var metadataList = new FrameMetadata[frames.Count];
            for (int i = 0; i < frames.Count; i++)
            {
                images[i] = frames[i]?.imageBase64;
                metadataList[i] = frames[i]?.metadata;
            }

            return new LLMRequest
            {
                requestId = request.RequestId,
                taskId = request.TaskId,
                trialId = request.TrialId,
                systemPrompt = request.SystemPrompt ?? GetDefaultSystemPrompt(request.TaskId),
                taskPrompt = request.TaskPrompt ?? GetDefaultTaskPrompt(request.TaskId, request.TrialId),
                imageBase64 = first?.imageBase64,
                imagesBase64 = images,
                metadata = first?.metadata,
                metadataList = metadataList,
                tools = request.Tools ?? GetDefaultTools(request.TaskId),
                timeoutMs = requestTimeoutMs
            };
        }
        
        /// <summary>
        /// 发布响应事件
        /// </summary>
        private void PublishResponse(PerceptionRequest request, LLMResponse response)
        {
            if (response.type == "inference")
            {
                var inferenceData = new InferenceReceivedEventData
                {
                    requestId = request.RequestId,
                    taskId = request.TaskId,
                    trialId = request.TrialId,
                    timestamp = DateTime.UtcNow,
                    response = response,
                    providerId = response.providerId
                };
                
                eventBus.InferenceReceived?.Publish(inferenceData);
            }
            else if (response.type == "action_plan")
            {
                var actionPlanData = new ActionPlanReceivedEventData
                {
                    requestId = request.RequestId,
                    taskId = request.TaskId,
                    trialId = request.TrialId,
                    timestamp = DateTime.UtcNow,
                    actions = response.actions,
                    providerId = response.providerId
                };
                
                eventBus.ActionPlanReceived?.Publish(actionPlanData);
            }
        }
        
        /// <summary>
        /// 发布错误事件
        /// </summary>
        private void PublishError(PerceptionRequest request, Exception ex)
        {
            eventBus?.PublishError(
                "PerceptionSystem",
                ErrorSeverity.Error,
                ex.GetType().Name,
                ex.Message,
                new { RequestId = request.RequestId, TaskId = request.TaskId, TrialId = request.TrialId }
            );
        }
        
        /// <summary>
        /// 取消所有请求
        /// </summary>
        private void CancelAllRequests()
        {
            _requestQueue.Clear();
            _activeRequests.Clear();
        }
        
        /// <summary>
        /// 生成请求ID
        /// </summary>
        private string GenerateRequestId()
        {
            return $"req_{++_requestCounter}_{DateTime.UtcNow.Ticks}";
        }
        
        /// <summary>
        /// 获取默认系统提示
        /// </summary>
        private string GetDefaultSystemPrompt(string taskId)
        {
            // 统一使用 PromptTemplates 的系统提示
            return PromptTemplates.GetSystemPrompt(taskId);
        }
        
        /// <summary>
        /// 获取默认任务提示
        /// </summary>
        private string GetDefaultTaskPrompt(string taskId, int trialId)
        {
            // 当上层未传入任务提示词时，使用模板化提示（占位参数）
            return taskId switch
            {
                "distance_compression" => PromptTemplates.BuildDistanceCompressionPrompt("unknown", 0f, "open_field", trialId),
                "semantic_size_bias" => PromptTemplates.BuildSemanticSizeBiasPrompt("A", "B", "equal", "none"),
                "relative_depth_order" => PromptTemplates.BuildRelativeDepthOrderPrompt("none", "equal", false, 60f),
                "change_detection" => PromptTemplates.BuildChangeDetectionPrompt("none", 60f, trialId),
                _ => "Analyze the image and respond with appropriate JSON."
            };
        }
        
        /// <summary>
        /// 获取默认工具
        /// </summary>
        private ToolSpec[] GetDefaultTools(string taskId)
        {
            return null;
        }
    }
    
    /// <summary>
    /// 感知请求数据结构
    /// </summary>
    public class PerceptionRequest
    {
        public string RequestId { get; set; }
        public string TaskId { get; set; }
        public int TrialId { get; set; }
        public string Requester { get; set; }
        public FrameCaptureOptions CaptureOptions { get; set; }
        public DateTime Timestamp { get; set; }
        public string SystemPrompt { get; set; }
        public string TaskPrompt { get; set; }
        public ToolSpec[] Tools { get; set; }
    }
}
