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
    /// 材质识别任务（Material Perception）
    /// - 目标：基于高光/反射/粗糙度等线索判断对象材质（metal/glass/wood/fabric/sand/rock）
    /// - 变量：光照预设、背景、几何形状、视角/主光源方向
    /// - 输出：{"type":"inference","answer":{"material":"metal|glass|wood|fabric|sand|rock"},"confidence":0..1}
    /// </summary>
    public class MaterialPerceptionTask : ITask
    {
        public string TaskId => "material_perception";

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(1234);

        private ExperimentSceneManager _scene;
        private ObjectPlacer _placer;
        private Light _keyLight;

        // 当前使用的材质类别标签；需与 PromptTemplates 和 NormalizeMaterial 保持一致
        private readonly string[] _materials = { "metal", "glass", "wood", "fabric", "sand", "rock" };
        private readonly string[] _backgrounds = { "none", "indoor", "street" };
        private readonly string[] _lightings = { "bright", "dim", "hdr" };
        private readonly string[] _shapes = { "sphere", "cube", "cylinder" };
        private readonly float[] _yawOptions = { -30f, 0f, 20f, 45f };
        private readonly float[] _lightYawOptions = { -60f, -30f, 15f, 40f };

        private readonly Dictionary<string, Material> _materialCache =
            new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

        public MaterialPerceptionTask(TaskRunnerContext ctx)
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

            var trials = new List<TrialSpec>();
            int shapeIndex = 0;

            foreach (var material in _materials)
            {
                foreach (var bg in _backgrounds)
                {
                    foreach (var light in _lightings)
                    {
                        var shape = _shapes[shapeIndex % _shapes.Length];
                        shapeIndex++;

                        float objYaw = _yawOptions[_rand.Next(_yawOptions.Length)];
                        float lightYaw = _lightYawOptions[_rand.Next(_lightYawOptions.Length)];
                        float lightPitch = 35f + (float)(_rand.NextDouble() * 25f); // 35-60°

                        trials.Add(new TrialSpec
                        {
                            taskId = TaskId,
                            environment = "open_field",
                            background = bg,
                            fovDeg = 60f,
                            textureDensity = 1.0f,
                            lighting = light,
                            occlusion = false,

                            // 目标与真值
                            targetKind = shape,
                            material = material,
                            objectYawDeg = objYaw,
                            lightYawDeg = lightYaw,
                            lightPitchDeg = lightPitch
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
            return PromptTemplates.GetToolsForMaterialPerception();
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            return PromptTemplates.BuildMaterialPerceptionPrompt(
                string.IsNullOrEmpty(trial.targetKind) ? "object" : trial.targetKind,
                trial.background,
                trial.lighting,
                trial.objectYawDeg);
        }

        public async Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            TryBindHelpers();

            if (_scene != null)
            {
                var env = string.IsNullOrEmpty(trial.environment) ? "open_field" : trial.environment;
                var lighting = string.IsNullOrEmpty(trial.lighting) ? "bright" : trial.lighting;
                _scene.SetupEnvironment(env, trial.textureDensity, lighting, false);
            }

            var fov = trial.fovDeg > 0 ? trial.fovDeg : 60f;
            _ctx?.stimulus?.SetCameraFOV(fov);

            SetupKeyLight(trial);
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
                TryDestroyByPrefix("mp_target_");
            }

            if (_keyLight != null)
            {
                _keyLight.gameObject.SetActive(false);
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

            string predictedMaterial = null;

            if (response != null && response.type == "inference")
            {
                if (!TryExtractMaterialFromAnswer(response.answer, out predictedMaterial))
                {
                    TryExtractMaterialFromText(response.explanation, out predictedMaterial);
                }
            }

            if (string.IsNullOrEmpty(predictedMaterial))
            {
                eval.success = false;
                eval.failureReason = "No material field found in model output";
                return eval;
            }

            var normalizedPred = NormalizeMaterial(predictedMaterial);
            var trueMaterial = NormalizeMaterial(trial.material);

            eval.isCorrect = !string.IsNullOrEmpty(trueMaterial) &&
                             string.Equals(normalizedPred, trueMaterial, StringComparison.OrdinalIgnoreCase);

            var extra = new MaterialPerceptionExtra
            {
                trueMaterial = trueMaterial,
                predictedMaterial = normalizedPred,
                rawPredicted = predictedMaterial,
                lighting = string.IsNullOrEmpty(trial.lighting) ? "bright" : trial.lighting,
                background = string.IsNullOrEmpty(trial.background) ? "none" : trial.background,
                targetKind = string.IsNullOrEmpty(trial.targetKind) ? "object" : trial.targetKind,
                objectYawDeg = trial.objectYawDeg,
                lightYawDeg = trial.lightYawDeg,
                lightPitchDeg = trial.lightPitchDeg
            };

            try
            {
                eval.extraJson = JsonUtility.ToJson(extra);
            }
            catch
            {
                // ignore serialization errors
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

        private void SetupKeyLight(TrialSpec trial)
        {
            if (_keyLight == null)
            {
                var go = new GameObject("mp_key_light");
                _keyLight = go.AddComponent<Light>();
                _keyLight.type = LightType.Directional;
                _keyLight.shadows = LightShadows.Soft;
            }

            float yaw = trial.lightYawDeg;
            float pitch = trial.lightPitchDeg;
            if (Mathf.Abs(pitch) < 1e-3f) pitch = 45f;

            _keyLight.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);

            switch ((trial.lighting ?? "bright").ToLowerInvariant())
            {
                case "dim":
                    _keyLight.intensity = 0.35f;
                    _keyLight.color = Color.white;
                    break;
                case "hdr":
                    _keyLight.intensity = 1.5f;
                    _keyLight.color = new Color(1.05f, 1.05f, 1.05f);
                    break;
                default:
                    _keyLight.intensity = 1.0f;
                    _keyLight.color = Color.white;
                    break;
            }

            _keyLight.gameObject.SetActive(true);
        }

        private void PlaceTarget(TrialSpec trial)
        {
            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null) return;

            var origin = cam.transform.position;
            var forward = cam.transform.forward;

            float depth = 3.0f;
            var pos = origin + forward * depth;
            pos.y = origin.y + 1.3f;

            var kind = string.IsNullOrEmpty(trial.targetKind) ? "sphere" : trial.targetKind;
            var mat = GetOrCreateMaterial(string.IsNullOrEmpty(trial.material) ? "metal" : trial.material);

            GameObject placed = null;
            if (_placer != null)
            {
                placed = _placer.Place(kind, pos, 1.1f, mat, "mp_target_" + kind);
            }
            else
            {
                placed = CreatePrimitive(kind, mat, pos);
            }

            if (placed != null)
            {
                placed.transform.rotation = Quaternion.Euler(0f, trial.objectYawDeg, 0f);
            }
        }

        private GameObject CreatePrimitive(string kind, Material material, Vector3 pos)
        {
            GameObject go;
            switch ((kind ?? "sphere").ToLowerInvariant())
            {
                case "cube":
                    go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    break;
                case "cylinder":
                    go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    break;
                case "sphere":
                default:
                    go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    break;
            }

            go.name = "mp_target_" + kind;
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * 1.1f;

            var r = go.GetComponent<Renderer>();
            if (r != null && material != null)
            {
                r.sharedMaterial = material;
            }

            return go;
        }

        private Material GetOrCreateMaterial(string id)
        {
            var key = NormalizeMaterial(id) ?? "metal";
            if (_materialCache.TryGetValue(key, out var cached) && cached != null)
            {
                return cached;
            }

            // 优先从 Resources 中加载用户配置的材质球：
            // 期望路径：Assets/Resources/MaterialsPerception/<key>/*.mat
            // 例如 key = "metal" 时，对应 Resources.LoadAll<Material>("MaterialsPerception/metal")
            Material mat = null;
            try
            {
                var loaded = Resources.LoadAll<Material>($"MaterialsPerception/{key}");
                if (loaded != null && loaded.Length > 0)
                {
                    var idx = _rand.Next(loaded.Length);
                    mat = loaded[idx];
                }
            }
            catch
            {
                // 忽略 Resources 相关异常，退回默认材质逻辑
            }

            // 若 Resources 中未配置该类别，则退回到程序生成的默认材质，
            // 保证任务在缺少自定义材质时仍然可用。
            if (mat == null)
            {
                var shader = Shader.Find("Standard");
                mat = new Material(shader) { name = "mp_mat_" + key };

                switch (key)
                {
                    case "metal":
                        mat.color = new Color(0.7f, 0.72f, 0.75f);
                        mat.SetFloat("_Metallic", 1f);
                        mat.SetFloat("_Glossiness", 0.9f);
                        break;
                    case "glass":
                        mat.color = new Color(0.8f, 0.9f, 1f, 0.25f);
                        mat.SetFloat("_Metallic", 0f);
                        mat.SetFloat("_Glossiness", 0.95f);
                        ApplyTransparentSettings(mat);
                        break;
                    case "wood":
                        mat.color = new Color(0.55f, 0.37f, 0.2f);
                        mat.SetFloat("_Metallic", 0f);
                        mat.SetFloat("_Glossiness", 0.35f);
                        break;
                    case "fabric":
                        mat.color = new Color(0.7f, 0.7f, 0.78f);
                        mat.SetFloat("_Metallic", 0f);
                        mat.SetFloat("_Glossiness", 0.18f);
                        break;
                    case "sand":
                        mat.color = new Color(0.86f, 0.82f, 0.70f);
                        mat.SetFloat("_Metallic", 0f);
                        mat.SetFloat("_Glossiness", 0.25f);
                        break;
                    case "rock":
                        mat.color = new Color(0.45f, 0.46f, 0.50f);
                        mat.SetFloat("_Metallic", 0.1f);
                        mat.SetFloat("_Glossiness", 0.35f);
                        break;
                    default:
                        mat.color = Color.gray;
                        mat.SetFloat("_Metallic", 0.2f);
                        mat.SetFloat("_Glossiness", 0.5f);
                        break;
                }
            }

            _materialCache[key] = mat;
            return mat;
        }

        private void ApplyTransparentSettings(Material mat)
        {
            if (mat == null) return;

            mat.SetFloat("_Mode", 3f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        private bool TryExtractMaterialFromAnswer(object answer, out string material)
        {
            material = null;
            if (answer == null) return false;

            try
            {
                var t = answer.GetType();
                var matProp = t.GetProperty("material", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (matProp != null)
                {
                    var v = matProp.GetValue(answer)?.ToString();
                    if (!string.IsNullOrEmpty(v)) material = v;
                }

                if (string.IsNullOrEmpty(material))
                {
                    var catProp = t.GetProperty("category", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                    if (catProp != null)
                    {
                        var v = catProp.GetValue(answer)?.ToString();
                        if (!string.IsNullOrEmpty(v)) material = v;
                    }
                }

                if (!string.IsNullOrEmpty(material)) return true;

                var json = JsonUtility.ToJson(answer);
                if (!string.IsNullOrEmpty(json))
                {
                    var parsed = JsonUtility.FromJson<MaterialAnswer>(json);
                    if (parsed != null && !string.IsNullOrEmpty(parsed.material))
                    {
                        material = parsed.material;
                        return true;
                    }
                    if (parsed != null && !string.IsNullOrEmpty(parsed.category))
                    {
                        material = parsed.category;
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
                return TryExtractMaterialFromText(s, out material);
            }
            catch
            {
                return false;
            }
        }

        private bool TryExtractMaterialFromText(string text, out string material)
        {
            material = null;
            if (string.IsNullOrEmpty(text)) return false;

            var m = Regex.Match(text, "\"?material\"?\\s*[:=]\\s*\"?([a-zA-Z_]+)\"?", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                material = m.Groups[1].Value;
            }
            else
            {
                foreach (var token in _materials)
                {
                    if (Regex.IsMatch(text, $"\\b{token}\\b", RegexOptions.IgnoreCase))
                    {
                        material = token;
                        break;
                    }
                }

                if (material == null)
                {
                    if (Regex.IsMatch(text, "metallic|steel|iron|aluminum", RegexOptions.IgnoreCase)) material = "metal";
                    else if (Regex.IsMatch(text, "glass|transparent|translucent", RegexOptions.IgnoreCase)) material = "glass";
                    else if (Regex.IsMatch(text, "wood|wooden", RegexOptions.IgnoreCase)) material = "wood";
                    else if (Regex.IsMatch(text, "fabric|cloth|textile", RegexOptions.IgnoreCase)) material = "fabric";
                    else if (Regex.IsMatch(text, "sand|sandy", RegexOptions.IgnoreCase)) material = "sand";
                    else if (Regex.IsMatch(text, "rock|stone|rocky", RegexOptions.IgnoreCase)) material = "rock";
                }
            }

            return !string.IsNullOrEmpty(material);
        }

        private string NormalizeMaterial(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var s = raw.Trim().ToLowerInvariant();

            if (s.Contains("metal") || s.Contains("steel") || s.Contains("iron") || s.Contains("aluminum")) return "metal";
            if (s.Contains("glass") || s.Contains("transparent") || s.Contains("translucent")) return "glass";
            if (s.Contains("wood")) return "wood";
            if (s.Contains("fabric") || s.Contains("cloth") || s.Contains("textile")) return "fabric";
            if (s.Contains("sand") || s.Contains("sandy")) return "sand";
            if (s.Contains("rock") || s.Contains("stone") || s.Contains("rocky")) return "rock";

            return s;
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
        private class MaterialAnswer
        {
            public string material;
            public string category;
            public float confidence;
        }

        [Serializable]
        private class MaterialPerceptionExtra
        {
            public string trueMaterial;
            public string predictedMaterial;
            public string rawPredicted;
            public string lighting;
            public string background;
            public string targetKind;
            public float objectYawDeg;
            public float lightYawDeg;
            public float lightPitchDeg;
        }
    }
}
