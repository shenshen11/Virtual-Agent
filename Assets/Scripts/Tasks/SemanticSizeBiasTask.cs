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
    /// 语义大小偏差任务（Semantic Size Bias）
    /// - 同屏展示一对日常物体（A/B），真实物理尺寸可相等或反转
    /// - 背景：none/indoor/street（此实现以环境光与简单地面模拟）
    /// - 被试输出：{"type":"inference","answer":{"larger":"A|B"},"confidence":0..1}
    /// </summary>
    public class SemanticSizeBiasTask : ITask
    {
        public string TaskId => "semantic_size_bias";

        private TaskRunnerContext _ctx;
        private System.Random _rand = new System.Random(1234);

        // 运行时引用（若存在则优先使用）
        private ExperimentSceneManager _scene;
        private ObjectPlacer _placer;

        // 语义基准尺寸（仅用于“相等/反转”的物理缩放参考，非真实单位）
        private readonly Dictionary<string, float> _baseSize = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            { "chair",   1.0f },
            { "cup",     0.3f },
            { "toy_car", 0.4f },
            { "apple",   0.25f },
            { "human",   1.8f },
            { "cube",    1.0f },
            { "sphere",  1.0f }
        };

        public SemanticSizeBiasTask(TaskRunnerContext ctx)
        {
            _ctx = ctx;
            TryBindHelpers(ctx);
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
            TryBindHelpers(_ctx);
        }

        public TrialSpec[] BuildTrials(int seed)
        {
            // 使用 seed 仅控制 trial 顺序，试次集合本身为固定的均衡设计
            _rand = new System.Random(seed);

            var pairs = new (string A, string B)[] {
                ("chair","cup"),
                ("toy_car","apple"),
                ("cube","sphere")
            };
            var bgs = new[] { "none", "indoor", "street" };

            var trials = new List<TrialSpec>();

            // 设计：每个物体对生成 10 个试次（5 个 equal, 5 个 reversed），共 30 条
            for (int pairIndex = 0; pairIndex < pairs.Length; pairIndex++)
            {
                var p = pairs[pairIndex];

                for (int i = 0; i < 10; i++)
                {
                    // 交替生成 equal / reversed（每个 pair 各 5 条）
                    var relation = (i % 2 == 0) ? "equal" : "reversed";
                    // 背景轮换分配，避免完全随机导致分布不均
                    var bg = bgs[(pairIndex + i) % bgs.Length];

                    trials.Add(new TrialSpec
                    {
                        taskId = TaskId,
                        objectA = p.A,
                        objectB = p.B,
                        sizeRelation = relation,    // equal | reversed
                        background = bg,
                        environment = "open_field", // 简化用 open_field（如需走廊可扩展）
                        fovDeg = 60f,
                        textureDensity = 1.0f,
                        lighting = bg == "indoor" ? "dim" : "bright",
                        occlusion = false
                    });
                }
            }

            // 使用 seed 控制试次顺序
            Shuffle(trials);
            return trials.ToArray();
        }

        public string GetSystemPrompt()
        {
            return PromptTemplates.GetSystemPrompt(TaskId);
        }

        public ToolSpec[] GetTools()
        {
            // 该任务通常只需一次判断，无需动作闭环（可扩展 snapshot/tool）
            return null;
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            return PromptTemplates.BuildSemanticSizeBiasPrompt(trial.objectA, trial.objectB, trial.sizeRelation, trial.background);
        }

        public async Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            TryBindHelpers(_ctx);

            // 场景/光照/纹理
            if (_scene != null)
            {
                // 使用环境与光照模拟背景
                _scene.SetupEnvironment(trial.environment ?? "open_field", trial.textureDensity, trial.lighting, trial.occlusion);
            }

            // 相机 FOV
            _ctx?.stimulus?.SetCameraFOV(trial.fovDeg > 0 ? trial.fovDeg : 60f);

            // 放置 A/B（同距相机前方，左右分布）
            PlacePair(trial);

            await Task.Yield();
        }

        public async Task OnAfterTrialAsync(TrialSpec trial, LLMResponse response, CancellationToken ct)
        {
            // 清理
            if (_placer != null)
            {
                _placer.ClearAll();
            }
            else
            {
                // 作为兜底，尝试销毁名称前缀的对象
                TryDestroyIfExists("ssb_A");
                TryDestroyIfExists("ssb_B");
                TryDestroyIfExists("ssb_ground");
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

            // 期望答案（按“视觉上更大者”定义）
            var expected = ComputeTrueLarger(trial) ?? "A";

            string predicted = null;
            if (response != null && response.type == "inference")
            {
                if (TryExtractLargerFromAnswer(response.answer, out var l1))
                    predicted = l1;
                else
                    predicted = TryExtractLargerFromText(response.explanation);
            }

            if (!string.IsNullOrEmpty(predicted))
            {
                eval.predictedLarger = predicted.ToUpperInvariant();
                eval.isCorrect = string.Equals(eval.predictedLarger, expected, StringComparison.OrdinalIgnoreCase);
                eval.success = true;
            }
            else
            {
                eval.success = false;
                eval.failureReason = "No larger (A/B) found";
            }

            return eval;
        }

        // =============== Helpers ===============

        /// <summary>
        /// 使用当前任务内的有种子随机源 _rand 对列表做 Fisher-Yates 洗牌，保持顺序可复现。
        /// </summary>
        private void Shuffle<T>(IList<T> list)
        {
            if (list == null || list.Count <= 1) return;

            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rand.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private void TryBindHelpers(TaskRunnerContext ctx)
        {
            if (ctx?.runner != null)
            {
                if (_scene == null) _scene = ctx.runner.GetComponent<ExperimentSceneManager>();
                if (_placer == null) _placer = ctx.runner.GetComponent<ObjectPlacer>();
            }
            if (_scene == null) _scene = UnityEngine.Object.FindObjectOfType<ExperimentSceneManager>();
            if (_placer == null) _placer = UnityEngine.Object.FindObjectOfType<ObjectPlacer>();
        }

        private void PlacePair(TrialSpec trial)
        {
            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null) return;

            // 与相机的距离（固定 5m）
            var distance = 5f;
            var center = cam.transform.position + cam.transform.forward * distance;

            // 左右偏移
            var offset = cam.transform.right * 0.75f;
            var posA = center - offset; // 屏幕左侧
            var posB = center + offset; // 屏幕右侧

            // 计算两物体的物理缩放
            var (scaleA, scaleB) = ComputeScales(trial);

            if (_placer != null)
            {
                _placer.Place(trial.objectA ?? "cube", posA, scaleA, null, "ssb_A");
                _placer.Place(trial.objectB ?? "cube", posB, scaleB, null, "ssb_B");
            }
            else
            {
                // 兜底创建
                var goA = CreatePrimitiveForKind(trial.objectA ?? "cube");
                var goB = CreatePrimitiveForKind(trial.objectB ?? "cube");
                if (goA != null) { goA.name = "ssb_A"; goA.transform.position = posA; goA.transform.localScale = Vector3.one * scaleA; }
                if (goB != null) { goB.name = "ssb_B"; goB.transform.position = posB; goB.transform.localScale = Vector3.one * scaleB; }

                // 简单地面
                var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.name = "ssb_ground";
                ground.transform.position = new Vector3(0, 0, 0);
                ground.transform.localScale = new Vector3(2, 1, 2);
            }
        }

        private (float scaleA, float scaleB) ComputeScales(TrialSpec trial)
        {
            var a = trial.objectA ?? "cube";
            var b = trial.objectB ?? "cube";
            var sizeA = _baseSize.TryGetValue(a, out var sa) ? sa : 1.0f;
            var sizeB = _baseSize.TryGetValue(b, out var sb) ? sb : 1.0f;

            // equal: 强制相等（以较大的基准靠拢）
            // reversed: 反转常识比例（大变小，小变大）
            if (string.Equals(trial.sizeRelation, "equal", StringComparison.OrdinalIgnoreCase))
            {
                var avg = (sizeA + sizeB) * 0.5f;
                return (avg, avg);
            }
            else // reversed
            {
                // 简单反转：A 的比例取 (baseB)，B 取 (baseA)
                return (sizeB, sizeA);
            }
        }

        private string ComputeTrueLarger(TrialSpec trial)
        {
            // 根据“屏幕投影尺寸”近似为物体缩放大小（距离相等）
            var (sA, sB) = ComputeScales(trial);
            if (Mathf.Approximately(sA, sB)) return null;
            return sA > sB ? "A" : "B";
        }

        private static bool TryExtractLargerFromAnswer(object answer, out string larger)
        {
            larger = null;
            if (answer == null) return false;

            // 1) 反射/JSON 尝试
            try
            {
                var t = answer.GetType();
                var prop = t.GetProperty("larger", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var v = prop.GetValue(answer)?.ToString();
                    if (!string.IsNullOrEmpty(v)) { larger = v.ToUpperInvariant(); return true; }
                }
                var json = JsonUtility.ToJson(answer);
                if (!string.IsNullOrEmpty(json))
                {
                    var parsed = JsonUtility.FromJson<SizeAnswer>(json);
                    if (!string.IsNullOrEmpty(parsed?.larger)) { larger = parsed.larger.ToUpperInvariant(); return true; }
                }
            }
            catch { /* ignore */ }

            // 2) ToString 粗提取
            try
            {
                var s = answer.ToString();
                var m = Regex.Match(s, @"larger[^A-Za-z]*([AB])", RegexOptions.IgnoreCase);
                if (m.Success) { larger = m.Groups[1].Value.ToUpperInvariant(); return true; }
            }
            catch { /* ignore */ }

            return false;
        }

        private static string TryExtractLargerFromText(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = Regex.Match(text, @"larger[^A-Za-z]*([AB])", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();

            if (text.IndexOf("A", StringComparison.OrdinalIgnoreCase) >= 0 &&
                text.IndexOf("B", StringComparison.OrdinalIgnoreCase) < 0) return "A";
            if (text.IndexOf("B", StringComparison.OrdinalIgnoreCase) >= 0 &&
                text.IndexOf("A", StringComparison.OrdinalIgnoreCase) < 0) return "B";
            return null;
        }

        private static GameObject CreatePrimitiveForKind(string kind)
        {
            switch (kind.ToLowerInvariant())
            {
                case "cube":     return GameObject.CreatePrimitive(PrimitiveType.Cube);
                case "sphere":   return GameObject.CreatePrimitive(PrimitiveType.Sphere);
                case "human":    return GameObject.CreatePrimitive(PrimitiveType.Capsule);
                case "toy_car":  return GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                case "apple":    return GameObject.CreatePrimitive(PrimitiveType.Sphere);
                case "chair":    return GameObject.CreatePrimitive(PrimitiveType.Cube);
                default:         return GameObject.CreatePrimitive(PrimitiveType.Cube);
            }
        }

        private static void TryDestroyIfExists(string name)
        {
            var go = GameObject.Find(name);
            if (go == null) return;
#if UNITY_EDITOR
            UnityEngine.Object.DestroyImmediate(go);
#else
            UnityEngine.Object.Destroy(go);
#endif
        }

        [Serializable]
        private class SizeAnswer
        {
            public string larger;
            public float confidence;
        }
    }
}
