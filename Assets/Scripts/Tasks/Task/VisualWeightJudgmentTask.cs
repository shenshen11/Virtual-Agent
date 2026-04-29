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
    /// 外观线索冲突下的视觉重量判断（Visual Weight Judgment under Cue Conflict）
    /// - 自变量：大小（small/large）× 材质重量感（heavy/light）× 明度（dark/light）
    /// - 每次同时呈现两个立方体（A=左, B=右），被试判断"哪个看起来更重"
    /// - 核心：冲突题中记录线索偏好（size vs material vs lightness）
    /// - 输出：{"type":"inference","answer":{"heavier":"A"|"B"},"confidence":0..1}
    /// </summary>
    public class VisualWeightJudgmentTask : ITask, ITaskRunLifecycle
    {
        public string TaskId => "visual_weight_judgment";

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(1234);

        private ExperimentSceneManager _scene;
        private ObjectPlacer _placer;
        private bool _referenceFrameInitialized;
        private Vector3 _referenceOrigin;
        private Vector3 _referenceForward;
        private float _referenceEyeY;

        // ── 布局常量 ──
        private const float SmallScale = 0.40f;
        private const float LargeScale = 0.50f;
        private const float PlacementDistance = 3.0f;
        private const float LateralOffset = 0.55f;

        private const string MaterialAssetRoot = "VisualWeightJudgment";
        private static readonly string[] HeavyBaseVariants = { "Metal", "Stone" };
        private static readonly string[] LightBaseVariants = { "Wood", "Fabric" };

        private int _heavyVariantCursor;
        private int _lightVariantCursor;

        // ── 材质缓存 ──
        private readonly Dictionary<string, Material> _matCache =
            new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

        public VisualWeightJudgmentTask(TaskRunnerContext ctx)
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
                    stimulus = runner ? runner.GetComponent<StimulusCapture>() : null,
                    humanReferenceFrame = runner ? runner.GetComponent<HumanReferenceFrameService>() : null
                };
            }
            else if (_ctx.humanReferenceFrame == null)
            {
                _ctx.humanReferenceFrame = runner ? runner.GetComponent<HumanReferenceFrameService>() : null;
            }
            TryBindHelpers();
        }

        public Task OnRunBeginAsync(CancellationToken ct)
        {
            TryBindHelpers();
            if (IsHumanMode() && !TryUseHumanSharedReferenceFrame())
            {
                CaptureReferenceFrameIfNeeded(forceRefresh: true);
            }
            else if (!IsHumanMode())
            {
                _referenceFrameInitialized = false;
            }

            return Task.CompletedTask;
        }

        public Task OnRunEndAsync(CancellationToken ct)
        {
            _referenceFrameInitialized = false;
            _matCache.Clear();
            TryDestroyFallbackObjects();
            return Task.CompletedTask;
        }

        // ════════════════════════════════════════════
        //  BuildTrials — 28 trials, ≤30
        // ════════════════════════════════════════════
        public TrialSpec[] BuildTrials(int seed)
        {
            _rand = new System.Random(seed);
            _heavyVariantCursor = Mathf.Abs(seed) % HeavyBaseVariants.Length;
            _lightVariantCursor = Mathf.Abs(seed / HeavyBaseVariants.Length) % LightBaseVariants.Length;
            var trials = new List<TrialSpec>(28);

            // ── 冲突题 12 条（核心）──
            // Type A: material+lightness vs size  （小暗重感材质 vs 大亮轻感材质）
            AddPair(trials, "conflict_ml_vs_s", "dark_heavy", SmallScale, "light_light", LargeScale, 2);
            // Type B: size+material vs lightness  （大亮重感材质 vs 小暗轻感材质）
            AddPair(trials, "conflict_sm_vs_l", "light_heavy", LargeScale, "dark_light", SmallScale, 2);
            // Type C: size+lightness vs material  （大暗轻感材质 vs 小亮重感材质）
            AddPair(trials, "conflict_sl_vs_m", "dark_light", LargeScale, "light_heavy", SmallScale, 2);

            // ── 一致题 4 条（验证）──
            AddPair(trials, "congruent", "dark_heavy", LargeScale, "light_light", SmallScale, 2);

            // ── 单线索控制 12 条 ──
            // size only
            AddPair(trials, "size_only", "dark_heavy", LargeScale, "dark_heavy", SmallScale, 1);
            AddPair(trials, "size_only", "light_light", LargeScale, "light_light", SmallScale, 1);
            // material only
            AddPair(trials, "material_only", "dark_heavy", SmallScale, "dark_light", SmallScale, 1);
            AddPair(trials, "material_only", "dark_heavy", LargeScale, "dark_light", LargeScale, 1);
            // lightness only
            AddPair(trials, "lightness_only", "dark_heavy", SmallScale, "light_heavy", SmallScale, 1);
            AddPair(trials, "lightness_only", "dark_light", LargeScale, "light_light", LargeScale, 1);

            Shuffle(trials);
            return trials.ToArray();
        }

        private void AddPair(List<TrialSpec> list, string trialType,
            string descA, float sA, string descB, float sB, int repeats)
        {
            for (int r = 0; r < repeats; r++)
            {
                list.Add(MakeTrial(trialType, descA, sA, descB, sB));
                list.Add(MakeTrial(trialType, descB, sB, descA, sA)); // 左右互换
            }
        }

        private TrialSpec MakeTrial(string trialType, string descA, float sA, string descB, float sB)
        {
            var trial = new TrialSpec
            {
                taskId = TaskId,
                weightDescA = descA,
                weightDescB = descB,
                scaleA = sA,
                scaleB = sB,
                weightTrialType = trialType,
                environment = "open_field",
                lighting = "bright",
                fovDeg = 60f,
                textureDensity = 1.0f,
                occlusion = false
            };

            AssignMaterialVariants(trial);
            return trial;
        }

        // ════════════════════════════════════════════
        //  Prompt
        // ════════════════════════════════════════════
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
            return PromptTemplates.BuildVisualWeightJudgmentPrompt(trial.trialId);
        }

        // ════════════════════════════════════════════
        //  Scene setup
        // ════════════════════════════════════════════
        public async Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            TryBindHelpers();
            _placer?.SetActiveTrialContext(trial.taskId, trial.trialId);
            if (IsHumanMode() && !TryUseHumanSharedReferenceFrame())
            {
                CaptureReferenceFrameIfNeeded(forceRefresh: false);
            }

            if (_scene != null)
            {
                _scene.SetupEnvironment(
                    trial.environment ?? "open_field",
                    trial.textureDensity,
                    trial.lighting ?? "bright",
                    trial.occlusion);
            }

            _ctx?.stimulus?.SetCameraFOV(trial.fovDeg > 0 ? trial.fovDeg : 60f);
            PlacePair(trial);
            await Task.Yield();
        }

        public async Task OnAfterTrialAsync(TrialSpec trial, LLMResponse response, CancellationToken ct)
        {
            if (_placer != null)
            {
                _placer.ClearAll();
                _placer.ClearActiveTrialContext();
            }
            TryDestroyFallbackObjects();
            await Task.Yield();
        }

        private void PlacePair(TrialSpec trial)
        {
            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null) return;

            ResolvePlacementReference(cam, out var origin, out var forward, out var right, out var eyeY);

            var center = origin + forward * PlacementDistance;
            right *= LateralOffset;

            var posA = center - right; // 左
            var posB = center + right; // 右

            // 底部对齐：让两个立方体底面在同一高度
            float groundY = eyeY - 0.3f;
            posA.y = groundY + trial.scaleA * 0.5f;
            posB.y = groundY + trial.scaleB * 0.5f;

            var matA = GetMaterial(trial.materialVariantA);
            var matB = GetMaterial(trial.materialVariantB);

            if (_placer != null)
            {
                _placer.Place("cube", posA, trial.scaleA, matA, "vwj_A");
                _placer.Place("cube", posB, trial.scaleB, matB, "vwj_B");
            }
            else
            {
                var goA = GameObject.CreatePrimitive(PrimitiveType.Cube);
                goA.name = "vwj_A";
                goA.transform.position = posA;
                goA.transform.localScale = Vector3.one * trial.scaleA;
                TrialObjectMarker.AttachOrUpdate(goA, trial.taskId, trial.trialId, $"{trial.taskId}_{trial.trialId}_vwj_A", "cube", "target");
                var rA = goA.GetComponent<Renderer>();
                if (rA != null && matA != null) rA.sharedMaterial = matA;

                var goB = GameObject.CreatePrimitive(PrimitiveType.Cube);
                goB.name = "vwj_B";
                goB.transform.position = posB;
                goB.transform.localScale = Vector3.one * trial.scaleB;
                TrialObjectMarker.AttachOrUpdate(goB, trial.taskId, trial.trialId, $"{trial.taskId}_{trial.trialId}_vwj_B", "cube", "target");
                var rB = goB.GetComponent<Renderer>();
                if (rB != null && matB != null) rB.sharedMaterial = matB;
            }
        }

        // ════════════════════════════════════════════
        //  Evaluate
        // ════════════════════════════════════════════
        public TrialEvaluation Evaluate(TrialSpec trial, LLMResponse response)
        {
            var eval = new TrialEvaluation
            {
                responseType = response?.type,
                providerId = response?.providerId,
                latencyMs = response?.latencyMs ?? 0,
                confidence = response?.confidence ?? 0,
                weightTrialType = trial.weightTrialType
            };

            string predicted = null;
            if (response != null && response.type == "inference")
            {
                if (!TryExtractHeavier(response.answer, out predicted))
                    TryExtractHeavierFromText(response.explanation, out predicted);
            }

            if (string.IsNullOrEmpty(predicted))
            {
                eval.success = false;
                eval.failureReason = "No heavier (A/B) found in model output";
                return eval;
            }

            var rawPredicted = predicted;
            if (!TryNormalizeSide(rawPredicted, out predicted))
            {
                eval.success = false;
                eval.failureReason = $"Invalid heavier value: {rawPredicted}";
                return eval;
            }

            eval.predictedHeavierSide = predicted;
            eval.success = true;

            var type = trial.weightTrialType ?? "";

            if (type.StartsWith("conflict", StringComparison.OrdinalIgnoreCase))
            {
                eval.cueFollowed = DetermineFollowedCue(trial, predicted);
                // 冲突题无绝对正确
            }
            else
            {
                // 一致题 / 控制题有客观正确答案
                var expected = DetermineExpectedHeavier(trial);
                if (!string.IsNullOrEmpty(expected))
                {
                    eval.isCorrect = string.Equals(predicted, expected, StringComparison.OrdinalIgnoreCase);
                }
            }

            try
            {
                var extra = new WeightExtra
                {
                    descA = trial.weightDescA,
                    descB = trial.weightDescB,
                    materialVariantA = trial.materialVariantA,
                    materialVariantB = trial.materialVariantB,
                    scaleA = trial.scaleA,
                    scaleB = trial.scaleB,
                    trialType = trial.weightTrialType,
                    cueFollowed = eval.cueFollowed
                };
                eval.extraJson = JsonUtility.ToJson(extra);
            }
            catch { /* ignore */ }

            return eval;
        }

        // ════════════════════════════════════════════
        //  Cue logic helpers
        // ════════════════════════════════════════════

        private static bool TryNormalizeSide(string value, out string side)
        {
            side = null;
            if (string.IsNullOrWhiteSpace(value)) return false;

            var normalized = value.Trim().Trim('"').ToUpperInvariant();
            if (normalized != "A" && normalized != "B") return false;

            side = normalized;
            return true;
        }

        /// <summary>
        /// 判断每条线索指向 A 还是 B 更重。
        /// heavier cue: larger size, heavy material group > light material group, dark > light
        /// </summary>
        private static (string size, string material, string lightness) GetCueDirections(TrialSpec trial)
        {
            // size
            string sizeCue = trial.scaleA > trial.scaleB ? "A" :
                             trial.scaleB > trial.scaleA ? "B" : null;

            // material: heavy group > light group
            bool aIsHeavy = IsHeavyMaterialGroup(trial.weightDescA);
            bool bIsHeavy = IsHeavyMaterialGroup(trial.weightDescB);
            string matCue = aIsHeavy && !bIsHeavy ? "A" :
                            bIsHeavy && !aIsHeavy ? "B" : null;

            // lightness: dark > light
            bool aIsDark = (trial.weightDescA ?? "").StartsWith("dark", StringComparison.OrdinalIgnoreCase);
            bool bIsDark = (trial.weightDescB ?? "").StartsWith("dark", StringComparison.OrdinalIgnoreCase);
            string lightCue = aIsDark && !bIsDark ? "A" :
                              bIsDark && !aIsDark ? "B" : null;

            return (sizeCue, matCue, lightCue);
        }

        private static string DetermineExpectedHeavier(TrialSpec trial)
        {
            var (sizeCue, matCue, lightCue) = GetCueDirections(trial);
            var type = (trial.weightTrialType ?? "").ToLowerInvariant();

            if (type == "congruent")
            {
                // 三条线索都指向同一边
                return sizeCue ?? matCue ?? lightCue;
            }
            if (type == "size_only") return sizeCue;
            if (type == "material_only") return matCue;
            if (type == "lightness_only") return lightCue;

            return null; // conflict → 无客观正确
        }

        private static string DetermineFollowedCue(TrialSpec trial, string predicted)
        {
            var (sizeCue, matCue, lightCue) = GetCueDirections(trial);

            bool followedSize = sizeCue != null && string.Equals(predicted, sizeCue, StringComparison.OrdinalIgnoreCase);
            bool followedMat  = matCue  != null && string.Equals(predicted, matCue,  StringComparison.OrdinalIgnoreCase);
            bool followedLight= lightCue!= null && string.Equals(predicted, lightCue,StringComparison.OrdinalIgnoreCase);

            // conflict_ml_vs_s: material+lightness vs size
            if (followedMat && followedLight && !followedSize) return "material+lightness";
            if (followedSize && !followedMat && !followedLight) return "size";
            // conflict_sm_vs_l: size+material vs lightness
            if (followedSize && followedMat && !followedLight) return "size+material";
            if (followedLight && !followedSize && !followedMat) return "lightness";
            // conflict_sl_vs_m: size+lightness vs material
            if (followedSize && followedLight && !followedMat) return "size+lightness";
            if (followedMat && !followedSize && !followedLight) return "material";

            return "unknown";
        }

        // ════════════════════════════════════════════
        //  Material selection
        // ════════════════════════════════════════════
        private void AssignMaterialVariants(TrialSpec trial)
        {
            if (!TryParseAppearanceDescriptor(trial.weightDescA, out var lightnessA, out var groupA) ||
                !TryParseAppearanceDescriptor(trial.weightDescB, out var lightnessB, out var groupB))
            {
                trial.materialVariantA = null;
                trial.materialVariantB = null;
                return;
            }

            string baseA;
            string baseB;

            if (string.Equals(trial.weightDescA, trial.weightDescB, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(groupA, groupB, StringComparison.OrdinalIgnoreCase))
            {
                baseA = baseB = NextBaseVariant(groupA);
            }
            else
            {
                baseA = NextBaseVariant(groupA);
                baseB = NextBaseVariant(groupB);
            }

            trial.materialVariantA = BuildVariantName(baseA, lightnessA);
            trial.materialVariantB = BuildVariantName(baseB, lightnessB);
        }

        private string NextBaseVariant(string materialGroup)
        {
            if (string.Equals(materialGroup, "heavy", StringComparison.OrdinalIgnoreCase))
            {
                return HeavyBaseVariants[_heavyVariantCursor++ % HeavyBaseVariants.Length];
            }

            return LightBaseVariants[_lightVariantCursor++ % LightBaseVariants.Length];
        }

        private static string BuildVariantName(string baseName, string lightness)
        {
            return $"{baseName}_{ToTitleCase(lightness)}";
        }

        private Material GetMaterial(string variant)
        {
            if (string.IsNullOrEmpty(variant)) return null;

            if (_matCache.TryGetValue(variant, out var cached) && cached != null)
                return cached;

            var mat = Resources.Load<Material>($"{MaterialAssetRoot}/VWJ_{variant}");
            if (mat == null)
            {
                Debug.LogWarning($"VisualWeightJudgment material not found: Resources/{MaterialAssetRoot}/VWJ_{variant}.mat");
            }

            _matCache[variant] = mat;
            return mat;
        }

        private static bool TryParseAppearanceDescriptor(string desc, out string lightness, out string materialGroup)
        {
            lightness = null;
            materialGroup = null;
            if (string.IsNullOrWhiteSpace(desc)) return false;

            var parts = desc.Trim().Split('_');
            if (parts.Length != 2) return false;

            lightness = parts[0].ToLowerInvariant();
            materialGroup = parts[1].ToLowerInvariant();

            bool validLightness = lightness == "dark" || lightness == "light";
            bool validGroup = materialGroup == "heavy" || materialGroup == "light";
            return validLightness && validGroup;
        }

        private static bool IsHeavyMaterialGroup(string desc)
        {
            return TryParseAppearanceDescriptor(desc, out _, out var materialGroup) &&
                   materialGroup == "heavy";
        }

        private static string ToTitleCase(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (value.Length == 1) return value.ToUpperInvariant();
            return char.ToUpperInvariant(value[0]) + value.Substring(1).ToLowerInvariant();
        }

        // ════════════════════════════════════════════
        //  Response parsing
        // ════════════════════════════════════════════
        private static bool TryExtractHeavier(object answer, out string heavier)
        {
            heavier = null;
            if (answer == null) return false;

            try
            {
                var t = answer.GetType();
                var prop = t.GetProperty("heavier", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var v = prop.GetValue(answer)?.ToString();
                    if (TryNormalizeSide(v, out heavier)) return true;
                }

                var field = t.GetField("heavier", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    var v = field.GetValue(answer)?.ToString();
                    if (TryNormalizeSide(v, out heavier)) return true;
                }

                var rawJsonProp = t.GetProperty("raw_json", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (rawJsonProp != null)
                {
                    var raw = rawJsonProp.GetValue(answer)?.ToString();
                    if (TryExtractHeavierFromString(raw, out heavier)) return true;
                }

                var rawContentProp = t.GetProperty("raw_content", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (rawContentProp != null)
                {
                    var raw = rawContentProp.GetValue(answer)?.ToString();
                    if (TryExtractHeavierFromString(raw, out heavier)) return true;
                }

                var json = JsonUtility.ToJson(answer);
                if (!string.IsNullOrEmpty(json))
                {
                    var parsed = JsonUtility.FromJson<WeightAnswer>(json);
                    if (parsed != null && TryNormalizeSide(parsed.heavier, out heavier))
                    {
                        return true;
                    }

                    if (TryExtractHeavierFromString(json, out heavier)) return true;
                }
            }
            catch { /* ignore */ }

            try { return TryExtractHeavierFromString(answer.ToString(), out heavier); }
            catch { return false; }
        }

        private static bool TryExtractHeavierFromText(string text, out string heavier)
        {
            return TryExtractHeavierFromString(text, out heavier);
        }

        private static bool TryExtractHeavierFromString(string text, out string heavier)
        {
            heavier = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var trimmed = text.Trim();
            if (TryNormalizeSide(trimmed, out heavier)) return true;

            var directJson = Regex.Match(trimmed, @"\bheavier\b\s*[:=]\s*[\""']?([AB])[\""']?", RegexOptions.IgnoreCase);
            if (directJson.Success)
            {
                heavier = directJson.Groups[1].Value.ToUpperInvariant();
                return true;
            }

            var nestedJson = Regex.Match(trimmed, @"\bresponse\b.*?\bheavier\b\s*[:=]\s*[\""']?([AB])[\""']?", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (nestedJson.Success)
            {
                heavier = nestedJson.Groups[1].Value.ToUpperInvariant();
                return true;
            }

            var sentence = Regex.Match(trimmed, @"\bheavier\b[^AB\r\n]*\b([AB])\b", RegexOptions.IgnoreCase);
            if (sentence.Success)
            {
                heavier = sentence.Groups[1].Value.ToUpperInvariant();
                return true;
            }

            return false;
        }

        // ════════════════════════════════════════════
        //  Utilities
        // ════════════════════════════════════════════
        private void TryBindHelpers()
        {
            if (_ctx?.runner != null)
            {
                if (_scene == null) _scene = _ctx.runner.GetComponent<ExperimentSceneManager>();
                if (_placer == null) _placer = _ctx.runner.GetComponent<ObjectPlacer>();
                if (_ctx.humanReferenceFrame == null) _ctx.humanReferenceFrame = _ctx.runner.GetComponent<HumanReferenceFrameService>();
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

            _referenceOrigin = cam.transform.position;
            _referenceForward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (_referenceForward.sqrMagnitude < 1e-6f) _referenceForward = Vector3.forward;
            _referenceForward.Normalize();
            _referenceEyeY = cam.transform.position.y;
            _referenceFrameInitialized = true;
        }

        private bool TryUseHumanSharedReferenceFrame()
        {
            if (!IsHumanMode()) return false;

            var humanRef = _ctx?.humanReferenceFrame;
            if (humanRef == null || !humanRef.HasReferenceFrame) return false;

            _referenceOrigin = humanRef.Origin;
            _referenceForward = Vector3.ProjectOnPlane(humanRef.Forward, Vector3.up);
            if (_referenceForward.sqrMagnitude < 1e-6f) _referenceForward = Vector3.forward;
            _referenceForward.Normalize();
            _referenceEyeY = humanRef.EyeY;
            _referenceFrameInitialized = true;
            return true;
        }

        private void ResolvePlacementReference(Camera cam, out Vector3 origin, out Vector3 forward, out Vector3 right, out float eyeY)
        {
            var fallbackRight = cam != null ? cam.transform.right : Vector3.right;
            if (TryUseHumanSharedReferenceFrame())
            {
                origin = _referenceOrigin;
                forward = _referenceForward;
                eyeY = _referenceEyeY;

                var humanRef = _ctx?.humanReferenceFrame;
                if (humanRef != null && humanRef.HasReferenceFrame && humanRef.Right.sqrMagnitude >= 1e-6f)
                    fallbackRight = humanRef.Right;

                right = ComputePlacementRight(forward, fallbackRight);
                return;
            }

            origin = _referenceFrameInitialized ? _referenceOrigin : cam.transform.position;
            forward = _referenceFrameInitialized ? _referenceForward : Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();
            eyeY = _referenceFrameInitialized ? _referenceEyeY : cam.transform.position.y;
            right = ComputePlacementRight(forward, fallbackRight);
        }

        private Vector3 ComputePlacementRight(Vector3 forward, Vector3 fallbackRight)
        {
            var horizontalForward = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (horizontalForward.sqrMagnitude < 1e-6f) horizontalForward = Vector3.forward;
            horizontalForward.Normalize();

            var right = Vector3.Cross(Vector3.up, horizontalForward);
            if (right.sqrMagnitude >= 1e-6f)
            {
                right.Normalize();
                return right;
            }

            var projectedFallback = Vector3.ProjectOnPlane(fallbackRight, Vector3.up);
            if (projectedFallback.sqrMagnitude >= 1e-6f)
            {
                projectedFallback.Normalize();
                return projectedFallback;
            }

            return Vector3.right;
        }

        private bool IsHumanMode()
        {
            return _ctx?.runner != null && _ctx.runner.CurrentSubjectMode == SubjectMode.Human;
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

        private static void TryDestroyFallbackObjects()
        {
            GameObject[] all;
            try { all = Resources.FindObjectsOfTypeAll<GameObject>(); }
            catch { return; }

            foreach (var go in all)
            {
                if (go == null) continue;
                if (!go.scene.IsValid()) continue;
                if (!string.Equals(go.name, "vwj_A", StringComparison.Ordinal) &&
                    !string.Equals(go.name, "vwj_B", StringComparison.Ordinal)) continue;
#if UNITY_EDITOR
                UnityEngine.Object.DestroyImmediate(go);
#else
                UnityEngine.Object.Destroy(go);
#endif
            }
        }

        [Serializable]
        private class WeightAnswer
        {
            public string heavier;
            public float confidence;
        }

        [Serializable]
        private class WeightExtra
        {
            public string descA;
            public string descB;
            public string materialVariantA;
            public string materialVariantB;
            public float scaleA;
            public float scaleB;
            public string trialType;
            public string cueFollowed;
        }
    }
}
