using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;
using VRPerception.UI;

namespace VRPerception.Tasks
{
    /// <summary>
    /// 变化检测任务（Change Detection）
    /// - 单场景时序：A -> mask -> B
    /// - 人类与 MLLM 共享相同的时序呈现；MLLM 额外抓取 before/after 两帧作为输入
    /// - 目标：判断是否发生变化，并给出变化类别（appearance/disappearance/movement/replacement/none）
    /// </summary>
    public class ChangeDetectionTask : ITask, ITemporalInferenceTask, ITaskRunLifecycle
    {
        public string TaskId => "change_detection";

        private const string SceneObjectPrefix = "cd_";
        private const int SceneRenderSettleFrames = 5;
        private const int SceneRenderSettleDelayMs = 50;
        private const int SceneAExposureMs = 2000;
        private const int MaskDurationMs = 500;
        private const float ClusterDistance = 9f;
        private const int ChangeCategoryRepetitions = 5;
        private const int GridRows = 4;
        private const int GridCols = 4;
        private const int GridCandidateCount = GridRows * GridCols;
        private const int MinBaseObjectCount = 10;
        private const int MaxBaseObjectCount = 12;
        private const float GridSpacingX = 1.00f;
        private const float GridSpacingY = 0.70f;
        private const float GridJitterX = 0.30f;
        private const float GridJitterY = 0.08f;
        private const float DisplayObjectScale = 0.48f;
        private const float DisplayPlaneMinCenterY = 1.60f;

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(1234);

        private ExperimentSceneManager _scene;
        private ObjectPlacer _placer;
        private TrialBlackoutOverlay _blackoutOverlay;
        private Material _grayMaterial;

        // 统一灰色材质（去掉颜色线索，迫使依赖空间/形状检测变化）
        private static readonly Color s_grayColor = new Color(0.5f, 0.5f, 0.5f);

        // 形状池：4x4 隐式候选格中固定抽取 12 个物体，形状均衡重复。
        private static readonly string[] s_shapePool =
        {
            "cube", "sphere", "cylinder", "capsule",
            "cube", "sphere", "cylinder", "capsule",
            "cube", "sphere", "cylinder", "capsule"
        };

        private static readonly string[] s_changeCategories = { "none", "appearance", "disappearance", "movement", "replacement" };

        private Vector3 _sceneCenter;
        private Vector3 _sceneRight;
        private bool _sceneAnchorReady;
        private bool _referenceFrameInitialized;
        private Vector3 _referenceOrigin;
        private Vector3 _referenceForward;
        private float _referenceEyeY;

