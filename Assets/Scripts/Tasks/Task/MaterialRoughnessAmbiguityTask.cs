using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VRPerception.Perception;

namespace VRPerception.Tasks
{
    /// <summary>
    /// 材质粗糙度歧义任务（Material Roughness under Ambiguity）
    /// - 自变量：roughness（0..1）与环境（Complex vs Simple module）
    /// - 因变量：模型/人类报告的 roughness（0..1）
    /// - 输出（模型侧）：{"roughness":<0..1>,"confidence":<0..1>}（Provider 会包装成 LLMResponse.type="inference"）
    /// </summary>
    public class MaterialRoughnessAmbiguityTask : ITask
    {
        private const float DefaultFovDeg = 60f;
        private const float DefaultSphereScale = 0.30f; // 单位=米（Sphere primitive 直径=1）
        private const float DefaultFallbackDistanceM = 1.0f;

        private readonly string _taskId;
        private readonly bool _requireHeadMotion;

        public string TaskId => _taskId;

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(1234);

        private ExperimentSceneManager _scene;
        private ObjectPlacer _placer;

        private readonly Dictionary<string, Material> _materialCache =
            new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

        public MaterialRoughnessAmbiguityTask(TaskRunnerContext ctx, string taskId, bool requireHeadMotion)
        {
            _ctx = ctx;
            _taskId = string.IsNullOrWhiteSpace(taskId) ? "material_roughness" : taskId;
            _requireHeadMotion = requireHeadMotion;
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

            // 计划：roughness 6 级 × 环境 2 种 × 重复 3 次 = 36 trial
            var roughnessLevels = new[] { 0.0f, 0.2f, 0.4f, 0.6f, 0.8f, 1.0f };
            var moduleEnvs = new[] { "module:room_complex", "module:black_simple" };

            const int repeats = 3;
            var trials = new List<TrialSpec>(roughnessLevels.Length * moduleEnvs.Length * repeats);

            for (int r = 0; r < repeats; r++)
            {
                foreach (var env in moduleEnvs)
                {
                    foreach (var roughness in roughnessLevels)
                    {
                        trials.Add(new TrialSpec
                        {
                            taskId = TaskId,
                            environment = env,
                            textureDensity = 1.0f,
                            lighting = "module",
                            occlusion = false,
                            fovDeg = DefaultFovDeg,

                            // 目标定义
                            targetKind = "sphere",
                            material = "metal",
                            roughness = Mathf.Clamp01(roughness),
                            requireHeadMotion = _requireHeadMotion
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
            return PromptTemplates.GetToolsForMaterialRoughness();
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            var env = string.IsNullOrWhiteSpace(trial.environment) ? "unknown" : trial.environment;
            return PromptTemplates.BuildMaterialRoughnessPrompt(env, trial.requireHeadMotion, trial.fovDeg, trial.trialId);
        }

        public async Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            TryBindHelpers();

            if (_scene != null)
            {
                var env = string.IsNullOrEmpty(trial.environment) ? "open_field" : trial.environment;
                _scene.SetupEnvironment(env, trial.textureDensity, string.IsNullOrEmpty(trial.lighting) ? "module" : trial.lighting, trial.occlusion);
            }

            _ctx?.stimulus?.SetCameraFOV(trial.fovDeg > 0 ? trial.fovDeg : DefaultFovDeg);

            PlaceTarget(trial);
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
                TryDestroyByPrefix("mr_target_");
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

            if (!TryExtractRoughness(response.answer, out var predictedRoughness))
            {
                // 兜底：尝试从 explanation 文本中抓取（有些 provider 会把 JSON 放到 explanation）
                if (!TryExtractRoughnessFromText(response.explanation, out predictedRoughness))
                {
                    eval.success = false;
                    eval.failureReason = "No roughness field found in model output";
                    return eval;
                }
            }

            predictedRoughness = Mathf.Clamp01(predictedRoughness);
            var trueR = Mathf.Clamp01(trial.roughness);

            eval.predictedRoughness = predictedRoughness;
            eval.trueRoughness = trueR;
            eval.roughnessSignedError = predictedRoughness - trueR;
            eval.roughnessAbsError = Mathf.Abs(eval.roughnessSignedError);

            try
            {
                var extra = new RoughnessExtra
                {
                    env = trial.environment,
                    requireHeadMotion = trial.requireHeadMotion,
                    trueRoughness = trueR,
                    predictedRoughness = predictedRoughness
                };
                eval.extraJson = JsonUtility.ToJson(extra);
            }
            catch
            {
                // ignore
            }

            eval.success = true;
            return eval;
        }

        // =============== Helpers ===============

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

            var anchor = _scene != null ? _scene.GetModuleAnchor("StimulusAnchor") : null;
            Vector3 pos;
            if (anchor != null)
            {
                pos = anchor.position;
            }
            else
            {
                pos = cam.transform.position + cam.transform.forward * DefaultFallbackDistanceM;
                pos.y = cam.transform.position.y;
            }

            var mat = GetOrCreateMetalMaterial(trial.roughness);

            GameObject placed = null;
            if (_placer != null)
            {
                placed = _placer.Place("sphere", pos, DefaultSphereScale, mat, "mr_target_sphere");
            }
            else
            {
                placed = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                placed.name = "mr_target_sphere";
                placed.transform.position = pos;
                placed.transform.localScale = Vector3.one * DefaultSphereScale;
                var r = placed.GetComponent<Renderer>();
                if (r != null && mat != null) r.sharedMaterial = mat;
            }

            if (placed != null)
            {
                // 统一对齐：面向相机（避免随机姿态引入额外线索）
                var lookDir = (cam.transform.position - placed.transform.position);
                lookDir.y = 0f;
                if (lookDir.sqrMagnitude > 1e-4f)
                {
                    placed.transform.rotation = Quaternion.LookRotation(lookDir.normalized, Vector3.up);
                }
            }
        }

        private Material GetOrCreateMetalMaterial(float roughness)
        {
            float r = Mathf.Clamp01(roughness);
            // key 做离散化，避免 float 精度导致缓存 miss
            var key = $"metal_r{Mathf.RoundToInt(r * 1000f):0000}";
            if (_materialCache.TryGetValue(key, out var cached) && cached != null)
            {
                return cached;
            }

            var shader = Shader.Find("Standard");
            var mat = new Material(shader)
            {
                name = "mr_" + key
            };

            mat.color = new Color(0.70f, 0.70f, 0.70f, 1f);
            mat.SetFloat("_Metallic", 1f);
            mat.SetFloat("_Glossiness", Mathf.Clamp01(1f - r));

            _materialCache[key] = mat;
            return mat;
        }

        private bool TryExtractRoughness(object answer, out float roughness)
        {
            roughness = 0f;
            if (answer == null) return false;

            if (answer is float f)
            {
                roughness = f;
                return true;
            }
            if (answer is double d)
            {
                roughness = (float)d;
                return true;
            }
            if (answer is int i)
            {
                roughness = i;
                return true;
            }

            try
            {
                var json = JsonUtility.ToJson(answer);
                if (!string.IsNullOrEmpty(json))
                {
                    var parsed = JsonUtility.FromJson<RoughnessAnswer>(json);
                    if (parsed != null)
                    {
                        // 兼容：roughness 或 value 字段
                        if (!float.IsNaN(parsed.roughness))
                        {
                            roughness = parsed.roughness;
                            return true;
                        }
                        if (!float.IsNaN(parsed.value))
                        {
                            roughness = parsed.value;
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                return TryExtractRoughnessFromText(answer.ToString(), out roughness);
            }
            catch
            {
                return false;
            }
        }

        private bool TryExtractRoughnessFromText(string text, out float roughness)
        {
            roughness = 0f;
            if (string.IsNullOrWhiteSpace(text)) return false;

            // roughness: 0.4 / "roughness":0.4 / roughness=0.4
            var m = Regex.Match(text, "\"?roughness\"?\\s*[:=]\\s*([0-9]*\\.?[0-9]+)", RegexOptions.IgnoreCase);
            if (m.Success && float.TryParse(m.Groups[1].Value, out roughness))
            {
                return true;
            }

            // 兼容：value 字段
            m = Regex.Match(text, "\"?value\"?\\s*[:=]\\s*([0-9]*\\.?[0-9]+)", RegexOptions.IgnoreCase);
            if (m.Success && float.TryParse(m.Groups[1].Value, out roughness))
            {
                return true;
            }

            return false;
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

        [Serializable]
        private class RoughnessAnswer
        {
            public float roughness;
            public float value;
            public float confidence;
        }

        [Serializable]
        private class RoughnessExtra
        {
            public string env;
            public bool requireHeadMotion;
            public float trueRoughness;
            public float predictedRoughness;
        }
    }
}
