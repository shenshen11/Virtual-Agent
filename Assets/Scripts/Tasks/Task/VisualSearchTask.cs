using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;

namespace VRPerception.Tasks
{
    /// <summary>
    /// 杂乱视觉搜索任务（Visual Search in Clutter）
    /// - 目标：在干扰项中判断目标是否存在，可选返回目标名称
    /// - 核心自变量：set size / similarityLevel / background / targetPresent
    /// - 对应 VR_Perception_Scenarios_10 的场景 7
    /// </summary>
    public class VisualSearchTask : ITask
    {
        public string TaskId => "visual_search";

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(1234);
        private int _seed = 1234;

        private ExperimentSceneManager _scene;
        private ObjectPlacer _placer;

        public VisualSearchTask(TaskRunnerContext ctx)
        {
            _ctx = ctx;
            TryBindHelpers();
        }

        public void Initialize(TaskRunner runner, EventBusManager eventBus)
        {
            if (_ctx == null)
            {
                _ctx = new TaskRunnerContext
                {
                    runner = runner,
                    eventBus = eventBus,
                    perception = runner ? runner.GetComponent<PerceptionSystem>() : null,
                    stimulus = runner ? runner.GetComponent<StimulusCapture>() : null
                };
            }

            TryBindHelpers();
        }

        public TrialSpec[] BuildTrials(int seed)
        {
            _seed = seed;
            _rand = new System.Random(seed);

            var setSizes = new[] { 4, 8, 16 };
            var similarities = new[] { "easy", "hard" };
            var backgrounds = new[] { "none", "indoor" };
            var presentOptions = new[] { true, false };

            var trials = new List<TrialSpec>();

            foreach (var n in setSizes)
            foreach (var sim in similarities)
            foreach (var bg in backgrounds)
            foreach (var present in presentOptions)
            {
                for (int rep = 0; rep < 2; rep++)
                {
                    var distractor = sim == "easy" ? "blue_cup" : "green_cup";

                    trials.Add(new TrialSpec
                    {
                        taskId = TaskId,
                        environment = "open_field",
                        background = bg,
                        textureDensity = 1.0f,
                        lighting = BackgroundToLighting(bg),
                        occlusion = false,
                        fovDeg = 60f,

                        targetCategory = "red_cup",
                        targetPresent = present,
                        targetCount = present ? 1 : 0,
                        trueCount = present ? 1 : 0,

                        setSize = n,
                        distractorCategory = distractor,
                        similarityLevel = sim
                    });
                }
            }

            Shuffle(trials);
            return trials.ToArray();
        }

        public string GetSystemPrompt()
        {
            return PromptTemplates.GetSystemPrompt(TaskId);
        }

        public ToolSpec[] GetTools()
        {
            return PromptTemplates.GetToolsForVisualSearch();
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            return PromptTemplates.BuildVisualSearchPrompt(
                trial.targetCategory,
                trial.distractorCategory,
                trial.setSize,
                trial.similarityLevel,
                trial.background,
                trial.fovDeg);
        }

        public async Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            TryBindHelpers();

            if (_scene != null)
            {
                var env = string.IsNullOrEmpty(trial.environment) ? "open_field" : trial.environment;
                var lighting = string.IsNullOrEmpty(trial.lighting)
                    ? BackgroundToLighting(trial.background)
                    : trial.lighting;

                _scene.SetupEnvironment(env, trial.textureDensity, lighting, trial.occlusion);
            }

            var fov = trial.fovDeg > 0 ? trial.fovDeg : 60f;
            _ctx?.stimulus?.SetCameraFOV(fov);

            PlaceSearchLayout(trial);

            await Task.Yield();
        }

        public async Task OnAfterTrialAsync(TrialSpec trial, LLMResponse response, CancellationToken ct)
        {
            if (_placer != null)
            {
                _placer.ClearAll();
            }
            else
            {
                TryDestroyByPrefix("vs_");
            }

            await Task.Yield();
        }

        public TrialEvaluation Evaluate(TrialSpec trial, LLMResponse response)
        {
            var eval = new TrialEvaluation
            {
                responseType = response?.type,
                providerId = response?.providerId,
                latencyMs = response?.latencyMs ?? 0,
                confidence = response?.confidence ?? 0,
                truePresent = trial.targetPresent,
                trueCount = trial.targetCount > 0 ? trial.targetCount : (trial.targetPresent ? 1 : 0)
            };

            bool hasPrediction = false;
            bool predictedFound = false;
            string predictedTarget = null;

            if (response != null && response.type == "inference")
            {
                if (TryExtractFromAnswer(response.answer, out var foundOpt, out var targetOpt))
                {
                    hasPrediction = true;
                    if (foundOpt.HasValue) predictedFound = foundOpt.Value;
                    predictedTarget = targetOpt ?? predictedTarget;
                }
                else if (TryExtractFromText(response.explanation, out foundOpt, out targetOpt))
                {
                    hasPrediction = true;
                    if (foundOpt.HasValue) predictedFound = foundOpt.Value;
                    predictedTarget = targetOpt ?? predictedTarget;
                }
            }

            if (!hasPrediction)
            {
                eval.success = false;
                eval.failureReason = "No found/target information found in model output";
                return eval;
            }

            eval.predictedFound = predictedFound;
            eval.predictedTarget = predictedTarget;

            var truthTarget = string.IsNullOrEmpty(trial.targetCategory) ? "red_cup" : trial.targetCategory;
            bool foundCorrect = predictedFound == eval.truePresent;
            bool targetCorrect = true;
            if (!string.IsNullOrEmpty(predictedTarget))
            {
                targetCorrect = string.Equals(predictedTarget, truthTarget, StringComparison.OrdinalIgnoreCase);
            }

            eval.isCorrect = foundCorrect && targetCorrect;
            eval.success = true;
            return eval;
        }

