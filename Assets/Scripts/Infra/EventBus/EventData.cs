using System;
using UnityEngine;
using VRPerception.Perception;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 帧请求事件数据
    /// </summary>
    [Serializable]
    public class FrameRequestedEventData
    {
        public string requestId;
        public string taskId;
        public int trialId;
        public string requester; // 请求来源
        public DateTime timestamp;
        public FrameCaptureOptions options;
    }
    
    /// <summary>
    /// 帧捕获选项
    /// </summary>
    [Serializable]
    public class FrameCaptureOptions
    {
        public int width = 1280;
        public int height = 720;
        public float fov = 60f;
        public string format = "jpeg"; // "png" or "jpeg"
        public int quality = 75; // for jpeg
        public bool includeMetadata = true;
        public string label; // 可选标签
    }
    
    /// <summary>
    /// 帧捕获完成事件数据
    /// </summary>
    [Serializable]
    public class FrameCapturedEventData
    {
        public string requestId;
        public string taskId;
        public int trialId;
        public DateTime timestamp;
        public string imageBase64;
        public FrameMetadata metadata;
        public bool success;
        public string errorMessage;
    }
    
    /// <summary>
    /// 推理接收事件数据
    /// </summary>
    [Serializable]
    public class InferenceReceivedEventData
    {
        public string requestId;
        public string taskId;
        public int trialId;
        public DateTime timestamp;
        public LLMResponse response;
        public string providerId;
    }
    
    /// <summary>
    /// 动作计划接收事件数据
    /// </summary>
    [Serializable]
    public class ActionPlanReceivedEventData
    {
        public string requestId;
        public string taskId;
        public int trialId;
        public DateTime timestamp;
        public ActionCommand[] actions;
        public string providerId;
    }
    
    /// <summary>
    /// 执行器状态变化事件数据
    /// </summary>
    [Serializable]
    public class ExecutorStateEventData
    {
        public string executorId;
        public ExecutorState previousState;
        public ExecutorState currentState;
        public DateTime timestamp;
        public string reason;
    }
    
    /// <summary>
    /// 执行器状态枚举
    /// </summary>
    public enum ExecutorState
    {
        Idle,
        ExecutingBlocking,
        ExecutingNonBlocking,
        Paused,
        Error
    }
    
    /// <summary>
    /// 命令生命周期事件数据
    /// </summary>
    [Serializable]
    public class CommandLifecycleEventData
    {
        public string commandId;
        public string commandName;
        public CommandLifecycleState state;
        public DateTime timestamp;
        public object parameters;
        public string errorMessage;
        public long executionTimeMs;
    }
    
    /// <summary>
    /// 命令生命周期状态
    /// </summary>
    public enum CommandLifecycleState
    {
        Queued,
        Started,
        Completed,
        Failed,
        Cancelled,
        Timeout
    }
    
    /// <summary>
    /// 连接状态变化事件数据
    /// </summary>
    [Serializable]
    public class ConnectionStateEventData
    {
        public string connectionId;
        public string providerId;
        public ConnectionState previousState;
        public ConnectionState currentState;
        public DateTime timestamp;
        public string reason;
        public string endpoint;
    }
    
    /// <summary>
    /// 连接状态枚举
    /// </summary>
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting,
        Error
    }
    
    /// <summary>
    /// 错误事件数据
    /// </summary>
    [Serializable]
    public class ErrorEventData
    {
        public string errorId;
        public string source; // 错误来源组件
        public ErrorSeverity severity;
        public string errorCode;
        public string message;
        public string stackTrace;
        public DateTime timestamp;
        public object context; // 错误上下文信息
    }
    
    /// <summary>
    /// 错误严重程度
    /// </summary>
    public enum ErrorSeverity
    {
        Info,
        Warning,
        Error,
        Critical
    }
    
    /// <summary>
    /// 试验生命周期事件数据
    /// </summary>
    [Serializable]
    public class TrialLifecycleEventData
    {
        public string taskId;
        public int trialId;
        public TrialLifecycleState state;
        public DateTime timestamp;
        public object trialConfig;
        public object results;
        public string errorMessage;
        public string humanInputPrompt;
    }
    
    /// <summary>
    /// 试验生命周期状态
    /// </summary>
    public enum TrialLifecycleState
    {
        Initialized,
        Started,
        SceneSetup,
        WaitingForInput,
        Processing,
        Completed,
        Failed,
        Cancelled
    }

    /// <summary>
    /// 编排器状态事件数据
    /// </summary>
    [Serializable]
    public class OrchestratorStateEventData
    {
        public string playlistId;
        public string playlistDisplayName;
        public int currentEntryIndex;
        public string taskId;
        public OrchestratorLifecycleState state;
        public DateTime timestamp;
        public string message;
        public object payload;
        public bool isCheckpointRestore;
    }

    /// <summary>
    /// 编排器生命周期状态
    /// </summary>
    public enum OrchestratorLifecycleState
    {
        Idle,
        Preparing,
        LoadingPlaylist,
        RunningEntry,
        WaitingForRest,
        Paused,
        Resumed,
        SkippedEntry,
        SavingCheckpoint,
        RestoringCheckpoint,
        Completed,
        Cancelled,
        Error
    }
    
    /// <summary>
    /// 日志刷新请求事件数据
    /// </summary>
    [Serializable]
    public class LogFlushEventData
    {
        public string requestId;
        public DateTime timestamp;
        public string logType; // "trial", "performance", "error", etc.
        public bool forceFlush;
        public string outputPath;
    }
    
    /// <summary>
    /// 性能指标事件数据
    /// </summary>
    [Serializable]
    public class PerformanceMetricEventData
    {
        public string metricName;
        public string category; // "latency", "throughput", "memory", etc.
        public double value;
        public string unit;
        public DateTime timestamp;
        public object tags; // 额外标签信息
    }
    
    /// <summary>
    /// 场景对象变化事件数据
    /// </summary>
    [Serializable]
    public class SceneObjectEventData
    {
        public string objectId;
        public string objectName;
        public SceneObjectAction action;
        public DateTime timestamp;
        public Vector3 position;
        public Vector3 rotation;
        public Vector3 scale;
        public object properties;
    }
    
    /// <summary>
    /// 场景对象动作
    /// </summary>
    public enum SceneObjectAction
    {
        Created,
        Destroyed,
        Moved,
        Rotated,
        Scaled,
        PropertyChanged,
        Activated,
        Deactivated
    }
}
