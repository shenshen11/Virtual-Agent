using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;

namespace VRPerception.Tasks
{
    /// <summary>
    /// Scenario 11: Rapid Numerosity Comparison (Weber law).
    /// - One-shot only: decide which side (left/right) has more items.
    /// - For MLLM: enforce low-res by requesting a low-res snapshot via action_plan when needed.
    /// - For Human: stimulus is shown briefly then covered by black screen (handled by TrialBlackoutOverlay).
    /// </summary>
    public sealed class NumerosityComparisonTask : ITask
    {
        public string TaskId => "numerosity_comparison";

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(12345);

        private GameObject _leftDots;
        private GameObject _rightDots;
        private GameObject _divider;

        private ParticleSystem _leftPs;
        private ParticleSystem _rightPs;

        public NumerosityComparisonTask(TaskRunnerContext ctx)
        {
            _ctx = ctx;
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
        }

        public TrialSpec[] BuildTrials(int seed)
        {
            _rand = new System.Random(seed);

            int[] baseCounts = { 10, 50, 100, 200, 500 };
            float[] ratios = { 1.1f, 1.2f, 1.5f, 2.0f };

            var trials = new List<TrialSpec>();
            bool largerOnLeft = true;

            foreach (var baseCount in baseCounts)
            foreach (var ratio in ratios)
            {
                int smaller = Mathf.Max(1, baseCount);
                int larger = Mathf.Max(smaller + 1, Mathf.CeilToInt(smaller * ratio));

                // Two reps, alternating which side is larger.
                for (int rep = 0; rep < 2; rep++)
                {
                    int left = largerOnLeft ? larger : smaller;
                    int right = largerOnLeft ? smaller : larger;
                    largerOnLeft = !largerOnLeft;

                    trials.Add(new TrialSpec
                    {
                        taskId = TaskId,
                        environment = "open_field",
                        background = "none",
                        lighting = "bright",
                        occlusion = false,
                        fovDeg = 60f,

                        // Reuse generic fields:
                        // - trueCount => leftCount
                        // - targetCount => rightCount
                        // - sizeRelation => ratio string
                        trueCount = left,
                        targetCount = right,
                        sizeRelation = ratio.ToString("0.00", CultureInfo.InvariantCulture),
                        layoutPattern = "random_scatter",
                        countingMode = "compare",

                        // Task specific fields
                        baseCountN = baseCount,
                        ratioR = ratio,
                        leftCount = left,
                        rightCount = right,
                        trueMoreSide = left > right ? "left" : "right",

                        // Human exposure control (handled by TrialBlackoutOverlay / TaskRunner timing)
                        exposureDurationMs = 500f,
                        // 固定点半径（米）。注意：ParticleSystem 里会用直径 = 2 * dotRadius。
                        // 取值与旧实现的 startSize(约 0.03~0.12m) 对齐：radius=0.04 -> size≈0.08m。
                        dotRadius = 0.04f
                    });
                }
            }

            Shuffle(trials);
            for (int i = 0; i < trials.Count; i++) trials[i].trialId = i + 1;
            return trials.ToArray();
        }

        public string GetSystemPrompt()
        {
            // One-shot: 禁 action_plan；低分辨率/模糊由 TaskRunner/StimulusCapture 侧约束。
            return PromptTemplates.GetSystemPrompt(TaskId);
        }

        public ToolSpec[] GetTools()
        {
            // NumerosityComparison: one-shot decision, no action_plan needed.
            return Array.Empty<ToolSpec>();
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            var fov = trial.fovDeg > 0 ? trial.fovDeg : 60f;
            return PromptTemplates.BuildNumerosityComparisonPrompt(fov);
        }

        public Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            EnsureParticleObjects();
            PlaceStimuli(trial);

