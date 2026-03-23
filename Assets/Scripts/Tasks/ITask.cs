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
    /// 可选：任务级生命周期钩子（每次 RunAsync 调用一次）。
    /// 用于在任务开始/结束时创建与销毁临时场景/对象，避免污染其他场景。
    /// </summary>
    public interface ITaskRunLifecycle
    {
        Task OnRunBeginAsync(CancellationToken ct);
        Task OnRunEndAsync(CancellationToken ct);
    }

    /// <summary>
    /// 可选：需要显式控制时序呈现的任务。
    /// 用于 A -> mask -> B 这类先播放刺激、再采集人类/MLLM 判断的任务。
    /// </summary>
    public interface ITemporalInferenceTask
    {
        Task RunTemporalHumanPresentationAsync(TrialSpec trial, CancellationToken ct);
        Task<LLMResponse> RunTemporalMllmInferenceAsync(TrialSpec trial, CancellationToken ct);
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
        public bool isAnchor;             // 是否为锚定试次（不计入正式拟合）
        public string targetKind;         // "cube"|"sphere"|"human"
        public float trueDistanceM;       // 真值（米）

        // Horizon Cue Integration 字段
        public float horizonAngleDeg;     // 地平线偏移角（度）：-6/-3/0/+3/+6
        public int repetitionIndex;       // 重复序号（1..N）
        public float sphereScreenY01;     // 运行时记录：球体屏幕 Y（0..1），用于校验“屏幕静止”

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

        // Material Roughness（Ambiguity）可能用到
        public float roughness;           // 真值粗糙度（0..1），0=镜面，1=完全哑光
        public bool requireHeadMotion;    // Human 条件：是否要求头动门控（optic flow）

        // Numerosity Comparison 专用字段
        public float baseCountN;                  // 基准数量（较少一侧）：10/50/100/200/500
        public float ratioR;                      // 比例：1.1-2.0
        public int leftCount;                     // 左侧实际数量
        public int rightCount;                    // 右侧实际数量
        public string trueMoreSide;               // 真值："left" | "right"
        public float exposureDurationMs = 500f;   // 曝光时长（默认 500ms）
        public float dotRadius = 0.2f;            // 点的半径

        // Visual Crowding 专用字段
        public float eccentricityDeg;             // 目标字母离心率（度）
        public float spacingDeg;                  // 目标与最近干扰项的间距（度）
        public string targetLetter;               // 真值：目标字母
        public string[] flankerLetters;           // 干扰字母序列（长度 5 时目标索引在 2）
        public int targetIndex = 2;               // 目标在串中的索引位置，默认 0..4 中的 2
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

        // Material Roughness 指标（连续）
        public float predictedRoughness;   // 0..1
        public float trueRoughness;        // 0..1
        public float roughnessAbsError;    // |pred-true|
        public float roughnessSignedError; // pred-true

        // Numerosity Comparison 指标
        public string predictedMoreSide;          // "left" | "right"
        public string trueMoreSide;               // "left" | "right"
        public bool isMoreSideCorrect;            // 是否判断正确
        public long humanReactionTimeMs;          // 人类反应时（从 mask 出现到提交）

        // Visual Crowding 指标
        public string predictedLetter;            // 模型/人类预测的字母
        public string trueLetter;                 // 真值字母
        public bool isLetterCorrect;              // 是否识别正确

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
