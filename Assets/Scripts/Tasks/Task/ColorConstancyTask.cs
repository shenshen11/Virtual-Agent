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

            // 颜色恒常实验用光照预设：强度（bright/dim）× 色温（neutral/warm/cool）
            var lightingPresets = new[]
            {
                "bright_neutral",
                "dim_neutral",
                "bright_warm",
                "dim_warm",
                "bright_cool",
                "dim_cool"
            };

            var shadowConditions = new[] { false, true };

            var trials = new List<TrialSpec>();

            foreach (var kind in patchKinds)
            {
                if (!_colorTable.TryGetValue(kind, out var meta))
                {
                    // 未在真值表中的 kind，跳过
                    continue;
                }

                foreach (var bg in backgrounds)
                {
                    foreach (var light in lightingPresets)
                    {
                        foreach (var hasShadow in shadowConditions)
                        {
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

                                // 简化：统一使用哑光材质，hasShadow 控制是否启用方向光阴影
                                material = "matte",
                                hasShadow = hasShadow
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
                var lighting = string.IsNullOrEmpty(trial.lighting) ? "bright_neutral" : trial.lighting;
                _scene.SetupEnvironment(env, trial.textureDensity, lighting, trial.occlusion);
                _scene.SetShadowMode(trial.hasShadow);
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

            // 计算 RGB 误差与 ΔE2000（若真值与预测均存在）
            float rgbDistance = 0f;
            float deltaE00 = 0f;
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

                // 近似将 sRGB 转为 Lab，并计算 ΔE2000 作为感知差异指标
                RgbToLab(tr, tg, tb, out var L1, out var a1, out var b1);
                RgbToLab(pr, pg, pb, out var L2, out var a2, out var b2);
                deltaE00 = DeltaE2000(L1, a1, b1, L2, a2, b2);
            }

            // 通过 extraJson 暴露颜色相关指标，便于后续分析
            var extra = new ColorConstancyExtra
            {
                trueColorName = trueName,
                predictedColorName = normPredictedName,
                trueRgb = new[] { trial.trueR, trial.trueG, trial.trueB },
                predictedRgb = predictedRgb,
                rgbDistance = rgbDistance,
                deltaE00 = deltaE00
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

        /// <summary>
        /// 将 sRGB (0-255) 近似转换到 CIE Lab（D65）。
        /// </summary>
        private static void RgbToLab(int r, int g, int b, out float L, out float a, out float bb)
        {
            // 归一化到 0-1
            float rNorm = r / 255f;
            float gNorm = g / 255f;
            float bNorm = b / 255f;

            // sRGB → 线性
            rNorm = (rNorm <= 0.04045f) ? (rNorm / 12.92f) : Mathf.Pow((rNorm + 0.055f) / 1.055f, 2.4f);
            gNorm = (gNorm <= 0.04045f) ? (gNorm / 12.92f) : Mathf.Pow((gNorm + 0.055f) / 1.055f, 2.4f);
            bNorm = (bNorm <= 0.04045f) ? (bNorm / 12.92f) : Mathf.Pow((bNorm + 0.055f) / 1.055f, 2.4f);

            // 线性 RGB → XYZ (D65)
            float X = (0.4124564f * rNorm + 0.3575761f * gNorm + 0.1804375f * bNorm) * 100f;
            float Y = (0.2126729f * rNorm + 0.7151522f * gNorm + 0.0721750f * bNorm) * 100f;
            float Z = (0.0193339f * rNorm + 0.1191920f * gNorm + 0.9503041f * bNorm) * 100f;

            // 参考白点 D65
            const float Xn = 95.047f;
            const float Yn = 100.000f;
            const float Zn = 108.883f;

            float x = X / Xn;
            float y = Y / Yn;
            float z = Z / Zn;

            static float F(float t)
            {
                const float delta = 6f / 29f;
                const float delta3 = delta * delta * delta;
                if (t > delta3)
                {
                    return Mathf.Pow(t, 1f / 3f);
                }
                return t / (3f * delta * delta) + 4f / 29f;
            }

            float fx = F(x);
            float fy = F(y);
            float fz = F(z);

            L = 116f * fy - 16f;
            a = 500f * (fx - fy);
            bb = 200f * (fy - fz);
        }

        /// <summary>
        /// 计算两点 Lab 颜色的 CIEDE2000 距离（ΔE00）。
        /// </summary>
        private static float DeltaE2000(float L1, float a1, float b1, float L2, float a2, float b2)
        {
            const float kL = 1f;
            const float kC = 1f;
            const float kH = 1f;

            float C1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float C2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float CBar = 0.5f * (C1 + C2);

            float CBar7 = Mathf.Pow(CBar, 7f);
            float G = 0.5f * (1f - Mathf.Sqrt(CBar7 / (CBar7 + Mathf.Pow(25f, 7f))));

            float a1Prime = (1f + G) * a1;
            float a2Prime = (1f + G) * a2;

            float C1Prime = Mathf.Sqrt(a1Prime * a1Prime + b1 * b1);
            float C2Prime = Mathf.Sqrt(a2Prime * a2Prime + b2 * b2);

            float h1Prime = Mathf.Atan2(b1, a1Prime) * Mathf.Rad2Deg;
            if (h1Prime < 0f) h1Prime += 360f;

            float h2Prime = Mathf.Atan2(b2, a2Prime) * Mathf.Rad2Deg;
            if (h2Prime < 0f) h2Prime += 360f;

            float deltaLPrime = L2 - L1;
            float deltaCPrime = C2Prime - C1Prime;

            float deltahPrime;
            if (C1Prime * C2Prime == 0f)
            {
                deltahPrime = 0f;
            }
            else
            {
                float dh = h2Prime - h1Prime;
                if (dh > 180f) dh -= 360f;
                else if (dh < -180f) dh += 360f;
                deltahPrime = dh;
            }

            float deltaHPrime = 2f * Mathf.Sqrt(C1Prime * C2Prime) * Mathf.Sin(deltahPrime * Mathf.Deg2Rad * 0.5f);

            float LBarPrime = 0.5f * (L1 + L2);
            float CBarPrime = 0.5f * (C1Prime + C2Prime);

            float hBarPrime;
            if (C1Prime * C2Prime == 0f)
            {
                hBarPrime = h1Prime + h2Prime;
            }
            else
            {
                float hDiff = Mathf.Abs(h1Prime - h2Prime);
                if (hDiff <= 180f)
                {
                    hBarPrime = 0.5f * (h1Prime + h2Prime);
                }
                else
                {
                    float sum = h1Prime + h2Prime;
                    if (sum < 360f) sum += 360f;
                    else sum -= 360f;
                    hBarPrime = 0.5f * sum;
                }
            }

            float T =
                1f
                - 0.17f * Mathf.Cos(Mathf.Deg2Rad * (hBarPrime - 30f))
                + 0.24f * Mathf.Cos(Mathf.Deg2Rad * (2f * hBarPrime))
                + 0.32f * Mathf.Cos(Mathf.Deg2Rad * (3f * hBarPrime + 6f))
                - 0.20f * Mathf.Cos(Mathf.Deg2Rad * (4f * hBarPrime - 63f));

            float deltaTheta = 30f * Mathf.Exp(-Mathf.Pow((hBarPrime - 275f) / 25f, 2f));
            float CBarPrime7 = Mathf.Pow(CBarPrime, 7f);
            float Rc = 2f * Mathf.Sqrt(CBarPrime7 / (CBarPrime7 + Mathf.Pow(25f, 7f)));
            float Sl = 1f + (0.015f * Mathf.Pow(LBarPrime - 50f, 2f)) / Mathf.Sqrt(20f + Mathf.Pow(LBarPrime - 50f, 2f));
            float Sc = 1f + 0.045f * CBarPrime;
            float Sh = 1f + 0.015f * CBarPrime * T;
            float Rt = -Rc * Mathf.Sin(2f * Mathf.Deg2Rad * deltaTheta);

            float dLTerm = deltaLPrime / (kL * Sl);
            float dCTerm = deltaCPrime / (kC * Sc);
            float dHTerm = deltaHPrime / (kH * Sh);

            float deltaE =
                dLTerm * dLTerm +
                dCTerm * dCTerm +
                dHTerm * dHTerm +
                Rt * dCTerm * dHTerm;

            return Mathf.Sqrt(Mathf.Max(deltaE, 0f));
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
            public float deltaE00;
        }
    }
}