        // ===== Helpers =====

        private void TryBindHelpers()
        {
            if (_ctx?.runner != null)
            {
                if (_scene == null) _scene = _ctx.runner.GetComponent<ExperimentSceneManager>();
                if (_placer == null) _placer = _ctx.runner.GetComponent<ObjectPlacer>();
            }

            if (_scene == null) _scene = UnityEngine.Object.FindObjectOfType<ExperimentSceneManager>();
            if (_placer == null) _placer = UnityEngine.Object.FindObjectOfType<ObjectPlacer>();
        }

        private static string BackgroundToLighting(string background)
        {
            var bg = (background ?? "none").ToLowerInvariant();
            return bg switch
            {
                "indoor" => "dim",
                "street" => "hdr",
                _ => "bright"
            };
        }

        private void Shuffle<T>(IList<T> list)
        {
            if (list == null || list.Count <= 1) return;

            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rand.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private void PlaceSearchLayout(TrialSpec trial)
        {
            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null) return;

            int count = trial.setSize > 0 ? trial.setSize : 8;
            int targetCount = trial.targetPresent ? Math.Max(1, trial.targetCount) : 0;
            targetCount = Mathf.Clamp(targetCount, 0, count);

            var origin = cam.transform.position;
            var forward = cam.transform.forward;
            var right = cam.transform.right;

            float baseDepth = 6.0f;
            float radius = count <= 4 ? 3.0f : (count <= 8 ? 4.2f : 5.2f);
            float minSpacing = 0.7f;

            // 复现性：基于 seed 与 trialId 的独立随机源
            var rand = new System.Random(_seed + trial.trialId * 7919 + 17);

            var positions = new List<Vector3>();
            int maxAttempts = count * 40;
            int attempts = 0;

            while (positions.Count < count && attempts < maxAttempts)
            {
                attempts++;

                float angle = Mathf.Lerp(-55f, 55f, (float)rand.NextDouble());
                float dist = Mathf.Lerp(radius * 0.45f, radius, (float)rand.NextDouble());
                float lateral = Mathf.Lerp(-radius * 0.35f, radius * 0.35f, (float)rand.NextDouble());

                var dir = Quaternion.AngleAxis(angle, Vector3.up) * forward;
                var pos = origin + dir * (baseDepth + dist * 0.2f) + right * lateral;
                pos.y = origin.y + 0.8f;

                bool ok = true;
                for (int i = 0; i < positions.Count; i++)
                {
                    if (Vector3.Distance(positions[i], pos) < minSpacing)
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok) positions.Add(pos);
            }

            // 回退：若采样不足，用小网格填充
            if (positions.Count < count)
            {
                for (int i = positions.Count; i < count; i++)
                {
                    float offsetX = (i % 5 - 2) * 0.6f;
                    float offsetZ = (i / 5) * 0.5f;
                    var pos = origin + forward * (baseDepth + 1.0f + offsetZ) + right * offsetX;
                    pos.y = origin.y + 0.8f;
                    positions.Add(pos);
                }
            }

            // 随机选定目标索引
            var targetIndices = new HashSet<int>();
            while (targetIndices.Count < targetCount && targetIndices.Count < positions.Count)
            {
                targetIndices.Add(rand.Next(positions.Count));
            }

            for (int i = 0; i < positions.Count; i++)
            {
                bool isTarget = targetIndices.Contains(i);
                string kind = isTarget ? GetTargetKind(trial) : GetDistractorKind(trial, rand);
                float scale = 1.0f;
                string name = isTarget ? $"vs_target_{i}" : $"vs_dist_{i}";

                PlaceObject(kind, positions[i], scale, name);
            }
        }

        private string GetTargetKind(TrialSpec trial)
        {
            return string.IsNullOrEmpty(trial.targetCategory) ? "red_cup" : trial.targetCategory;
        }

        private string GetDistractorKind(TrialSpec trial, System.Random rand)
        {
            var primary = string.IsNullOrEmpty(trial.distractorCategory) ? "blue_cup" : trial.distractorCategory;

            // 为大 set size 注入少量几何噪声，增加杂乱度
            if (trial.setSize >= 12)
            {
                double r = rand.NextDouble();
                if (r < 0.15) return "cube";
                if (r < 0.30) return "sphere";
            }

            return primary;
        }