            // Human 模式短时曝光：由 Overlay 负责延迟黑屏，防止二次观察（运行时也生效）。
            // 若场景未挂载 TrialBlackoutOverlay，则不会影响任务执行。
            try
            {
                var overlay = UnityEngine.Object.FindObjectOfType<VRPerception.UI.TrialBlackoutOverlay>();
                if (overlay != null)
                {
                    int exposureMs = Mathf.Clamp(Mathf.RoundToInt(trial.exposureDurationMs > 0 ? trial.exposureDurationMs : 500f), 0, 60000);
                    overlay.BeginBlackoutAfterMs(exposureMs);
                }
            }
            catch { }

            return Task.CompletedTask;
        }

        public Task OnAfterTrialAsync(TrialSpec trial, LLMResponse response, CancellationToken ct)
        {
            ClearParticles(_leftPs);
            ClearParticles(_rightPs);
            if (_divider != null) _divider.SetActive(false);
            return Task.CompletedTask;
        }

        public TrialEvaluation Evaluate(TrialSpec trial, LLMResponse response)
        {
            var eval = new TrialEvaluation
            {
                responseType = response?.type,
                providerId = response?.providerId,
                latencyMs = response?.latencyMs ?? 0,
                confidence = response?.confidence ?? 0f
            };

            if (response == null)
            {
                eval.success = false;
                eval.failureReason = "No response";
                return eval;
            }

            if (!string.Equals(response.type, "inference", StringComparison.OrdinalIgnoreCase))
            {
                eval.success = false;
                eval.failureReason = "Expected inference";
                return eval;
            }

            string predicted = null;
            if (!TryExtractMoreSide(response.answer, out predicted))
            {
                TryExtractMoreSideFromText(response.explanation, out predicted);
            }

            if (string.IsNullOrEmpty(predicted))
            {
                eval.success = false;
                eval.failureReason = "No more_side found";
                return eval;
            }

            int left = Mathf.Max(0, trial.trueCount);
            int right = Mathf.Max(0, trial.targetCount);
            string truth = left > right ? "left" : "right";
            eval.predictedMoreSide = predicted;
            eval.trueMoreSide = truth;

            eval.isCorrect = string.Equals(predicted, truth, StringComparison.OrdinalIgnoreCase);
            eval.isMoreSideCorrect = eval.isCorrect;
            if (string.Equals(response.providerId, "human", StringComparison.OrdinalIgnoreCase) && response.latencyMs > 0)
            {
                eval.humanReactionTimeMs = response.latencyMs;
            }
            eval.success = true;

            try
            {
                eval.extraJson = JsonUtility.ToJson(new
                {
                    ratio = trial.sizeRelation,
                    baseCountN = trial.baseCountN,
                    ratioR = trial.ratioR,
                    trialLeftCount = trial.leftCount,
                    trialRightCount = trial.rightCount,
                    visibleLeftCount = left,
                    visibleRightCount = right,
                    true_more_side = truth,
                    predicted_more_side = predicted
                });
            }
            catch { }

            return eval;
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

        private void EnsureParticleObjects()
        {
            if (_leftDots == null)
            {
                _leftDots = new GameObject("num_left_dots");
                _leftPs = _leftDots.AddComponent<ParticleSystem>();
                ConfigureParticles(_leftPs);
            }

            if (_rightDots == null)
            {
                _rightDots = new GameObject("num_right_dots");
                _rightPs = _rightDots.AddComponent<ParticleSystem>();
                ConfigureParticles(_rightPs);
            }
        }

        private static void ConfigureParticles(ParticleSystem ps)
        {
            var main = ps.main;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSpeed = 0f;
            main.startLifetime = 999f;
            main.maxParticles = 3000;

            var emission = ps.emission;
            emission.enabled = false;

            var shape = ps.shape;
            shape.enabled = false;

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                var shader = Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Sprites/Default");
                if (shader != null) renderer.material = new Material(shader) { color = Color.white };
            }
        }

        private void PlaceStimuli(TrialSpec trial)
        {
            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null) return;

            int leftCount = Mathf.Max(0, trial.trueCount);
            int rightCount = Mathf.Max(0, trial.targetCount);

            float depth = 12.0f;
            float gap = 0.9f;
            float regionWidth = 5.0f;
            float regionHeight = 3.5f;

