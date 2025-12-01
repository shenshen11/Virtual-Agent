using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;

namespace VRPerception.Tasks
{
    /// <summary>
    /// 数量估计任务（场景 9：Counting & Density Estimation，当前实现 Counting 模式）。
    /// - 目标：估计视野内同类对象的数量。
    /// - 自变量：trueCount / layoutPattern / background / occlusionRatio。
    /// - 因变量：count（int）。
    /// </summary>
    public class ObjectCountingTask : ITask
    {
        public string TaskId => "object_counting";

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(1234);
        private int _seed = 1234;

        private ExperimentSceneManager _scene;
        private ObjectPlacer _placer;

        public ObjectCountingTask(TaskRunnerContext ctx)
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

            var trials = new List<TrialSpec>();

            int[] countsNoOcc = { 1, 2, 4, 8, 16, 32 };
            string[] layouts = { "grid", "random" };

            foreach (var c in countsNoOcc)
            foreach (var layout in layouts)
            {
                for (int rep = 0; rep < 2; rep++)
                {
                    trials.Add(new TrialSpec
                    {
                        taskId = TaskId,
                        environment = "open_field",
                        background = "none",
                        textureDensity = 1.0f,
                        lighting = "bright",
                        occlusion = false,
                        occlusionRatio = 0f,
                        fovDeg = 60f,

                        targetCategory = "count_ball",
                        trueCount = c,
                        countingMode = "count",
                        layoutPattern = layout,
                        areaSize = EstimateAreaSize(c, layout)
                    });
                }
            }

