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
    public class DistanceCompressionTask : ITask, ITaskRunLifecycle
    {
        public string TaskId => "distance_compression";

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(12345);
        private ExperimentSceneManager _scene;
        private ObjectPlacer _placer;
        private bool _referenceFrameInitialized;
        private Vector3 _referenceOrigin;
        private Vector3 _referenceForward;
        private float _referenceEyeY;
        
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
                    stimulus = runner ? runner.GetComponent<StimulusCapture>() : null,
                    humanReferenceFrame = runner ? runner.GetComponent<HumanReferenceFrameService>() : null
                };
            }
            TryBindHelpers();
        }

        public Task OnRunBeginAsync(CancellationToken ct)
        {
            TryBindHelpers();
            if (!TryUseHumanSharedReferenceFrame())
            {
                CaptureReferenceFrameIfNeeded(forceRefresh: true);
            }

            return Task.CompletedTask;
        }

        public Task OnRunEndAsync(CancellationToken ct)
        {
            _referenceFrameInitialized = false;
            return Task.CompletedTask;
        }

        public TrialSpec[] BuildTrials(int seed)
        {
            _rand = new System.Random(seed);

            var envs = new[] { "open_field", "corridor" };
            var fovs = new[] { 60f };
            var kinds = new[] { "sphere"};
            // 改成用对数分布的6个距离点
            var dists = new[] { 2f, 3.2f, 5f, 8f, 12.6f, 20f };
            var textures = new[] { 0.5f, 1.0f, 1.5f };
            var lightings = new[] { "bright", "dim" };

            // 锚定试次：固定前四个（2/5/10/20m），不打乱
            var anchorDistances = new[] { 2f, 5f, 10f, 20f };
            string anchorEnv = envs.Length > 0 ? envs[0] : "open_field";
            float anchorFov = fovs.Length > 0 ? fovs[0] : 60f;
            string anchorKind = kinds.Length > 0 ? kinds[0] : "sphere";
            float anchorTexture = textures.Length > 1 ? textures[1] : (textures.Length > 0 ? textures[0] : 1f);
            string anchorLighting = lightings.Length > 0 ? lightings[0] : "bright";

            var anchorTrials = new List<TrialSpec>();
            foreach (var dist in anchorDistances)
            {
                anchorTrials.Add(new TrialSpec
                {
                    taskId = TaskId,
                    environment = anchorEnv,
                    fovDeg = anchorFov,
                    targetKind = anchorKind,
                    trueDistanceM = dist,
                    textureDensity = anchorTexture,
                    lighting = anchorLighting,
                    occlusion = false,
                    isAnchor = true
                });
            }

            // 先构造 (env, fov, kind) 的所有组合
            var triples = new List<(string env, float fov, string kind)>();
            foreach (var env in envs)
            {
                foreach (var f in fovs)
                {
                    foreach (var k in kinds)
                    {
                        triples.Add((env, f, k));
                    }
                }
            }

            var mainTrials = new List<TrialSpec>();

            int textureIndex = 0;
            int lightingIndex = 0;

            // 生成 36 次试验：6 个距离 × 6 次重复（正式试次）
            int repeatsPerDistance = 6;

            foreach (var dist in dists)
            {
                for (int repeat = 0; repeat < repeatsPerDistance; repeat++)
                {
                    var t = triples[(repeat) % triples.Count];
                    var tex = textures[textureIndex % textures.Length];
                    var light = lightings[lightingIndex % lightings.Length];

                    mainTrials.Add(new TrialSpec
                    {
                        taskId = TaskId,
                        environment = t.env,
                        fovDeg = t.fov,
                        targetKind = t.kind,
                        trueDistanceM = dist,
                        textureDensity = tex,
                        lighting = light,
                        occlusion = false,
                        isAnchor = false
                    });

                    textureIndex++;
                    lightingIndex++;
                }
            }

            // 仅打乱正式试次，锚定试次固定在前四个
            Shuffle(mainTrials);

            var all = new List<TrialSpec>(anchorTrials.Count + mainTrials.Count);
            all.AddRange(anchorTrials);
            all.AddRange(mainTrials);
            return all.ToArray();
        }

        public string GetSystemPrompt()
        {
            return PromptTemplates.GetSystemPrompt(TaskId);
        }

        public ToolSpec[] GetTools()
        {
            // 距离估计优先直接返回，工具可留空；若需闭环可扩展 snapshot/head_look_at 等
            return null;
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            if (trial != null && trial.isAnchor)
            {
                return PromptTemplates.BuildDistanceCompressionCalibrationPrompt(
                    trial.targetKind,
                    trial.fovDeg,
                    trial.environment,
                    trial.trueDistanceM,
                    trial.trialId);
            }
            return PromptTemplates.BuildDistanceCompressionPrompt(
                trial?.targetKind,
                trial?.fovDeg ?? 0f,
                trial?.environment,
                trial?.trialId ?? 0);
        }

        public async Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            TryBindHelpers();
            _placer?.SetActiveTrialContext(trial.taskId, trial.trialId);
            if (!TryUseHumanSharedReferenceFrame())
            {
                CaptureReferenceFrameIfNeeded(forceRefresh: false);
            }

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
                _placer.ClearActiveTrialContext();
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

        private void CaptureReferenceFrameIfNeeded(bool forceRefresh)
        {
            if (_referenceFrameInitialized && !forceRefresh) return;

            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null)
            {
                _referenceFrameInitialized = false;
                return;
            }

            // Use horizontal forward so head pitch does not change target bearing.
            _referenceOrigin = cam.transform.position;
            _referenceForward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (_referenceForward.sqrMagnitude < 1e-6f) _referenceForward = Vector3.forward;
            _referenceForward.Normalize();
            _referenceEyeY = cam.transform.position.y;
            _referenceFrameInitialized = true;
        }

        private void PlaceTarget(TrialSpec trial)
        {
            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null) return;

            ResolvePlacementReference(cam, out var origin, out var forward, out var eyeY);

            var pos = origin + forward * Mathf.Max(0.1f, trial.trueDistanceM);
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
                    pos.y = eyeY;
                    scale = 0.9f;
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

        private bool TryUseHumanSharedReferenceFrame()
        {
            if (!IsHumanMode()) return false;

            var humanRef = _ctx?.humanReferenceFrame;
            if (humanRef == null || !humanRef.HasReferenceFrame) return false;

            _referenceOrigin = humanRef.Origin;
            _referenceForward = humanRef.Forward;
            if (_referenceForward.sqrMagnitude < 1e-6f) _referenceForward = Vector3.forward;
            _referenceForward.Normalize();
            _referenceEyeY = humanRef.EyeY;
            _referenceFrameInitialized = true;
            return true;
        }

        private void ResolvePlacementReference(Camera cam, out Vector3 origin, out Vector3 forward, out float eyeY)
        {
            if (TryUseHumanSharedReferenceFrame())
            {
                origin = _referenceOrigin;
                forward = _referenceForward;
                eyeY = _referenceEyeY;
                return;
            }

            origin = _referenceFrameInitialized ? _referenceOrigin : cam.transform.position;
            forward = _referenceFrameInitialized ? _referenceForward : Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();
            eyeY = _referenceFrameInitialized ? _referenceEyeY : cam.transform.position.y;
        }

        private bool IsHumanMode()
        {
            return _ctx?.runner != null && _ctx.runner.CurrentSubjectMode == SubjectMode.Human;
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

        /// <summary>
        /// 使用当前任务内的有种子随机源 _rand 对列表做 Fisher-Yates 洗牌。
        /// 这样 trial 顺序完全由 BuildTrials(seed) 传入的 seed 决定，可复现。
        /// </summary>
        private void Shuffle<T>(IList<T> list)
        {
            if (list == null || list.Count <= 1) return;

            // 确保 _rand 已经在 BuildTrials(seed) 中初始化
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rand.Next(i + 1);
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
            var ackProp = t.GetProperty("acknowledged_distance_m", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (ackProp != null)
            {
                if (TryToFloat(ackProp.GetValue(answer), out distance)) return true;
            }
            var ackField = t.GetField("acknowledged_distance_m", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (ackField != null)
            {
                if (TryToFloat(ackField.GetValue(answer), out distance)) return true;
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
                    if (parsed != null && parsed.acknowledged_distance_m > 0)
                    {
                        distance = parsed.acknowledged_distance_m;
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
            public float acknowledged_distance_m;
            public float confidence;
            public string explanation;
        }
    }
}
