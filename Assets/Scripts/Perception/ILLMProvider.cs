using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VRPerception.Perception
{
    /// <summary>
    /// LLM Provider 抽象接口，统一不同后端的推理调用
    /// </summary>
    public interface ILLMProvider
    {
        /// <summary>
        /// Provider 类型标识
        /// </summary>
        string ProviderType { get; }
        
        /// <summary>
        /// Provider 名称
        /// </summary>
        string ProviderName { get; }
        
        /// <summary>
        /// 是否可用
        /// </summary>
        bool IsAvailable { get; }
        
        /// <summary>
        /// 健康状态检查
        /// </summary>
        Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
        
        /// <summary>
        /// 执行推理
        /// </summary>
        /// <param name="request">推理请求</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>推理响应</returns>
        Task<LLMResponse> InferAsync(LLMRequest request, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// LLM 请求数据结构
    /// </summary>
    [Serializable]
    public class LLMRequest
    {
        public string requestId;
        public string taskId;
        public int trialId;
        public string systemPrompt;
        public string taskPrompt;
        public string imageBase64;
        public string[] imagesBase64;
        public FrameMetadata metadata;
        public FrameMetadata[] metadataList;
        public ToolSpec[] tools;
        public float temperature = 0;
        public float topP = 1;
        public string[] stopSequences;
        public int maxTokens = 1000;
        public int timeoutMs = 30000;
    }

    /// <summary>
    /// LLM 响应数据结构
    /// </summary>
    [Serializable]
    public class LLMResponse
    {
        public string type; // "inference" or "action_plan" or "error"
        public string taskId;
        public int trialId;
        public object answer; // 推理结果
        public ActionCommand[] actions; // 动作计划
        public float confidence;
        public string explanation;
        public string errorCode;
        public string errorMessage;
        public long latencyMs;
        public string providerId;
    }

    /// <summary>
    /// 帧元数据
    /// </summary>
    [Serializable]
    public class FrameMetadata
    {
        public double timestamp;
        public CameraInfo camera;
        public ConditionInfo conditions;
        public ObjectInfo[] objects;
        public MetaInfo meta;
    }

    [Serializable]
    public class CameraInfo
    {
        public float fov;
        public int[] resolution;
        public PoseInfo pose;
    }

    [Serializable]
    public class PoseInfo
    {
        public Vector3 position;
        public Vector3 rotationEuler;
    }

    [Serializable]
    public class ConditionInfo
    {
        public float textureDensity;
        public string lighting;
        public bool occlusion;
        public string environment;
    }

    [Serializable]
    public class ObjectInfo
    {
        public string name;
        public string kind;
        public Vector3 position;
        public float trueDistance;
        public Vector3 scale;
    }

    [Serializable]
    public class MetaInfo
    {
        public int seed;
        public int fpsCap;
        public string transport;
        public string provider;
    }

    /// <summary>
    /// 工具规范
    /// </summary>
    [Serializable]
    public class ToolSpec
    {
        public string name;
        public string description;
        public ParameterSpec parameters;
    }

    [Serializable]
    public class ParameterSpec
    {
        public string type;
        public PropertySpec properties;
        public string[] required;
    }

    [Serializable]
    public class PropertySpec
    {
        // 动态属性，根据具体工具定义
    }

    /// <summary>
    /// 动作命令
    /// </summary>
    [Serializable]
    public class ActionCommand
    {
        public string id;
        public string name;
        public object parameters;
        public bool wait = true;
        public int timeoutMs = 5000;
        public int retries = 0;
    }
}
