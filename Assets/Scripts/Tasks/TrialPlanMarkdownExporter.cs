using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRPerception.Orchestration;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;

namespace VRPerception.Tasks
{
    /// <summary>
    /// 根据给定 taskId 和随机种子导出该任务的 Trial 列表到 Markdown。
    /// - 只调用 BuildTrials(seed)，不会真正跑实验或布置场景。
    /// - 建议在对应任务场景（如 Task.unity）中挂载，并通过右键菜单导出。
    /// </summary>
    public class TrialPlanMarkdownExporter : MonoBehaviour
    {
        public enum ConfigSource
        {
            // 手动指定 taskId/seed/maxTrials（适合快速导出对比）
            Manual,
            // 从场景中的 TaskRunner 读取当前配置（taskId/seed/maxTrials）
            FromTaskRunner,
            // 从某个 TaskPlaylist 的条目读取配置（推荐在使用 Playlist/Orchestrator 做实验时使用）
            FromPlaylistEntry
        }

        [Header("Config Source")]
        [Tooltip("导出配置来源：手动 / TaskRunner / Playlist 条目")]
        public ConfigSource configSource = ConfigSource.Manual;

        [Tooltip("当选择 FromTaskRunner 时（可选）：显式指定 TaskRunner；为空则自动查找场景中第一个 TaskRunner")]
        public TaskRunner taskRunnerOverride;

        [Tooltip("当选择 FromPlaylistEntry 时：指定要导出的 TaskPlaylist 资产")]
        public TaskPlaylist playlist;

        [Tooltip("当选择 FromPlaylistEntry 时：要导出的条目索引（0-based）")]
        public int playlistEntryIndex = 0;

        [Header("Task Config")]
        [Tooltip("任务标识，例如 distance_compression / semantic_size_bias")]
        public string taskId = "distance_compression";

        [Tooltip("用于生成 TrialSpec 的随机种子，应与实际实验配置保持一致")]
        public int seed = 12345;

        [Tooltip("可选：限制导出的最大 trial 数量，0 表示不限制")]
        public int maxTrials = 0;

        [Header("Output")]
        [Tooltip("输出目录（相对于工程根目录），例如 Docs 或 Docs/TrialPlans")]
        public string outputDirectory = "Docs";

        [Tooltip("文件名前缀，例如 TrialPlan_，最终文件名 = 前缀 + taskId + _seed_<seed>.md")]
        public string fileNamePrefix = "TrialPlan_";

        /// <summary>
        /// 在 Inspector 中右键组件，选择该菜单即可导出 Markdown。
        /// </summary>
        [ContextMenu("Export Trial Plan (Markdown)")]
        public void ExportTrialPlanMarkdown()
        {
            // 记录“请求的”taskId 字符串，仅用于文档展示
            var requestedTaskId = taskId;

            if (!TryResolveConfig(out var resolvedTaskId, out var usedSeed, out var usedMaxTrials))
                return;

            // 构造 TaskRunner 上下文，以便 TaskRegistry 中的任务可以访问必要引用
            var ctx = BuildContext();

            if (!TaskRegistry.Instance.TryCreate(resolvedTaskId, ctx, out var task))
            {
                // 尝试使用 TaskMode 的名称兼容（例如 DistanceCompression）
                if (!TryCreateBuiltinTask(resolvedTaskId, ctx, out task))
                {
                    Debug.LogError($"[TrialPlanMarkdownExporter] Failed to create task for taskId='{resolvedTaskId}'.");
                    return;
                }
            }

            try
            {
                // Initialize（有些任务可能在这里做额外设置）
                task.Initialize(ctx.runner, ctx.eventBus);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TrialPlanMarkdownExporter] Initialize failed for task '{taskId}': {ex.Message}");
            }

            TrialSpec[] trials;
            try
            {
                trials = task.BuildTrials(usedSeed) ?? Array.Empty<TrialSpec>();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TrialPlanMarkdownExporter] BuildTrials(seed={usedSeed}) failed: {ex.Message}");
                return;
            }

            // 对 trialId 与 taskId 做一次规范化，保持与 TaskRunner 行为一致
            for (int i = 0; i < trials.Length; i++)
            {
                if (trials[i] == null) continue;
                trials[i].trialId = i;
                if (string.IsNullOrWhiteSpace(trials[i].taskId))
                {
                    trials[i].taskId = task.TaskId;
                }
            }

