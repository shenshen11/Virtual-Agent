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
    /// 颜色恒常（调色/适应版）：
    /// - 白光基线：调节球体至视觉灰并记录 RGB
    /// - 红光/蓝光：先适应，再在有家具/无家具条件下交替调节
    /// - 输出：记录被试/模型给出的 RGB，与基线差值
    /// </summary>
    public class ColorConstancyAdjustmentTask : ITask
    {
        public string TaskId => "color_constancy_adjustment";

        private const float DefaultFovDeg = 60f;
        private const int DefaultRepeatsPerCondition = 5;
        private const int DefaultCandidateCount = 9;
        private const float DefaultAdaptSeconds = 30f;
        private const float DefaultRestSeconds = 15f;
        private const float AdjustableSphereScale = 0.2f;
        private const float AdjustableSphereDistance = 1.2f;
        private const float AdjustableSphereYOffset = -0.1f;
        private const float CandidateSphereScale = 0.12f;
        private const float CandidateGridDistance = 1.6f;
        private const float CandidateGridSpacing = 0.32f;

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(1234);

        private ExperimentSceneManager _scene;
        private FurnitureScatter _furniture;

        private readonly Dictionary<TrialSpec, TrialMeta> _metaByTrial = new Dictionary<TrialSpec, TrialMeta>();
        private readonly List<GameObject> _spawned = new List<GameObject>();

        private int _plannedTrialCount;
        private string _currentFurniturePhase;

        private int[] _baselineRgb;
        private bool _baselineSet;
        private bool _redAdapted;
        private bool _blueAdapted;
        private bool _restedForBlue;

        public ColorConstancyAdjustmentTask(TaskRunnerContext ctx)
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
            _metaByTrial.Clear();

            _baselineRgb = null;
            _baselineSet = false;
            _redAdapted = false;
            _blueAdapted = false;
            _restedForBlue = false;
            _currentFurniturePhase = null;

            var trials = new List<TrialSpec>();

            // Baseline (white neutral, no furniture)
            trials.Add(CreateTrial("baseline", hasFurniture: false, lighting: "white_neutral"));

            // Red block (adaptation + alternating furniture)
            AddBlock(trials, "red", "adapt_red", DefaultRepeatsPerCondition);

            // Blue block (rest + adaptation + alternating furniture)
            AddBlock(trials, "blue", "adapt_blue", DefaultRepeatsPerCondition);

            _plannedTrialCount = trials.Count;
            return trials.ToArray();
        }

        public string GetSystemPrompt()
        {
            return PromptTemplates.GetSystemPrompt(TaskId);
        }

        public ToolSpec[] GetTools()
        {
            return PromptTemplates.GetToolsForColorConstancyAdjustment();
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            if (!_metaByTrial.TryGetValue(trial, out var meta))
            {
                return PromptTemplates.BuildColorConstancyAdjustmentPrompt("unknown", "none", trial.lighting, trial.fovDeg, trial.trialId);
            }

            var background = meta.hasFurniture ? "furniture" : "empty";
            var phase = meta.phase ?? "unknown";
            return PromptTemplates.BuildColorConstancyAdjustmentPrompt(phase, background, meta.lighting, trial.fovDeg, trial.trialId);
        }

        public async Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            TryBindHelpers();

            if (!_metaByTrial.TryGetValue(trial, out var meta))
            {
                meta = new TrialMeta { phase = "unknown", hasFurniture = false, lighting = trial.lighting };
            }

            if (trial.trialId == 0)
            {
                _currentFurniturePhase = null;
                _furniture?.Clear();
            }

            bool isHuman = IsHumanSubject();

            // Lighting first (adaptation uses this state)
            if (_scene != null)
            {
                var lighting = string.IsNullOrEmpty(meta.lighting) ? "white_neutral" : meta.lighting;
                _scene.SetLighting(lighting);
                _scene.SetShadowMode(false);
            }

            // Human-only adaptation/rest steps
            if (isHuman)
            {
                if (string.Equals(meta.phase, "red", StringComparison.OrdinalIgnoreCase) && !_redAdapted)
                {
                    EnsureFurnitureForAdaptation(meta.phase);
                    await DelaySeconds(DefaultAdaptSeconds, ct);
                    _redAdapted = true;
                }
                else if (string.Equals(meta.phase, "blue", StringComparison.OrdinalIgnoreCase) && !_blueAdapted)
                {
                    if (!_restedForBlue && DefaultRestSeconds > 0.1f)
                    {
                        await DelaySeconds(DefaultRestSeconds, ct);
                        _restedForBlue = true;
                    }
                    EnsureFurnitureForAdaptation(meta.phase);
                    await DelaySeconds(DefaultAdaptSeconds, ct);
                    _blueAdapted = true;
                }
            }

            // Apply trial-specific furniture condition
            ApplyFurniture(meta.hasFurniture, meta.phase);

            // Camera FOV
            var fov = trial.fovDeg > 0 ? trial.fovDeg : DefaultFovDeg;
            _ctx?.stimulus?.SetCameraFOV(fov);

            // Place stimuli
            ClearSpawned();
            if (IsHumanSubject())
            {
                SpawnAdjustableSphere(meta);
            }
            else
            {
                SpawnCandidateGrid(meta, DefaultCandidateCount);
            }

            await Task.Yield();
        }

        public async Task OnAfterTrialAsync(TrialSpec trial, LLMResponse response, CancellationToken ct)
        {
            ClearSpawned();
            if (IsLastTrial(trial))
            {
                _furniture?.Clear();
                _currentFurniturePhase = null;
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

            if (response == null || response.type != "inference")
            {
                eval.success = false;
                eval.failureReason = "No inference response";
                return eval;
            }

            string choice = null;
            int[] predictedRgb = null;

            _metaByTrial.TryGetValue(trial, out var meta);

            if (!TryExtractAnswer(response.answer, out choice))
            {
                TryExtractFromText(response.explanation, out choice);
            }

            if (string.IsNullOrEmpty(choice))
            {
                eval.success = false;
                eval.failureReason = "No choice information found in model output";
                return eval;
            }

            if (meta != null && meta.candidateLabels != null && meta.candidateRgbs != null)
            {
                for (int i = 0; i < meta.candidateLabels.Length && i < meta.candidateRgbs.Length; i++)
                {
                    if (string.Equals(meta.candidateLabels[i], choice, StringComparison.OrdinalIgnoreCase))
                    {
                        predictedRgb = meta.candidateRgbs[i];
                        break;
                    }
                }
            }

            if (predictedRgb == null || predictedRgb.Length < 3)
            {
                eval.success = false;
                eval.failureReason = "Choice does not map to a valid candidate color";
                return eval;
            }

            predictedRgb = new[]
            {
                Mathf.Clamp(predictedRgb[0], 0, 255),
                Mathf.Clamp(predictedRgb[1], 0, 255),
                Mathf.Clamp(predictedRgb[2], 0, 255)
            };

            if (!_baselineSet && _metaByTrial.TryGetValue(trial, out var baselineMeta) &&
                string.Equals(baselineMeta.phase, "baseline", StringComparison.OrdinalIgnoreCase))
            {
                _baselineRgb = (int[])predictedRgb.Clone();
                _baselineSet = true;
            }

            int[] delta = null;
            if (_baselineSet && _baselineRgb != null && _baselineRgb.Length >= 3)
            {
                delta = new[]
                {
                    predictedRgb[0] - _baselineRgb[0],
                    predictedRgb[1] - _baselineRgb[1],
                    predictedRgb[2] - _baselineRgb[2]
                };
            }

            try
            {
                RgbTriplet[] candidates = null;
                if (meta != null && meta.candidateRgbs != null)
                {
                    candidates = new RgbTriplet[meta.candidateRgbs.Length];
                    for (int i = 0; i < meta.candidateRgbs.Length; i++)
                    {
                        var c = meta.candidateRgbs[i];
                        if (c == null || c.Length < 3)
                        {
                            candidates[i] = new RgbTriplet(0, 0, 0);
                        }
                        else
                        {
                            candidates[i] = new RgbTriplet(c[0], c[1], c[2]);
                        }
                    }
                }

                var extra = new ColorConstancyAdjustmentExtra
                {
                    phase = meta != null ? meta.phase : "unknown",
                    hasFurniture = meta != null && meta.hasFurniture,
                    lighting = meta != null ? meta.lighting : trial.lighting,
                    initialRgb = meta != null ? meta.initialRgb : null,
                    baselineRgb = _baselineRgb,
                    predictedRgb = predictedRgb,
                    deltaRgb = delta,
                    choice = choice,
                    candidateLabels = meta != null ? meta.candidateLabels : null,
                    candidateRgbs = candidates
                };
                eval.extraJson = JsonUtility.ToJson(extra);
            }
            catch
            {
                // ignore
            }

            eval.success = true;
            eval.isCorrect = true;
            return eval;
        }

        // =============== Helpers ===============

        private void TryBindHelpers()
        {
            if (_ctx?.runner != null)
            {
                if (_scene == null) _scene = _ctx.runner.GetComponent<ExperimentSceneManager>();
                if (_furniture == null) _furniture = _ctx.runner.GetComponent<FurnitureScatter>();
            }

            if (_scene == null) _scene = UnityEngine.Object.FindObjectOfType<ExperimentSceneManager>();
            if (_furniture == null) _furniture = UnityEngine.Object.FindObjectOfType<FurnitureScatter>();
        }

        private TrialSpec CreateTrial(string phase, bool hasFurniture, string lighting)
        {
            var rgb = RandomRgb();

            var trial = new TrialSpec
            {
                taskId = TaskId,
                environment = "room",
                background = hasFurniture ? "furniture" : "empty",
                lighting = lighting,
                fovDeg = DefaultFovDeg,
                textureDensity = 1.0f,
                occlusion = false,
                targetKind = "sphere",
                trueR = rgb[0],
                trueG = rgb[1],
                trueB = rgb[2]
            };

            var meta = new TrialMeta
            {
                phase = phase,
                hasFurniture = hasFurniture,
                lighting = lighting,
                initialRgb = rgb
            };

            if (!IsHumanSubject())
            {
                BuildCandidates(meta, DefaultCandidateCount);
            }

            _metaByTrial[trial] = meta;
            return trial;
        }

        private void AddBlock(List<TrialSpec> trials, string phase, string lighting, int repeatsPerCondition)
        {
            int total = Mathf.Max(1, repeatsPerCondition) * 2;
            for (int i = 0; i < total; i++)
            {
                bool hasFurniture = (i % 2 == 0);
                trials.Add(CreateTrial(phase, hasFurniture, lighting));
            }
        }

        private bool IsHumanSubject()
        {
            if (_ctx?.runner == null) return false;
            try
            {
                var field = typeof(TaskRunner).GetField("subjectMode", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null && field.GetValue(_ctx.runner) is SubjectMode mode)
                {
                    return mode == SubjectMode.Human;
                }
            }
            catch { }
            return false;
        }

        private void EnsureFurnitureForAdaptation(string phase)
        {
            if (_furniture == null) return;
            EnsureFurnitureLayout(phase);
            _furniture.SetActive(true);
        }

        private void ApplyFurniture(bool hasFurniture, string phase)
        {
            if (_furniture == null) return;
            if (!hasFurniture)
            {
                _furniture.SetActive(false);
                return;
            }

            EnsureFurnitureLayout(phase);
            _furniture.SetActive(true);
        }

        private void EnsureFurnitureLayout(string phase)
        {
            if (_furniture == null) return;
            var normalizedPhase = string.IsNullOrEmpty(phase) ? "unknown" : phase;
            if (!string.Equals(_currentFurniturePhase, normalizedPhase, StringComparison.OrdinalIgnoreCase) || !_furniture.HasSpawned)
            {
                var cam = ResolveCamera();
                _furniture.Spawn(_rand, cam != null ? cam.transform : null);
                _currentFurniturePhase = normalizedPhase;
            }
        }

        private bool IsLastTrial(TrialSpec trial)
        {
            int total = _plannedTrialCount;
            if (_ctx?.runner != null)
            {
                int maxTrials = _ctx.runner.CurrentMaxTrials;
                if (maxTrials > 0)
                {
                    total = Mathf.Min(total, maxTrials);
                }
            }

            return total > 0 && trial.trialId >= total - 1;
        }

        private void SpawnAdjustableSphere(TrialMeta meta)
        {
            var cam = ResolveCamera();
            if (cam == null) return;

            var origin = cam.transform.position;
            var forward = cam.transform.forward;

            var pos = origin + forward * AdjustableSphereDistance;
            pos.y = origin.y + AdjustableSphereYOffset;

            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "cc_adjustable_sphere";
            sphere.transform.position = pos;
            sphere.transform.localScale = Vector3.one * AdjustableSphereScale;

            var adjustable = sphere.AddComponent<ColorAdjustableTarget>();
            if (meta?.initialRgb != null && meta.initialRgb.Length >= 3)
            {
                adjustable.SetColor(ToColor(meta.initialRgb));
            }

            _spawned.Add(sphere);
        }

        private void SpawnCandidateGrid(TrialMeta meta, int count)
        {
            var cam = ResolveCamera();
            if (cam == null) return;

            if (meta == null) return;
            if (meta.candidateRgbs == null || meta.candidateRgbs.Length == 0)
            {
                BuildCandidates(meta, count);
            }

            int cols = Mathf.CeilToInt(Mathf.Sqrt(meta.candidateRgbs.Length));
            int rows = Mathf.CeilToInt(meta.candidateRgbs.Length / (float)cols);

            var center = cam.transform.position + cam.transform.forward * CandidateGridDistance;
            center.y = cam.transform.position.y;

            var right = cam.transform.right;
            var up = cam.transform.up;

            for (int i = 0; i < meta.candidateRgbs.Length; i++)
            {
                int row = i / cols;
                int col = i % cols;

                float offsetX = (col - (cols - 1) * 0.5f) * CandidateGridSpacing;
                float offsetY = ((rows - 1) * 0.5f - row) * CandidateGridSpacing;

                var pos = center + right * offsetX + up * offsetY;

                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = $"cc_choice_{meta.candidateLabels[i]}";
                sphere.transform.position = pos;
                sphere.transform.localScale = Vector3.one * CandidateSphereScale;

                var renderer = sphere.GetComponent<Renderer>();
                if (renderer != null)
                {
                    var mat = new Material(Shader.Find("Standard"));
                    mat.color = ToColor(meta.candidateRgbs[i]);
                    renderer.material = mat;
                }

                AddLabel(sphere.transform, meta.candidateLabels[i], cam.transform);
                _spawned.Add(sphere);
            }
        }

        private void AddLabel(Transform parent, string label, Transform cameraTransform)
        {
            var go = new GameObject($"label_{label}");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0.2f, 0f);

            var text = go.AddComponent<TextMesh>();
            text.text = label;
            text.fontSize = 48;
            text.characterSize = 0.05f;
            text.color = Color.white;
            text.anchor = TextAnchor.MiddleCenter;

            if (cameraTransform != null)
            {
                go.transform.rotation = Quaternion.LookRotation(cameraTransform.position - go.transform.position);
            }

            _spawned.Add(go);
        }

        private static Color ToColor(int[] rgb)
        {
            if (rgb == null || rgb.Length < 3) return Color.gray;
            return new Color(rgb[0] / 255f, rgb[1] / 255f, rgb[2] / 255f, 1f);
        }

        private int[] RandomRgb()
        {
            return new[]
            {
                _rand.Next(30, 226),
                _rand.Next(30, 226),
                _rand.Next(30, 226)
            };
        }

        private void BuildCandidates(TrialMeta meta, int count)
        {
            count = Mathf.Clamp(count, 2, 26);
            var rgbs = new int[count][];
            var labels = new string[count];
            for (int i = 0; i < count; i++)
            {
                rgbs[i] = RandomRgb();
                labels[i] = ((char)('A' + i)).ToString();
            }
            meta.candidateRgbs = rgbs;
            meta.candidateLabels = labels;
        }

        private Camera ResolveCamera()
        {
            if (_ctx?.stimulus != null && _ctx.stimulus.HeadCamera != null) return _ctx.stimulus.HeadCamera;
            return Camera.main;
        }

        private static async Task DelaySeconds(float seconds, CancellationToken ct)
        {
            if (seconds <= 0.01f) return;
            int ms = Mathf.RoundToInt(seconds * 1000f);
            await Task.Delay(ms, ct);
        }

        private void ClearSpawned()
        {
            for (int i = _spawned.Count - 1; i >= 0; i--)
            {
                var go = _spawned[i];
                if (go == null) { _spawned.RemoveAt(i); continue; }
#if UNITY_EDITOR
                UnityEngine.Object.DestroyImmediate(go);
#else
                UnityEngine.Object.Destroy(go);
#endif
                _spawned.RemoveAt(i);
            }
        }

        private static bool TryExtractAnswer(object answer, out string choice)
        {
            choice = null;
            if (answer == null) return false;

            try
            {
                var t = answer.GetType();
                var choiceProp = t.GetProperty("choice", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (choiceProp != null)
                {
                    var v = choiceProp.GetValue(answer)?.ToString();
                    if (!string.IsNullOrEmpty(v)) choice = v.Trim();
                }

                if (!string.IsNullOrEmpty(choice)) return true;

                var json = JsonUtility.ToJson(answer);
                if (!string.IsNullOrEmpty(json))
                {
                        var parsed = JsonUtility.FromJson<ColorChoiceAnswer>(json);
                        if (parsed != null)
                        {
                            if (!string.IsNullOrEmpty(parsed.choice)) choice = parsed.choice;
                            return !string.IsNullOrEmpty(choice);
                        }
                }
            }
            catch
            {
                // ignore
            }

            return false;
        }

        private static bool TryExtractFromText(string text, out string choice)
        {
            choice = null;
            if (string.IsNullOrEmpty(text)) return false;

            var mChoice = Regex.Match(text, "\"?choice\"?\\s*[:=]\\s*\"?([A-Za-z])\"?");
            if (mChoice.Success)
            {
                choice = mChoice.Groups[1].Value;
            }
            else
            {
                var mLetter = Regex.Match(text, "\\b([A-Z])\\b");
                if (mLetter.Success) choice = mLetter.Groups[1].Value;
            }

            return !string.IsNullOrEmpty(choice);
        }

        [Serializable]
        private class ColorChoiceAnswer
        {
            public string choice;
            public float confidence;
        }

        [Serializable]
        private struct RgbTriplet
        {
            public int r;
            public int g;
            public int b;

            public RgbTriplet(int r, int g, int b)
            {
                this.r = r;
                this.g = g;
                this.b = b;
            }
        }

        [Serializable]
        private class ColorConstancyAdjustmentExtra
        {
            public string phase;
            public bool hasFurniture;
            public string lighting;
            public int[] initialRgb;
            public int[] baselineRgb;
            public int[] predictedRgb;
            public int[] deltaRgb;
            public string choice;
            public string[] candidateLabels;
            public RgbTriplet[] candidateRgbs;
        }

        private class TrialMeta
        {
            public string phase;
            public bool hasFurniture;
            public string lighting;
            public int[] initialRgb;
            public string[] candidateLabels;
            public int[][] candidateRgbs;
        }
    }
}
