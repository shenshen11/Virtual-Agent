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

        private readonly string[] _letterPool = new[]
        {
            "A","B","C","D","E","F","G","H","J","K","L","M","N","P","R","S","T","U","V","W","X","Y","Z"
        }; // 去掉 I/O/Q 降低混淆

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(1234);
        private ExperimentSceneManager _scene;
        private readonly List<GameObject> _spawned = new List<GameObject>();
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

            var eccentricities = new[] { 8f, 12f, 16f };
            var spacings = new[] { 1.0f, 1.5f, 2.0f, 2.5f };
            const int repetitions = 2; // 最小可行：每组合重复 2 次，合计 24 条

            var trials = new List<TrialSpec>();

            foreach (var ecc in eccentricities)
            {
                foreach (var sp in spacings)
                {
                    for (int rep = 0; rep < repetitions; rep++)
                    {
                        var target = SampleLetter();
                        var flankers = BuildFlankers(target);

                        trials.Add(new TrialSpec
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
                            targetLetter = target,
                            flankerLetters = flankers,
                            targetIndex = 2
                        });
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

            // 固定深度布置：注视点靠近，字母串稍远，保证视角换算稳定
            const float fixationDepth = 7.0f;
            const float lettersDepth = 7.0f;

            PlaceFixation(origin, forward, eyeY, fixationDepth);
            PlaceLetters(origin, forward, right, eyeY, lettersDepth, trial);

            await Task.Yield();
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

            // 十字注视点：水平+垂直细条
            var horiz = GameObject.CreatePrimitive(PrimitiveType.Cube);
            horiz.transform.SetParent(root.transform, worldPositionStays: false);
            horiz.transform.localScale = new Vector3(0.36f, 0.05f, 0.01f);

            var vert = GameObject.CreatePrimitive(PrimitiveType.Cube);
            vert.transform.SetParent(root.transform, worldPositionStays: false);
            vert.transform.localScale = new Vector3(0.05f, 0.36f, 0.01f);

            var rendererH = horiz.GetComponent<Renderer>();
            var rendererV = vert.GetComponent<Renderer>();
            if (rendererH != null) rendererH.material.color = Color.red;
            if (rendererV != null) rendererV.material.color = Color.red;

            _spawned.Add(root);
        }

        private void PlaceLetters(Vector3 origin, Vector3 forward, Vector3 right, float eyeY, float depth, TrialSpec trial)
        {
            if (trial == null || trial.flankerLetters == null || trial.flankerLetters.Length < 5) return;

            var basePos = origin + forward * depth;

            // 放大间距比例避免拥挤过度，并确保整串在注视点右侧
            var baseSpacing = 0.5f; // 保底间距，防止重叠
            var spacingOffset = Mathf.Max(Mathf.Tan(trial.spacingDeg * Mathf.Deg2Rad) * depth * 2.5f, baseSpacing);
            var eccOffset = Mathf.Tan(trial.eccentricityDeg * Mathf.Deg2Rad) * depth;
            var minEccOffset = (trial.targetIndex + 1.0f) * spacingOffset; // 左端留半个间距的安全距离
            if (eccOffset < minEccOffset) eccOffset = minEccOffset;

            for (int i = 0; i < trial.flankerLetters.Length; i++)
            {
                var idxOffset = i - trial.targetIndex;
                var offset = eccOffset + spacingOffset * idxOffset;
                var pos = basePos + right * offset;
                pos.y = eyeY; // 与参考眼高等高，模拟水平字母串

                var go = CreateLetterObject(trial.flankerLetters[i], pos, forward);
                if (go != null)
                {
                    go.name = $"vc_letter_{i}";
                    _spawned.Add(go);
                }
            }
        }

        private GameObject CreateLetterObject(string letter, Vector3 position, Vector3 forward)
        {
            var go = new GameObject("vc_letter", typeof(TextMesh));
            go.transform.position = position;
            go.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);

            var tm = go.GetComponent<TextMesh>();
            tm.text = string.IsNullOrEmpty(letter) ? "?" : letter.ToUpperInvariant();
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.characterSize = 0.06f;
            tm.fontSize = 100;
            tm.color = Color.white;

            return go;
        }

        private void ClearSpawned()
        {
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
