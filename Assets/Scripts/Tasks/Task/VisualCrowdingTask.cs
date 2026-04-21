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
    /// 视觉拥挤任务（Visual Crowding）
    /// - 屏幕中心固定注视点，右侧呈现 5 字母串，目标在中间
    /// - 被试输出：{"type":"inference","answer":{"letter":"A-Z"},"confidence":0..1}
    /// - 自变量：离心率（deg）× 间距（deg），单帧 one-shot，无 action_plan
    /// </summary>
    public class VisualCrowdingTask : ITask, ITaskRunLifecycle
    {
        public string TaskId => "visual_crowding";

        private const float DisplayDistanceM = 1.5f;
        private const float LetterHeightDeg = 5.0f;
        private const float DesignLetterWidthDeg = 3.5f;
        private const float FixationSizeDeg = 2.0f;
        private const int LetterCount = 5;
        private const int TargetIndex = 2;
        private const int CrowdedRepetitions = 4;
        private const int IsolatedRepetitions = 3;
        private static readonly float[] EccentricitiesDeg = { 14f, 16f };
        private static readonly float[] CenterSpacingsDeg = { 3.8f, 4.3f, 4.8f };

        private readonly string[] _letterPool = new[]
        {
            "A","B","C","D","E","F","G","H","J","K","L","M","N","P","R","S","T","U","V","W","X","Y","Z"
        }; // 去掉 I/O/Q 降低混淆

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(1234);
        private ExperimentSceneManager _scene;
        private readonly List<GameObject> _spawned = new List<GameObject>();
        private readonly List<GameObject> _letterSpawned = new List<GameObject>();
        private GameObject _fixationRoot;
        private readonly Dictionary<string, int> _snapshotObjectCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int _activeTrialId = -1;

        // 字母消除延迟（秒），注视点保留
        private const float LetterHideDelaySec = 3f;
        private bool _referenceFrameInitialized;
        private Vector3 _referenceOrigin;
        private Vector3 _referenceForward;
        private float _referenceEyeY;

        public VisualCrowdingTask(TaskRunnerContext ctx)
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
            return Task.CompletedTask;
        }

        public TrialSpec[] BuildTrials(int seed)
        {
            _rand = new System.Random(seed);
            return BuildCrowdingTrials();
        }

        private TrialSpec[] BuildCrowdingTrials()
        {
            var isolatedTrials = new List<TrialSpec>();
            var crowdedTrials = new List<TrialSpec>();

            foreach (var ecc in EccentricitiesDeg)
            {
                foreach (var sp in CenterSpacingsDeg)
                {
                    if (!TryComputeGeometry(ecc, sp, DesignLetterWidthDeg, TargetIndex, LetterCount,
                            out var edgeGapDeg,
                            out var spacingRatio,
                            out var leftmostDeg,
                            out var rightmostDeg))
                    {
                        Debug.LogWarning(
                            $"[VisualCrowdingTask] Skipping invalid geometry: E={ecc:F2}deg S={sp:F2}deg width={DesignLetterWidthDeg:F2}deg.");
                        continue;
                    }

                    for (int rep = 0; rep < CrowdedRepetitions; rep++)
                    {
                        var target = SampleLetter();
                        var flankers = BuildFlankers(target);

                        crowdedTrials.Add(new TrialSpec
                        {
                            taskId = TaskId,
                            environment = "open_field",
                            background = "none",
                            fovDeg = 60f,
                            textureDensity = 1f,
                            lighting = "bright",
                            occlusion = false,

                            eccentricityDeg = ecc,
                            spacingDeg = sp,
                            displayDistanceM = DisplayDistanceM,
                            letterHeightDeg = LetterHeightDeg,
                            letterWidthDeg = DesignLetterWidthDeg,
                            edgeGapDeg = edgeGapDeg,
                            spacingEccentricityRatio = spacingRatio,
                            leftmostLetterEccDeg = leftmostDeg,
                            rightmostLetterEccDeg = rightmostDeg,
                            visualCrowdingCondition = "crowded",
                            targetLetter = target,
                            flankerLetters = flankers,
                            targetIndex = TargetIndex
                        });
                    }
                }

                for (int rep = 0; rep < IsolatedRepetitions; rep++)
                {
                    var target = SampleLetter();

                    isolatedTrials.Add(new TrialSpec
                    {
                        taskId = TaskId,
                        environment = "open_field",
                        background = "none",
                        fovDeg = 60f,
                        textureDensity = 1f,
                        lighting = "bright",
                        occlusion = false,

                        eccentricityDeg = ecc,
                        spacingDeg = 0f,
                        displayDistanceM = DisplayDistanceM,
                        letterHeightDeg = LetterHeightDeg,
                        letterWidthDeg = DesignLetterWidthDeg,
                        edgeGapDeg = 0f,
                        spacingEccentricityRatio = 0f,
                        leftmostLetterEccDeg = ecc,
                        rightmostLetterEccDeg = ecc,
                        visualCrowdingCondition = "isolated",
                        targetLetter = target,
                        flankerLetters = new[] { target },
                        targetIndex = 0
                    });
                }
            }

            Shuffle(isolatedTrials);
            Shuffle(crowdedTrials);

            var trials = new List<TrialSpec>(isolatedTrials.Count + crowdedTrials.Count);
            trials.AddRange(isolatedTrials);
            trials.AddRange(crowdedTrials);
            EnsureNoAdjacentSameTarget(trials);
            return trials.ToArray();
        }

        public string GetSystemPrompt()
        {
            return PromptTemplates.GetSystemPrompt(TaskId);
        }

        public ToolSpec[] GetTools()
        {
            // one-shot 任务，不提供动作计划工具
            return null;
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            return PromptTemplates.BuildVisualCrowdingPrompt(
                trial.eccentricityDeg,
                trial.spacingDeg,
                trial.targetLetter,
                trial.flankerLetters,
                trial.trialId);
        }

        public async Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            TryBindHelpers();
            ClearSpawned();
            _snapshotObjectCounts.Clear();
            _activeTrialId = trial != null ? trial.trialId : -1;

            if (_scene != null)
            {
                _scene.SetupEnvironment("open_field", trial.textureDensity <= 0 ? 1f : trial.textureDensity, "bright", false);
            }

            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null) return;

            if (!TryUseHumanSharedReferenceFrame())
            {
                CaptureReferenceFrameIfNeeded(forceRefresh: false);
            }

            var fov = trial.fovDeg > 0 ? trial.fovDeg : 60f;
            _ctx?.stimulus?.SetCameraFOV(fov);

            ResolvePlacementReference(cam, out var origin, out var forward, out var right, out var eyeY);
            EnsureGeometryFields(trial);

            // 注视点和字母位于同一显示平面，所有关键尺寸由视觉角换算。
            var displayDistance = trial.displayDistanceM > 0f ? trial.displayDistanceM : DisplayDistanceM;

            PlaceFixation(origin, forward, eyeY, displayDistance);
            PlaceLetters(origin, forward, right, eyeY, displayDistance, trial);

            // Human 模式：等待 3s 后隐藏字母，注视点保留；
            // OnBeforeTrialAsync 返回后 TaskRunner 才发布 WaitingForInput，保证字母消失在前
            if (IsHumanMode())
            {
                await Task.Delay(TimeSpan.FromSeconds(LetterHideDelaySec), ct);
                HideLetters();
            }
        }

        /// <summary>
        /// 隐藏当前 trial 的字母对象（SetActive false），注视点保留。
        /// 不销毁，保留在场景中供 RecordTrialObjects 扫描记录；
        /// 真正销毁由 ClearSpawned 在 OnAfterTrialAsync 中统一完成。
        /// </summary>
        private void HideLetters()
        {
            for (int i = 0; i < _letterSpawned.Count; i++)
            {
                var go = _letterSpawned[i];
                if (go != null) go.SetActive(false);
            }
            // 不清空 _letterSpawned，留给 ClearSpawned 销毁
        }

        public async Task OnAfterTrialAsync(TrialSpec trial, LLMResponse response, CancellationToken ct)
        {
            ClearSpawned();
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
                trueLetter = trial.targetLetter
            };

            string predicted = null;
            if (response != null && response.type == "inference")
            {
                if (TryExtractLetterFromAnswer(response.answer, out var letter))
                {
                    predicted = letter;
                }
                else if (TryExtractLetterFromString(response.explanation, out var letter2))
                {
                    predicted = letter2;
                }
            }

            if (!string.IsNullOrEmpty(predicted))
            {
                eval.predictedLetter = predicted.ToUpperInvariant();
                eval.isLetterCorrect = string.Equals(eval.predictedLetter, trial.targetLetter, StringComparison.OrdinalIgnoreCase);
                eval.success = true;
            }
            else
            {
                eval.success = false;
                eval.failureReason = "No letter found in response";
            }

            return eval;
        }

        // =============== Helpers ===============

        private void TryBindHelpers()
        {
            if (_ctx?.runner != null && _scene == null)
            {
                _scene = _ctx.runner.GetComponent<ExperimentSceneManager>();
            }

            if (_scene == null)
            {
                _scene = UnityEngine.Object.FindObjectOfType<ExperimentSceneManager>();
            }
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

        private void ResolvePlacementReference(Camera cam, out Vector3 origin, out Vector3 forward, out Vector3 right, out float eyeY)
        {
            if (TryUseHumanSharedReferenceFrame())
            {
                origin = _referenceOrigin;
                forward = _referenceForward;
                eyeY = _referenceEyeY;
            }
            else
            {
                origin = _referenceFrameInitialized ? _referenceOrigin : cam.transform.position;
                forward = _referenceFrameInitialized ? _referenceForward : Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
                if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
                forward.Normalize();
                eyeY = _referenceFrameInitialized ? _referenceEyeY : cam.transform.position.y;
            }

            right = Vector3.Cross(Vector3.up, forward);
            if (right.sqrMagnitude < 1e-6f) right = cam.transform.right;
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            right.Normalize();
        }

        private bool IsHumanMode()
        {
            return _ctx?.runner != null && _ctx.runner.CurrentSubjectMode == SubjectMode.Human;
        }

        private string SampleLetter()
        {
            var idx = _rand.Next(_letterPool.Length);
            return _letterPool[idx];
        }

        private string SampleLetterExcept(string excluded)
        {
            var candidates = new List<string>(_letterPool.Length);
            for (int i = 0; i < _letterPool.Length; i++)
            {
                var letter = _letterPool[i];
                if (!string.Equals(letter, excluded, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(letter);
                }
            }

            if (candidates.Count == 0) return SampleLetter();
            return candidates[_rand.Next(candidates.Count)];
        }

        private string[] BuildFlankers(string target)
        {
            var arr = new string[5];
            arr[2] = target;
            var candidates = new List<string>(_letterPool.Length);
            for (int i = 0; i < _letterPool.Length; i++)
            {
                var letter = _letterPool[i];
                if (!string.Equals(letter, target, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(letter);
                }
            }
            for (int i = 0; i < arr.Length; i++)
            {
                if (i == 2) continue;
                if (candidates.Count == 0)
                {
                    arr[i] = SampleLetter();
                    continue;
                }

                var idx = _rand.Next(candidates.Count);
                arr[i] = candidates[idx];
                candidates.RemoveAt(idx);
            }
            return arr;
        }

        private void PlaceFixation(Vector3 origin, Vector3 forward, float eyeY, float depth)
        {
            var pos = origin + forward * depth;
            pos.y = eyeY; // 保持与字母同一高度
            var root = new GameObject("vc_fixation");
            root.transform.position = pos;
            root.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            AttachSnapshotMarker(root, "fixation", "fixation");

            // 十字注视点：水平+垂直细条
            var size = AngleToWorldSize(FixationSizeDeg, depth);
            var thickness = Mathf.Max(0.001f, size * 0.18f);
            var depthThickness = Mathf.Max(0.001f, size * 0.08f);
            var horiz = GameObject.CreatePrimitive(PrimitiveType.Cube);
            horiz.transform.SetParent(root.transform, worldPositionStays: false);
            horiz.transform.localScale = new Vector3(size, thickness, depthThickness);

            var vert = GameObject.CreatePrimitive(PrimitiveType.Cube);
            vert.transform.SetParent(root.transform, worldPositionStays: false);
            vert.transform.localScale = new Vector3(thickness, size, depthThickness);

            var rendererH = horiz.GetComponent<Renderer>();
            var rendererV = vert.GetComponent<Renderer>();
            if (rendererH != null) rendererH.material.color = Color.red;
            if (rendererV != null) rendererV.material.color = Color.red;

            // 注视点独立存储，不加入 _spawned，Human 模式下字母消除后仍保留
            _fixationRoot = root;
            _spawned.Add(root);
        }

        private void PlaceLetters(Vector3 origin, Vector3 forward, Vector3 right, float eyeY, float depth, TrialSpec trial)
        {
            if (trial == null || trial.flankerLetters == null || trial.flankerLetters.Length == 0) return;

            var basePos = origin + forward * depth;
            var letterHeightM = AngleToWorldSize(trial.letterHeightDeg > 0f ? trial.letterHeightDeg : LetterHeightDeg, depth);

            for (int i = 0; i < trial.flankerLetters.Length; i++)
            {
                var idxOffset = i - trial.targetIndex;
                var centerAngleDeg = trial.eccentricityDeg + trial.spacingDeg * idxOffset;
                var offset = Mathf.Tan(centerAngleDeg * Mathf.Deg2Rad) * depth;
                var pos = basePos + right * offset;
                pos.y = eyeY; // 与参考眼高等高，模拟水平字母串

                var go = CreateLetterObject(trial.flankerLetters[i], pos, forward, letterHeightM);
                if (go != null)
                {
                    go.name = $"vc_letter_{i}";
                    AttachSnapshotMarker(go, "letter", i == trial.targetIndex ? "target" : "flanker");
                    _letterSpawned.Add(go); // 字母单独追踪，Human 模式下 3s 后消除
                    _spawned.Add(go);
                }
            }
        }

        private GameObject CreateLetterObject(string letter, Vector3 position, Vector3 forward, float letterHeightM)
        {
            var go = new GameObject("vc_letter", typeof(TextMesh));
            go.transform.position = position;
            go.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);

            var tm = go.GetComponent<TextMesh>();
            tm.text = string.IsNullOrEmpty(letter) ? "?" : letter.ToUpperInvariant();
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.characterSize = 1f;
            tm.fontSize = 100;
            tm.color = Color.white;

            FitTextMeshToHeight(go, letterHeightM);
            return go;
        }

        private static void FitTextMeshToHeight(GameObject go, float targetHeightM)
        {
            if (go == null) return;

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return;

            var currentHeight = renderer.bounds.size.y;
            if (currentHeight <= 1e-6f) return;

            var scale = Mathf.Max(0.001f, targetHeightM) / currentHeight;
            go.transform.localScale = Vector3.one * scale;
        }

        private static float AngleToWorldSize(float angleDeg, float depth)
        {
            return 2f * depth * Mathf.Tan(Mathf.Max(0.001f, angleDeg) * 0.5f * Mathf.Deg2Rad);
        }

        private static bool TryComputeGeometry(
            float eccentricityDeg,
            float spacingDeg,
            float letterWidthDeg,
            int targetIndex,
            int letterCount,
            out float edgeGapDeg,
            out float spacingEccentricityRatio,
            out float leftmostLetterEccDeg,
            out float rightmostLetterEccDeg)
        {
            edgeGapDeg = spacingDeg - letterWidthDeg;
            spacingEccentricityRatio = eccentricityDeg > 0f ? spacingDeg / eccentricityDeg : 0f;
            leftmostLetterEccDeg = eccentricityDeg - targetIndex * spacingDeg;
            rightmostLetterEccDeg = eccentricityDeg + (letterCount - 1 - targetIndex) * spacingDeg;

            return edgeGapDeg > 0f && leftmostLetterEccDeg > 0f && rightmostLetterEccDeg > leftmostLetterEccDeg;
        }

        private static void EnsureGeometryFields(TrialSpec trial)
        {
            if (trial == null) return;

            if (trial.displayDistanceM <= 0f) trial.displayDistanceM = DisplayDistanceM;
            if (trial.letterHeightDeg <= 0f) trial.letterHeightDeg = LetterHeightDeg;
            if (trial.letterWidthDeg <= 0f) trial.letterWidthDeg = DesignLetterWidthDeg;

            var letterCount = trial.flankerLetters != null && trial.flankerLetters.Length > 0
                ? trial.flankerLetters.Length
                : LetterCount;

            if (letterCount == 1)
            {
                trial.spacingDeg = 0f;
                trial.edgeGapDeg = 0f;
                trial.spacingEccentricityRatio = 0f;
                trial.leftmostLetterEccDeg = trial.eccentricityDeg;
                trial.rightmostLetterEccDeg = trial.eccentricityDeg;
                if (string.IsNullOrEmpty(trial.visualCrowdingCondition))
                {
                    trial.visualCrowdingCondition = "isolated";
                }
                return;
            }

            TryComputeGeometry(
                trial.eccentricityDeg,
                trial.spacingDeg,
                trial.letterWidthDeg,
                trial.targetIndex,
                letterCount,
                out trial.edgeGapDeg,
                out trial.spacingEccentricityRatio,
                out trial.leftmostLetterEccDeg,
                out trial.rightmostLetterEccDeg);

            if (string.IsNullOrEmpty(trial.visualCrowdingCondition))
            {
                trial.visualCrowdingCondition = "crowded";
            }
        }

        private void ClearSpawned()
        {
            // 清除所有 spawned 对象（包含 fixation + letters）
            for (int i = _spawned.Count - 1; i >= 0; i--)
            {
                var go = _spawned[i];
                if (go != null)
                {
#if UNITY_EDITOR
                    UnityEngine.Object.DestroyImmediate(go);
#else
                    UnityEngine.Object.Destroy(go);
#endif
                }
            }
            _spawned.Clear();
            _letterSpawned.Clear(); // 字母可能已被 HideLettersAfterDelayAsync 提前清除，确保同步
            _fixationRoot = null;
            _snapshotObjectCounts.Clear();
            _activeTrialId = -1;
        }

        private void AttachSnapshotMarker(GameObject go, string kind, string role)
        {
            if (go == null) return;

            string taskId = _ctx?.runner?.CurrentConfiguredTaskId ?? TaskId;
            string baseName = string.IsNullOrWhiteSpace(go.name) ? "unnamed" : go.name.Trim();
            if (!_snapshotObjectCounts.TryGetValue(baseName, out var count)) count = 0;
            count++;
            _snapshotObjectCounts[baseName] = count;

            string objectId = count <= 1
                ? $"{taskId}_{_activeTrialId}_{baseName}"
                : $"{taskId}_{_activeTrialId}_{baseName}_{count}";

            TrialObjectMarker.AttachOrUpdate(go, taskId, _activeTrialId, objectId, kind, role);
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

        private void EnsureNoAdjacentSameTarget(IList<TrialSpec> trials)
        {
            if (trials == null || trials.Count <= 1) return;

            for (int i = 1; i < trials.Count; i++)
            {
                if (!SameTarget(trials[i - 1], trials[i])) continue;

                RetargetTrialAvoidingPrevious(trials[i], trials[i - 1]?.targetLetter);
            }
        }

        private void RetargetTrialAvoidingPrevious(TrialSpec trial, string previousTarget)
        {
            if (trial == null) return;

            var target = SampleLetterExcept(previousTarget);
            trial.targetLetter = target;

            if (trial.flankerLetters != null && trial.flankerLetters.Length > 1)
            {
                trial.flankerLetters = BuildFlankers(target);
                trial.targetIndex = TargetIndex;
            }
            else
            {
                trial.flankerLetters = new[] { target };
                trial.targetIndex = 0;
            }
        }

        private static bool SameTarget(TrialSpec a, TrialSpec b)
        {
            return a != null &&
                   b != null &&
                   !string.IsNullOrEmpty(a.targetLetter) &&
                   string.Equals(a.targetLetter, b.targetLetter, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryExtractLetterFromAnswer(object answer, out string letter)
        {
            letter = null;
            if (answer == null) return false;

            try
            {
                var t = answer.GetType();
                var prop = t.GetProperty("letter", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var v = prop.GetValue(answer)?.ToString();
                    if (!string.IsNullOrEmpty(v))
                    {
                        letter = v.Trim().ToUpperInvariant();
                        return true;
                    }
                }

                var json = JsonUtility.ToJson(answer);
                if (!string.IsNullOrEmpty(json))
                {
                    var parsed = JsonUtility.FromJson<LetterAnswer>(json);
                    if (!string.IsNullOrEmpty(parsed?.letter))
                    {
                        letter = parsed.letter.Trim().ToUpperInvariant();
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
                return TryExtractLetterFromString(s, out letter);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryExtractLetterFromString(string text, out string letter)
        {
            letter = null;
            if (string.IsNullOrEmpty(text)) return false;

            var m = Regex.Match(text, @"letter[^A-Za-z]*([A-Za-z])", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                letter = m.Groups[1].Value.ToUpperInvariant();
                return true;
            }

            // fallback: if single letter present
            var stripped = text.Trim();
            if (stripped.Length == 1 && char.IsLetter(stripped[0]))
            {
                letter = stripped.ToUpperInvariant();
                return true;
            }

            return false;
        }

        [Serializable]
        private class LetterAnswer
        {
            public string letter;
            public float confidence;
        }
    }
}
