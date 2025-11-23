using System;
using System.Text.RegularExpressions;
using UnityEngine;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;
using VRPerception.Tasks;

namespace VRPerception.Infra
{
    /// <summary>
    /// 结果评测器：
    /// - 订阅 TrialLifecycle(Completed) 可选做汇总/二次评测
    /// - 提供静态评测工具方法，供 Task 复用或独立单元测试
    /// </summary>
    public class ResultEvaluator : MonoBehaviour
    {
        [Header("Event Bus")]
        [SerializeField] private EventBusManager eventBus;
        [SerializeField] private bool autoSubscribe = false;

        private void Awake()
        {
            if (eventBus == null) eventBus = EventBusManager.Instance;
        }

        private void OnEnable()
        {
            if (autoSubscribe)
            {
                eventBus?.TrialLifecycle?.Subscribe(OnTrialLifecycle);
            }
        }

        private void OnDisable()
        {
            if (autoSubscribe)
            {
                eventBus?.TrialLifecycle?.Unsubscribe(OnTrialLifecycle);
            }
        }

        private void OnTrialLifecycle(TrialLifecycleEventData data)
        {
            // 当任务本身已评测完成，此处可用于二次校验/补充指标/聚合
            if (data.state != TrialLifecycleState.Completed) return;

            try
            {
                var spec = data.trialConfig as TrialSpec;
                var eval = data.results as TrialEvaluation;

                if (spec == null || eval == null) return;

                // 示例：如果缺少 absError/relError 且为距离任务，补算一次
                if (spec.taskId == "distance_compression")
                {
                    if (Mathf.Approximately(eval.absError, 0f) && eval.predictedDistanceM > 0 && spec.trueDistanceM > 0)
                    {
                        eval.absError = Mathf.Abs(eval.predictedDistanceM - spec.trueDistanceM);
                        eval.relError = spec.trueDistanceM > 0.0001f ? eval.absError / spec.trueDistanceM : 0f;
                    }
                }
            }
            catch (Exception ex)
            {
                eventBus?.PublishError("ResultEvaluator", ErrorSeverity.Warning, "EVAL_POST_ERROR", ex.Message);
            }
        }

        // ============ 静态评测工具 ============

        /// <summary>
        /// 距离压缩任务评测：根据 LLMResponse 抽取 distance_m 并与真值比对。
        /// </summary>
        public static TrialEvaluation EvaluateDistanceCompression(TrialSpec trial, LLMResponse response)
        {
            var eval = new TrialEvaluation
            {
                responseType = response?.type,
                providerId = response?.providerId,
                latencyMs = response?.latencyMs ?? 0,
                confidence = response?.confidence ?? 0
            };

            float predicted = float.NaN;

            if (response != null && response.type == "inference")
            {
                if (TryExtractDistanceFromAnswer(response.answer, out var d1))
                {
                    predicted = d1;
                }
                else if (TryExtractDistanceFromExplanation(response.explanation, out var d2))
                {
                    predicted = d2;
                }
            }

            if (!float.IsNaN(predicted))
            {
                eval.predictedDistanceM = predicted;
                eval.absError = Mathf.Abs(predicted - trial.trueDistanceM);
                eval.relError = trial.trueDistanceM > 0.0001f ? eval.absError / trial.trueDistanceM : 0f;
                eval.success = true;
            }
            else
            {
                eval.success = false;
                eval.failureReason = "No distance_m found";
            }

            return eval;
        }

        /// <summary>
        /// 语义大小偏差任务评测：比较预测更大的对象与真值关系。
        /// </summary>
        public static TrialEvaluation EvaluateSemanticSizeBias(TrialSpec trial, LLMResponse response, string trueLarger /*"A"|"B"*/)
        {
            var eval = new TrialEvaluation
            {
                responseType = response?.type,
                providerId = response?.providerId,
                latencyMs = response?.latencyMs ?? 0,
                confidence = response?.confidence ?? 0
            };

            string predicted = null;

            if (response != null && response.type == "inference")
            {
                predicted = TryExtractLargerFromAnswer(response.answer) ?? TryExtractLargerFromText(response.explanation);
            }

            if (!string.IsNullOrEmpty(predicted))
            {
                eval.predictedLarger = predicted;
                eval.isCorrect = string.Equals(predicted, trueLarger, StringComparison.OrdinalIgnoreCase);
                eval.success = true;
            }
            else
            {
                eval.success = false;
                eval.failureReason = "No larger(A/B) found";
            }

            return eval;
        }

        // ============ 抽取/解析 ============

        private static bool TryExtractDistanceFromAnswer(object answer, out float distance)
        {
            distance = float.NaN;
            if (answer == null) return false;

            try
            {
                // 优先：JSON 反序列化结构 { distance_m, confidence, explanation }
                var json = JsonUtility.ToJson(answer);
                if (!string.IsNullOrEmpty(json))
                {
                    var parsed = JsonUtility.FromJson<DistanceAnswer>(json);
                    if (parsed != null && parsed.distance_m > 0)
                    {
                        distance = parsed.distance_m;
                        return true;
                    }
                }
            }
            catch { /* ignore */ }

            // 后备：ToString 粗提取
            try
            {
                var s = answer.ToString();
                if (TryExtractDistanceFromString(s, out var d))
                {
                    distance = d;
                    return true;
                }
            }
            catch { /* ignore */ }

            return false;
        }

        private static bool TryExtractDistanceFromExplanation(string explanation, out float distance)
        {
            distance = float.NaN;
            if (string.IsNullOrEmpty(explanation)) return false;
            return TryExtractDistanceFromString(explanation, out distance);
        }

        private static bool TryExtractDistanceFromString(string text, out float distance)
        {
            distance = float.NaN;
            if (string.IsNullOrEmpty(text)) return false;

            var m = Regex.Match(text, @"distance[_\s]*m[^\d\-]*([-+]?\d+(\.\d+)?)", RegexOptions.IgnoreCase);
            if (m.Success && float.TryParse(m.Groups[1].Value, out var d))
            {
                distance = d;
                return true;
            }

            var m2 = Regex.Match(text, @"([-+]?\d+(\.\d+)?)");
            if (m2.Success && float.TryParse(m2.Groups[1].Value, out var d2))
            {
                distance = d2;
                return true;
            }

            return false;
        }

        private static string TryExtractLargerFromAnswer(object answer)
        {
            if (answer == null) return null;
            try
            {
                var json = JsonUtility.ToJson(answer);
                if (!string.IsNullOrEmpty(json))
                {
                    var parsed = JsonUtility.FromJson<SizeBiasAnswer>(json);
                    if (parsed != null && !string.IsNullOrEmpty(parsed.larger))
                    {
                        return parsed.larger;
                    }
                }
            }
            catch { /* ignore */ }

            return TryExtractLargerFromText(answer.ToString());
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

        [Serializable]
        private class DistanceAnswer
        {
            public float distance_m;
            public float confidence;
            public string explanation;
        }

        [Serializable]
        private class SizeBiasAnswer
        {
            public string larger; // "A"|"B"
            public float confidence;
        }
    }
}