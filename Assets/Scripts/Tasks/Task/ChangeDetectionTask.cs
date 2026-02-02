using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VRPerception.Perception;

namespace VRPerception.Tasks
{
    /// <summary>
    /// 变化检测任务（Change Detection）
    /// - 同一帧中展示场景 A/B：左侧为 A（before），右侧为 B（after）
    /// - 目标：判断是否发生变化，并给出变化类别（appearance/disappearance/movement/replacement/none）
    /// - 对应文档场景 8：变化检测（Change Detection）
    /// </summary>
    public class ChangeDetectionTask : ITask
    {
        public string TaskId => "change_detection";

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(1234);

        private ExperimentSceneManager _scene;
        private ObjectPlacer _placer;

        // Phase 1: 高对比度颜色池，用于区分不同物体
        private static readonly Color[] s_objectColors = new Color[]
        {
            new Color(1.0f, 0.2f, 0.2f),   // 红色
            new Color(0.2f, 0.5f, 1.0f),   // 蓝色
            new Color(0.2f, 0.9f, 0.3f),   // 绿色
            new Color(1.0f, 0.9f, 0.2f),   // 黄色
            new Color(0.7f, 0.2f, 0.9f),   // 紫色
            new Color(1.0f, 0.6f, 0.1f),   // 橙色
        };

        public ChangeDetectionTask(TaskRunnerContext ctx)
        {
            _ctx = ctx;
            TryBindHelpers();
        }

        public void Initialize(TaskRunner runner, VRPerception.Infra.EventBus.EventBusManager eventBus)
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
            _rand = new System.Random(seed);

            var backgrounds = new[] { "none", "indoor", "street" };
            var fovs = new[] { 60f };
            var categories = new[] { "none", "appearance", "disappearance", "movement", "replacement" };
            var textures = new[] { 0.5f, 1.0f, 1.5f };

            var trials = new List<TrialSpec>();
            int texIndex = 0;

            // 简单均衡设计：背景 × FOV × 变化类别
            foreach (var bg in backgrounds)
            {
                foreach (var fov in fovs)
                {
                    foreach (var cat in categories)
                    {
                        bool changed = !string.Equals(cat, "none", StringComparison.OrdinalIgnoreCase);
                        var lighting = BackgroundToLighting(bg);
                        var tex = textures[texIndex % textures.Length];
                        texIndex++;

                        trials.Add(new TrialSpec
                        {
                            taskId = TaskId,
                            environment = "open_field",
                            background = bg,
                            fovDeg = fov,
                            textureDensity = tex,
                            lighting = lighting,
                            occlusion = false,
                            changed = changed,
                            changeCategory = cat
                        });
                    }
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
            // 可根据需要启用动作闭环（snapshot/head_look_at/focus_target）
            return PromptTemplates.GetToolsForChangeDetection();
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            return PromptTemplates.BuildChangeDetectionPrompt(trial.background, trial.fovDeg, trial.trialId);
        }

        public async Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            TryBindHelpers();

            // 场景与光照
            if (_scene != null)
            {
                var env = string.IsNullOrEmpty(trial.environment) ? "open_field" : trial.environment;
                var lighting = string.IsNullOrEmpty(trial.lighting)
                    ? BackgroundToLighting(trial.background)
                    : trial.lighting;

                _scene.SetupEnvironment(env, trial.textureDensity, lighting, trial.occlusion);
            }

            // 相机 FOV
            var fov = trial.fovDeg > 0 ? trial.fovDeg : 60f;
            _ctx?.stimulus?.SetCameraFOV(fov);

            // 同一帧中布置 A/B 两个子场景（左右并排）
            PlaceChangeScene(trial);

            // 等待渲染完成（关键修复：确保物体完全渲染后再抓帧）
            await WaitForRenderingComplete();
        }

        /// <summary>
        /// 等待渲染完成
        /// 确保物体完全渲染后再进行抓帧操作
        /// </summary>
        private async System.Threading.Tasks.Task WaitForRenderingComplete()
        {
            // 等待至少5帧，确保物体完全渲染
            for (int i = 0; i < 5; i++)
            {
                await System.Threading.Tasks.Task.Yield();
            }
        }