            int[] countsOcc = { 2, 4, 8, 16 };
            foreach (var c in countsOcc)
            {
                for (int rep = 0; rep < 2; rep++)
                {
                    trials.Add(new TrialSpec
                    {
                        taskId = TaskId,
                        environment = "open_field",
                        background = "indoor",
                        textureDensity = 1.0f,
                        lighting = BackgroundToLighting("indoor"),
                        occlusion = true,
                        occlusionRatio = 0.4f,
                        fovDeg = 60f,

                        targetCategory = "count_ball",
                        trueCount = c,
                        countingMode = "count",
                        layoutPattern = "random",
                        areaSize = EstimateAreaSize(c, "random")
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
            return PromptTemplates.GetToolsForObjectCounting();
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            return PromptTemplates.BuildObjectCountingPrompt(
                trial.targetCategory,
                trial.layoutPattern,
                trial.occlusionRatio,
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

                // 任务自行放置遮挡体，因此不启用 SceneManager 内建遮挡
                _scene.SetupEnvironment(env, trial.textureDensity, lighting, false);
            }

            var fov = trial.fovDeg > 0 ? trial.fovDeg : 60f;
            _ctx?.stimulus?.SetCameraFOV(fov);

            PlaceCountingLayout(trial);

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
                TryDestroyByPrefix("cnt_obj_");
                TryDestroyByPrefix("cnt_occ_");
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
                trueCount = trial.trueCount
            };

            if (response == null)
            {
                eval.success = false;
                eval.failureReason = "No response";
                return eval;
            }

            if (response.type != "inference")
            {
                eval.success = false;
                eval.failureReason = "Expected inference but got action_plan";
                return eval;
            }

            bool hasPrediction = false;
            int predictedCount = 0;

            if (response.answer != null && TryExtractCountFromAnswer(response.answer, out var c1))
            {
                hasPrediction = true;
                predictedCount = c1;
            }
            else if (TryExtractCountFromText(response.explanation, out var c2))
            {
                hasPrediction = true;
                predictedCount = c2;
            }

            if (!hasPrediction)
            {
                eval.success = false;
                eval.failureReason = "No count found in model output";
                return eval;
            }

            eval.predictedCount = Mathf.Max(0, predictedCount);
            eval.countAbsError = Mathf.Abs(eval.predictedCount - trial.trueCount);
            eval.countRelError = trial.trueCount > 0
                ? eval.countAbsError / Mathf.Max(1f, trial.trueCount)
                : 0f;
            eval.isCorrect = eval.predictedCount == trial.trueCount;
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

        private float EstimateAreaSize(int count, string layout)
        {
            if (count <= 4) return 2.5f;
            if (count <= 8) return 3.5f;
            if (count <= 16) return layout == "grid" ? 4.5f : 5.0f;
            return 5.5f;
        }

        private void PlaceCountingLayout(TrialSpec trial)
        {
            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null) return;

            var origin = cam.transform.position;
            var forward = cam.transform.forward;
            var right = cam.transform.right;

            int count = Mathf.Max(0, trial.trueCount);
            float area = trial.areaSize > 0 ? trial.areaSize : EstimateAreaSize(count, trial.layoutPattern);
            float baseDepth = 6.0f;
            float y = origin.y + 0.8f;
            var center = origin + forward * baseDepth;
            center.y = y;

            var positions = trial.layoutPattern == "grid"
                ? BuildGridPositions(center, right, forward, count, area)
                : BuildRandomPositions(center, right, forward, count, area, trial.trialId);

            string kind = string.IsNullOrEmpty(trial.targetCategory) ? "count_ball" : trial.targetCategory;
            for (int i = 0; i < positions.Count; i++)
            {
                PlaceObject(kind, positions[i], 1.0f, $"cnt_obj_{i}");
            }

            if (trial.occlusionRatio > 0f)
            {
                PlaceOccluders(center, forward, right, y, trial.occlusionRatio);
            }
        }

        private List<Vector3> BuildGridPositions(Vector3 center, Vector3 right, Vector3 forward, int count, float area)
        {
            var list = new List<Vector3>(Mathf.Max(1, count));
            if (count <= 0) return list;

            int side = Mathf.CeilToInt(Mathf.Sqrt(count));
            float spacing = Mathf.Max(0.6f, (area * 2f) / Mathf.Max(1, side));
            float start = -spacing * (side - 1) * 0.5f;

            var planeForward = Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
            if (planeForward.sqrMagnitude < 1e-4f) planeForward = Vector3.forward;

            int placed = 0;
            for (int r = 0; r < side && placed < count; r++)
            {
                for (int c = 0; c < side && placed < count; c++)
                {
                    var offset = right * (start + c * spacing) + planeForward * (start + r * spacing);
                    var pos = center + offset;
                    pos.y = center.y;
                    list.Add(pos);
                    placed++;
                }
            }

            return list;
        }

        private List<Vector3> BuildRandomPositions(Vector3 center, Vector3 right, Vector3 forward, int count, float area, int trialId)
        {
            var list = new List<Vector3>(Mathf.Max(1, count));
            if (count <= 0) return list;

            float radius = Mathf.Max(1.0f, area);
            float minSpacing = 0.6f;

            var rand = new System.Random(_seed + trialId * 7919 + 23);
            int attempts = 0;
            int maxAttempts = count * 50;

            var planeForward = Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
            if (planeForward.sqrMagnitude < 1e-4f) planeForward = Vector3.forward;

            while (list.Count < count && attempts < maxAttempts)
            {
                attempts++;
                float angle = (float)(rand.NextDouble() * Math.PI * 2.0);
                float dist = Mathf.Sqrt((float)rand.NextDouble()) * radius; // 均匀分布在圆面

                var offset = (float)Math.Cos(angle) * right * dist + (float)Math.Sin(angle) * planeForward * dist;
                var pos = center + offset;
                pos.y = center.y;

                bool ok = true;
                for (int i = 0; i < list.Count; i++)
                {
                    if (Vector3.Distance(list[i], pos) < minSpacing)
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    list.Add(pos);
                }
            }

            // 回退：若采样不足，填充简单圆环
            for (int i = list.Count; i < count; i++)
            {
                float t = (float)i / Mathf.Max(1, count);
                float ang = t * Mathf.PI * 2f;
                var pos = center + right * (Mathf.Cos(ang) * radius * 0.6f) + forward * (Mathf.Sin(ang) * radius * 0.4f);
                pos.y = center.y;
                list.Add(pos);
            }

            return list;
        }

        private void PlaceOccluders(Vector3 center, Vector3 forward, Vector3 right, float y, float occlusionRatio)
        {
            int occCount = occlusionRatio > 0.3f ? 2 : 1;
            float width = Mathf.Lerp(0.8f, 2.4f, Mathf.Clamp01(occlusionRatio));
            float height = Mathf.Lerp(1.2f, 2.4f, Mathf.Clamp01(occlusionRatio));

            for (int i = 0; i < occCount; i++)
            {
                float lateral = (i == 0) ? -0.4f : 0.6f;
                var pos = center - forward * 1.2f + right * lateral;
                pos.y = y;

                var go = PlaceObject("occluder_wall", pos, 1.0f, $"cnt_occ_{i}");
                if (go != null)
                {
                    go.transform.localScale = new Vector3(width, height, 0.35f);
                }
            }
        }

        private GameObject PlaceObject(string kind, Vector3 position, float scale, string name)
        {
            if (_placer != null)
            {
                return _placer.Place(kind, position, scale, null, name);
            }

            var go = CreatePrimitiveForKind(kind);
            if (go == null) return null;

            if (!string.IsNullOrEmpty(name)) go.name = name;
            go.transform.position = position;
            go.transform.localScale = Vector3.one * scale;
            return go;
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
                "count_ball" => GameObject.CreatePrimitive(PrimitiveType.Sphere),
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

        private static bool TryExtractCountFromAnswer(object answer, out int count)
        {
            count = 0;
            if (answer == null) return false;

            try
            {
                var t = answer.GetType();
                var countProp = t.GetProperty("count", System.Reflection.BindingFlags.IgnoreCase | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (countProp != null && TryToInt(countProp.GetValue(answer), out var ci))
                {
                    count = ci;
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
                return TryExtractCountFromString(s, out count);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryExtractCountFromText(string text, out int count)
        {
            return TryExtractCountFromString(text, out count);
        }

        private static bool TryExtractCountFromString(string text, out int count)
        {
            count = 0;
            if (string.IsNullOrEmpty(text)) return false;

            var mCount = Regex.Match(text, @"count[^0-9\-]*([-+]?\d+)", RegexOptions.IgnoreCase);
            if (!mCount.Success)
            {
                mCount = Regex.Match(text, @"([-+]?\d+)");
            }

            if (mCount.Success && int.TryParse(mCount.Groups[1].Value, out var ci))
            {
                count = ci;
                return true;
            }

            return false;
        }

        private static bool TryToInt(object v, out int i)
        {
            i = 0;
            if (v == null) return false;

            switch (v)
            {
                case int iv:
                    i = iv;
                    return true;
                case long lv:
                    i = (int)lv;
                    return true;
                case float fv:
                    i = Mathf.RoundToInt(fv);
                    return true;
                case double dv:
                    i = (int)Math.Round(dv);
                    return true;
                case string sv when int.TryParse(sv, out var parsed):
                    i = parsed;
                    return true;
                default:
                    return false;
            }
        }
    }
}
