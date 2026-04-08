using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;
using VRPerception.Tasks;

namespace VRPerception.Infra
{
    /// <summary>
    /// 实验日志器：
    /// - JSONL 行式日志：Trial 生命周期、模型响应、性能指标、错误
    /// - CSV 汇总：针对任务（先支持 Distance Compression）输出误差统计
    /// - 截图保存：按 Trial/时间戳保存 Stimulus 抓帧（可选）
    /// </summary>
    public class ExperimentLogger : MonoBehaviour
    {
        [Header("Output")]
        [SerializeField] private string rootFolderName = "VRP_Logs";
        [SerializeField] private bool enableJsonl = true;
        [SerializeField] private bool enableCsvSummary = true;
        [SerializeField] private bool saveScreenshots = true;

        [Header("JSONL")]
        [SerializeField] private string jsonlFileName = "events.jsonl";
        [SerializeField] private int flushEveryNLines = 20;

        [Header("Event Bus")]
        [SerializeField] private EventBusManager eventBus;

        private string _sessionDir;
        private string _imagesDir;
        private string _jsonlPath;

        private readonly List<string> _jsonlBuffer = new List<string>(128);

        // 汇总缓存（用于 CSV）
        private readonly List<TrialCompletedRecord> _completed = new List<TrialCompletedRecord>();
        private bool _csvWritten = false;

        private void Awake()
        {
            if (eventBus == null) eventBus = EventBusManager.Instance;

            var session = LogSessionPaths.GetOrCreateSessionId(rootFolderName);
            _sessionDir = LogSessionPaths.GetOrCreateSessionDirectory(rootFolderName);
            _imagesDir = Path.Combine(_sessionDir, "images");
            Directory.CreateDirectory(_sessionDir);

            _jsonlPath = Path.Combine(_sessionDir, jsonlFileName);

            SafeAppendLine(_jsonlPath, "# VR Perception Logger Session " + session);
        }

        private void OnEnable()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
            FlushJsonl();
            if (enableCsvSummary) WriteCsvSummaryOnce();
        }

        private void OnApplicationQuit()
        {
            FlushJsonl();
            if (enableCsvSummary) WriteCsvSummaryOnce();
        }

        private void Subscribe()
        {
            if (eventBus == null) return;

            eventBus.InferenceReceived?.Subscribe(OnInferenceReceived);
            eventBus.ActionPlanReceived?.Subscribe(OnActionPlanReceived);
            eventBus.PerformanceMetric?.Subscribe(OnPerformanceMetric);
            eventBus.Error?.Subscribe(OnError);
            eventBus.TrialLifecycle?.Subscribe(OnTrialLifecycle);
            eventBus.LogFlush?.Subscribe(OnLogFlush);
            if (saveScreenshots)
                eventBus.FrameCaptured?.Subscribe(OnFrameCaptured);
        }

        private void Unsubscribe()
        {
            if (eventBus == null) return;

            eventBus.InferenceReceived?.Unsubscribe(OnInferenceReceived);
            eventBus.ActionPlanReceived?.Unsubscribe(OnActionPlanReceived);
            eventBus.PerformanceMetric?.Unsubscribe(OnPerformanceMetric);
            eventBus.Error?.Unsubscribe(OnError);
            eventBus.TrialLifecycle?.Unsubscribe(OnTrialLifecycle);
            eventBus.LogFlush?.Unsubscribe(OnLogFlush);
            if (saveScreenshots)
                eventBus.FrameCaptured?.Unsubscribe(OnFrameCaptured);
        }

        // ============ Handlers ============

        private void OnInferenceReceived(InferenceReceivedEventData data)
        {
            if (!enableJsonl) return;

            var entry = new InferenceLine
            {
                type = "inference",
                taskId = data.taskId,
                trialId = data.trialId,
                timestamp = DateTime.UtcNow.ToString("o"),
                providerId = data.providerId,
                latencyMs = data.response?.latencyMs ?? 0,
                confidence = data.response?.confidence ?? 0f,
                answerJson = TryToJson(data.response?.answer),
                explanation = data.response?.explanation
            };

            BufferJson(entry);
        }

