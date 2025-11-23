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
    /// 距离压缩感知任务（Distance Compression）
    /// - 场景：开阔地 / 走廊（简易搭建）
    /// - 目标：cube / sphere / human
    /// - 距离范围：2–30 m
    /// - FOV：50 / 60 / 90 度
    /// </summary>
    public class DistanceCompressionTask : ITask
    {
        public string TaskId => "distance_compression";

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(12345);
        private ExperimentSceneManager _scene;
        private ObjectPlacer _placer;
        
        public DistanceCompressionTask(TaskRunnerContext ctx)
        {
            _ctx = ctx;
        }

        public void Initialize(TaskRunner runner, VRPerception.Infra.EventBus.EventBusManager eventBus)
        {
            // 可在此做更复杂的初始化
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

            var envs = new[] { "open_field", "corridor" };
            var fovs = new[] { 50f, 60f, 90f };
            var kinds = new[] { "cube", "sphere", "human" };
            var dists = new[] { 2f, 5f, 10f, 15f, 20f, 25f, 30f };
            var textures = new[] { 0.5f, 1.0f, 1.5f };
            var lightings = new[] { "bright", "dim" };

            var candidates = new List<TrialSpec>();

            // 采样组合（避免全笛卡尔爆炸，随机挑选）
            foreach (var env in envs)
            {
                foreach (var f in fovs)
                {
                    foreach (var k in kinds)
                    {
                        // 每种目标取若干距离
                        for (int i = 0; i < 3; i++)
                        {
                            var dist = dists[_rand.Next(dists.Length)];
                            var tex = textures[_rand.Next(textures.Length)];
                            var light = lightings[_rand.Next(lightings.Length)];

                            candidates.Add(new TrialSpec
                            {
                                taskId = TaskId,
                                environment = env,
                                fovDeg = f,
                                targetKind = k,
                                trueDistanceM = dist,
                                textureDensity = tex,
                                lighting = light,
                                occlusion = false
                            });
                        }
                    }
                }
            }

            // 打乱并限制数量（例如 24 条）
            Shuffle(candidates);
            var max = Mathf.Min(24, candidates.Count);
            return candidates.GetRange(0, max).ToArray();
        }

        public string GetSystemPrompt()
        {
            // 严格 ONLY JSON 的系统提示
            return
                "You are a vision agent. ONLY output JSON according to task rules. " +
                "If you can answer directly, output an inference JSON with distance. " +
                "Format: {\"type\":\"inference\",\"taskId\":\"distance_compression\",\"trialId\":<int>," +
                "\"answer\":{\"distance_m\":<number>},\"confidence\":<0..1>}. " +
                "No extra text. If information is insufficient, you may request actions, but prefer answering directly.";
        }

        public ToolSpec[] GetTools()
        {
            // 距离估计优先直接返回，工具可留空；若需闭环可扩展 snapshot/head_look_at 等
            return null;
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            var envText = trial.environment == "corridor" ? "a long corridor" : "an open field";
            return
                $"Task: Estimate the distance to the target object in meters.\n" +
                $"Scene: {envText}. Target kind: {trial.targetKind}. Camera FOV: {trial.fovDeg} deg.\n" +
                $"Output ONLY JSON with fields: type=inference, answer.distance_m (float), confidence (0..1).";
        }

        public async Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            TryBindHelpers();

            // 场景布置（通过 ExperimentSceneManager）
            if (_scene != null)
            {
                _scene.SetupEnvironment(trial.environment ?? "open_field", trial.textureDensity, trial.lighting, trial.occlusion);
            }

            // 设置相机 FOV（供被试预览）；采样时 PerceptionSystem 会用 FrameCaptureOptions 覆盖
            _ctx?.stimulus?.SetCameraFOV(trial.fovDeg);

            // 放置目标
            PlaceTarget(trial);

            await Task.Yield();
        }

        public async Task OnAfterTrialAsync(TrialSpec trial, LLMResponse response, CancellationToken ct)
        {
            // 仅清理试次内放置的对象；环境在下一个 SetupEnvironment 时会自动重建
            if (_placer != null)
            {
                _placer.ClearAll();
            }
            else
            {
                TryDestroyByPrefix("dc_target_");
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
                confidence = response?.confidence ?? 0
            };

            float predicted = float.NaN;

            if (response != null && response.type == "inference")
            {
                // 多策略提取 distance_m
                if (TryExtractDistanceFromAnswer(response.answer, out var d1))
                {
                    predicted = d1;
                }
                else if (TryExtractDistanceFromExplanation(response.explanation, out var d2))
                {
                    predicted = d2;
                }
            }

            if (!float.IsNaN(predicted))
            {
                eval.predictedDistanceM = predicted;
                eval.absError = Mathf.Abs(predicted - trial.trueDistanceM);
                eval.relError = trial.trueDistanceM > 0.0001f ? eval.absError / trial.trueDistanceM : 0f;
                eval.success = true;
            }
            else
            {
                eval.success = false;
                eval.failureReason = "No distance_m found in model output";
            }

            return eval;
        }

        // --- helpers ---

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

        private void PlaceTarget(TrialSpec trial)
        {
            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null) return;

            var origin = cam.transform.position;
            var forward = cam.transform.forward;

            var pos = origin + forward * Mathf.Max(0.1f, trial.trueDistanceM);
            float yCenter = 1.0f;
            float scale = 1.0f;

            switch (trial.targetKind)
            {
                case "human":
                    pos.y = 0.9f;
                    scale = 1.0f;
                    break;
                case "sphere":
                case "cube":
                default:
                    pos.y = yCenter;
                    scale = 1.0f;
                    break;
            }

            // 走廊边界检查：如果在走廊环境中，确保物体在边界内
            if (_scene != null && _scene.CurrentEnvironment == "corridor")
            {
                if (!_scene.IsPositionInBounds(pos, margin: 0.5f))
                {
                    // 位置超出边界，进行限制
                    Vector3 clampedPos = _scene.ClampPositionToBounds(pos, margin: 0.5f);

                    // 记录警告日志
                    Debug.LogWarning($"[DistanceCompressionTask] Target position {pos} is out of corridor bounds. " +
                                   $"Clamped to {clampedPos}. Trial: {trial.trialId}, Distance: {trial.trueDistanceM}m");

                    pos = clampedPos;
                }
            }

            var kind = string.IsNullOrEmpty(trial.targetKind) ? "cube" : trial.targetKind;

            if (_placer != null)
            {
                _placer.Place(kind, pos, scale, null, "dc_target_" + kind);
                return;
            }

            // 兜底：直接创建原生 Primitive
            GameObject go = null;
            switch (kind)
            {
                case "cube":
                    go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go.transform.localScale = Vector3.one * 1.0f;
                    break;
                case "sphere":
                    go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.transform.localScale = Vector3.one * 1.0f;
                    break;
                case "human":
                    go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    go.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
                    break;
                default:
                    go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go.transform.localScale = Vector3.one;
                    break;
            }
            if (go != null)
            {
                go.name = "dc_target_" + kind;
                go.transform.position = pos;
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
            catch { /* ignore */ }
        }

        private static void Shuffle<T>(IList<T> list)
        {
            var rand = new System.Random();
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private bool TryExtractDistanceFromAnswer(object answer, out float distance)
        {
            distance = float.NaN;
            if (answer == null) return false;

            // 1) 反射属性/字段 distance_m
            var t = answer.GetType();
            var prop = t.GetProperty("distance_m", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (prop != null)
            {
                if (TryToFloat(prop.GetValue(answer), out distance)) return true;
            }
            var field = t.GetField("distance_m", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                if (TryToFloat(field.GetValue(answer), out distance)) return true;
            }

            // 2) 尝试 JSON 序列化后再解析
            try
            {
                var json = JsonUtility.ToJson(answer);
                if (!string.IsNullOrEmpty(json))
                {
                    var parsed = JsonUtility.FromJson<DistanceAnswer>(json);
                    if (parsed != null && parsed.distance_m > 0)
                    {
                        distance = parsed.distance_m;
                        return true;
                    }
                }
            }
            catch { /* ignore */ }

            // 3) ToString() 粗提取
            try
            {
                var s = answer.ToString();
                if (!string.IsNullOrEmpty(s) && TryExtractDistanceFromString(s, out distance))
                    return true;
            }
            catch { /* ignore */ }

            return false;
        }

        private bool TryExtractDistanceFromExplanation(string explanation, out float distance)
        {
            distance = float.NaN;
            if (string.IsNullOrEmpty(explanation)) return false;
            return TryExtractDistanceFromString(explanation, out distance);
        }

        private bool TryExtractDistanceFromString(string text, out float distance)
        {
            distance = float.NaN;
            if (string.IsNullOrEmpty(text)) return false;

            // 匹配 distance_m 或带单位的数字
            var m = Regex.Match(text, @"distance[_\s]*m[^\d\-]*([-+]?\d+(\.\d+)?)", RegexOptions.IgnoreCase);
            if (m.Success && float.TryParse(m.Groups[1].Value, out var d))
            {
                distance = d;
                return true;
            }

            // 后备：任意首个浮点数（风险高）
            var m2 = Regex.Match(text, @"([-+]?\d+(\.\d+)?)");
            if (m2.Success && float.TryParse(m2.Groups[1].Value, out var d2))
            {
                distance = d2;
                return true;
            }

            return false;
        }

        private bool TryToFloat(object v, out float f)
        {
            f = float.NaN;
            if (v == null) return false;
            switch (v)
            {
                case float fv: f = fv; return true;
                case double dv: f = (float)dv; return true;
                case int iv: f = iv; return true;
                case long lv: f = lv; return true;
                case string sv when float.TryParse(sv, out var parsed): f = parsed; return true;
                default: return false;
            }
        }

        [Serializable]
        private class DistanceAnswer
        {
            public float distance_m;
            public float confidence;
            public string explanation;
        }
    }
}