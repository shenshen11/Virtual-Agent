using System;
using System.Collections;
using UnityEngine;
using VRPerception.Infra.EventBus;
using VRPerception.Tasks;

namespace VRPerception.Perception
{
    /// <summary>
    /// 刺激捕获组件，负责从头部相机抓帧并编码
    /// </summary>
    public class StimulusCapture : MonoBehaviour
    {
        [Header("Camera Settings")]
        [SerializeField] private Camera headCamera;
        [SerializeField] private bool autoFindHeadCamera = true;
        
        [Header("Capture Settings")]
        [SerializeField] private int defaultWidth = 1280;
        [SerializeField] private int defaultHeight = 720;
        [SerializeField] private float defaultFOV = 60f;
        [SerializeField] private ImageFormat defaultFormat = ImageFormat.Jpeg;
        [SerializeField] private int defaultJpegQuality = 75;
        
        [Header("Performance")]
        [SerializeField] private bool useAsyncEncoding = true;
        [SerializeField] private int maxConcurrentCaptures = 3;
        
        [Header("Event Bus")]
        [SerializeField] private EventBusManager eventBus;
        
        private int _activeCaptureCount = 0;
        private ExperimentSceneManager _sceneManager;
        
        public Camera HeadCamera => headCamera;
        public bool IsCapturing => _activeCaptureCount > 0;
        
        private void Awake()
        {
            if (eventBus == null)
                eventBus = EventBusManager.Instance;
            
            if (autoFindHeadCamera && headCamera == null)
            {
                FindHeadCamera();
            }

            TryBindSceneManager();
        }
        
        private void Start()
        {
            ValidateSetup();
        }
        
        /// <summary>
        /// 自动查找头部相机
        /// </summary>
        private void FindHeadCamera()
        {
            // 尝试查找主相机
            if (Camera.main != null)
            {
                headCamera = Camera.main;
                Debug.Log($"[StimulusCapture] Using main camera: {headCamera.name}");
                return;
            }
            
            // 查找标记为 "MainCamera" 的相机
            var mainCameraGO = GameObject.FindWithTag("MainCamera");
            if (mainCameraGO != null)
            {
                headCamera = mainCameraGO.GetComponent<Camera>();
                if (headCamera != null)
                {
                    Debug.Log($"[StimulusCapture] Found camera with MainCamera tag: {headCamera.name}");
                    return;
                }
            }
            
            // 查找第一个激活的相机
            var cameras = FindObjectsOfType<Camera>();
            foreach (var cam in cameras)
            {
                if (cam.enabled && cam.gameObject.activeInHierarchy)
                {
                    headCamera = cam;
                    Debug.Log($"[StimulusCapture] Using first active camera: {headCamera.name}");
                    return;
                }
            }
            
            Debug.LogError("[StimulusCapture] No suitable camera found!");
        }
        
        /// <summary>
        /// 验证设置
        /// </summary>
        private void ValidateSetup()
        {
            if (headCamera == null)
            {
                Debug.LogError("[StimulusCapture] Head camera not assigned!");
                enabled = false;
                return;
            }
            
            if (eventBus == null)
            {
                Debug.LogError("[StimulusCapture] EventBus not available!");
                enabled = false;
                return;
            }
        }
        
        /// <summary>
        /// 捕获帧
        /// </summary>
        public void CaptureFrame(string requestId, string taskId, int trialId, FrameCaptureOptions options = null)
        {
            if (!enabled || headCamera == null)
            {
                PublishCaptureError(requestId, taskId, trialId, "StimulusCapture not ready");
                return;
            }
            
            if (_activeCaptureCount >= maxConcurrentCaptures)
            {
                PublishCaptureError(requestId, taskId, trialId, "Too many concurrent captures");
                return;
            }
            
            options ??= CreateDefaultOptions();
            
            StartCoroutine(CaptureFrameCoroutine(requestId, taskId, trialId, options));
        }
        
