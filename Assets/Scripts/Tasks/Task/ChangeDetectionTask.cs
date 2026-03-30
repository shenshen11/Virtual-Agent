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
        private const int SceneBExposureMs = 1000;
        private const float ClusterDistance = 9f;

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(1234);

        private ExperimentSceneManager _scene;
        private ObjectPlacer _placer;
        private TrialBlackoutOverlay _blackoutOverlay;

        // 统一灰色材质（去掉颜色线索，迫使依赖空间/形状检测变化）
        private static readonly Color s_grayColor = new Color(0.5f, 0.5f, 0.5f);

        // 空间层级名称（front/middle/back 实际深度 = ClusterDistance + z偏移）
        private static readonly string[] s_layerNames = { "front", "middle", "back" };

        // 形状池（每层 2 个物体，共 6 个，预分配不同形状）
        private static readonly string[] s_shapePool = { "cube", "sphere", "cylinder", "capsule", "cube", "sphere" };

        // 大小池：front=0.65、middle=0.62、back=0.78（back层加大补偿透视缩小）
        private static readonly float[] s_scalePool = { 0.65f, 0.65f, 0.62f, 0.62f, 0.78f, 0.78f };

        // movement 视角等比偏移量（每层按深度缩放，保持轻微但仍可察觉的视觉跳变）
        // 实际深度：front≈6.5m, middle≈9m, back≈12m；当前约对应 4.9° 的横向变化
        private static readonly float[] s_movementDeltaX = { 0.56f, 0.78f, 1.03f };

        private static readonly string[] s_changeCategories = { "appearance", "disappearance", "movement", "replacement" };

        // 三层物体偏移（相对 sceneCenter）：
        //   每层两个物体关于 x=0 对称；z 偏移决定层深度（ClusterDistance=9m）
        //   front:  实际深度 ≈ 6.5m，横向间距 2.2m
        //   middle: 实际深度 ≈ 9.0m，横向间距 2.4m
        //   back:   实际深度 ≈ 12.0m，横向间距 2.6m
        private static readonly Vector3[][] s_layerOffsets =
        {
            new[] { new Vector3(-1.10f, 0f, -2.5f), new Vector3(+1.10f, 0f, -2.5f) }, // front
            new[] { new Vector3(-1.20f, 0f,  0.0f), new Vector3(+1.20f, 0f,  0.0f) }, // middle
            new[] { new Vector3(-1.30f, 0f, +3.0f), new Vector3(+1.30f, 0f, +3.0f) }, // back
        };

        private Vector3 _sceneCenter;
        private Vector3 _sceneForward;
        private Vector3 _sceneRight;
        private float _sceneGroundY;
        private bool _sceneAnchorReady;
        private bool _referenceFrameInitialized;
        private Vector3 _referenceOrigin;
        private Vector3 _referenceForward;
        private float _referenceEyeY;

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
            if (!TryUseHumanSharedReferenceFrame())
            {
                CaptureReferenceFrameIfNeeded(forceRefresh: true);
            }

            return Task.CompletedTask;
        }

        public Task OnRunEndAsync(CancellationToken ct)
        {
            _referenceFrameInitialized = false;
            _sceneAnchorReady = false;
            return Task.CompletedTask;
        }

        public TrialSpec[] BuildTrials(int seed)
        {
            _rand = new System.Random(seed);

            var backgrounds = new[] { "none", "indoor", "street" };
            var fovs = new[] { 60f };

            var trials = new List<TrialSpec>();

            foreach (var bg in backgrounds)
            {
                foreach (var fov in fovs)
                {
                    // none 类别：无层级
                    trials.Add(new TrialSpec
                    {
                        taskId = TaskId,
                        environment = "open_field",
                        background = bg,
                        fovDeg = fov,
                        lighting = BackgroundToLighting(bg),
                        occlusion = false,
                        changed = false,
                        changeCategory = "none",
                        sceneVariantSeed = _rand.Next(),
                        changeTargetObjectIndex = _rand.Next(0, 2)
                    });

                    // 其他类别 × 空间层级
                    foreach (var cat in s_changeCategories)
                    {
                        foreach (var layer in s_layerNames)
                        {
                            trials.Add(new TrialSpec
                            {
                                taskId = TaskId,
                                environment = "open_field",
                                background = bg,
                                fovDeg = fov,
                                lighting = BackgroundToLighting(bg),
                                occlusion = false,
                                changed = true,
                                changeCategory = $"{cat}_{layer}",
                                sceneVariantSeed = _rand.Next(),
                                changeTargetObjectIndex = _rand.Next(0, 2)
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
            return PromptTemplates.GetSystemPrompt(TaskId);
        }

        public ToolSpec[] GetTools()
        {
            // 变化检测为纯推理任务：模型直接看 before/after 双帧作答，不需要工具调用
            return null;
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            return PromptTemplates.BuildChangeDetectionPrompt(trial.background, trial.fovDeg, trial.trialId);
        }

        public async Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            TryBindHelpers();
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

            // 从复合编码中提取纯 category（模型只需回答 category，不需要回答 layer）
            ParseCategoryAndLayer(trial.changeCategory, out var trueCategory, out _);
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

            if (SceneBExposureMs > 0)
            {
                await Task.Delay(SceneBExposureMs, ct);
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

            ResolvePlacementReference(cam, out var origin, out var forward, out var eyeY);
            _sceneForward = forward;
            if (_sceneForward.sqrMagnitude < 0.0001f)
            {
                _sceneForward = Vector3.forward;
            }

            _sceneRight = Vector3.Cross(Vector3.up, _sceneForward).normalized;
            _sceneGroundY = eyeY;
            _sceneCenter = origin + _sceneForward * ClusterDistance;
            _sceneCenter.y = _sceneGroundY;
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
            PlaceLayeredCluster(
                SceneObjectPrefix,
                null,
                -1,
                trial != null ? trial.sceneVariantSeed : 0,
                trial != null ? Mathf.Clamp(trial.changeTargetObjectIndex, 0, 1) : 0);
        }

        private void ApplySceneB(TrialSpec trial)
        {
            ClearChangeScene();
            ParseCategoryAndLayer(trial.changeCategory, out var category, out var layer);
            var targetLayerIdx = Array.IndexOf(s_layerNames, layer);
            PlaceLayeredCluster(
                SceneObjectPrefix,
                category,
                targetLayerIdx,
                trial.sceneVariantSeed,
                Mathf.Clamp(trial.changeTargetObjectIndex, 0, 1));
        }

        /// <summary>
        /// 放置三层物体群。changeCategory 和 targetLayerIdx 控制 B 场景的变化。
        /// </summary>
        private void PlaceLayeredCluster(string prefix, string changeCategory, int targetLayerIdx, int sceneVariantSeed, int changeTargetObjectIndex)
        {
            var grayMat = CreateObjectMaterial();
            var shapePool = BuildShapePoolVariant(sceneVariantSeed);
            int placedObjectIdx = 0;

            for (int li = 0; li < s_layerOffsets.Length; li++)
            {
                var offsets = s_layerOffsets[li];
                bool isTargetLayer = (li == targetLayerIdx && !string.IsNullOrEmpty(changeCategory));

                for (int oi = 0; oi < offsets.Length; oi++)
                {
                    int slotIdx = li * offsets.Length + oi;
                    bool isChangeTarget = isTargetLayer && oi == changeTargetObjectIndex;
                    var local = offsets[oi];
                    var kind = GetBaseKind(shapePool, slotIdx);
                    float objScale = GetBaseScale(slotIdx);

                    if (isChangeTarget)
                    {
                        switch (changeCategory)
                        {
                            case "disappearance":
                                continue; // 跳过，不放置
                            case "movement":
                                local = ResolveMovementLocalOffset(li, local, changeTargetObjectIndex, sceneVariantSeed);
                                break;
                            case "replacement":
                                kind = GetReplacementKind(shapePool, slotIdx);
                                break;
                        }
                    }

                    var pos = _sceneCenter + _sceneRight * local.x + _sceneForward * local.z;
                    pos.y = _sceneGroundY;
                    var name = $"{prefix}{placedObjectIdx}";

                    if (_placer != null)
                    {
                        var placed = _placer.Place(kind, pos, objScale, grayMat, name);
                        AdjustShapeTransform(placed, kind, objScale, pos, _sceneGroundY);
                    }
                    else
                    {
                        var go = CreatePrimitiveForKind(kind);
                        if (go != null)
                        {
                            go.name = name;
                            AdjustShapeTransform(go, kind, objScale, pos, _sceneGroundY);
                            var renderer = go.GetComponent<Renderer>();
                            if (renderer != null) renderer.material = grayMat;
                        }
                    }
                    placedObjectIdx++;
                }

                // appearance：仅在目标层内，围绕已有物体附近新增一个额外物体，避免固定中心插入过于显眼。
                if (isTargetLayer && changeCategory == "appearance")
                {
                    var extraLocal = ResolveAppearanceLocalOffset(li, offsets, changeTargetObjectIndex, sceneVariantSeed);
                    var pos = _sceneCenter + _sceneRight * extraLocal.x + _sceneForward * extraLocal.z;
                    pos.y = _sceneGroundY;
                    var name = $"{prefix}{placedObjectIdx}";
                    const float extraScale = 0.58f;

                    if (_placer != null)
                    {
                        var placed = _placer.Place("cylinder", pos, extraScale, grayMat, name);
                        AdjustShapeTransform(placed, "cylinder", extraScale, pos, _sceneGroundY);
                    }
                    else
                    {
                        var go = CreatePrimitiveForKind("cylinder");
                        if (go != null)
                        {
                            go.name = name;
                            AdjustShapeTransform(go, "cylinder", extraScale, pos, _sceneGroundY);
                            var renderer = go.GetComponent<Renderer>();
                            if (renderer != null) renderer.material = grayMat;
                        }
                    }
                    placedObjectIdx++;
                }
            }
        }

        private static Vector3 ResolveAppearanceLocalOffset(int layerIndex, IReadOnlyList<Vector3> offsets, int changeTargetObjectIndex, int sceneVariantSeed)
        {
            if (offsets == null || offsets.Count == 0)
            {
                return new Vector3(0f, 0f, 0.15f);
            }

            int anchorIndex = Mathf.Clamp(changeTargetObjectIndex, 0, offsets.Count - 1);
            Vector3 anchor = offsets[anchorIndex];

            float outwardSign = Mathf.Sign(anchor.x);
            if (Mathf.Abs(outwardSign) < 0.01f)
            {
                outwardSign = anchorIndex == 0 ? -1f : 1f;
            }

            // 深层稍微放大偏移，保持各层上的局部邻近感接近一致。
            float layerScale = 1f + Mathf.Clamp(layerIndex, 0, 2) * 0.12f;
            float lateralOffset = 0.28f * layerScale * outwardSign;

            // 用 sceneVariantSeed 打散前后方向，保持同一试次可复现。
            bool pushForward = ((sceneVariantSeed + layerIndex + anchorIndex) & 1) == 0;
            float depthOffset = (pushForward ? 0.22f : -0.18f) * layerScale;

            return new Vector3(anchor.x + lateralOffset, anchor.y, anchor.z + depthOffset);
        }

        private static Vector3 ResolveMovementLocalOffset(int layerIndex, Vector3 local, int changeTargetObjectIndex, int sceneVariantSeed)
        {
            float deltaX = layerIndex < s_movementDeltaX.Length ? s_movementDeltaX[layerIndex] : 1.92f;

            float outwardSign = Mathf.Sign(local.x);
            if (Mathf.Abs(outwardSign) < 0.01f)
            {
                outwardSign = changeTargetObjectIndex == 0 ? -1f : 1f;
            }

            // 默认朝层外移动，部分 seed 会翻转为朝层内移动，避免形成固定方向的可学习模式。
            bool moveOutward = ((sceneVariantSeed + layerIndex + changeTargetObjectIndex) & 1) == 0;
            float signedDeltaX = deltaX * (moveOutward ? outwardSign : -outwardSign);
            return new Vector3(local.x + signedDeltaX, local.y, local.z);
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

        private static void AdjustShapeTransform(GameObject go, string kind, float baseScale, Vector3 basePos, float groundY)
        {
            if (go == null) return;

            var scale = GetShapeScale(kind, baseScale);
            var pos = basePos;
            pos.y = groundY + scale.y * 0.5f;

            go.transform.position = pos;
            go.transform.localScale = scale;
        }

        private static Vector3 GetShapeScale(string kind, float baseScale)
        {
            var k = (kind ?? "cube").ToLowerInvariant();
            return k switch
            {
                "sphere" => Vector3.one * (baseScale * 0.95f),
                "cylinder" => new Vector3(baseScale * 0.84f, baseScale * 0.76f, baseScale * 0.84f),
                "capsule" => new Vector3(baseScale * 0.82f, baseScale * 0.80f, baseScale * 0.82f),
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

        private static float GetBaseScale(int idx)
        {
            return s_scalePool[idx % s_scalePool.Length];
        }

        private static string GetReplacementKind(int idx)
        {
            var current = GetBaseKind(idx);
            return current switch
            {
                "cube" => "cylinder",
                "sphere" => "capsule",
                "cylinder" => "cube",
                "capsule" => "sphere",
                _ => "cylinder"
            };
        }

        private static string GetReplacementKind(IReadOnlyList<string> shapePool, int idx)
        {
            var current = GetBaseKind(shapePool, idx);
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
            var rand = new System.Random(seed);

            for (int i = variant.Length - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                (variant[i], variant[j]) = (variant[j], variant[i]);
            }

            return variant;
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

        /// <summary>
        /// 从复合编码 "category_layer" 中解析出纯 category 和 layer。
        /// 例如 "movement_back" → category="movement", layer="back"；"none" → category="none", layer=null。
        /// </summary>
        private static void ParseCategoryAndLayer(string raw, out string category, out string layer)
        {
            category = "none";
            layer = null;
            if (string.IsNullOrEmpty(raw)) return;

            var s = raw.Trim().ToLowerInvariant();
            int idx = s.LastIndexOf('_');
            if (idx > 0)
            {
                var suffix = s.Substring(idx + 1);
                if (suffix == "front" || suffix == "middle" || suffix == "back")
                {
                    category = s.Substring(0, idx);
                    layer = suffix;
                    return;
                }
            }
            category = s;
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

