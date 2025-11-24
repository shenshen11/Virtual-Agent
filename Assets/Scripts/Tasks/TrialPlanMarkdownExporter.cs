using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
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
            if (string.IsNullOrWhiteSpace(taskId))
            {
                Debug.LogError("[TrialPlanMarkdownExporter] taskId is empty.");
                return;
            }

            // 构造 TaskRunner 上下文，以便 TaskRegistry 中的任务可以访问必要引用
            var ctx = BuildContext();

            if (!TaskRegistry.Instance.TryCreate(taskId, ctx, out var task))
            {
                // 尝试使用 TaskMode 的名称兼容（例如 DistanceCompression）
                if (!TryCreateBuiltinTask(taskId, ctx, out task))
                {
                    Debug.LogError($"[TrialPlanMarkdownExporter] Failed to create task for taskId='{taskId}'.");
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
                trials = task.BuildTrials(seed) ?? Array.Empty<TrialSpec>();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TrialPlanMarkdownExporter] BuildTrials(seed={seed}) failed: {ex.Message}");
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

            if (maxTrials > 0 && trials.Length > maxTrials)
            {
                Array.Resize(ref trials, maxTrials);
            }

            var scene = SceneManager.GetActiveScene();

            var md = BuildMarkdown(task, trials, seed, scene);

            try
            {
                var fullPath = WriteMarkdownToFile(task.TaskId, seed, md);
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

        private string BuildMarkdown(ITask task, TrialSpec[] trials, int usedSeed, Scene scene)
        {
            var sb = new StringBuilder(4096);

            var sceneName = scene.IsValid() ? scene.name : "(unknown)";
            var scenePath = scene.IsValid() ? scene.path : "(unknown)";

            sb.AppendLine("# Trial Plan");
            sb.AppendLine();
            sb.AppendLine($"- Task: `{task.TaskId}` (requested: `{taskId}`)");
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

