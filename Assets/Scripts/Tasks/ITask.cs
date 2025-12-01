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

        // Relative Depth Ordering 字段
        public float depthA;              // 对象 A 距离（米）
        public float depthB;              // 对象 B 距离（米）
        public float scaleA = 1f;         // 对象 A 缩放（统一比例）
        public float scaleB = 1f;         // 对象 B 缩放（统一比例）
        public string trueCloser;         // 真值：更近者 "A"|"B"

        // Semantic Size Bias 可能用到
        public string objectA;
        public string objectB;
        public string sizeRelation;       // "equal"|"reversed"
        public string background;         // "none"|"indoor"|"street"

        // Change Detection 可能用到
        public bool changed;              // 场景是否发生变化（A->B）
        public string changeCategory;     // "appearance"|"disappearance"|"movement"|"replacement"|"none"

        // Occlusion Reasoning & Counting 可能用到
        public float occlusionRatio;      // 遮挡率（0..0.8）
        public string occluderType;       // 遮挡体类型："wall"|"plant"|"human"
        public string targetCategory;     // 目标类别（与 targetKind/objectA/B 语义靠近）
        public bool targetPresent;        // 真值：视野内是否存在至少一个目标
        public int trueCount;             // 真值：可见目标数量（>=0）

        // Visual Search 相关
        public int setSize;               // 场景中对象总数量（含目标与干扰项）
        public string distractorCategory; // 干扰项类别（如 blue_cup/green_cup）
        public string similarityLevel;    // 目标-干扰项相似度标签：easy/hard
        public int targetCount;           // 目标数量（通常 0 或 1）

        // Object Counting / Density Estimation
        public string layoutPattern;      // "grid"|"random"|"clustered"
        public float areaSize;            // 场景布置区域的尺度（半径或边长，米）
        public string countingMode;       // "count"|"density"（当前实现以 count 为主）

        // Color Constancy 可能用到
        public string colorName;          // 真值：表面颜色类别（如 "red"|"green"|"blue"|"yellow"|"white"|"gray"）
        public int trueR;                 // 真值：RGB 0-255
        public int trueG;
        public int trueB;
        public string material;           // 目标材质标签（如 "matte"|"glossy"），目前仅用于日志
        public bool hasShadow;            // 是否存在明显阴影

        // Material Perception 可能用到
        public float objectYawDeg;        // 物体自身绕 Y 轴旋转（度）
        public float lightYawDeg;         // 主光源方位（度）
        public float lightPitchDeg;       // 主光源俯仰角（度）
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

        // Size Bias / 二选一 A/B 任务指标
        public string predictedLarger; // "A"|"B"
        public bool isCorrect;

        // Relative Depth Ordering 指标（更近者 A/B）
        public string predictedCloser; // "A"|"B"
        public string trueCloser;      // "A"|"B"

        // Change Detection 指标
        public bool predictedChanged;
        public bool trueChanged;
        public string predictedChangeCategory;
        public string trueChangeCategory;

        // Occlusion Reasoning & Counting 指标
        public bool predictedPresent;
        public bool truePresent;
        public int predictedCount;
        public int trueCount;
        public float countAbsError;
        public float countRelError;

        // Visual Search 指标
        public bool predictedFound;
        public string predictedTarget;

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