            var origin = cam.transform.position + cam.transform.forward * depth;
            var right = cam.transform.right;
            var up = cam.transform.up;
            var forward = cam.transform.forward;

            var leftCenter = origin - right * (gap * 0.5f + regionWidth * 0.5f);
            var rightCenter = origin + right * (gap * 0.5f + regionWidth * 0.5f);

            float leftMinDist = ComputeMinDistance(regionWidth, regionHeight, leftCount);
            float rightMinDist = ComputeMinDistance(regionWidth, regionHeight, rightCount);
            // 关键：固定点大小，不随数量/密度变化（避免 size 成为比较线索）。
            // TrialSpec.dotRadius 表示“半径”（米），ParticleSystem.startSize 使用“直径/尺寸”（米）。
            float dotRadius = trial.dotRadius > 0 ? trial.dotRadius : 0.06f;
            float dotSize = Mathf.Clamp(dotRadius * 2f, 0.01f, 0.5f);

            // 为避免大量重叠，最小间距下限与点大小相关；过大可能导致高数量条件放置失败。
            float minDistFloor = dotSize * 0.55f;
            leftMinDist = Mathf.Max(leftMinDist, minDistFloor);
            rightMinDist = Mathf.Max(rightMinDist, minDistFloor);

            SetParticles(_leftPs, leftCount, leftCenter, right, up, regionWidth, regionHeight, leftMinDist, dotSize);
            SetParticles(_rightPs, rightCount, rightCenter, right, up, regionWidth, regionHeight, rightMinDist, dotSize);