            if (usedMaxTrials > 0 && trials.Length > usedMaxTrials)
            {
                Array.Resize(ref trials, usedMaxTrials);
            }

            var scene = SceneManager.GetActiveScene();

            var md = BuildMarkdown(task, trials, usedSeed, scene, requestedTaskId, resolvedTaskId);

            try
            {
                var fullPath = WriteMarkdownToFile(task.TaskId, usedSeed, md);
                Debug.Log($"[TrialPlanMarkdownExporter] Exported {trials.Length} trials to: {fullPath}");

#if UNITY_EDITOR
                UnityEditor.AssetDatabase.Refresh();
#endif
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TrialPlanMarkdownExporter] Failed to write markdown file: {ex.Message}");
            }
        }

        /// <summary>
        /// 根据配置来源解析最终用于导出的 taskId/seed/maxTrials。
        /// - Manual: 使用本组件上的 taskId/seed/maxTrials 字段
        /// - FromTaskRunner: 读取 TaskRunner 当前配置
        /// - FromPlaylistEntry: 读取指定 TaskPlaylist 条目的配置
        /// </summary>
        private bool TryResolveConfig(out string resolvedTaskId, out int usedSeed, out int usedMaxTrials)
        {
            resolvedTaskId = taskId;
            usedSeed = seed;
            usedMaxTrials = maxTrials;

            switch (configSource)
            {
                case ConfigSource.Manual:
                    if (string.IsNullOrWhiteSpace(resolvedTaskId))
                    {
                        Debug.LogError("[TrialPlanMarkdownExporter] taskId is empty in Manual mode.");
                        return false;
                    }
                    return true;

                case ConfigSource.FromTaskRunner:
                {
                    var runner = taskRunnerOverride != null ? taskRunnerOverride : FindObjectOfType<TaskRunner>();
                    if (runner == null)
                    {
                        Debug.LogError("[TrialPlanMarkdownExporter] No TaskRunner found in scene for FromTaskRunner mode.");
                        return false;
                    }

                    resolvedTaskId = runner.CurrentConfiguredTaskId;
                    usedSeed = runner.CurrentRandomSeed;
                    usedMaxTrials = runner.CurrentMaxTrials;

                    if (string.IsNullOrWhiteSpace(resolvedTaskId))
                    {
                        Debug.LogError("[TrialPlanMarkdownExporter] TaskRunner.CurrentConfiguredTaskId is empty.");
                        return false;
                    }

                    return true;
                }

                case ConfigSource.FromPlaylistEntry:
                {
                    if (playlist == null)
                    {
                        Debug.LogError("[TrialPlanMarkdownExporter] Playlist is not assigned for FromPlaylistEntry mode.");
                        return false;
                    }

                    if (!playlist.TryGetEntry(playlistEntryIndex, out var entry))
                    {
                        Debug.LogError($"[TrialPlanMarkdownExporter] Invalid playlistEntryIndex={playlistEntryIndex} for playlist '{playlist.name}'.");
                        return false;
                    }

                    resolvedTaskId = entry.ResolveTaskId();
                    usedSeed = entry.ResolveRandomSeed(playlist.DefaultRandomSeed);
                    usedMaxTrials = entry.maxTrials;

                    if (string.IsNullOrWhiteSpace(resolvedTaskId))
                    {
                        Debug.LogError("[TrialPlanMarkdownExporter] ResolvedTaskId from playlist entry is empty.");
                        return false;
                    }

                    return true;
                }

                default:
                    Debug.LogError($"[TrialPlanMarkdownExporter] Unknown ConfigSource: {configSource}");
                    return false;
            }
        }

        private TaskRunnerContext BuildContext()
        {
            var ctx = new TaskRunnerContext();

            // 尝试在场景中查找已有组件引用
            ctx.runner = FindObjectOfType<TaskRunner>();
            ctx.eventBus = EventBusManager.Instance;
            ctx.perception = FindObjectOfType<PerceptionSystem>();
            ctx.stimulus = FindObjectOfType<StimulusCapture>();

            return ctx;
        }

        private bool TryCreateBuiltinTask(string id, TaskRunnerContext ctx, out ITask task)
        {
            task = null;

            // 支持直接传入 TaskMode 的名称（例如 DistanceCompression）
            if (Enum.TryParse<TaskMode>(id, true, out var mode))
            {
                switch (mode)
                {
                    case TaskMode.DistanceCompression:
                        task = new DistanceCompressionTask(ctx);
                        return true;
                    case TaskMode.SemanticSizeBias:
                        task = new SemanticSizeBiasTask(ctx);
                        return true;
                }
            }

            // 兼容常用 taskId
            if (string.Equals(id, "distance_compression", StringComparison.OrdinalIgnoreCase))
            {
                task = new DistanceCompressionTask(ctx);
                return true;
            }

            if (string.Equals(id, "semantic_size_bias", StringComparison.OrdinalIgnoreCase))
            {
                task = new SemanticSizeBiasTask(ctx);
                return true;
            }

            if (string.Equals(id, "horizon_cue_integration", StringComparison.OrdinalIgnoreCase))
            {
                task = new HorizonCueIntegrationTask(ctx);
                return true;
            }

            return false;
        }

        private string BuildMarkdown(ITask task, TrialSpec[] trials, int usedSeed, Scene scene, string requestedTaskId, string resolvedTaskId)
        {
            var sb = new StringBuilder(4096);

            var sceneName = scene.IsValid() ? scene.name : "(unknown)";
            var scenePath = scene.IsValid() ? scene.path : "(unknown)";

            sb.AppendLine("# Trial Plan");
            sb.AppendLine();
            var requested = string.IsNullOrWhiteSpace(requestedTaskId) ? "(none)" : requestedTaskId;
            sb.AppendLine($"- Task: `{task.TaskId}` (requested: `{requested}`, resolved: `{resolvedTaskId}`)");
            sb.AppendLine($"- Seed: `{usedSeed}`");
            sb.AppendLine($"- Scene: `{sceneName}`");
            sb.AppendLine($"- Scene Path: `{scenePath}`");
            sb.AppendLine($"- Trial Count: `{trials.Length}`");
            sb.AppendLine();

            var systemPrompt = SafeGetSystemPrompt(task);
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                sb.AppendLine("## System Prompt");
                sb.AppendLine();
                sb.AppendLine("```text");
                sb.AppendLine(systemPrompt.TrimEnd());
                sb.AppendLine("```");
                sb.AppendLine();
            }

            // Adaptive staircase tasks: BuildTrials 仅能导出“计划结构”，核心刺激参数会在运行时自适应变化。
            if (string.Equals(task.TaskId, "depth_jnd_staircase", StringComparison.OrdinalIgnoreCase))
            {
                var cfg = GetDepthJndStaircaseConfig(task);

                sb.AppendLine("## Staircase Notes");
                sb.AppendLine();
                sb.AppendLine("- Note: `depthA/depthB/trueCloser` are generated online (adaptive).");
                sb.AppendLine("- Note: This exporter calls `BuildTrials(seed)` only; it does not simulate staircase updates.");
                sb.AppendLine($"- defaultMaxTrials: `{cfg.defaultMaxTrials}`");
                sb.AppendLine($"- fovDeg: `{cfg.fovDeg}`");
                sb.AppendLine($"- baseDistanceRangeM: `[{cfg.baseDistanceMinM}, {cfg.baseDistanceMaxM}]`");
                sb.AppendLine($"- minPresentableDistanceM: `{cfg.minPresentableDistanceM}`");
                sb.AppendLine($"- deltaStartM: `{cfg.deltaStartM}`");
                sb.AppendLine($"- deltaMinM: `{cfg.deltaMinM}`");
                sb.AppendLine($"- deltaMaxM: `{cfg.deltaMaxM}`");
                sb.AppendLine($"- kappa: `{cfg.kappa}`");
                sb.AppendLine($"- reversalTargetPerGroup: `{cfg.reversalTargetPerGroup}`");
                sb.AppendLine($"- thresholdUseLastReversals: `{cfg.thresholdUseLastReversals}`");
                sb.AppendLine();
            }

            sb.AppendLine("## Trials");
            sb.AppendLine();

            for (int i = 0; i < trials.Length; i++)
            {
                var t = trials[i] ?? new TrialSpec();

                sb.AppendLine($"### Trial {t.trialId}");
                sb.AppendLine();

                sb.AppendLine($"- taskId: `{t.taskId}`");
                if (!string.IsNullOrWhiteSpace(t.environment))
                    sb.AppendLine($"- environment: `{t.environment}`");
                sb.AppendLine($"- fovDeg: `{t.fovDeg}`");
                sb.AppendLine($"- textureDensity: `{t.textureDensity}`");
                if (!string.IsNullOrWhiteSpace(t.lighting))
                    sb.AppendLine($"- lighting: `{t.lighting}`");
                sb.AppendLine($"- occlusion: `{t.occlusion}`");
                if (!string.IsNullOrWhiteSpace(t.background))
                    sb.AppendLine($"- background: `{t.background}`");

                // Distance Compression & 通用 target 字段
                if (!string.IsNullOrWhiteSpace(t.targetKind))
                    sb.AppendLine($"- targetKind: `{t.targetKind}`");
                if (t.trueDistanceM > 0f)
                {
                    sb.AppendLine($"- trueDistanceM: `{t.trueDistanceM}`");
                }

                // Horizon Cue Integration 字段
                if (Math.Abs(t.horizonAngleDeg) > 0.001f || t.repetitionIndex > 0)
                {
                    sb.AppendLine($"- horizonAngleDeg: `{t.horizonAngleDeg}`");
                    if (t.repetitionIndex > 0)
                        sb.AppendLine($"- repetitionIndex: `{t.repetitionIndex}`");
                }

                // Semantic Size Bias 字段
                if (!string.IsNullOrWhiteSpace(t.objectA) || !string.IsNullOrWhiteSpace(t.objectB))
                {
                    if (!string.IsNullOrWhiteSpace(t.objectA))
                        sb.AppendLine($"- objectA: `{t.objectA}`");
                    if (!string.IsNullOrWhiteSpace(t.objectB))
                        sb.AppendLine($"- objectB: `{t.objectB}`");
                    if (!string.IsNullOrWhiteSpace(t.sizeRelation))
                        sb.AppendLine($"- sizeRelation: `{t.sizeRelation}`");
                }

                // Relative Depth Ordering 字段
                bool isDepthTask =
                    string.Equals(t.taskId, "relative_depth_order", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.taskId, "depth_jnd_staircase", StringComparison.OrdinalIgnoreCase);

                if (isDepthTask || t.depthA > 0f || t.depthB > 0f || !string.IsNullOrWhiteSpace(t.trueCloser))
                {
                    sb.AppendLine($"- depthA: `{FormatFloatOrRuntime(t.depthA, isDepthTask)}`");
                    sb.AppendLine($"- depthB: `{FormatFloatOrRuntime(t.depthB, isDepthTask)}`");
                    sb.AppendLine($"- scaleA: `{t.scaleA}`");
                    sb.AppendLine($"- scaleB: `{t.scaleB}`");
                    sb.AppendLine($"- trueCloser: `{FormatStringOrRuntime(t.trueCloser, isDepthTask)}`");
                }

                // Change Detection 字段
                if (t.changed || !string.IsNullOrWhiteSpace(t.changeCategory))
                {
                    sb.AppendLine($"- changed: `{t.changed}`");
                    if (!string.IsNullOrWhiteSpace(t.changeCategory))
                        sb.AppendLine($"- changeCategory: `{t.changeCategory}`");
                }

                // Occlusion Reasoning & Counting 字段
                if (t.occlusionRatio > 0f || !string.IsNullOrWhiteSpace(t.occluderType) ||
                    !string.IsNullOrWhiteSpace(t.targetCategory) || t.trueCount > 0 || t.targetPresent)
                {
                    sb.AppendLine($"- occlusionRatio: `{t.occlusionRatio}`");
                    if (!string.IsNullOrWhiteSpace(t.occluderType))
                        sb.AppendLine($"- occluderType: `{t.occluderType}`");
                    if (!string.IsNullOrWhiteSpace(t.targetCategory))
                        sb.AppendLine($"- targetCategory: `{t.targetCategory}`");
                    sb.AppendLine($"- targetPresent: `{t.targetPresent}`");
                    sb.AppendLine($"- trueCount: `{t.trueCount}`");
                }

                // Object Counting / Density Estimation 字段
                if (!string.IsNullOrWhiteSpace(t.countingMode) || !string.IsNullOrWhiteSpace(t.layoutPattern) || t.areaSize > 0f)
                {
                    if (!string.IsNullOrWhiteSpace(t.countingMode))
                        sb.AppendLine($"- countingMode: `{t.countingMode}`");
                    if (!string.IsNullOrWhiteSpace(t.layoutPattern))
                        sb.AppendLine($"- layoutPattern: `{t.layoutPattern}`");
                    if (t.areaSize > 0f)
                        sb.AppendLine($"- areaSize: `{t.areaSize}`");
                }

                // Visual Search 字段
                if (t.setSize > 0 ||
                    !string.IsNullOrWhiteSpace(t.distractorCategory) ||
                    !string.IsNullOrWhiteSpace(t.similarityLevel) ||
                    t.targetCount != 0)
                {
                    if (t.setSize > 0)
                        sb.AppendLine($"- setSize: `{t.setSize}`");
                    if (!string.IsNullOrWhiteSpace(t.distractorCategory))
                        sb.AppendLine($"- distractorCategory: `{t.distractorCategory}`");
                    if (!string.IsNullOrWhiteSpace(t.similarityLevel))
                        sb.AppendLine($"- similarityLevel: `{t.similarityLevel}`");
                    sb.AppendLine($"- targetCount: `{t.targetCount}`");
                }

                // Color Constancy 字段
                if (!string.IsNullOrWhiteSpace(t.colorName) || t.trueR != 0 || t.trueG != 0 || t.trueB != 0 || t.hasShadow)
                {
                    if (!string.IsNullOrWhiteSpace(t.colorName))
                        sb.AppendLine($"- colorName: `{t.colorName}`");
                    sb.AppendLine($"- trueRGB: `({t.trueR}, {t.trueG}, {t.trueB})`");
                    sb.AppendLine($"- hasShadow: `{t.hasShadow}`");
                }

                // Material Perception / 材质相关字段
                if (!string.IsNullOrWhiteSpace(t.material) ||
                    Math.Abs(t.objectYawDeg) > 0.001f ||
                    Math.Abs(t.lightYawDeg) > 0.001f ||
                    Math.Abs(t.lightPitchDeg) > 0.001f)
                {
                    if (!string.IsNullOrWhiteSpace(t.material))
                        sb.AppendLine($"- material: `{t.material}`");
                    sb.AppendLine($"- objectYawDeg: `{t.objectYawDeg}`");
                    sb.AppendLine($"- lightYawDeg: `{t.lightYawDeg}`");
                    sb.AppendLine($"- lightPitchDeg: `{t.lightPitchDeg}`");
                }

                // Numerosity Comparison 字段
                if (t.baseCountN > 0 || t.ratioR > 0 || t.leftCount > 0 || t.rightCount > 0)
                {
                    if (t.baseCountN > 0)
                        sb.AppendLine($"- baseCountN: `{t.baseCountN}`");
                    if (t.ratioR > 0)
                        sb.AppendLine($"- ratioR: `{t.ratioR:F2}`");
                    sb.AppendLine($"- leftCount: `{t.leftCount}`");
                    sb.AppendLine($"- rightCount: `{t.rightCount}`");
                    if (!string.IsNullOrWhiteSpace(t.trueMoreSide))
                        sb.AppendLine($"- trueMoreSide: `{t.trueMoreSide}`");
                    if (t.exposureDurationMs > 0)
                        sb.AppendLine($"- exposureDurationMs: `{t.exposureDurationMs}`");
                    if (t.dotRadius > 0)
                        sb.AppendLine($"- dotRadius: `{t.dotRadius}`");
                }

                // 任务提示词（有助于理解每个 trial 的实际文案）
                var taskPrompt = SafeBuildTaskPrompt(task, t);
                if (!string.IsNullOrWhiteSpace(taskPrompt))
                {
                    sb.AppendLine();
                    sb.AppendLine("```text");
                    sb.AppendLine(taskPrompt.TrimEnd());
                    sb.AppendLine("```");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string FormatFloatOrRuntime(float value, bool preferRuntimePlaceholder)
        {
            if (value > 0f) return value.ToString("0.###");
            return preferRuntimePlaceholder ? "runtime" : value.ToString("0.###");
        }

        private static string FormatStringOrRuntime(string value, bool preferRuntimePlaceholder)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value;
            return preferRuntimePlaceholder ? "runtime" : "";
        }

        private sealed class DepthJndStaircaseConfig
        {
            public int defaultMaxTrials = 60;
            public float fovDeg = 60f;
            public float baseDistanceMinM = 4.0f;
            public float baseDistanceMaxM = 10.0f;
            public float minPresentableDistanceM = 1.0f;
            public float deltaStartM = 0.50f;
            public float deltaMinM = 0.02f;
            public float deltaMaxM = 2.00f;
            public float kappa = 1.4142135f;
            public int reversalTargetPerGroup = 8;
            public int thresholdUseLastReversals = 4;
        }

        private static DepthJndStaircaseConfig GetDepthJndStaircaseConfig(ITask task)
        {
            var cfg = new DepthJndStaircaseConfig();
            if (task == null) return cfg;

            // DepthJndStaircaseTask 使用 private const；用反射读取以避免在此处重复维护。
            try
            {
                var t = task.GetType();
                if (!string.Equals(t.Name, "DepthJndStaircaseTask", StringComparison.Ordinal))
                {
                    return cfg;
                }

                TryReadConstInt(t, "DefaultMaxTrials", ref cfg.defaultMaxTrials);
                TryReadConstFloat(t, "DefaultFovDeg", ref cfg.fovDeg);
                TryReadConstFloat(t, "BaseDistanceMinM", ref cfg.baseDistanceMinM);
                TryReadConstFloat(t, "BaseDistanceMaxM", ref cfg.baseDistanceMaxM);
                TryReadConstFloat(t, "MinPresentableDistanceM", ref cfg.minPresentableDistanceM);
                TryReadConstFloat(t, "DeltaStartM", ref cfg.deltaStartM);
                TryReadConstFloat(t, "DeltaMinM", ref cfg.deltaMinM);
                TryReadConstFloat(t, "DeltaMaxM", ref cfg.deltaMaxM);
                TryReadConstFloat(t, "Kappa", ref cfg.kappa);
                TryReadConstInt(t, "ReversalTargetPerGroup", ref cfg.reversalTargetPerGroup);
                TryReadConstInt(t, "ThresholdUseLastReversals", ref cfg.thresholdUseLastReversals);
            }
            catch
            {
                // ignore, keep defaults
            }

            return cfg;
        }

        private static void TryReadConstInt(Type type, string fieldName, ref int target)
        {
            var f = type.GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (f == null || !f.IsLiteral) return;
            if (f.GetRawConstantValue() is int v) target = v;
        }

        private static void TryReadConstFloat(Type type, string fieldName, ref float target)
        {
            var f = type.GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (f == null || !f.IsLiteral) return;

            var raw = f.GetRawConstantValue();
            if (raw is float fv) { target = fv; return; }
            if (raw is double dv) { target = (float)dv; return; }
        }

        private static string SafeGetSystemPrompt(ITask task)
        {
            try
            {
                return task.GetSystemPrompt();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TrialPlanMarkdownExporter] GetSystemPrompt failed: {ex.Message}");
                return null;
            }
        }

        private static string SafeBuildTaskPrompt(ITask task, TrialSpec trial)
        {
            try
            {
                return task.BuildTaskPrompt(trial);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TrialPlanMarkdownExporter] BuildTaskPrompt failed for trial {trial?.trialId}: {ex.Message}");
                return null;
            }
        }

        private string WriteMarkdownToFile(string resolvedTaskId, int usedSeed, string content)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;
            var dir = string.IsNullOrWhiteSpace(outputDirectory)
                ? projectRoot
                : Path.Combine(projectRoot, outputDirectory);

            Directory.CreateDirectory(dir);

            var safeTaskId = string.IsNullOrWhiteSpace(resolvedTaskId) ? "unknown_task" : resolvedTaskId;
            var fileName = $"{fileNamePrefix}{safeTaskId}_seed_{usedSeed}.md";

            var fullPath = Path.Combine(dir, fileName);
            File.WriteAllText(fullPath, content, Encoding.UTF8);

            return fullPath;
        }
    }
}
