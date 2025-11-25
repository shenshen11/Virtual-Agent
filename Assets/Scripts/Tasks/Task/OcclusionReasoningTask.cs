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
    /// 遮挡推理与计数任务（Occlusion Reasoning & Counting）
    /// - 目标：在部分遮挡条件下判断目标是否存在，并估计可见数量
    /// - 自变量：遮挡率 / 遮挡体类型 / 背景 / 光照 / 目标类别
    /// - 因变量：present(bool) / count(int)
    /// </summary>
    public class OcclusionReasoningTask : ITask
    {
        public string TaskId => "occlusion_reasoning";

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(1234);

        private ExperimentSceneManager _scene;
        private ObjectPlacer _placer;

        public OcclusionReasoningTask(TaskRunnerContext ctx)
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
            // 使用 seed 仅控制 trial 顺序，试次集合本身为固定设计
            _rand = new System.Random(seed);

            var backgrounds = new[] { "none", "indoor", "street" };
            // 遮挡体类型与 VRP_Stimulus_Prefab_Spec 对齐，便于通过 ObjectPlacer 绑定 Prefab
            var occluderTypes = new[] { "occluder_wall", "occluder_plant", "occluder_pedestrian" };
            var occlusionLevels = new[] { 0f, 0.2f, 0.4f, 0.6f, 0.8f };
            // 目标类别与 Stimulus 规范对齐
            var targetCategories = new[] { "apple", "cup", "toy_car", "chair", "human", "count_ball" };

            var trials = new List<TrialSpec>();

            foreach (var bg in backgrounds)
            {
                foreach (var occType in occluderTypes)
                {
                    foreach (var ratio in occlusionLevels)
                    {
                        // 为该组合选择一个目标类别
                        var targetIndex = _rand.Next(targetCategories.Length);
                        string target = targetCategories[targetIndex];

                        // 存在试次：遮挡较低时单个目标，较高时 3 个目标
                        int presentCount = ratio < 0.4f ? 1 : 3;

                        trials.Add(new TrialSpec
                        {
                            taskId = TaskId,
                            environment = "open_field",
                            background = bg,
                            fovDeg = 60f,
                            textureDensity = 1.0f,
                            lighting = BackgroundToLighting(bg),
                            occlusion = ratio > 0f,
                            occlusionRatio = ratio,
                            occluderType = occType,
                            targetCategory = target,
                            targetPresent = true,
                            trueCount = presentCount
                        });

                        // 不存在试次：相同条件，仅移除目标
                        trials.Add(new TrialSpec
                        {
                            taskId = TaskId,
                            environment = "open_field",
                            background = bg,
                            fovDeg = 60f,
                            textureDensity = 1.0f,
                            lighting = BackgroundToLighting(bg),
                            occlusion = ratio > 0f,
                            occlusionRatio = ratio,
                            occluderType = occType,
                            targetCategory = target,
                            targetPresent = false,
                            trueCount = 0
                        });
                    }
                }
            }

            // 使用种子洗牌，保证顺序可复现
            Shuffle(trials);
            return trials.ToArray();
        }

        public string GetSystemPrompt()
        {
            return PromptTemplates.GetSystemPrompt(TaskId);
        }

        public ToolSpec[] GetTools()
        {
            // 允许 head_look_at / turn_yaw / snapshot / focus_target 等工具
            return PromptTemplates.GetToolsForOcclusionReasoning();
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            return PromptTemplates.BuildOcclusionReasoningPrompt(
                trial.targetCategory ?? trial.targetKind,
                trial.occluderType,
                trial.occlusionRatio,
                trial.background,
                trial.fovDeg);
        }

        public async Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            TryBindHelpers();

            // 场景/光照：不启用 ExperimentSceneManager 的默认遮挡体，由本任务自行放置
            if (_scene != null)
            {
                var env = string.IsNullOrEmpty(trial.environment) ? "open_field" : trial.environment;
                var lighting = string.IsNullOrEmpty(trial.lighting)
                    ? BackgroundToLighting(trial.background)
                    : trial.lighting;

                _scene.SetupEnvironment(env, trial.textureDensity, lighting, false);
            }

            // 相机 FOV
            var fov = trial.fovDeg > 0 ? trial.fovDeg : 60f;
            _ctx?.stimulus?.SetCameraFOV(fov);

            // 布置目标与遮挡体
            PlaceTargetsAndOccluder(trial);

            await Task.Yield();
        }

        public async Task OnAfterTrialAsync(TrialSpec trial, LLMResponse response, CancellationToken ct)
        {
            // 清理试次内生成的对象
            if (_placer != null)
            {
                _placer.ClearAll();
            }
            else
            {
                TryDestroyByPrefix("occ_target_");
                TryDestroyByPrefix("occ_occluder_");
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
                truePresent = trial.targetPresent || trial.trueCount > 0,
                trueCount = trial.trueCount
            };

            bool hasPrediction = false;
            bool predictedPresent = false;
            int predictedCount = 0;

            if (response != null && response.type == "inference")
            {
                if (TryExtractFromAnswer(response.answer, out var presentOpt, out var countOpt))
                {
                    hasPrediction = true;
                    if (presentOpt.HasValue) predictedPresent = presentOpt.Value;
                    if (countOpt.HasValue) predictedCount = countOpt.Value;
                }
                else if (TryExtractFromText(response.explanation, out presentOpt, out countOpt))
                {
                    hasPrediction = true;
                    if (presentOpt.HasValue) predictedPresent = presentOpt.Value;
                    if (countOpt.HasValue) predictedCount = countOpt.Value;
                }
            }

            if (!hasPrediction)
            {
                eval.success = false;
                eval.failureReason = "No present/count information found in model output";
                return eval;
            }

            // 若未显式给出存在性，则根据 count 粗略推断
            if (!eval.truePresent && predictedCount <= 0)
            {
                predictedPresent = false;
            }
            else if (predictedCount > 0)
            {
                predictedPresent = true;
            }

            eval.predictedPresent = predictedPresent;
            eval.predictedCount = Mathf.Max(0, predictedCount);

            // 存在性准确率
            eval.isCorrect = (eval.predictedPresent == eval.truePresent);

            // 计数误差：只要给出了 count 就计算 MAE / 相对误差
            if (trial.trueCount >= 0)
            {
                eval.countAbsError = Mathf.Abs(eval.predictedCount - trial.trueCount);
                eval.countRelError = trial.trueCount > 0
                    ? eval.countAbsError / Mathf.Max(1f, trial.trueCount)
                    : 0f;
            }

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

        private void PlaceTargetsAndOccluder(TrialSpec trial)
        {
            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null) return;

            var origin = cam.transform.position;
            var forward = cam.transform.forward;
            var right = cam.transform.right;

            float baseDepth = 8f;
            float y = origin.y + 1.0f;

            var center = origin + forward * baseDepth;
            center.y = y;

            // 放置目标（可见目标个数 = trueCount）
            if (trial.trueCount > 0)
            {
                string kind = !string.IsNullOrEmpty(trial.targetCategory)
                    ? trial.targetCategory
                    : (!string.IsNullOrEmpty(trial.targetKind) ? trial.targetKind : "cube");

                int count = Mathf.Max(1, trial.trueCount);
                float spacing = 0.8f;
                float start = -spacing * (count - 1) * 0.5f;

                for (int i = 0; i < count; i++)
                {
                    var pos = center + right * (start + i * spacing);
                    PlaceTarget(kind, pos, 1.0f, $"occ_target_{i}");
                }
            }

            // 放置遮挡体：遮挡率 0 时作为对照，将遮挡体放在一侧；>0 时位于目标前方
            string occluderType = string.IsNullOrEmpty(trial.occluderType) ? "occluder_wall" : trial.occluderType;
            float ratio = Mathf.Clamp01(trial.occlusionRatio);

            Vector3 occCenter;
            if (ratio <= 0f)
            {
                occCenter = center + right * 3.0f;
            }
            else
            {
                float depthOffset = Mathf.Lerp(0.3f, 1.5f, ratio);
                occCenter = origin + forward * (baseDepth - depthOffset);
                occCenter.y = y;

                // 低遮挡率时轻微水平偏移，避免完全挡住所有目标
                float jitter = Mathf.Lerp(0.2f, 0.0f, ratio);
                occCenter += right * jitter;
            }

            PlaceOccluder(occluderType, occCenter, ratio);
        }

        private void PlaceTarget(string kind, Vector3 position, float scale, string name)
        {
            GameObject go = null;
            if (_placer != null)
            {
                go = _placer.Place(kind, position, scale, null, name);
            }
            else
            {
                go = CreatePrimitiveForKind(kind);
                if (go != null)
                {
                    if (!string.IsNullOrEmpty(name)) go.name = name;
                    go.transform.position = position;
                    go.transform.localScale = Vector3.one * scale;
                }
            }
        }

        private void PlaceOccluder(string occluderType, Vector3 center, float ratio)
        {
            // kind 字段用于 ObjectPlacer 查找 Prefab，优先保持与 occluderType 一致
            string kindKey = occluderType;

            GameObject go = null;
            if (_placer != null)
            {
                go = _placer.Place(kindKey, center, 1.0f, null, $"occ_occluder_{occluderType}");
            }
            else
            {
                go = CreatePrimitiveForKind(kindKey);
                if (go != null)
                {
                    go.name = $"occ_occluder_{occluderType}";
                    go.transform.position = center;
                }
            }

            if (go != null)
            {
                // 粗略用缩放控制遮挡率：ratio 从 0..1 映射到宽和高因子
                float width = Mathf.Lerp(0.4f, 2.0f, ratio);
                float height = Mathf.Lerp(1.5f, 3.0f, ratio);
                float depth = 0.3f;

                go.transform.localScale = new Vector3(width, height, depth);
            }
        }

        private static GameObject CreatePrimitiveForKind(string kind)
        {
            var k = (kind ?? "cube").ToLowerInvariant();
            switch (k)
            {
                case "cube": return GameObject.CreatePrimitive(PrimitiveType.Cube);
                case "sphere": return GameObject.CreatePrimitive(PrimitiveType.Sphere);
                case "human":
                case "capsule": return GameObject.CreatePrimitive(PrimitiveType.Capsule);
                case "cylinder": return GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                case "quad": return GameObject.CreatePrimitive(PrimitiveType.Quad);
                case "plane": return GameObject.CreatePrimitive(PrimitiveType.Plane);
                // 语义对象简化到基础 Primitive
                case "toy_car": return GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                case "apple": return GameObject.CreatePrimitive(PrimitiveType.Sphere);
                case "cup": return GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                case "chair": return GameObject.CreatePrimitive(PrimitiveType.Cube);
                case "count_ball": return GameObject.CreatePrimitive(PrimitiveType.Sphere);
                default:
                    // 遮挡体 kind（如 occluder_wall/plant/pedestrian/fence）按语义近似
                    if (k.Contains("plant")) return GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    if (k.Contains("pedestrian") || k.Contains("human")) return GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    if (k.Contains("wall") || k.Contains("fence")) return GameObject.CreatePrimitive(PrimitiveType.Cube);
                    return GameObject.CreatePrimitive(PrimitiveType.Cube);
            }
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

        private static bool TryExtractFromAnswer(object answer, out bool? present, out int? count)
        {
            present = null;
            count = null;
            if (answer == null) return false;

            try
            {
                // 1) 反射属性 present / count
                var t = answer.GetType();
                var presentProp = t.GetProperty("present", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                var countProp = t.GetProperty("count", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

                bool? presentVal = null;
                int? countVal = null;

                if (presentProp != null && TryToBool(presentProp.GetValue(answer), out var pb))
                {
                    presentVal = pb;
                }

                if (countProp != null && TryToInt(countProp.GetValue(answer), out var ci))
                {
                    countVal = ci;
                }

                if (presentVal.HasValue || countVal.HasValue)
                {
                    present = presentVal;
                    count = countVal;
                    return true;
                }

                // 2) JSON 序列化路径
                var json = JsonUtility.ToJson(answer);
                if (!string.IsNullOrEmpty(json))
                {
                    var parsed = JsonUtility.FromJson<OcclusionAnswer>(json);
                    if (parsed != null)
                    {
                        present = parsed.present;
                        count = parsed.count;
                        return true;
                    }
                }
            }
            catch
            {
                // ignore，后续尝试 ToString
            }

            // 3) ToString 粗提取
            try
            {
                var s = answer.ToString();
                return TryExtractFromString(s, out present, out count);
            }
            catch
            {
                // ignore
            }

            return false;
        }

        private static bool TryExtractFromText(string text, out bool? present, out int? count)
        {
            present = null;
            count = null;
            if (string.IsNullOrEmpty(text)) return false;
            return TryExtractFromString(text, out present, out count);
        }

        private static bool TryExtractFromString(string text, out bool? present, out int? count)
        {
            present = null;
            count = null;
            if (string.IsNullOrEmpty(text)) return false;

            // 提取 present: true/false 或关键字
            var mPresent = Regex.Match(text, @"present[^A-Za-z0-9]*(true|false)", RegexOptions.IgnoreCase);
            if (mPresent.Success && bool.TryParse(mPresent.Groups[1].Value, out var pb))
            {
                present = pb;
            }
            else
            {
                if (Regex.IsMatch(text, @"no\s+target|none|absent", RegexOptions.IgnoreCase))
                {
                    present = false;
                }
                else if (Regex.IsMatch(text, @"present|visible|exists", RegexOptions.IgnoreCase))
                {
                    present = true;
                }
            }

            // 提取 count：优先匹配 "count" 字段，其次任意整数
            var mCount = Regex.Match(text, @"count[^0-9\-]*([-+]?\d+)", RegexOptions.IgnoreCase);
            if (!mCount.Success)
            {
                mCount = Regex.Match(text, @"([-+]?\d+)");
            }

            if (mCount.Success && int.TryParse(mCount.Groups[1].Value, out var ci))
            {
                count = ci;
            }

            return present.HasValue || count.HasValue;
        }

        private static bool TryToBool(object v, out bool b)
        {
            b = false;
            if (v == null) return false;

            switch (v)
            {
                case bool bv:
                    b = bv;
                    return true;
                case string sv when bool.TryParse(sv, out var parsed):
                    b = parsed;
                    return true;
                default:
                    return false;
            }
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

        [Serializable]
        private class OcclusionAnswer
        {
            public bool present;
            public int count;
            public float confidence;
        }
    }
}
