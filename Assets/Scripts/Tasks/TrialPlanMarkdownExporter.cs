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

                // Distance Compression 专用字段
                if (!string.IsNullOrWhiteSpace(t.targetKind) || t.trueDistanceM > 0f)
                {
                    if (!string.IsNullOrWhiteSpace(t.targetKind))
                        sb.AppendLine($"- targetKind: `{t.targetKind}`");
                    sb.AppendLine($"- trueDistanceM: `{t.trueDistanceM}`");
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
                    if (!string.IsNullOrWhiteSpace(t.background))
                        sb.AppendLine($"- background: `{t.background}`");
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