        private void OnActionPlanReceived(ActionPlanReceivedEventData data)
        {
            if (!enableJsonl) return;

            var entry = new ActionPlanLine
            {
                type = "action_plan",
                taskId = data.taskId,
                trialId = data.trialId,
                timestamp = DateTime.UtcNow.ToString("o"),
                providerId = data.providerId,
                actionsCount = data.actions?.Length ?? 0
            };

            BufferJson(entry);
        }

        private void OnPerformanceMetric(PerformanceMetricEventData data)
        {
            if (!enableJsonl) return;

            var entry = new MetricLine
            {
                type = "metric",
                timestamp = DateTime.UtcNow.ToString("o"),
                name = data.metricName,
                category = data.category,
                value = data.value,
                unit = data.unit,
                tags = TryToJson(data.tags)
            };

            BufferJson(entry);
        }

        private void OnError(ErrorEventData data)
        {
            if (!enableJsonl) return;

            var entry = new ErrorLine
            {
                type = "error",
                timestamp = data.timestamp.ToString("o"),
                source = data.source,
                severity = data.severity.ToString(),
                code = data.errorCode,
                message = data.message,
                context = TryToJson(data.context)
            };

            BufferJson(entry);
        }

        private void OnTrialLifecycle(TrialLifecycleEventData data)
        {
            if (enableJsonl)
            {
                var entry = new TrialLine
                {
                    type = "trial",
                    timestamp = DateTime.UtcNow.ToString("o"),
                    taskId = data.taskId,
                    trialId = data.trialId,
                    state = data.state.ToString(),
                    config = TryToJson(data.trialConfig),
                    results = TryToJson(data.results),
                    errorMessage = data.errorMessage
                };
                BufferJson(entry);
            }

            // 收集 Completed 以便 CSV 汇总
            if (enableCsvSummary && data.state == TrialLifecycleState.Completed)
            {
                var spec = data.trialConfig as TrialSpec;
                var eval = data.results as TrialEvaluation;

                if (spec != null && eval != null)
                {
                    _completed.Add(new TrialCompletedRecord
                    {
                        taskId = data.taskId,
                        trialId = data.trialId,
                        trial = spec,
                        evaluation = eval
                    });

                    if (string.Equals(data.taskId, "depth_jnd_staircase", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteCsvSummary();
                    }
                }
            }
        }

        private void OnLogFlush(LogFlushEventData data)
        {
            FlushJsonl();
            if (enableCsvSummary)
            {
                WriteCsvSummary();
            }
        }

        private void OnFrameCaptured(FrameCapturedEventData data)
        {
            if (!saveScreenshots || data == null || !data.success) return;
            if (IsVideoCapture(data)) return;

            try
            {
                if (string.IsNullOrEmpty(data.imageBase64)) return;
                var bytes = Convert.FromBase64String(data.imageBase64);
                Directory.CreateDirectory(_imagesDir);

                var safeTask = string.IsNullOrEmpty(data.taskId) ? "unknown" : data.taskId;
                var file = $"{safeTask}_{data.trialId}_{data.timestamp:yyyyMMdd_HHmmssfff}.jpg";
                var path = Path.Combine(_imagesDir, file);

                File.WriteAllBytes(path, bytes);

                if (enableJsonl)
                {
                    BufferJson(new ImageSavedLine
                    {
                        type = "image_saved",
                        timestamp = DateTime.UtcNow.ToString("o"),
                        taskId = data.taskId,
                        trialId = data.trialId,
                        path = Relativize(path)
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ExperimentLogger] Save screenshot failed: {ex.Message}");
            }
        }

        private static bool IsVideoCapture(FrameCapturedEventData data)
        {
            return string.Equals(
                data?.metadata?.meta?.captureMode,
                CaptureMode.Video.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }

        // ============ JSONL Buffering ============

        private void BufferJson(object line)
        {
            try
            {
                var json = JsonUtility.ToJson(line);
                lock (_jsonlBuffer)
                {
                    _jsonlBuffer.Add(json);
                    if (_jsonlBuffer.Count >= flushEveryNLines)
                    {
                        FlushJsonl();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ExperimentLogger] JSONL serialize failed: {ex.Message}");
            }
        }

        private void FlushJsonl()
        {
            if (!enableJsonl) return;

            string[] lines;
            lock (_jsonlBuffer)
            {
                if (_jsonlBuffer.Count == 0) return;
                lines = _jsonlBuffer.ToArray();
                _jsonlBuffer.Clear();
            }

            try
            {
                using (var sw = new StreamWriter(_jsonlPath, append: true, Encoding.UTF8))
                {
                    foreach (var l in lines) sw.WriteLine(l);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ExperimentLogger] Flush failed: {ex.Message}");
            }
        }

        // ============ CSV Summary ============

        private void WriteCsvSummaryOnce()
        {
            if (_csvWritten) return;
            _csvWritten = true;
            WriteCsvSummary();
        }

        private void WriteCsvSummary()
        {
            try
            {
                // 仅对本次会话中“实际跑过的任务”输出对应 CSV（避免固定写出多个文件）
                var savedCsvPaths = new List<string>();

                // distance_compression
                if (_completed.Exists(r => r.taskId == "distance_compression"))
                {
                    var path = Path.Combine(_sessionDir, "distance_compression_summary.csv");
                    using (var sw = new StreamWriter(path, false, Encoding.UTF8))
                    {
                        // distance_compression_summary.csv 字段说明：
                        // - taskId: 任务ID
                        // - trialId: 试次ID
                        // - environment: 场景类型（open_field/corridor）
                        // - fovDeg: 相机视场角（度）
                        // - targetKind: 目标类型（sphere/cube/human...）
                        // - trueDistanceM: 真值距离（米）
                        // - predictedDistanceM: 模型预测距离（米）
                        // - absError: 绝对误差 |pred-true|（米）
                        // - relError: 相对误差 absError/trueDistanceM
                        // - confidence: 模型置信度（0..1，来自模型输出）
                        // - providerId: 使用的模型/服务提供方ID
                        // - latencyMs: 推理耗时（毫秒）
                        sw.WriteLine("taskId,trialId,environment,fovDeg,targetKind,trueDistanceM,predictedDistanceM,absError,relError,confidence,providerId,latencyMs");

                        foreach (var r in _completed)
                        {
                            if (r.taskId != "distance_compression") continue;
                            var t = r.trial;
                            var e = r.evaluation;

                            sw.WriteLine(string.Join(",",
                                Escape(r.taskId),
                                r.trialId,
                                Escape(t.environment),
                                t.fovDeg.ToString("F0"),
                                Escape(t.targetKind),
                                t.trueDistanceM.ToString("F3"),
                                e.predictedDistanceM.ToString("F3"),
                                e.absError.ToString("F3"),
                                e.relError.ToString("F3"),
                                e.confidence.ToString("F3"),
                                Escape(e.providerId),
                                e.latencyMs
                            ));
                        }
                    }
                    savedCsvPaths.Add(path);
                }

                // visual_crowding
                if (_completed.Exists(r => r.taskId == "visual_crowding"))
                {
                    var vcPath = Path.Combine(_sessionDir, "visual_crowding_summary.csv");
                    using (var sw = new StreamWriter(vcPath, false, Encoding.UTF8))
                    {
                        // visual_crowding_summary.csv 字段说明：
                        // - taskId: 任务ID
                        // - trialId: 试次ID
                        // - eccentricityDeg: 目标离心率（度）
                        // - spacingDeg: 目标与最近干扰项间距（度）
                        // - targetLetter: 真值目标字母
                        // - flankerLetters: 干扰字母序列（空格分隔）
                        // - predictedLetter: 模型预测字母
                        // - isCorrect: 是否正确（1/0）
                        // - confidence/providerId/latencyMs: 同上
                        sw.WriteLine("taskId,trialId,eccentricityDeg,spacingDeg,targetLetter,flankerLetters,predictedLetter,isCorrect,confidence,providerId,latencyMs");

                        foreach (var r in _completed)
                        {
                            if (r.taskId != "visual_crowding") continue;
                            var t = r.trial;
                            var e = r.evaluation;

                            var flankers = t.flankerLetters != null && t.flankerLetters.Length > 0
                                ? string.Join(" ", t.flankerLetters)
                                : "";

                            sw.WriteLine(string.Join(",",
                                Escape(r.taskId),
                                r.trialId,
                                t.eccentricityDeg.ToString("F3"),
                                t.spacingDeg.ToString("F3"),
                                Escape(t.targetLetter),
                                Escape(flankers),
                                Escape(e.predictedLetter),
                                e.isLetterCorrect ? "1" : "0",
                                e.confidence.ToString("F3"),
                                Escape(e.providerId),
                                e.latencyMs
                            ));
                        }
                    }
                    savedCsvPaths.Add(vcPath);
                }

                // depth_jnd_staircase
                if (_completed.Exists(r => r.taskId == "depth_jnd_staircase"))
                {
                    var path = Path.Combine(_sessionDir, "depth_jnd_staircase_summary.csv");
                    using (var sw = new StreamWriter(path, false, Encoding.UTF8))
                    {
                        // depth_jnd_staircase_summary.csv 字段说明：
                        // - taskId/trialId/environment/fovDeg: 同上
                        // - depthA/depthB: 本试次 A(左)/B(右) 的真实深度（米）
                        // - deltaM: 深度差 |depthA-depthB|（米）
                        // - baseDistanceM: 基准深度 max(depthA, depthB)（米）
                        // - trueCloser: 真值更近者（A/B）
                        // - predictedCloser: 模型预测更近者（A/B）
                        // - isCorrect: 是否判断正确（1/0）
                        // - confidence/providerId/latencyMs: 同上
                        // - groupIndex: 阶梯组索引（从 0 开始）
                        // - trialIndexInGroup: 组内试次计数（内部计数）
                        // - staircaseDeltaNextM: 更新后的下一试次阶梯Δ（米）
                        // - reversalHappened: 是否发生反转（1/0）
                        // - reversalCount: 已发生反转次数
                        // - thresholdEstimateM: 当前阈值估计（米）
                        // - groupEnded: 是否达到组结束条件（1/0）
                        sw.WriteLine("taskId,trialId,environment,fovDeg,depthA,depthB,deltaM,baseDistanceM,trueCloser,predictedCloser,isCorrect,confidence,providerId,latencyMs,groupIndex,trialIndexInGroup,staircaseDeltaNextM,reversalHappened,reversalCount,thresholdEstimateM,groupEnded");

                        foreach (var r in _completed)
                        {
                            if (r.taskId != "depth_jnd_staircase") continue;
                            var t = r.trial;
                            var e = r.evaluation;

                            float depthA = t.depthA;
                            float depthB = t.depthB;
                            float deltaM = Mathf.Abs(depthA - depthB);
                            float baseDistanceM = Mathf.Max(depthA, depthB);

                            var extra = TryParseDepthJndExtra(e.extraJson);

                            sw.WriteLine(string.Join(",",
                                Escape(r.taskId),
                                r.trialId,
                                Escape(t.environment),
                                t.fovDeg.ToString("F0"),
                                depthA.ToString("F3"),
                                depthB.ToString("F3"),
                                deltaM.ToString("F3"),
                                baseDistanceM.ToString("F3"),
                                Escape(e.trueCloser),
                                Escape(e.predictedCloser),
                                e.isCorrect ? "1" : "0",
                                e.confidence.ToString("F3"),
                                Escape(e.providerId),
                                e.latencyMs,
                                extra != null ? extra.groupIndex.ToString() : "",
                                extra != null ? extra.trialIndexInGroup.ToString() : "",
                                extra != null ? extra.staircaseDeltaNextM.ToString("F3") : "",
                                extra != null ? (extra.reversalHappened ? "1" : "0") : "",
                                extra != null ? extra.reversalCount.ToString() : "",
                                extra != null ? extra.thresholdEstimateM.ToString("F3") : "",
                                extra != null ? (extra.groupEnded ? "1" : "0") : ""
                            ));
                        }
                    }
                    savedCsvPaths.Add(path);
                }

                // horizon_cue_integration
                if (_completed.Exists(r => r.taskId == "horizon_cue_integration"))
                {
                    var path = Path.Combine(_sessionDir, "horizon_cue_integration_summary.csv");
                    using (var sw = new StreamWriter(path, false, Encoding.UTF8))
                    {
                        // horizon_cue_integration_summary.csv 字段说明：
                        // - taskId/trialId: 同上
                        // - fovDeg: 相机视场角（度）
                        // - trueDistanceM: 真值距离（米）
                        // - horizonAngleDeg: 地平线俯仰偏移角（度）
                        // - repetitionIndex: 重复序号（1..N）
                        // - sphereScreenY01: 红球中心在屏幕上的 Y（0..1），用于校验“屏幕静止”
                        // - predictedDistanceM/absError/relError: 同 distance_compression
                        // - confidence/providerId/latencyMs: 同上
                        sw.WriteLine("taskId,trialId,fovDeg,trueDistanceM,horizonAngleDeg,repetitionIndex,sphereScreenY01,predictedDistanceM,absError,relError,confidence,providerId,latencyMs");

                        foreach (var r in _completed)
                        {
                            if (r.taskId != "horizon_cue_integration") continue;
                            var t = r.trial;
                            var e = r.evaluation;

                            sw.WriteLine(string.Join(",",
                                Escape(r.taskId),
                                r.trialId,
                                t.fovDeg.ToString("F0"),
                                t.trueDistanceM.ToString("F3"),
                                t.horizonAngleDeg.ToString("F3"),
                                t.repetitionIndex,
                                t.sphereScreenY01.ToString("F3"),
                                e.predictedDistanceM.ToString("F3"),
                                e.absError.ToString("F3"),
                                e.relError.ToString("F3"),
                                e.confidence.ToString("F3"),
                                Escape(e.providerId),
                                e.latencyMs
                            ));
                        }
                    }
                    savedCsvPaths.Add(path);
                }

                // numerosity_comparison
                if (_completed.Exists(r => r.taskId == "numerosity_comparison"))
                {
                    var path = Path.Combine(_sessionDir, "numerosity_comparison_summary.csv");
                    using (var sw = new StreamWriter(path, false, Encoding.UTF8))
                    {
                        // numerosity_comparison_summary.csv 字段说明：
                        // - taskId/trialId: 同上
                        // - baseCountN/ratioR: 实验条件（Weber law 参数）
                        // - leftCount/rightCount: 本试次左右数量（真值）
                        // - trueMoreSide/predictedMoreSide: 真值与预测（left/right）
                        // - isCorrect: 是否正确（1/0）
                        // - exposureDurationMs/dotRadius: 刺激曝光时长/点大小（用于复现实验条件）
                        // - confidence/providerId/latencyMs: 同上
                        sw.WriteLine("taskId,trialId,baseCountN,ratioR,leftCount,rightCount,trueMoreSide,predictedMoreSide,isCorrect,exposureDurationMs,dotRadius,confidence,providerId,latencyMs");

                        foreach (var r in _completed)
                        {
                            if (r.taskId != "numerosity_comparison") continue;
                            var t = r.trial;
                            var e = r.evaluation;

                            // 兼容：部分 trial 可能复用 trueCount/targetCount 存左右数量
                            int left = t.leftCount > 0 ? t.leftCount : t.trueCount;
                            int right = t.rightCount > 0 ? t.rightCount : t.targetCount;

                            sw.WriteLine(string.Join(",",
                                Escape(r.taskId),
                                r.trialId,
                                t.baseCountN.ToString("F0"),
                                t.ratioR.ToString("F3"),
                                left,
                                right,
                                 Escape(string.IsNullOrEmpty(e.trueMoreSide) ? t.trueMoreSide : e.trueMoreSide),
                                 Escape(e.predictedMoreSide),
                                 e.isMoreSideCorrect ? "1" : (e.isCorrect ? "1" : "0"),
                                 t.exposureDurationMs.ToString("F0"),
                                 t.dotRadius.ToString("F3"),
                                 e.confidence.ToString("F3"),
                                 Escape(e.providerId),
                                 e.latencyMs
                             ));
                        }
                    }
                    savedCsvPaths.Add(path);
                }

                // change_detection
                if (_completed.Exists(r => r.taskId == "change_detection"))
                {
                    var path = Path.Combine(_sessionDir, "change_detection_summary.csv");
                    using (var sw = new StreamWriter(path, false, Encoding.UTF8))
                    {
                        // change_detection_summary.csv 字段说明：
                        // - taskId/trialId: 同上
                        // - background/fovDeg: 实验条件
                        // - changeLayer: 变化发生的空间层级（front/middle/back，none 时为空）
                        // - trueChanged/trueChangeCategory: 真值变化与纯类别（不含 layer 后缀）
                        // - predictedChanged/predictedChangeCategory: 模型预测
                        // - isCorrect: 正确性（先比较 changed；若 changed=true 再比较类别）
                        // - confidence/providerId/latencyMs: 同上
                        sw.WriteLine("taskId,trialId,background,fovDeg,changeLayer,trueChanged,trueChangeCategory,predictedChanged,predictedChangeCategory,isCorrect,confidence,providerId,latencyMs");

                        foreach (var r in _completed)
                        {
                            if (r.taskId != "change_detection") continue;
                            var t = r.trial;
                            var e = r.evaluation;

                            var trueChanged = e.trueChanged;

                            // 从 trial.changeCategory 解析层级和纯类别
                            var changeLayer = "";
                            var trueCat = string.IsNullOrEmpty(e.trueChangeCategory) ? (t.changeCategory ?? "none") : e.trueChangeCategory;
                            var fullCat = (t.changeCategory ?? "").Trim().ToLowerInvariant();
                            var lastUnderscore = fullCat.LastIndexOf('_');
                            if (lastUnderscore > 0)
                            {
                                var suffix = fullCat.Substring(lastUnderscore + 1);
                                if (suffix == "front" || suffix == "middle" || suffix == "back")
                                {
                                    changeLayer = suffix;
                                    trueCat = fullCat.Substring(0, lastUnderscore);
                                }
                            }

                            sw.WriteLine(string.Join(",",
                                Escape(r.taskId),
                                r.trialId,
                                Escape(t.background),
                                t.fovDeg.ToString("F0"),
                                Escape(changeLayer),
                                trueChanged ? "1" : "0",
                                Escape(trueCat),
                                e.predictedChanged ? "1" : "0",
                                Escape(string.IsNullOrEmpty(e.predictedChangeCategory) ? "none" : e.predictedChangeCategory),
                                e.isCorrect ? "1" : "0",
                                e.confidence.ToString("F3"),
                                Escape(e.providerId),
                                e.latencyMs
                            ));
                        }
                    }
                    savedCsvPaths.Add(path);
                }

                // material_roughness / material_roughness_motion / material_roughness_static
                if (_completed.Exists(r => r.taskId != null && r.taskId.StartsWith("material_roughness", StringComparison.OrdinalIgnoreCase)))
                {
                    var path = Path.Combine(_sessionDir, "material_roughness_summary.csv");
                    using (var sw = new StreamWriter(path, false, Encoding.UTF8))
                    {
                        // material_roughness_summary.csv 字段说明：
                        // - taskId/trialId: 任务ID/试次ID（含 _motion / _static 后缀）
                        // - environment: 场景类型
                        // - fovDeg: 相机视场角（度）
                        // - trueRoughness: 真值粗糙度（0..1）
                        // - requireHeadMotion: 是否要求头动门控（1/0）
                        // - predictedRoughness: 模型预测粗糙度（0..1）
                        // - roughnessAbsError: 绝对误差 |pred-true|
                        // - roughnessSignedError: 有符号误差 pred-true
                        // - confidence/providerId/latencyMs: 同上
                        sw.WriteLine("taskId,trialId,environment,fovDeg,trueRoughness,requireHeadMotion,predictedRoughness,roughnessAbsError,roughnessSignedError,confidence,providerId,latencyMs");

                        foreach (var r in _completed)
                        {
                            if (r.taskId == null || !r.taskId.StartsWith("material_roughness", StringComparison.OrdinalIgnoreCase)) continue;
                            var t = r.trial;
                            var e = r.evaluation;

                            sw.WriteLine(string.Join(",",
                                Escape(r.taskId),
                                r.trialId,
                                Escape(t.environment),
                                t.fovDeg.ToString("F0"),
                                t.roughness.ToString("F3"),
                                t.requireHeadMotion ? "1" : "0",
                                e.predictedRoughness.ToString("F3"),
                                e.roughnessAbsError.ToString("F3"),
                                e.roughnessSignedError.ToString("F3"),
                                e.confidence.ToString("F3"),
                                Escape(e.providerId),
                                e.latencyMs
                            ));
                        }
                    }
                    savedCsvPaths.Add(path);
                }

                // color_constancy_adjustment
                if (_completed.Exists(r => r.taskId == "color_constancy_adjustment"))
                {
                    var path = Path.Combine(_sessionDir, "color_constancy_adjustment_summary.csv");
                    using (var sw = new StreamWriter(path, false, Encoding.UTF8))
                    {
                        // color_constancy_adjustment_summary.csv 字段说明：
                        // - taskId/trialId: 任务ID/试次ID
                        // - phase: 实验阶段（main/校准等）
                        // - background/fovDeg/lighting: 实验条件
                        // - trueR/trueG/trueB: 真值RGB（0-255）
                        // - predictedChoice: 模型选择的标签
                        // - confidence/providerId/latencyMs: 同上
                        sw.WriteLine("taskId,trialId,phase,background,fovDeg,lighting,trueR,trueG,trueB,predictedChoice,confidence,providerId,latencyMs");

                        foreach (var r in _completed)
                        {
                            if (r.taskId != "color_constancy_adjustment") continue;
                            var t = r.trial;
                            var e = r.evaluation;

                            // 从 extraJson 解析数据
                            string phase = "unknown";
                            string predictedChoice = "";

                            try
                            {
                                var extra = JsonUtility.FromJson<ColorConstancyAdjustmentExtra>(e.extraJson);
                                if (extra != null)
                                {
                                    phase = extra.phase ?? "unknown";
                                    predictedChoice = extra.choice ?? "";
                                }
                            }
                            catch { }

                            sw.WriteLine(string.Join(",",
                                Escape(r.taskId),
                                r.trialId,
                                Escape(phase),
                                Escape(t.background),
                                t.fovDeg.ToString("F0"),
                                Escape(t.lighting),
                                t.trueR,
                                t.trueG,
                                t.trueB,
                                Escape(predictedChoice),
                                e.confidence.ToString("F3"),
                                Escape(e.providerId),
                                e.latencyMs
                            ));
                        }
                    }
                    savedCsvPaths.Add(path);
                }

                if (enableJsonl && savedCsvPaths.Count > 0)
                {
                    foreach (var p in savedCsvPaths)
                    {
                        BufferJson(new CsvSavedLine
                        {
                            type = "csv_saved",
                            timestamp = DateTime.UtcNow.ToString("o"),
                            path = Relativize(p)
                        });
                    }
                    FlushJsonl();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ExperimentLogger] WriteCsvSummary failed: {ex.Message}");
            }
        }

        // ============ Utils ============

        private static DepthJndExtraLine TryParseDepthJndExtra(string extraJson)
        {
            if (string.IsNullOrEmpty(extraJson)) return null;
            try
            {
                return JsonUtility.FromJson<DepthJndExtraLine>(extraJson);
            }
            catch
            {
                return null;
            }
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // 换行符替换为空格，防止破坏 CSV 行结构
            s = s.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
            if (s.Contains(",") || s.Contains("\""))
            {
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            }
            return s;
        }

        private string Relativize(string absPath)
        {
            if (string.IsNullOrEmpty(absPath)) return absPath;
            try
            {
                return absPath.Replace(_sessionDir, "").TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return absPath; }
        }

        private string TryToJson(object o)
        {
            if (o == null) return null;
            try
            {
                return JsonUtility.ToJson(o);
            }
            catch
            {
                try
                {
                    return $"\"{o.ToString()}\"";
                }
                catch
                {
                    return "\"<unserializable>\"";
                }
            }
        }

        private static void SafeAppendLine(string path, string line)
        {
            try
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* ignore */ }
        }

        // ============ Line Schemas ============

        [Serializable] private class InferenceLine
        {
            public string type;
            public string taskId;
            public int trialId;
            public string timestamp;
            public string providerId;
            public long latencyMs;
            public float confidence;
            public string answerJson;
            public string explanation;
        }

        [Serializable] private class DepthJndExtraLine
        {
            public int groupIndex;
            public int trialIndexInGroup;
            public float staircaseDeltaNextM;
            public bool reversalHappened;
            public int reversalCount;
            public float thresholdEstimateM;
            public bool groupEnded;
        }

        [Serializable] private class ActionPlanLine
        {
            public string type;
            public string taskId;
            public int trialId;
            public string timestamp;
            public string providerId;
            public int actionsCount;
        }

        [Serializable] private class MetricLine
        {
            public string type;
            public string timestamp;
            public string name;
            public string category;
            public double value;
            public string unit;
            public string tags;
        }

        [Serializable] private class ErrorLine
        {
            public string type;
            public string timestamp;
            public string source;
            public string severity;
            public string code;
            public string message;
            public string context;
        }

        [Serializable] private class TrialLine
        {
            public string type;
            public string timestamp;
            public string taskId;
            public int trialId;
            public string state;
            public string config;
            public string results;
            public string errorMessage;
        }

        [Serializable] private class ImageSavedLine
        {
            public string type;
            public string timestamp;
            public string taskId;
            public int trialId;
            public string path;
        }

        [Serializable] private class CsvSavedLine
        {
            public string type;
            public string timestamp;
            public string path;
        }

        private class TrialCompletedRecord
        {
            public string taskId;
            public int trialId;
            public TrialSpec trial;
            public TrialEvaluation evaluation;
        }

        [Serializable]
        private class ColorConstancyAdjustmentExtra
        {
            public string phase;
            public bool hasFurniture;
            public string lighting;
            public int[] initialRgb;
            public int[] baselineRgb;
            public int[] predictedRgb;
            public int[] deltaRgb;
            public string choice;
            public string[] candidateLabels;
            public RgbTriplet[] candidateRgbs;
        }

        [Serializable]
        private struct RgbTriplet
        {
            public int r;
            public int g;
            public int b;
        }
    }
}