        private void PlaceObject(string kind, Vector3 position, float scale, string name)
        {
            if (_placer != null)
            {
                _placer.Place(kind, position, scale, null, name);
                return;
            }

            var go = CreatePrimitiveForKind(kind);
            if (go == null) return;

            if (!string.IsNullOrEmpty(name)) go.name = name;
            go.transform.position = position;
            go.transform.localScale = Vector3.one * scale;
        }

        private static GameObject CreatePrimitiveForKind(string kind)
        {
            var k = (kind ?? "cube").ToLowerInvariant();
            return k switch
            {
                "cube" => GameObject.CreatePrimitive(PrimitiveType.Cube),
                "sphere" => GameObject.CreatePrimitive(PrimitiveType.Sphere),
                "cylinder" => GameObject.CreatePrimitive(PrimitiveType.Cylinder),
                "capsule" => GameObject.CreatePrimitive(PrimitiveType.Capsule),
                "human" => GameObject.CreatePrimitive(PrimitiveType.Capsule),
                "quad" => GameObject.CreatePrimitive(PrimitiveType.Quad),
                _ => GameObject.CreatePrimitive(PrimitiveType.Cube)
            };
        }

        private static void TryDestroyByPrefix(string prefix)
        {
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<GameObject>();
                foreach (var go in all)
                {
                    if (go == null) continue;
                    if (!go.name.StartsWith(prefix, StringComparison.Ordinal)) continue;
#if UNITY_EDITOR
                    UnityEngine.Object.DestroyImmediate(go);
#else
                    UnityEngine.Object.Destroy(go);
#endif
                }
            }
            catch
            {
                // ignore
            }
        }

        private static bool TryExtractFromAnswer(object answer, out bool? found, out string target)
        {
            found = null;
            target = null;
            if (answer == null) return false;

            try
            {
                var t = answer.GetType();

                var foundProp = t.GetProperty("found", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                var presentProp = t.GetProperty("present", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                var targetProp = t.GetProperty("target", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                var targetNameProp = t.GetProperty("targetName", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

                bool? foundVal = null;
                if (foundProp != null && TryToBool(foundProp.GetValue(answer), out var f)) foundVal = f;
                else if (presentProp != null && TryToBool(presentProp.GetValue(answer), out var p)) foundVal = p;

                string targetVal = null;
                if (targetProp != null && TryToString(targetProp.GetValue(answer), out var ts)) targetVal = ts;
                else if (targetNameProp != null && TryToString(targetNameProp.GetValue(answer), out var tn)) targetVal = tn;

                if (foundVal.HasValue || !string.IsNullOrEmpty(targetVal))
                {
                    found = foundVal;
                    target = targetVal;
                    return true;
                }
            }
            catch
            {
                // ignore reflection errors
            }

            try
            {
                var s = answer.ToString();
                return TryExtractFromString(s, out found, out target);
            }
            catch
            {
                // ignore
            }

            return false;
        }

        private static bool TryExtractFromText(string text, out bool? found, out string target)
        {
            found = null;
            target = null;
            if (string.IsNullOrEmpty(text)) return false;
            return TryExtractFromString(text, out found, out target);
        }

        private static bool TryExtractFromString(string text, out bool? found, out string target)
        {
            found = null;
            target = null;
            if (string.IsNullOrEmpty(text)) return false;

            var mBool = Regex.Match(text, @"\b(found|present|exists)\b[^A-Za-z0-9]*(true|false)", RegexOptions.IgnoreCase);
            if (mBool.Success && bool.TryParse(mBool.Groups[2].Value, out var b))
            {
                found = b;
            }

            var mTarget = Regex.Match(text, @"target[^A-Za-z0-9]*(name)?[^A-Za-z0-9]*[:=]?\s*([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase);
            if (mTarget.Success && mTarget.Groups.Count >= 3)
            {
                target = mTarget.Groups[2].Value;
            }

            if (!found.HasValue)
            {
                if (Regex.IsMatch(text, @"\b(no target|not found|absent|none)\b", RegexOptions.IgnoreCase))
                {
                    found = false;
                }
                else if (Regex.IsMatch(text, @"\b(found|detected|present)\b", RegexOptions.IgnoreCase))
                {
                    found = true;
                }
            }

            return found.HasValue || !string.IsNullOrEmpty(target);
        }

        private static bool TryToBool(object value, out bool result)
        {
            if (value == null)
            {
                result = false;
                return false;
            }

            if (value is bool b)
            {
                result = b;
                return true;
            }

            if (bool.TryParse(value.ToString(), out result))
            {
                return true;
            }

            if (value is int i)
            {
                result = i != 0;
                return true;
            }

            return false;
        }

        private static bool TryToString(object value, out string result)
        {
            if (value == null)
            {
                result = null;
                return false;
            }

            result = value.ToString();
            return !string.IsNullOrEmpty(result);
        }
    }
}