        private struct GridObjectSpec
        {
            public int CandidateIndex;
            public Vector3 LocalPosition;
            public string Kind;
            public float Scale;
        }

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
                    stimulus = runner ? runner.GetComponent<StimulusCapture>() : null,
                    humanReferenceFrame = runner ? runner.GetComponent<HumanReferenceFrameService>() : null,
                    trialObjectCsvRecorder = runner ? runner.GetComponent<TrialObjectCsvRecorder>() : null
                };
            }
            else
            {
                if (_ctx.humanReferenceFrame == null)
                {
                    _ctx.humanReferenceFrame = runner ? runner.GetComponent<HumanReferenceFrameService>() : null;
                }

                if (_ctx.trialObjectCsvRecorder == null)
                {
                    _ctx.trialObjectCsvRecorder = runner ? runner.GetComponent<TrialObjectCsvRecorder>() : null;
                }
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
            HideBlackout();
            DestroyMaterial(_grayMaterial);
            _grayMaterial = null;
            _referenceFrameInitialized = false;
            _sceneAnchorReady = false;
            return Task.CompletedTask;
        }

        public TrialSpec[] BuildTrials(int seed)
        {
            _rand = new System.Random(seed);

            var trials = new List<TrialSpec>();
            const string background = "indoor";
            const float fov = 60f;

            foreach (var cat in s_changeCategories)
            {
                for (int rep = 0; rep < ChangeCategoryRepetitions; rep++)
                {
                    int sceneVariantSeed = _rand.Next();
                    int baseObjectCount = ResolveBaseObjectCount(sceneVariantSeed);
                    bool changed = !string.Equals(cat, "none", StringComparison.OrdinalIgnoreCase);
                    trials.Add(new TrialSpec
                    {
                        taskId = TaskId,
                        environment = "open_field",
                        background = background,
                        fovDeg = fov,
                        lighting = BackgroundToLighting(background),
                        occlusion = false,
                        changed = changed,
                        changeCategory = cat,
                        sceneVariantSeed = sceneVariantSeed,
                        changeTargetObjectIndex = _rand.Next(0, baseObjectCount)
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
            // 变化检测为纯推理任务：模型直接看 before/after 双帧作答，不需要工具调用
            return null;
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            return PromptTemplates.BuildChangeDetectionPrompt(trial.trialId);
        }

        public async Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            TryBindHelpers();
            _placer?.SetActiveTrialContext(trial.taskId, trial.trialId);
            if (!TryUseHumanSharedReferenceFrame())
            {
                CaptureReferenceFrameIfNeeded(forceRefresh: false);
            }

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

            PrepareSceneAnchor();
            PlaceSceneA(trial);
            RecordTrialObjects(trial, "before");

            // 等待渲染完成（确保物体完全渲染后再进入 A->mask->B 时序）
            await WaitForRenderingComplete(ct);
        }

        public async Task OnAfterTrialAsync(TrialSpec trial, LLMResponse response, CancellationToken ct)
        {
            bool keepMaskedBetweenTrials = response == null ||
                                           string.Equals(response.providerId, "human", StringComparison.OrdinalIgnoreCase);

            if (keepMaskedBetweenTrials)
            {
                ShowBlackout();
            }
            else
            {
                HideBlackout();
            }

            ClearChangeScene();
            _placer?.ClearActiveTrialContext();
            _sceneAnchorReady = false;
            await Task.Yield();
        }

        public async Task RunTemporalHumanPresentationAsync(TrialSpec trial, CancellationToken ct)
        {
            await RunTemporalSequenceAsync(trial, captureFrames: false, ct);
        }

        public async Task<LLMResponse> RunTemporalMllmInferenceAsync(TrialSpec trial, CancellationToken ct)
        {
            var frames = await RunTemporalSequenceAsync(trial, captureFrames: true, ct);
            if (_ctx?.perception == null)
            {
                throw new InvalidOperationException("PerceptionSystem not available for change_detection temporal inference.");
            }

            HideBlackout();

            return await _ctx.perception.RequestInferenceFromFramesAsync(
                trial.taskId,
                trial.trialId,
                GetSystemPrompt(),
                BuildTaskPrompt(trial),
                GetTools(),
                frames,
                CreateCaptureOptions(trial, "temporal_pair"),
                ct
            );
        }

        public TrialEvaluation Evaluate(TrialSpec trial, LLMResponse response)
        {
            var eval = new TrialEvaluation
            {
                responseType = response?.type,
                providerId = response?.providerId,
                latencyMs = response?.latencyMs ?? 0,
                confidence = response?.confidence ?? 0,
                trueChanged = trial.changed
            };

            var trueCategory = NormalizeTrialCategory(trial.changeCategory);
            eval.trueChangeCategory = string.IsNullOrEmpty(trueCategory) ? "none" : trueCategory;

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
                    eval.isCorrect = !eval.predictedChanged &&
                                     string.Equals(eval.predictedChangeCategory, "none",
                                         StringComparison.OrdinalIgnoreCase);
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
            if (_blackoutOverlay == null) _blackoutOverlay = TrialBlackoutOverlay.Instance;
            if (_blackoutOverlay == null) _blackoutOverlay = UnityEngine.Object.FindObjectOfType<TrialBlackoutOverlay>();
        }

        private async Task<List<FrameCapturedEventData>> RunTemporalSequenceAsync(TrialSpec trial, bool captureFrames, CancellationToken ct)
        {
            TryBindHelpers();
            PrepareSceneAnchor();
            HideBlackout();

            await WaitForRenderingComplete(ct);

            List<FrameCapturedEventData> frames = captureFrames ? new List<FrameCapturedEventData>(2) : null;

            if (captureFrames)
            {
                frames.Add(await CaptureCurrentFrameAsync(trial, "before", ct));
            }

            if (SceneAExposureMs > 0)
            {
                await Task.Delay(SceneAExposureMs, ct);
            }

            ShowBlackout();
            ApplySceneB(trial);
            RecordTrialObjects(trial, "after");
            await WaitForRenderingComplete(ct);

            if (MaskDurationMs > 0)
            {
                await Task.Delay(MaskDurationMs, ct);
            }

            HideBlackout();
            await WaitForRenderingComplete(ct);

            if (captureFrames)
            {
                frames.Add(await CaptureCurrentFrameAsync(trial, "after", ct));
            }

            return frames;
        }

        private async Task<FrameCapturedEventData> CaptureCurrentFrameAsync(TrialSpec trial, string label, CancellationToken ct)
        {
            if (_ctx?.perception == null)
            {
                throw new InvalidOperationException("PerceptionSystem not available for change_detection frame capture.");
            }

            return await _ctx.perception.CaptureFrameAsync(
                trial.taskId,
                trial.trialId,
                CreateCaptureOptions(trial, label),
                ct
            );
        }

        private static FrameCaptureOptions CreateCaptureOptions(TrialSpec trial, string label)
        {
            return new FrameCaptureOptions
            {
                captureMode = CaptureMode.SingleImage,
                trajectoryMode = CaptureTrajectoryMode.Fixed,
                fov = trial.fovDeg > 0 ? trial.fovDeg : 60f,
                width = 1280,
                height = 720,
                format = "jpeg",
                quality = 75,
                includeMetadata = true,
                label = label
            };
        }

        private async Task WaitForRenderingComplete(CancellationToken ct)
        {
            for (int i = 0; i < SceneRenderSettleFrames; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            if (SceneRenderSettleDelayMs > 0)
            {
                await Task.Delay(SceneRenderSettleDelayMs, ct);
            }
        }

        private void PrepareSceneAnchor()
        {
            if (_sceneAnchorReady) return;

            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null)
            {
                throw new InvalidOperationException("No head camera available for change_detection.");
            }

            ResolvePlacementReference(cam, out var origin, out var sceneForward, out var eyeY);
            if (sceneForward.sqrMagnitude < 0.0001f)
            {
                sceneForward = Vector3.forward;
            }

            _sceneRight = Vector3.Cross(Vector3.up, sceneForward).normalized;
            _sceneCenter = origin + sceneForward * ClusterDistance;
            _sceneCenter.y = Mathf.Max(eyeY, DisplayPlaneMinCenterY);
            _sceneAnchorReady = true;
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

        private void PlaceSceneA(TrialSpec trial)
        {
            ClearChangeScene();
            PlaceGridScene(
                SceneObjectPrefix,
                null,
                trial != null ? trial.sceneVariantSeed : 0,
                trial != null ? Mathf.Clamp(trial.changeTargetObjectIndex, 0, ResolveBaseObjectCount(trial.sceneVariantSeed) - 1) : 0);
        }

        private void ApplySceneB(TrialSpec trial)
        {
            ClearChangeScene();
            var category = NormalizeTrialCategory(trial.changeCategory);
            PlaceGridScene(
                SceneObjectPrefix,
                category,
                trial.sceneVariantSeed,
                Mathf.Clamp(trial.changeTargetObjectIndex, 0, ResolveBaseObjectCount(trial.sceneVariantSeed) - 1));
        }

        /// <summary>
        /// 放置固定垂直展示平面上的 4x4 隐式网格。A/B 共享 seed，B 按 category 施加变化。
        /// </summary>
        private void PlaceGridScene(string prefix, string changeCategory, int sceneVariantSeed, int changeTargetObjectIndex)
        {
            var grayMat = GetObjectMaterial();
            var layout = BuildGridLayout(sceneVariantSeed);
            if (layout.Count == 0) return;

            string category = string.Equals(changeCategory, "none", StringComparison.OrdinalIgnoreCase)
                ? null
                : changeCategory;

            int targetObjectIndex = Mathf.Clamp(changeTargetObjectIndex, 0, layout.Count - 1);
            int movementCandidateIndex = ResolveEmptyCandidateIndex(layout, targetObjectIndex, sceneVariantSeed, 31);
            int placedObjectIdx = 0;

            for (int i = 0; i < layout.Count; i++)
            {
                var spec = layout[i];
                bool isChangeTarget = !string.IsNullOrEmpty(category) && i == targetObjectIndex;

                if (isChangeTarget)
                {
                    switch (category)
                    {
                        case "disappearance":
                            continue;
                        case "movement":
                            spec.CandidateIndex = movementCandidateIndex;
                            spec.LocalPosition = ResolveGridLocalOffset(movementCandidateIndex, sceneVariantSeed);
                            break;
                        case "replacement":
                            spec.Kind = GetReplacementKind(spec.Kind);
                            break;
                    }
                }

                PlaceGridObject($"{prefix}{placedObjectIdx}", spec, grayMat);
                placedObjectIdx++;
            }

            // appearance：新增物体占用一个空卡槽。
            if (string.Equals(category, "appearance", StringComparison.OrdinalIgnoreCase))
            {
                int appearanceCandidateIndex = ResolveEmptyCandidateIndex(layout, targetObjectIndex, sceneVariantSeed, 47);
                var extraSpec = new GridObjectSpec
                {
                    CandidateIndex = appearanceCandidateIndex,
                    LocalPosition = ResolveGridLocalOffset(appearanceCandidateIndex, sceneVariantSeed),
                    Kind = ResolveAppearanceKind(sceneVariantSeed, targetObjectIndex),
                    Scale = DisplayObjectScale
                };
                PlaceGridObject($"{prefix}{placedObjectIdx}", extraSpec, grayMat);
            }
        }

        private void PlaceGridObject(string name, GridObjectSpec spec, Material grayMat)
        {
            var pos = _sceneCenter + _sceneRight * spec.LocalPosition.x + Vector3.up * spec.LocalPosition.y;

            if (_placer != null)
            {
                var placed = _placer.Place(spec.Kind, pos, spec.Scale, grayMat, name);
                AdjustPlaneObjectTransform(placed, spec.Kind, spec.Scale, pos);
            }
            else
            {
                var go = CreatePrimitiveForKind(spec.Kind);
                if (go != null)
                {
                    go.name = name;
                    AdjustPlaneObjectTransform(go, spec.Kind, spec.Scale, pos);
                    var renderer = go.GetComponent<Renderer>();
                    if (renderer != null) renderer.material = grayMat;
                }
            }
        }

        private static List<GridObjectSpec> BuildGridLayout(int sceneVariantSeed)
        {
            var candidates = BuildCandidateIndexVariant(sceneVariantSeed);
            var shapePool = BuildShapePoolVariant(sceneVariantSeed);
            int baseObjectCount = ResolveBaseObjectCount(sceneVariantSeed);
            var layout = new List<GridObjectSpec>(baseObjectCount);

            for (int i = 0; i < baseObjectCount && i < candidates.Length; i++)
            {
                int candidateIndex = candidates[i];
                layout.Add(new GridObjectSpec
                {
                    CandidateIndex = candidateIndex,
                    LocalPosition = ResolveGridLocalOffset(candidateIndex, sceneVariantSeed),
                    Kind = GetBaseKind(shapePool, i),
                    Scale = DisplayObjectScale
                });
            }

            return layout;
        }

        private static int ResolveBaseObjectCount(int sceneVariantSeed)
        {
            return MinBaseObjectCount + PositiveMod(sceneVariantSeed, MaxBaseObjectCount - MinBaseObjectCount + 1);
        }

        private static int[] BuildCandidateIndexVariant(int sceneVariantSeed)
        {
            var candidates = new int[GridCandidateCount];
            for (int i = 0; i < candidates.Length; i++)
            {
                candidates[i] = i;
            }

            var rand = CreateDeterministicRandom(sceneVariantSeed, 0x5A17);
            for (int i = candidates.Length - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }

            return candidates;
        }

        private static Vector3 ResolveGridLocalOffset(int candidateIndex, int sceneVariantSeed)
        {
            candidateIndex = Mathf.Clamp(candidateIndex, 0, GridCandidateCount - 1);
            int row = candidateIndex / GridCols;
            int col = candidateIndex % GridCols;

            float x = (col - (GridCols - 1) * 0.5f) * GridSpacingX;
            float y = ((GridRows - 1) * 0.5f - row) * GridSpacingY;

            uint state = (uint)(sceneVariantSeed * 73856093 ^ candidateIndex * 19349663);
            x += (NextDeterministic01(ref state) * 2f - 1f) * GridJitterX;
            y += (NextDeterministic01(ref state) * 2f - 1f) * GridJitterY;

            return new Vector3(x, y, 0f);
        }

        private static float NextDeterministic01(ref uint state)
        {
            state = state * 1664525u + 1013904223u;
            return (state & 0x00FFFFFFu) / 16777215f;
        }

        private static int ResolveEmptyCandidateIndex(IReadOnlyList<GridObjectSpec> layout, int targetObjectIndex, int sceneVariantSeed, int salt)
        {
            if (layout == null || layout.Count == 0)
            {
                return 0;
            }

            var occupied = new bool[GridCandidateCount];
            for (int i = 0; i < layout.Count; i++)
            {
                int idx = layout[i].CandidateIndex;
                if (idx >= 0 && idx < occupied.Length) occupied[idx] = true;
            }

            var empty = new List<int>(GridCandidateCount - layout.Count);
            for (int i = 0; i < occupied.Length; i++)
            {
                if (!occupied[i]) empty.Add(i);
            }

            if (empty.Count == 0)
            {
                targetObjectIndex = Mathf.Clamp(targetObjectIndex, 0, layout.Count - 1);
                return layout[targetObjectIndex].CandidateIndex;
            }

            int choice = PositiveMod(sceneVariantSeed + targetObjectIndex * salt, empty.Count);
            return empty[choice];
        }

        private static Material CreateObjectMaterial()
        {
            var mat = new Material(Shader.Find("Standard"))
            {
                color = s_grayColor
            };
            mat.SetFloat("_Glossiness", 0.12f);
            return mat;
        }

        private Material GetObjectMaterial()
        {
            if (_grayMaterial == null)
            {
                _grayMaterial = CreateObjectMaterial();
            }

            return _grayMaterial;
        }

        private static void DestroyMaterial(Material mat)
        {
            if (mat == null) return;

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEngine.Object.DestroyImmediate(mat);
            }
            else
            {
                UnityEngine.Object.Destroy(mat);
            }
#else
            UnityEngine.Object.Destroy(mat);
#endif
        }

        private static void AdjustPlaneObjectTransform(GameObject go, string kind, float baseScale, Vector3 centerPos)
        {
            if (go == null) return;

            go.transform.position = centerPos;
            go.transform.localScale = GetShapeScale(kind, baseScale);
        }

        private static Vector3 GetShapeScale(string kind, float baseScale)
        {
            var k = (kind ?? "cube").ToLowerInvariant();
            return k switch
            {
                "sphere" => Vector3.one * (baseScale * 1.1f),
                "cylinder" => new Vector3(baseScale * 0.95f, baseScale * 0.55f, baseScale * 0.95f),
                "capsule" => new Vector3(baseScale * 0.90f, baseScale * 0.70f, baseScale * 0.90f),
                _ => Vector3.one * baseScale
            };
        }

        private static string GetBaseKind(int idx)
        {
            return s_shapePool[idx % s_shapePool.Length];
        }

        private static string GetBaseKind(IReadOnlyList<string> shapePool, int idx)
        {
            if (shapePool == null || shapePool.Count == 0)
            {
                return GetBaseKind(idx);
            }

            return shapePool[idx % shapePool.Count];
        }

        private static string GetReplacementKind(string current)
        {
            current = (current ?? "cube").ToLowerInvariant();
            return current switch
            {
                "cube" => "cylinder",
                "sphere" => "capsule",
                "cylinder" => "cube",
                "capsule" => "sphere",
                _ => "cylinder"
            };
        }

        private static string[] BuildShapePoolVariant(int seed)
        {
            var variant = (string[])s_shapePool.Clone();
            var rand = CreateDeterministicRandom(seed, 0x21C0);

            for (int i = variant.Length - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                (variant[i], variant[j]) = (variant[j], variant[i]);
            }

            return variant;
        }

        private static string ResolveAppearanceKind(int sceneVariantSeed, int targetObjectIndex)
        {
            var shapePool = BuildShapePoolVariant(sceneVariantSeed ^ 0x3A91);
            return GetBaseKind(shapePool, MaxBaseObjectCount + targetObjectIndex);
        }

        private static System.Random CreateDeterministicRandom(int seed, int salt)
        {
            unchecked
            {
                return new System.Random((seed ^ salt) & 0x7fffffff);
            }
        }

        private static int PositiveMod(int value, int modulus)
        {
            if (modulus <= 0) return 0;
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
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

        private void ClearChangeScene()
        {
            TryDestroyByPrefix(SceneObjectPrefix);
        }

        private void RecordTrialObjects(TrialSpec trial, string phase)
        {
            if (trial == null || _ctx?.trialObjectCsvRecorder == null) return;

            _ctx.trialObjectCsvRecorder.RecordTrialObjects(
                _ctx.runner != null ? _ctx.runner.CurrentRunId : null,
                _ctx.runner != null ? _ctx.runner.CurrentSubjectMode : SubjectMode.MLLM,
                _ctx.runner != null ? _ctx.runner.CurrentRandomSeed : 0,
                trial.trialId,
                trial,
                phase);
        }

        private void ShowBlackout()
        {
            TryBindHelpers();
            _blackoutOverlay?.Show();
        }

        private void HideBlackout()
        {
            TryBindHelpers();
            _blackoutOverlay?.Hide();
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
                    if (!Application.isPlaying)
                    {
                        UnityEngine.Object.DestroyImmediate(go);
                    }
                    else
                    {
                        UnityEngine.Object.Destroy(go);
                    }
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

        private static string NormalizeTrialCategory(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "none";
            return NormalizeCategory(raw) ?? "none";
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
                    category = NormalizeCategory(categoryVal);
                    changed = changedVal ?? CategoryImpliesChanged(category);
                    return true;
                }

                // JSON 路径
                var json = JsonUtility.ToJson(answer);
                if (!string.IsNullOrEmpty(json))
                {
                    bool hasChangedField = ContainsJsonField(json, "changed");
                    bool hasCategoryField = ContainsJsonField(json, "category");
                    var parsed = JsonUtility.FromJson<ChangeAnswer>(json);
                    if (parsed != null && (hasChangedField || hasCategoryField))
                    {
                        category = NormalizeCategory(parsed.category);
                        changed = hasChangedField ? parsed.changed : CategoryImpliesChanged(category);
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

            bool hasChangedSignal = false;

            // 尝试匹配 "changed": true/false
            var mChanged = Regex.Match(text, @"changed[^A-Za-z0-9]*(true|false)", RegexOptions.IgnoreCase);
            if (mChanged.Success && bool.TryParse(mChanged.Groups[1].Value, out var b))
            {
                changed = b;
                hasChangedSignal = true;
            }
            else
            {
                // 关键字启发
                if (text.IndexOf("no change", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("unchanged", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    changed = false;
                    hasChangedSignal = true;
                }
                else if (text.IndexOf("change", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    changed = true;
                    hasChangedSignal = true;
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
            else if (Regex.IsMatch(text, @"\bnone\b|no change|unchanged", RegexOptions.IgnoreCase))
            {
                category = "none";
            }
            else if (hasChangedSignal && !changed)
            {
                category = "none";
            }

            category = NormalizeCategory(category);

            if (!hasChangedSignal && CategoryImpliesChanged(category))
            {
                changed = true;
            }

            // 只要能确定 changed 或 category 之一，即认为有预测
            return hasChangedSignal || category != null;
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

        private static bool CategoryImpliesChanged(string category)
        {
            return !string.IsNullOrEmpty(category) &&
                   !string.Equals(category, "none", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsJsonField(string json, string fieldName)
        {
            return !string.IsNullOrEmpty(json) &&
                   !string.IsNullOrEmpty(fieldName) &&
                   Regex.IsMatch(json, $"\"{Regex.Escape(fieldName)}\"\\s*:", RegexOptions.IgnoreCase);
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