            PlaceDivider(origin, forward, up, regionHeight);
        }

        private static float ComputeMinDistance(float width, float height, int count)
        {
            if (count <= 0) return 0.1f;
            float area = Mathf.Max(0.01f, width * height);
            float spacing = Mathf.Sqrt(area / Mathf.Max(1, count));
            return Mathf.Clamp(spacing * 0.5f, 0.02f, 0.18f);
        }

        private void SetParticles(
            ParticleSystem ps,
            int count,
            Vector3 center,
            Vector3 right,
            Vector3 up,
            float width,
            float height,
            float minDist,
            float particleSize)
        {
            if (ps == null) return;
            ps.gameObject.SetActive(true);

            if (count <= 0)
            {
                ClearParticles(ps);
                return;
            }

            var positions = GenerateScatterPoints(count, width, height, minDist);
            var particles = new ParticleSystem.Particle[count];
            for (int i = 0; i < count; i++)
            {
                var p = positions[i];
                particles[i].position = center + right * p.x + up * p.y;
                particles[i].startColor = Color.white;
                particles[i].startSize = particleSize;
                particles[i].remainingLifetime = 999f;
                particles[i].startLifetime = 999f;
            }

            ps.Clear(true);
            ps.SetParticles(particles, count);
            ps.Play();
        }

        private List<Vector2> GenerateScatterPoints(int count, float width, float height, float minDist)
        {
            var result = new List<Vector2>(count);
            if (count <= 0) return result;

            float halfW = width * 0.5f;
            float halfH = height * 0.5f;
            float cell = Mathf.Max(0.001f, minDist);
            var grid = new Dictionary<Vector2Int, List<Vector2>>();

            bool IsFarEnough(Vector2 candidate)
            {
                var cellId = new Vector2Int(Mathf.FloorToInt(candidate.x / cell), Mathf.FloorToInt(candidate.y / cell));
                float minDistSq = minDist * minDist;
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    var key = new Vector2Int(cellId.x + dx, cellId.y + dy);
                    if (!grid.TryGetValue(key, out var bucket)) continue;
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        if ((bucket[i] - candidate).sqrMagnitude < minDistSq) return false;
                    }
                }
                return true;
            }

            void AddToGrid(Vector2 point)
            {
                var cellId = new Vector2Int(Mathf.FloorToInt(point.x / cell), Mathf.FloorToInt(point.y / cell));
                if (!grid.TryGetValue(cellId, out var bucket))
                {
                    bucket = new List<Vector2>(4);
                    grid[cellId] = bucket;
                }
                bucket.Add(point);
            }

            const int maxAttemptsPerPoint = 60;
            for (int i = 0; i < count; i++)
            {
                bool placed = false;
                for (int attempt = 0; attempt < maxAttemptsPerPoint; attempt++)
                {
                    float x = (float)(_rand.NextDouble() * width - halfW);
                    float y = (float)(_rand.NextDouble() * height - halfH);
                    var candidate = new Vector2(x, y);
                    if (!IsFarEnough(candidate)) continue;
                    result.Add(candidate);
                    AddToGrid(candidate);
                    placed = true;
                    break;
                }

                if (!placed)
                {
                    float x = (float)(_rand.NextDouble() * width - halfW);
                    float y = (float)(_rand.NextDouble() * height - halfH);
                    var p = new Vector2(x, y);
                    result.Add(p);
                    AddToGrid(p);
                }
            }

            return result;
        }

        private void PlaceDivider(Vector3 origin, Vector3 forward, Vector3 up, float height)
        {
            if (_divider == null)
            {
                // 用具有“厚度”的隔离物（细长立方体）替代纯贴片 Quad，视觉上更像明确的分隔/隔离条带。
                _divider = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _divider.name = "num_divider";
                var col = _divider.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.Destroy(col);
                var r = _divider.GetComponent<Renderer>();
                if (r != null)
                {
                    var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
                    if (shader != null)
                    {
                        r.material = new Material(shader);
                        r.material.color = new Color(1f, 1f, 1f, 1f);
                    }
                    try
                    {
                        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        r.receiveShadows = false;
                    }
                    catch { }
                }
            }

            _divider.SetActive(true);
            _divider.transform.position = origin - forward.normalized * 0.02f;
            _divider.transform.rotation = Quaternion.LookRotation(-forward.normalized, up);
            // x: 条带宽度（左右方向），y: 高度（上下方向），z: 厚度（前后方向）
            _divider.transform.localScale = new Vector3(0.06f, Mathf.Max(0.1f, height), 0.03f);
        }

        private static void ClearParticles(ParticleSystem ps)
        {
            if (ps == null) return;
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Clear(true);
        }

        private static bool TryExtractMoreSide(object answer, out string moreSide)
        {
            moreSide = null;
            if (answer == null) return false;

            try
            {
                // OpenAI/Anthropic: answer is typically InferenceResult; fields are top-level.
                var t = answer.GetType();
                var candidates = new[] { "more_side", "moreSide", "larger" };
                for (int i = 0; i < candidates.Length; i++)
                {
                    var name = candidates[i];

                    var prop = t.GetProperty(name);
                    if (prop != null && NormalizeSide(prop.GetValue(answer, null)?.ToString(), out moreSide))
                        return true;

                    var field = t.GetField(name);
                    if (field != null && NormalizeSide(field.GetValue(answer)?.ToString(), out moreSide))
                        return true;
                }

                if (answer is IDictionary dict)
                {
                    // Prefer more_side keys; tolerate variants.
                    var keys = new[] { "more_side", "moreSide", "larger" };
                    foreach (var wanted in keys)
                    {
                        foreach (var key in dict.Keys)
                        {
                            if (key == null) continue;
                            var k = key.ToString();
                            if (!string.Equals(k, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                            if (NormalizeSide(dict[key]?.ToString(), out moreSide)) return true;
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool TryExtractMoreSideFromText(string text, out string moreSide)
        {
            moreSide = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var m = Regex.Match(text, @"\b(left|right)\b", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            return NormalizeSide(m.Groups[1].Value, out moreSide);
        }

        private static bool NormalizeSide(string raw, out string side)
        {
            side = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var s = raw.Trim().ToLowerInvariant();
            if (s.Contains("left") || s == "l") { side = "left"; return true; }
            if (s.Contains("right") || s == "r") { side = "right"; return true; }
            return false;
        }
    }
}