        /// <summary>
        /// 捕获帧协程
        /// </summary>
        private IEnumerator CaptureFrameCoroutine(string requestId, string taskId, int trialId, FrameCaptureOptions options)
        {
            _activeCaptureCount++;
            
            try
            {
                var startTime = DateTime.UtcNow;
                
                // 保存原始相机设置
                var originalFOV = headCamera.fieldOfView;
                var originalTargetTexture = headCamera.targetTexture;
                
                // 应用捕获设置
                if (options.fov > 0)
                {
                    headCamera.fieldOfView = options.fov;
                }
                
                // 创建渲染纹理
                var renderTexture = new RenderTexture(options.width, options.height, 24, RenderTextureFormat.ARGB32);
                headCamera.targetTexture = renderTexture;
                
                // 渲染
                headCamera.Render();
                
                // 读取像素
                RenderTexture.active = renderTexture;
                var texture2D = new Texture2D(options.width, options.height, TextureFormat.RGB24, false);
                texture2D.ReadPixels(new Rect(0, 0, options.width, options.height), 0, 0);
                texture2D.Apply();
                RenderTexture.active = null;
                
                // 恢复相机设置
                headCamera.fieldOfView = originalFOV;
                headCamera.targetTexture = originalTargetTexture;
                
                // 编码图像
                byte[] imageBytes;
                if (options.format == "png")
                {
                    imageBytes = texture2D.EncodeToPNG();
                }
                else
                {
                    imageBytes = texture2D.EncodeToJPG(options.quality);
                }
                
                var imageBase64 = Convert.ToBase64String(imageBytes);
                
                // 收集元数据
                var metadata = CollectMetadata(taskId, trialId, options);
                
                // 清理资源
                DestroyImmediate(texture2D);
                renderTexture.Release();
                DestroyImmediate(renderTexture);
                
                // 发布成功事件
                var captureData = new FrameCapturedEventData
                {
                    requestId = requestId,
                    taskId = taskId,
                    trialId = trialId,
                    timestamp = DateTime.UtcNow,
                    imageBase64 = imageBase64,
                    metadata = metadata,
                    success = true
                };
                
                eventBus.FrameCaptured?.Publish(captureData);
                
                // 发布性能指标
                var captureTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                eventBus?.PublishMetric("frame_capture_time", "latency", captureTime, "ms", 
                    new { TaskId = taskId, Width = options.width, Height = options.height });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[StimulusCapture] Capture failed: {ex.Message}");
                PublishCaptureError(requestId, taskId, trialId, ex.Message);
            }
            finally
            {
                _activeCaptureCount--;
            }
            
            yield return null;
        }
        
        /// <summary>
        /// 收集元数据
        /// </summary>
        private FrameMetadata CollectMetadata(string taskId, int trialId, FrameCaptureOptions options)
        {
            var cameraTransform = headCamera.transform;
            
            return new FrameMetadata
            {
                timestamp = DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds,
                camera = new CameraInfo
                {
                    fov = headCamera.fieldOfView,
                    resolution = new int[] { options.width, options.height },
                    pose = new PoseInfo
                    {
                        position = cameraTransform.position,
                        rotationEuler = cameraTransform.eulerAngles
                    }
                },
                conditions = CollectConditionInfo(),
                objects = CollectObjectInfo(),
                meta = new MetaInfo
                {
                    seed = UnityEngine.Random.seed,
                    fpsCap = (int)(1f / Time.fixedDeltaTime),
                    transport = "event_bus",
                    provider = "stimulus_capture"
                }
            };
        }
        
        /// <summary>
        /// 收集环境条件信息
        /// </summary>
        private ConditionInfo CollectConditionInfo()
        {
            TryBindSceneManager();

            if (_sceneManager == null)
            {
                // 回退：使用保守默认值
                return new ConditionInfo
                {
                    textureDensity = 1.0f,
                    lighting = "default",
                    occlusion = false,
                    environment = "unknown"
                };
            }

            return new ConditionInfo
            {
                textureDensity = _sceneManager.CurrentTextureDensity,
                lighting = _sceneManager.CurrentLightingPreset,
                occlusion = _sceneManager.CurrentOcclusionEnabled,
                environment = _sceneManager.CurrentEnvironment ?? "unknown"
            };
        }
        
        /// <summary>
        /// 收集对象信息
        /// </summary>
        private ObjectInfo[] CollectObjectInfo()
        {
            // 这里可以收集场景中的重要对象信息
            // 暂时返回空数组，后续可以扩展
            return new ObjectInfo[0];
        }
        
        /// <summary>
        /// 创建默认捕获选项
        /// </summary>
        private FrameCaptureOptions CreateDefaultOptions()
        {
            return new FrameCaptureOptions
            {
                width = defaultWidth,
                height = defaultHeight,
                fov = defaultFOV,
                format = defaultFormat.ToString().ToLower(),
                quality = defaultJpegQuality,
                includeMetadata = true
            };
        }
        
        /// <summary>
        /// 发布捕获错误
        /// </summary>
        private void PublishCaptureError(string requestId, string taskId, int trialId, string errorMessage)
        {
            var captureData = new FrameCapturedEventData
            {
                requestId = requestId,
                taskId = taskId,
                trialId = trialId,
                timestamp = DateTime.UtcNow,
                success = false,
                errorMessage = errorMessage
            };
            
            eventBus.FrameCaptured?.Publish(captureData);
            
            eventBus?.PublishError("StimulusCapture", ErrorSeverity.Error, "CAPTURE_FAILED", errorMessage,
                new { RequestId = requestId, TaskId = taskId, TrialId = trialId });
        }
        
        /// <summary>
        /// 设置头部相机
        /// </summary>
        public void SetHeadCamera(Camera camera)
        {
            headCamera = camera;
            Debug.Log($"[StimulusCapture] Head camera set to: {camera?.name ?? "null"}");
        }
        
        /// <summary>
        /// 更新相机FOV
        /// </summary>
        public void SetCameraFOV(float fov)
        {
            if (headCamera != null)
            {
                headCamera.fieldOfView = fov;
                Debug.Log($"[StimulusCapture] Camera FOV set to: {fov}");
            }
        }

        /// <summary>
        /// 绑定场景管理器（ExperimentSceneManager），以便在元数据中记录真实环境条件。
        /// </summary>
        private void TryBindSceneManager()
        {
            if (_sceneManager != null) return;
            _sceneManager = FindObjectOfType<ExperimentSceneManager>();
        }
    }
}
