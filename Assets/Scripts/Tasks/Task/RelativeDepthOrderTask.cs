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
    /// 相对深度排序任务（Relative Depth Ordering）
    /// - 同屏展示对象 A / B，处于不同深度
    /// - 被试输出：{"type":"inference","answer":{"closer":"A|B"},"confidence":0..1}
    /// - 自变量示例：FOV / 光照 / 背景标签 / 纹理密度 / 尺寸条件（等大/不同）/ 遮挡
    /// </summary>
    public class RelativeDepthOrderTask : ITask
    {
        public string TaskId => "relative_depth_order";

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(1234);

        // 运行时引用
        private ExperimentSceneManager _scene;
        private ObjectPlacer _placer;

        public RelativeDepthOrderTask(TaskRunnerContext ctx)
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
            // 使用 seed 仅控制 trial 顺序，试次集合本身为固定设计
            _rand = new System.Random(seed);

            var fovs = new[] { 50f, 60f, 90f };
            var backgrounds = new[] { "none", "indoor", "street" };
            var occlusions = new[] { false, true };
            var sizeConds = new[] { "equal", "different" };
            var textures = new[] { 0.5f, 1.0f, 1.5f, 2.0f };

            var trials = new List<TrialSpec>();

            // 设计：FOV × 背景 × 遮挡 × 尺寸条件 共 3×3×2×2 = 36 条
            int texIndex = 0;
            bool nextCloserIsA = true; // 尝试全局平衡 A/B 作为“更近者”

            foreach (var fov in fovs)
            {
                foreach (var bg in backgrounds)
                {
                    foreach (var occ in occlusions)
                    {
                        foreach (var sizeCond in sizeConds)
                        {
                            // 固定一对距离：near/far
                            const float nearDist = 4.0f;
                            const float farDist = 8.0f;

                            bool aIsCloser = nextCloserIsA;
                            nextCloserIsA = !nextCloserIsA;

                            float depthA = aIsCloser ? nearDist : farDist;
                            float depthB = aIsCloser ? farDist : nearDist;
                            string trueCloser = aIsCloser ? "A" : "B";

                            // 尺寸条件：equal 时两者相同；different 时更远者略大，制造冲突线索
                            float scaleA = 1.0f;
                            float scaleB = 1.0f;
                            if (string.Equals(sizeCond, "different", StringComparison.OrdinalIgnoreCase))
                            {
                                if (aIsCloser)
                                {
                                    scaleA = 1.0f;
                                    scaleB = 1.4f;
                                }
                                else
                                {
                                    scaleA = 1.4f;
                                    scaleB = 1.0f;
                                }
                            }

                            var lighting = BackgroundToLighting(bg);
                            var tex = textures[texIndex % textures.Length];
                            texIndex++;

                            trials.Add(new TrialSpec
                            {
                                taskId = TaskId,
                                environment = "open_field", // 背景通过标签与光照模拟
                                background = bg,
                                fovDeg = fov,
                                textureDensity = tex,
                                lighting = lighting,
                                occlusion = occ,

                                // A/B 对象：使用简单几何体
                                objectA = "sphere",
                                objectB = "sphere",
                                sizeRelation = sizeCond, // 在该任务中表示“等大/不同”

                                // 相对深度参数
                                depthA = depthA,
                                depthB = depthB,
                                scaleA = scaleA,
                                scaleB = scaleB,
                                trueCloser = trueCloser
                            });
                        }
                    }
                }
            }

            Shuffle(trials);
            return trials.ToArray();
        }

        public string GetSystemPrompt()
        {
            // 统一由 PromptTemplates 管理系统提示
            return PromptTemplates.GetSystemPrompt(TaskId);
        }

        public ToolSpec[] GetTools()
        {
            // 当前实现中主要关注一次性判断；如需动作闭环，可在 PromptTemplates 中扩展工具
            return null;
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            return PromptTemplates.BuildRelativeDepthOrderPrompt(
                trial.background,
                trial.sizeRelation,
                trial.occlusion,
                trial.fovDeg);
        }

        public async Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            TryBindHelpers();

            // 场景布置
            if (_scene != null)
            {
                var env = string.IsNullOrEmpty(trial.environment) ? "open_field" : trial.environment;
                var lighting = string.IsNullOrEmpty(trial.lighting)
                    ? BackgroundToLighting(trial.background)
                    : trial.lighting;

                _scene.SetupEnvironment(env, trial.textureDensity, lighting, trial.occlusion);
            }

            // 设置相机 FOV
            var fov = trial.fovDeg > 0 ? trial.fovDeg : 60f;
            _ctx?.stimulus?.SetCameraFOV(fov);

            // 放置 A/B
            PlacePair(trial);

            await Task.Yield();
        }

        public async Task OnAfterTrialAsync(TrialSpec trial, LLMResponse response, CancellationToken ct)
        {
            // 清理试次内对象
            if (_placer != null)
            {
                _placer.ClearAll();
            }
            else
            {
                TryDestroyIfExists("rdo_A");
                TryDestroyIfExists("rdo_B");
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
                trueCloser = string.IsNullOrEmpty(trial.trueCloser) ? "A" : trial.trueCloser
            };

            string predicted = null;
            if (response != null && response.type == "inference")
            {
                if (TryExtractCloserFromAnswer(response.answer, out var c1))
                {
                    predicted = c1;
                }
                else
                {
                    predicted = TryExtractCloserFromText(response.explanation);
                }
            }

            if (!string.IsNullOrEmpty(predicted))
            {
                eval.predictedCloser = predicted.ToUpperInvariant();
                eval.isCorrect = string.Equals(
                    eval.predictedCloser,
                    eval.trueCloser,
                    StringComparison.OrdinalIgnoreCase);
                eval.success = true;
            }
            else
            {
                eval.success = false;
                eval.failureReason = "No closer (A/B) found";
            }

            return eval;
        }

        // =============== Helpers ===============

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

        private void PlacePair(TrialSpec trial)
        {
            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null) return;

            float depthA = trial.depthA > 0 ? trial.depthA : 4.0f;
            float depthB = trial.depthB > 0 ? trial.depthB : 8.0f;

            var origin = cam.transform.position;
            var forward = cam.transform.forward;
            var right = cam.transform.right;

            // 屏幕左侧为 A，右侧为 B
            float horizontalOffset = 0.7f;
            float y = origin.y + 1.0f;

            var posA = origin + forward * depthA - right * horizontalOffset;
            var posB = origin + forward * depthB + right * horizontalOffset;
            posA.y = y;
            posB.y = y;

            float scaleA = trial.scaleA > 0 ? trial.scaleA : 1.0f;
            float scaleB = trial.scaleB > 0 ? trial.scaleB : 1.0f;

            var kindA = string.IsNullOrEmpty(trial.objectA) ? "cube" : trial.objectA;
            var kindB = string.IsNullOrEmpty(trial.objectB) ? "cube" : trial.objectB;

            if (_placer != null)
            {
                _placer.Place(kindA, posA, scaleA, null, "rdo_A");
                _placer.Place(kindB, posB, scaleB, null, "rdo_B");
            }
            else
            {
                var goA = CreatePrimitiveForKind(kindA);
                var goB = CreatePrimitiveForKind(kindB);
                if (goA != null)
                {
                    goA.name = "rdo_A";
                    goA.transform.position = posA;
                    goA.transform.localScale = Vector3.one * scaleA;
                }
                if (goB != null)
                {
                    goB.name = "rdo_B";
                    goB.transform.position = posB;
                    goB.transform.localScale = Vector3.one * scaleB;
                }
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

        private void Shuffle<T>(IList<T> list)
        {
            if (list == null || list.Count <= 1) return;

            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rand.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private static GameObject CreatePrimitiveForKind(string kind)
        {
            switch ((kind ?? "cube").ToLowerInvariant())
            {
                case "cube": return GameObject.CreatePrimitive(PrimitiveType.Cube);
                case "sphere": return GameObject.CreatePrimitive(PrimitiveType.Sphere);
                case "human":
                case "capsule": return GameObject.CreatePrimitive(PrimitiveType.Capsule);
                case "cylinder": return GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                case "quad": return GameObject.CreatePrimitive(PrimitiveType.Quad);
                case "plane": return GameObject.CreatePrimitive(PrimitiveType.Plane);
                default: return GameObject.CreatePrimitive(PrimitiveType.Cube);
            }
        }

        private static void TryDestroyIfExists(string name)
        {
            var go = GameObject.Find(name);
            if (go == null) return;
#if UNITY_EDITOR
            UnityEngine.Object.DestroyImmediate(go);
#else
            UnityEngine.Object.Destroy(go);
#endif
        }

        private static bool TryExtractCloserFromAnswer(object answer, out string closer)
        {
            closer = null;
            if (answer == null) return false;

            // 1) 反射/JSON 尝试读取 closer 字段
            try
            {
                var t = answer.GetType();
                var prop = t.GetProperty("closer", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var v = prop.GetValue(answer)?.ToString();
                    if (!string.IsNullOrEmpty(v))
                    {
                        closer = v.ToUpperInvariant();
                        return true;
                    }
                }

                var json = JsonUtility.ToJson(answer);
                if (!string.IsNullOrEmpty(json))
                {
                    var parsed = JsonUtility.FromJson<CloserAnswer>(json);
                    if (!string.IsNullOrEmpty(parsed?.closer))
                    {
                        closer = parsed.closer.ToUpperInvariant();
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
                return TryExtractCloserFromString(s, out closer);
            }
            catch
            {
                // ignore
            }

            return false;
        }

        private static string TryExtractCloserFromText(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            return TryExtractCloserFromString(text, out var closer) ? closer : null;
        }

        private static bool TryExtractCloserFromString(string text, out string closer)
        {
            closer = null;
            if (string.IsNullOrEmpty(text)) return false;

            // 优先匹配 closer 字段
            var m = Regex.Match(text, @"closer[^AB]*([AB])", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                closer = m.Groups[1].Value.ToUpperInvariant();
                return true;
            }

            // 后备：只出现 A 或 B 其中一个时进行猜测
            bool hasA = text.IndexOf("A", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasB = text.IndexOf("B", StringComparison.OrdinalIgnoreCase) >= 0;
            if (hasA && !hasB) { closer = "A"; return true; }
            if (hasB && !hasA) { closer = "B"; return true; }

            return false;
        }

        [Serializable]
        private class CloserAnswer
        {
            public string closer;
            public float confidence;
        }
    }
}