        public async Task OnAfterTrialAsync(TrialSpec trial, LLMResponse response, CancellationToken ct)
        {
            if (_placer != null)
            {
                _placer.ClearAll();
            }
            else
            {
                TryDestroyByPrefix("cd_A_");
                TryDestroyByPrefix("cd_B_");
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
                trueChanged = trial.changed,
                trueChangeCategory = string.IsNullOrEmpty(trial.changeCategory) ? "none" : trial.changeCategory
            };

            bool hasPrediction = false;
            bool predictedChanged = false;
            string predictedCategory = null;

            if (response != null && response.type == "inference")
            {
                if (TryExtractChangeFromAnswer(response.answer, out var ch, out var cat))
                {
                    hasPrediction = true;
                    predictedChanged = ch;
                    predictedCategory = cat;
                }
                else if (TryExtractChangeFromText(response.explanation, out ch, out cat))
                {
                    hasPrediction = true;
                    predictedChanged = ch;
                    predictedCategory = cat;
                }
            }

            if (hasPrediction)
            {
                eval.predictedChanged = predictedChanged;
                eval.predictedChangeCategory = string.IsNullOrEmpty(predictedCategory) ? "none" : predictedCategory;

                // 正确性：优先比较 changed；若 changed=true 再比较类别
                if (!eval.trueChanged)
                {
                    eval.isCorrect = !eval.predictedChanged;
                }
                else
                {
                    eval.isCorrect = eval.predictedChanged &&
                                     string.Equals(eval.predictedChangeCategory, eval.trueChangeCategory,
                                         StringComparison.OrdinalIgnoreCase);
                }

                eval.success = true;
            }
            else
            {
                eval.success = false;
                eval.failureReason = "No changed/category information found in model output";
            }

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

        private void PlaceChangeScene(TrialSpec trial)
        {
            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null) return;

            var origin = cam.transform.position;
            var forward = cam.transform.forward;
            var right = cam.transform.right;

            float baseDepth = 8f;
            float clusterOffsetX = 2.5f;
            float y = origin.y;

            var centerA = origin + forward * baseDepth - right * clusterOffsetX;
            var centerB = origin + forward * baseDepth + right * clusterOffsetX;
            centerA.y = y;
            centerB.y = y;

            // 基准局部位置与形状（A 场景）
            var baseLocals = new[]
            {
                new Vector3(-0.6f, 0f, 0f),
                new Vector3(0.6f, 0f, 0f),
                new Vector3(0f, 0f, 0.8f)
            };
            var baseKinds = new[] { "cube", "cube", "cube" };

            // A：始终使用基准配置
            PlaceCluster("cd_A_", centerA, baseLocals, baseKinds, baseLocals.Length, forward, right, y);

            // B：根据变化类别修改
            string cat = string.IsNullOrEmpty(trial.changeCategory)
                ? "none"
                : trial.changeCategory.ToLowerInvariant();

            var localsB = (Vector3[])baseLocals.Clone();
            var kindsB = (string[])baseKinds.Clone();
            int countB = baseLocals.Length;

            switch (cat)
            {
                case "appearance":
                    // 在 B 中新增一个物体
                    localsB = new[]
                    {
                        baseLocals[0],
                        baseLocals[1],
                        baseLocals[2],
                        new Vector3(0f, 0f, -0.8f)
                    };
                    kindsB = new[] { "cube", "cube", "cube", "cube" };
                    countB = 4;
                    break;

                case "disappearance":
                    // 在 B 中移除最后一个物体
                    countB = 2;
                    break;

                case "movement":
                    // 在 B 中将最后一个物体大幅横向移动（从0.9m增加到1.5m）
                    localsB[2] = new Vector3(localsB[2].x + 1.5f, localsB[2].y, localsB[2].z);
                    break;

                case "replacement":
                    // 在 B 中将最后一个物体替换为 sphere
                    kindsB[2] = "sphere";
                    break;

                case "none":
                default:
                    // B 与 A 完全相同
                    break;
            }

            PlaceCluster("cd_B_", centerB, localsB, kindsB, countB, forward, right, y);
        }

        private void PlaceCluster(string prefix, Vector3 center, Vector3[] locals, string[] kinds, int count,
            Vector3 forward, Vector3 right, float y)
        {
            if (locals == null || kinds == null) return;
            count = Mathf.Clamp(count, 0, Math.Min(locals.Length, kinds.Length));

            for (int i = 0; i < count; i++)
            {
                var local = locals[i];
                var pos = center + right * local.x + forward * local.z;
                pos.y = y;

                var kind = kinds[i] ?? "cube";
                var name = $"{prefix}{i}";

                // Phase 1: 为每个物体分配不同颜色
                var color = s_objectColors[i % s_objectColors.Length];
                var material = new Material(Shader.Find("Standard")) { color = color };

                if (_placer != null)
                {
                    _placer.Place(kind, pos, 1.0f, material, name);
                }
                else
                {
                    var go = CreatePrimitiveForKind(kind);
                    if (go != null)
                    {
                        go.name = name;
                        go.transform.position = pos;
                        go.transform.localScale = Vector3.one;

                        // 应用颜色到材质
                        var renderer = go.GetComponent<Renderer>();
                        if (renderer != null && renderer.material != null)
                        {
                            renderer.material.color = color;
                        }
                    }
                }
            }
        }

