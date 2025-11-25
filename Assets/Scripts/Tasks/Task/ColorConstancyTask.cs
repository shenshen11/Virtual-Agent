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
    /// 颜色恒常与照明任务（Color Constancy）
    /// - 目标：在不同光照条件下判断物体表面的“感知颜色”
    /// - 自变量：光照强度/环境标签/纹理密度等（颜色温度目前用简单预设近似）
    /// - 因变量：颜色类别（red/green/...）与近似 RGB 值
    /// </summary>
    public class ColorConstancyTask : ITask
    {
        public string TaskId => "color_constancy";

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(1234);

        private ExperimentSceneManager _scene;
        private ObjectPlacer _placer;

        // 颜色真值表（kind → (name, RGB)）
        private readonly Dictionary<string, (string name, int r, int g, int b)> _colorTable =
            new Dictionary<string, (string name, int r, int g, int b)>(StringComparer.OrdinalIgnoreCase)
            {
                { "color_patch_red",    ("red",    220,  40,  30) },
                { "color_patch_green",  ("green",   50, 160,  60) },
                { "color_patch_blue",   ("blue",    40,  80, 200) },
                { "color_patch_yellow", ("yellow", 230, 220,  40) },
                { "color_patch_white",  ("white",  240, 240, 240) },
                { "color_patch_gray",   ("gray",   128, 128, 128) }
            };

        public ColorConstancyTask(TaskRunnerContext ctx)
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
            // 使用 seed 控制 trial 顺序，试次集合本身为固定设计
            _rand = new System.Random(seed);

            var patchKinds = new[]
            {
                "color_patch_red",
                "color_patch_green",
                "color_patch_blue",
                "color_patch_yellow",
                "color_patch_white",
                "color_patch_gray"
            };

            var backgrounds = new[] { "none", "indoor", "street" };
            var lightings = new[] { "bright", "dim" }; // 映射到 ExperimentSceneManager.SetLighting 预设

            var trials = new List<TrialSpec>();

            foreach (var kind in patchKinds)
            {
                foreach (var bg in backgrounds)
                {
                    foreach (var light in lightings)
                    {
                        if (!_colorTable.TryGetValue(kind, out var meta))
                        {
                            // 未在真值表中的 kind，跳过
                            continue;
                        }

                        trials.Add(new TrialSpec
                        {
                            taskId = TaskId,
                            environment = "open_field",
                            background = bg,
                            fovDeg = 60f,
                            textureDensity = 1.0f,
                            lighting = light,
                            occlusion = false,

                            // 颜色目标
                            targetKind = kind,

                            // 真值：颜色类别与 RGB
                            colorName = meta.name,
                            trueR = meta.r,
                            trueG = meta.g,
                            trueB = meta.b,

                            // 简化：统一使用哑光材质，无强阴影（可在未来版本中扩展）
                            material = "matte",
                            hasShadow = false
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
            // 允许 set_lighting / camera_set_fov / head_look_at / snapshot，实现简单闭环
            return PromptTemplates.GetToolsForColorConstancy();
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            return PromptTemplates.BuildColorConstancyPrompt(
                string.IsNullOrEmpty(trial.targetKind) ? "color_patch" : trial.targetKind,
                trial.background,
                trial.lighting,
                string.IsNullOrEmpty(trial.material) ? "matte" : trial.material,
                trial.hasShadow,
                trial.fovDeg);
        }

        public async Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            TryBindHelpers();

            // 场景与光照：使用 ExperimentSceneManager 统一布置
            if (_scene != null)
            {
                var env = string.IsNullOrEmpty(trial.environment) ? "open_field" : trial.environment;
                var lighting = string.IsNullOrEmpty(trial.lighting) ? "bright" : trial.lighting;
                _scene.SetupEnvironment(env, trial.textureDensity, lighting, trial.occlusion);
            }

            // 相机 FOV
            var fov = trial.fovDeg > 0 ? trial.fovDeg : 60f;
            _ctx?.stimulus?.SetCameraFOV(fov);

            // 放置颜色补丁：单一主目标，位于视野中央偏下
            PlaceColorPatch(trial);

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
                TryDestroyByPrefix("cc_patch_");
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

            string predictedName = null;
            int[] predictedRgb = null;

            if (response != null && response.type == "inference")
            {
                if (!TryExtractColorFromAnswer(response.answer, out predictedName, out predictedRgb))
                {
                    TryExtractColorFromText(response.explanation, out predictedName, out predictedRgb);
                }
            }

            if (string.IsNullOrEmpty(predictedName) && predictedRgb == null)
            {
                eval.success = false;
                eval.failureReason = "No color_name/rgb information found in model output";
                return eval;
            }

            // 真值颜色名（优先 TrialSpec.colorName，其次从 kind 推断）
            var trueName = !string.IsNullOrEmpty(trial.colorName)
                ? NormalizeColorName(trial.colorName)
                : NormalizeColorNameFromKind(trial.targetKind);

            var normPredictedName = NormalizeColorName(predictedName);

            if (!string.IsNullOrEmpty(trueName) && !string.IsNullOrEmpty(normPredictedName))
            {
                eval.isCorrect = string.Equals(trueName, normPredictedName, StringComparison.OrdinalIgnoreCase);
            }

            // 计算 RGB 误差（若真值与预测均存在）
            float rgbDistance = 0f;
            if (trial.trueR >= 0 && trial.trueG >= 0 && trial.trueB >= 0 && predictedRgb != null && predictedRgb.Length >= 3)
            {
                int pr = Mathf.Clamp(predictedRgb[0], 0, 255);
                int pg = Mathf.Clamp(predictedRgb[1], 0, 255);
                int pb = Mathf.Clamp(predictedRgb[2], 0, 255);

                int tr = Mathf.Clamp(trial.trueR, 0, 255);
                int tg = Mathf.Clamp(trial.trueG, 0, 255);
                int tb = Mathf.Clamp(trial.trueB, 0, 255);

                int dr = pr - tr;
                int dg = pg - tg;
                int db = pb - tb;

                rgbDistance = Mathf.Sqrt(dr * dr + dg * dg + db * db);
            }

            // 通过 extraJson 暴露颜色相关指标，便于后续分析
            var extra = new ColorConstancyExtra
            {
                trueColorName = trueName,
                predictedColorName = normPredictedName,
                trueRgb = new[] { trial.trueR, trial.trueG, trial.trueB },
                predictedRgb = predictedRgb,
                rgbDistance = rgbDistance
            };

            try
            {
                eval.extraJson = JsonUtility.ToJson(extra);
            }
            catch
            {
                // 序列化失败不影响主评测结果
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

        private void PlaceColorPatch(TrialSpec trial)
        {
            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null) return;

            var origin = cam.transform.position;
            var forward = cam.transform.forward;

            // 将色块放置在视野前方约 3 米处，略低于视线
            float depth = 3.0f;
            var pos = origin + forward * depth;
            pos.y = origin.y + 1.2f;

            var kind = string.IsNullOrEmpty(trial.targetKind) ? "color_patch_gray" : trial.targetKind;

            if (_placer != null)
            {
                _placer.Place(kind, pos, 0.5f, null, "cc_patch_" + kind);
            }
            else
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = "cc_patch_" + kind;
                go.transform.position = pos;
                go.transform.localScale = Vector3.one * 0.5f;
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

        private void Shuffle<T>(IList<T> list)
        {
            if (list == null || list.Count <= 1) return;

            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rand.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private bool TryExtractColorFromAnswer(object answer, out string colorName, out int[] rgb)
        {
            colorName = null;
            rgb = null;
            if (answer == null) return false;

            try
            {
                var t = answer.GetType();
                var nameProp = t.GetProperty("color_name", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (nameProp != null)
                {
                    var v = nameProp.GetValue(answer)?.ToString();
                    if (!string.IsNullOrEmpty(v))
                    {
                        colorName = v;
                    }
                }

                var rgbProp = t.GetProperty("rgb", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (rgbProp != null)
                {
                    if (TryConvertToIntArray(rgbProp.GetValue(answer), out var arr))
                    {
                        rgb = arr;
                    }
                }

                if (!string.IsNullOrEmpty(colorName) || rgb != null)
                {
                    return true;
                }

                // JSON 兜底路径
                var json = JsonUtility.ToJson(answer);
                if (!string.IsNullOrEmpty(json))
                {
                    var parsed = JsonUtility.FromJson<ColorAnswer>(json);
                    if (parsed != null)
                    {
                        if (!string.IsNullOrEmpty(parsed.color_name))
                        {
                            colorName = parsed.color_name;
                        }
                        if (parsed.rgb != null && parsed.rgb.Length > 0)
                        {
                            rgb = parsed.rgb;
                        }
                        if (!string.IsNullOrEmpty(colorName) || rgb != null)
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            // ToString 粗提取
            try
            {
                var s = answer.ToString();
                return TryExtractColorFromText(s, out colorName, out rgb);
            }
            catch
            {
                return false;
            }
        }

        private bool TryExtractColorFromText(string text, out string colorName, out int[] rgb)
        {
            colorName = null;
            rgb = null;

            if (string.IsNullOrEmpty(text)) return false;

            // 优先匹配 color_name 字段
            var mName = Regex.Match(text, "\"?color_name\"?\\s*[:=]\\s*\"?([a-zA-Z_]+)\"?", RegexOptions.IgnoreCase);
            if (mName.Success)
            {
                colorName = mName.Groups[1].Value;
            }
            else
            {
                // 关键字启发：在文本中寻找常见颜色词
                foreach (var c in new[] { "red", "green", "blue", "yellow", "white", "gray", "grey" })
                {
                    if (Regex.IsMatch(text, $"\\b{c}\\b", RegexOptions.IgnoreCase))
                    {
                        colorName = c;
                        break;
                    }
                }
            }

            // 尝试提取 rgb 数组：[r,g,b]
            var mRgb = Regex.Match(text, @"\[\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*\]");
            if (mRgb.Success)
            {
                if (int.TryParse(mRgb.Groups[1].Value, out var r) &&
                    int.TryParse(mRgb.Groups[2].Value, out var g) &&
                    int.TryParse(mRgb.Groups[3].Value, out var b))
                {
                    rgb = new[] { r, g, b };
                }
            }

            return !string.IsNullOrEmpty(colorName) || rgb != null;
        }

        private static bool TryConvertToIntArray(object value, out int[] arr)
        {
            arr = null;
            if (value == null) return false;

            switch (value)
            {
                case int[] ia:
                    arr = ia;
                    return true;
                case List<int> list:
                    arr = list.ToArray();
                    return true;
                case System.Collections.IEnumerable enumerable:
                    var tmp = new List<int>();
                    foreach (var v in enumerable)
                    {
                        if (v == null) continue;
                        if (int.TryParse(v.ToString(), out var iv))
                        {
                            tmp.Add(iv);
                        }
                    }
                    if (tmp.Count > 0)
                    {
                        arr = tmp.ToArray();
                        return true;
                    }
                    break;
            }

            return false;
        }

        private string NormalizeColorNameFromKind(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return null;

            if (_colorTable.TryGetValue(kind, out var meta))
            {
                return NormalizeColorName(meta.name);
            }

            // 简单 fallback：尝试从 kind 名中解析颜色词
            if (kind.IndexOf("red", StringComparison.OrdinalIgnoreCase) >= 0) return "red";
            if (kind.IndexOf("green", StringComparison.OrdinalIgnoreCase) >= 0) return "green";
            if (kind.IndexOf("blue", StringComparison.OrdinalIgnoreCase) >= 0) return "blue";
            if (kind.IndexOf("yellow", StringComparison.OrdinalIgnoreCase) >= 0) return "yellow";
            if (kind.IndexOf("white", StringComparison.OrdinalIgnoreCase) >= 0) return "white";
            if (kind.IndexOf("gray", StringComparison.OrdinalIgnoreCase) >= 0 ||
                kind.IndexOf("grey", StringComparison.OrdinalIgnoreCase) >= 0) return "gray";

            return null;
        }

        private static string NormalizeColorName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var s = raw.Trim().ToLowerInvariant();
            return s switch
            {
                "grey" => "gray",
                _ => s
            };
        }

        [Serializable]
        private class ColorAnswer
        {
            public string color_name;
            public int[] rgb;
            public float confidence;
        }

        [Serializable]
        private class ColorConstancyExtra
        {
            public string trueColorName;
            public string predictedColorName;
            public int[] trueRgb;
            public int[] predictedRgb;
            public float rgbDistance;
        }
    }
}

