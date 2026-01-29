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
    /// Scenario 12: Staircase Relative Depth JND (depth_jnd_staircase)
    /// - Two identical objects A(left) / B(right) differ only by depth.
    /// - Human/Model answers: {"type":"inference","answer":{"closer":"A|B"},"confidence":0..1}
    /// - Adaptive staircase (1-up / 2-down) updates Δd after each response.
    /// </summary>
    public sealed class DepthJndStaircaseTask : ITask
    {
        public string TaskId => "depth_jnd_staircase";

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(12345);

        private ExperimentSceneManager _scene;
        private ObjectPlacer _placer;

        // ---- Staircase config (minimal hard-coded defaults) ----
        private const int DefaultMaxTrials = 60;
        private const float DefaultFovDeg = 60f;
        private const float BaseDistanceMinM = 4.0f;
        private const float BaseDistanceMaxM = 10.0f;
        private const float MinPresentableDistanceM = 1.0f;

        private const float DeltaStartM = 0.50f;
        private const float DeltaMinM = 0.02f;
        private const float DeltaMaxM = 2.00f;
        private const float Kappa = 1.4142135f; // sqrt(2)
        private const int ReversalTargetPerGroup = 8;
        private const int ThresholdUseLastReversals = 4;

        // ---- Runtime staircase state ----
        private float _deltaM = DeltaStartM;
        private int _consecutiveCorrect = 0;
        private string _lastDirection = "init"; // "up"|"down"|"init"
        private int _reversalCount = 0;
        private readonly List<float> _reversalDeltas = new List<float>(16);
        private int _groupIndex = 0;
        private int _trialIndexInGroup = 0;
        private bool _endRequested = false;

        public DepthJndStaircaseTask(TaskRunnerContext ctx)
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

            ResetStaircaseForNewGroup(0);
            _endRequested = false;

            int desired = DefaultMaxTrials;
            try
            {
                if (_ctx?.runner != null && _ctx.runner.CurrentMaxTrials > 0)
                {
                    desired = Mathf.Max(1, _ctx.runner.CurrentMaxTrials);
                }
            }
            catch { }

            var trials = new TrialSpec[desired];
            for (int i = 0; i < desired; i++)
            {
                trials[i] = new TrialSpec
                {
                    taskId = TaskId,
                    trialId = i + 1,
                    environment = "open_field",
                    textureDensity = 1.0f,
                    lighting = "bright",
                    occlusion = false,
                    fovDeg = DefaultFovDeg,
                    objectA = "sphere",
                    objectB = "sphere",
                    sizeRelation = "equal",
                    background = "none"
                };
            }

            return trials;
        }

        public string GetSystemPrompt()
        {
            return PromptTemplates.GetSystemPrompt(TaskId);
        }

        public ToolSpec[] GetTools()
        {
            return Array.Empty<ToolSpec>();
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            return PromptTemplates.BuildDepthJndStaircasePrompt(
                trial.background,
                trial.fovDeg,
                trial.trialId);
        }

        public async Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            TryBindHelpers();

            if (_scene != null)
            {
                _scene.SetupEnvironment("open_field", 1.0f, "bright", false);
            }

            _ctx?.stimulus?.SetCameraFOV(DefaultFovDeg);

            ConfigureTrialDepths(trial);
            PlacePair(trial);

            _trialIndexInGroup++;
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
                TryDestroyIfExists("djnd_A");
                TryDestroyIfExists("djnd_B");
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
            if (response != null && string.Equals(response.type, "inference", StringComparison.OrdinalIgnoreCase))
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

            if (string.IsNullOrEmpty(predicted))
            {
                eval.success = false;
                eval.failureReason = "No closer (A/B) found";
                return eval;
            }

            eval.predictedCloser = predicted.ToUpperInvariant();
            eval.isCorrect = string.Equals(eval.predictedCloser, eval.trueCloser, StringComparison.OrdinalIgnoreCase);
            eval.success = true;

            var usedDelta = Mathf.Abs((trial.depthA > 0 ? trial.depthA : 0f) - (trial.depthB > 0 ? trial.depthB : 0f));
            var usedBase = Mathf.Max(trial.depthA, trial.depthB);

            bool stepped = UpdateStaircase(eval.isCorrect, out string direction, out bool reversalHappened);
            int groupIndexSnapshot = _groupIndex;
            int trialIndexInGroupSnapshot = _trialIndexInGroup;
            int reversalCountSnapshot = _reversalCount;
            int consecutiveCorrectSnapshot = _consecutiveCorrect;
            string lastDirectionSnapshot = _lastDirection;
            float deltaNextSnapshot = _deltaM;

            float thresholdEstimate = EstimateThreshold();
            bool groupEnded = reversalCountSnapshot >= ReversalTargetPerGroup;

            if (groupEnded && !_endRequested)
            {
                thresholdEstimate = EstimateThreshold();
                _endRequested = true;
                _ctx?.runner?.CancelRun();
            }

            try
            {
                eval.extraJson = JsonUtility.ToJson(new DepthJndExtra
                {
                    groupIndex = groupIndexSnapshot,
                    trialIndexInGroup = trialIndexInGroupSnapshot,
                    baseDistanceM = usedBase,
                    deltaM = usedDelta,
                    staircaseDeltaNextM = deltaNextSnapshot,
                    kappa = Kappa,
                    consecutiveCorrect = consecutiveCorrectSnapshot,
                    lastDirection = lastDirectionSnapshot,
                    stepped = stepped,
                    direction = direction,
                    reversalHappened = reversalHappened,
                    reversalCount = reversalCountSnapshot,
                    thresholdEstimateM = thresholdEstimate,
                    groupEnded = groupEnded
                });
            }
            catch { }

            return eval;
        }

        // ================= Internal helpers =================

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

        private void ResetStaircaseForNewGroup(int groupIndex)
        {
            _groupIndex = Mathf.Max(0, groupIndex);
            _trialIndexInGroup = 0;

            _deltaM = DeltaStartM;
            _consecutiveCorrect = 0;
            _lastDirection = "init";
            _reversalCount = 0;
            _reversalDeltas.Clear();
        }

        private void ConfigureTrialDepths(TrialSpec trial)
        {
            float delta = Mathf.Clamp(_deltaM, DeltaMinM, DeltaMaxM);

            // d_base sampled in [BaseDistanceMinM, BaseDistanceMaxM], but must satisfy d_base - delta >= MinPresentableDistanceM
            float minBase = Mathf.Max(BaseDistanceMinM, MinPresentableDistanceM + delta);
            float maxBase = Mathf.Max(minBase, BaseDistanceMaxM);
            float baseDist = (float)(_rand.NextDouble() * (maxBase - minBase) + minBase);

            float far = baseDist;
            float near = Mathf.Max(MinPresentableDistanceM, baseDist - delta);

            bool aIsCloser = _rand.NextDouble() < 0.5;
            trial.depthA = aIsCloser ? near : far;
            trial.depthB = aIsCloser ? far : near;
            trial.trueCloser = aIsCloser ? "A" : "B";

            trial.scaleA = 1.0f;
            trial.scaleB = 1.0f;
            trial.objectA = string.IsNullOrEmpty(trial.objectA) ? "sphere" : trial.objectA;
            trial.objectB = trial.objectA; // enforce identical objects
        }

        private void PlacePair(TrialSpec trial)
        {
            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null) return;

            float depthA = trial.depthA > 0 ? trial.depthA : 4.0f;
            float depthB = trial.depthB > 0 ? trial.depthB : 4.5f;

            var origin = cam.transform.position;
            var forward = cam.transform.forward;
            var right = cam.transform.right;

            float horizontalOffset = 0.7f;
            float y = origin.y + 1.0f;

            var posA = origin + forward * depthA - right * horizontalOffset;
            var posB = origin + forward * depthB + right * horizontalOffset;
            posA.y = y;
            posB.y = y;

            float scaleA = trial.scaleA > 0 ? trial.scaleA : 1.0f;
            float scaleB = trial.scaleB > 0 ? trial.scaleB : 1.0f;

            var kind = string.IsNullOrEmpty(trial.objectA) ? "sphere" : trial.objectA;

            if (_placer != null)
            {
                _placer.Place(kind, posA, scaleA, null, "djnd_A");
                _placer.Place(kind, posB, scaleB, null, "djnd_B");
            }
            else
            {
                var goA = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                var goB = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                goA.name = "djnd_A";
                goB.name = "djnd_B";
                goA.transform.position = posA;
                goB.transform.position = posB;
                goA.transform.localScale = Vector3.one * scaleA;
                goB.transform.localScale = Vector3.one * scaleB;
            }
        }

        private bool UpdateStaircase(bool isCorrect, out string direction, out bool reversalHappened)
        {
            direction = null;
            reversalHappened = false;

            float oldDelta = _deltaM;
            bool stepped = false;

            if (isCorrect)
            {
                _consecutiveCorrect++;
                if (_consecutiveCorrect >= 2)
                {
                    _consecutiveCorrect = 0;
                    _deltaM = Mathf.Clamp(oldDelta / Kappa, DeltaMinM, DeltaMaxM);
                    direction = "down";
                    stepped = true;
                }
            }
            else
            {
                _consecutiveCorrect = 0;
                _deltaM = Mathf.Clamp(oldDelta * Kappa, DeltaMinM, DeltaMaxM);
                direction = "up";
                stepped = true;
            }

            if (!stepped)
            {
                return false;
            }

            if (!string.Equals(_lastDirection, "init", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(direction) &&
                !string.Equals(direction, _lastDirection, StringComparison.OrdinalIgnoreCase))
            {
                _reversalCount++;
                reversalHappened = true;
                _reversalDeltas.Add(oldDelta);
            }

            _lastDirection = direction;
            return true;
        }

        private float EstimateThreshold()
        {
            if (_reversalDeltas.Count <= 0) return _deltaM;
            int n = Mathf.Clamp(ThresholdUseLastReversals, 1, _reversalDeltas.Count);
            float sum = 0f;
            for (int i = _reversalDeltas.Count - n; i < _reversalDeltas.Count; i++)
            {
                sum += _reversalDeltas[i];
            }
            return sum / n;
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

            var m = Regex.Match(text, @"closer[^AB]*([AB])", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                closer = m.Groups[1].Value.ToUpperInvariant();
                return true;
            }

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

        [Serializable]
        private class DepthJndExtra
        {
            public int groupIndex;
            public int trialIndexInGroup;
            public float baseDistanceM;
            public float deltaM;
            public float staircaseDeltaNextM;
            public float kappa;
            public int consecutiveCorrect;
            public string lastDirection;
            public bool stepped;
            public string direction;
            public bool reversalHappened;
            public int reversalCount;
            public float thresholdEstimateM;
            public bool groupEnded;
        }
    }
}
