using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;

namespace VRPerception.Tasks
{
    /// <summary>
    /// 任务接口：定义试次生成、提示词、前后置钩子与评测
    /// </summary>
    public interface ITask
    {
        /// <summary>任务标识（如 "distance_compression"）</summary>
        string TaskId { get; }

        /// <summary>初始化（由 TaskRunner 在 Start/Run 前调用）</summary>
        void Initialize(TaskRunner runner, EventBusManager eventBus);

        /// <summary>基于给定随机种子生成试次计划</summary>
        TrialSpec[] BuildTrials(int seed);

        /// <summary>系统提示词（要求严格 ONLY JSON、含任务接口约束）</summary>
        string GetSystemPrompt();

        /// <summary>该任务所需的工具（如需动作闭环）</summary>
        ToolSpec[] GetTools();

        /// <summary>根据试次构造任务提示词</summary>
        string BuildTaskPrompt(TrialSpec trial);

        /// <summary>试次开始前的场景/相机/对象布置（可异步）</summary>
        Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct);

        /// <summary>试次结束后的清理或补充记录（可异步）</summary>
        Task OnAfterTrialAsync(TrialSpec trial, LLMResponse response, CancellationToken ct);

        /// <summary>根据响应与真值计算评测指标</summary>
        TrialEvaluation Evaluate(TrialSpec trial, LLMResponse response);
    }

    /// <summary>
    /// 通用试次规格（不同任务可扩展其字段）
    /// </summary>
    [Serializable]
    public class TrialSpec
    {
        public string taskId;
        public int trialId;

        // 通用条件
        public string environment;        // "open_field" | "corridor" | "none/indoor/street" 等
        public float textureDensity = 1f; // 0.5/1.0/1.5/2.0
        public string lighting = "default"; // "bright"|"dim"|"hdr"
        public bool occlusion = false;    // 是否存在遮挡
        public float fovDeg = 60f;        // 50/60/90

        // Distance Compression 专用字段
        public string targetKind;         // "cube"|"sphere"|"human"
        public float trueDistanceM;       // 真值（米）

        // Semantic Size Bias 可能用到
        public string objectA;
        public string objectB;
        public string sizeRelation;       // "equal"|"reversed"
        public string background;         // "none"|"indoor"|"street"
    }

    /// <summary>
    /// 评测结果（可被日志器与汇总器使用）
    /// </summary>
    [Serializable]
    public class TrialEvaluation
    {
        public bool success;
        public string failureReason;

        // 模型输出摘要
        public string responseType;   // "inference"|"action_plan"|"error"
        public float confidence;
        public string providerId;
        public long latencyMs;

        // Distance Compression 指标
        public float predictedDistanceM;
        public float absError;
        public float relError;

        // Size Bias 指标
        public string predictedLarger; // "A"|"B"
        public bool isCorrect;

        // 其他扩展指标（键值对）
        public string extraJson;
    }

    /// <summary>
    /// TaskRunner 暴露给任务的上下文（必要引用）
    /// </summary>
    public class TaskRunnerContext
    {
        public TaskRunner runner;
        public EventBusManager eventBus;
        public PerceptionSystem perception;
        public StimulusCapture stimulus;
    }
}