        private static GameObject CreatePrimitiveForKind(string kind)
        {
            switch ((kind ?? "cube").ToLowerInvariant())
            {
                case "cube": return GameObject.CreatePrimitive(PrimitiveType.Cube);
                case "sphere": return GameObject.CreatePrimitive(PrimitiveType.Sphere);
                case "cylinder": return GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                case "human":
                case "capsule": return GameObject.CreatePrimitive(PrimitiveType.Capsule);
                default: return GameObject.CreatePrimitive(PrimitiveType.Cube);
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

        private void Shuffle<T>(IList<T> list)
        {
            if (list == null || list.Count <= 1) return;

            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rand.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
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

        private static bool TryExtractChangeFromAnswer(object answer, out bool changed, out string category)
        {
            changed = false;
            category = null;
            if (answer == null) return false;

            // 1) 反射 / JSON 尝试
            try
            {
                var t = answer.GetType();
                var changedProp = t.GetProperty("changed", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                var categoryProp = t.GetProperty("category", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

                bool? changedVal = null;
                string categoryVal = null;

                if (changedProp != null)
                {
                    if (TryToBool(changedProp.GetValue(answer), out var b))
                    {
                        changedVal = b;
                    }
                }

                if (categoryProp != null)
                {
                    var v = categoryProp.GetValue(answer)?.ToString();
                    if (!string.IsNullOrEmpty(v))
                    {
                        categoryVal = v;
                    }
                }

                if (changedVal.HasValue || !string.IsNullOrEmpty(categoryVal))
                {
                    changed = changedVal ?? false;
                    category = NormalizeCategory(categoryVal);
                    return true;
                }

                // JSON 路径
                var json = JsonUtility.ToJson(answer);
                if (!string.IsNullOrEmpty(json))
                {
                    var parsed = JsonUtility.FromJson<ChangeAnswer>(json);
                    if (parsed != null)
                    {
                        changed = parsed.changed;
                        category = NormalizeCategory(parsed.category);
                        return true;
                    }
                }
            }
            catch
            {
                // ignore
            }

            // 2) ToString 粗提取
            try
            {
                var s = answer.ToString();
                return TryExtractChangeFromString(s, out changed, out category);
            }
            catch
            {
                // ignore
            }

            return false;
        }

        private static bool TryExtractChangeFromText(string text, out bool changed, out string category)
        {
            changed = false;
            category = null;
            if (string.IsNullOrEmpty(text)) return false;
            return TryExtractChangeFromString(text, out changed, out category);
        }

        private static bool TryExtractChangeFromString(string text, out bool changed, out string category)
        {
            changed = false;
            category = null;
            if (string.IsNullOrEmpty(text)) return false;

            // 尝试匹配 "changed": true/false
            var mChanged = Regex.Match(text, @"changed[^A-Za-z0-9]*(true|false)", RegexOptions.IgnoreCase);
            if (mChanged.Success && bool.TryParse(mChanged.Groups[1].Value, out var b))
            {
                changed = b;
            }
            else
            {
                // 关键字启发
                if (text.IndexOf("no change", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("unchanged", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    changed = false;
                }
                else if (text.IndexOf("change", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    changed = true;
                }
            }

            // 尝试匹配类别
            if (Regex.IsMatch(text, "appearance", RegexOptions.IgnoreCase))
            {
                category = "appearance";
            }
            else if (Regex.IsMatch(text, "disappearance|missing|removed", RegexOptions.IgnoreCase))
            {
                category = "disappearance";
            }
            else if (Regex.IsMatch(text, "move|moved|shift", RegexOptions.IgnoreCase))
            {
                category = "movement";
            }
            else if (Regex.IsMatch(text, "replace|replacement|different object", RegexOptions.IgnoreCase))
            {
                category = "replacement";
            }
            else if (!changed)
            {
                category = "none";
            }

            category = NormalizeCategory(category);

            // 只要能确定 changed 或 category 之一，即认为有预测
            return mChanged.Success || category != null;
        }

        private static string NormalizeCategory(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var s = raw.Trim().ToLowerInvariant();
            return s switch
            {
                "none" => "none",
                "appearance" => "appearance",
                "disappearance" => "disappearance",
                "movement" => "movement",
                "replacement" => "replacement",
                _ => s
            };
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

        [Serializable]
        private class ChangeAnswer
        {
            public bool changed;
            public string category;
            public float confidence;
        }
    }
}

