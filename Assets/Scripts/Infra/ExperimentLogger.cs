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

        private void Awake()
        {
            if (eventBus == null) eventBus = EventBusManager.Instance;

            var root = Path.Combine(Application.persistentDataPath, rootFolderName);
            Directory.CreateDirectory(root);

            var session = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            _sessionDir = Path.Combine(root, session);
            _imagesDir = Path.Combine(_sessionDir, "images");
            Directory.CreateDirectory(_sessionDir);
            Directory.CreateDirectory(_imagesDir);

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
            if (enableCsvSummary) WriteCsvSummary();
        }

        private void OnApplicationQuit()
        {
            FlushJsonl();
            if (enableCsvSummary) WriteCsvSummary();
        }

        private void Subscribe()
        {
            if (eventBus == null) return;

            eventBus.InferenceReceived?.Subscribe(OnInferenceReceived);
            eventBus.ActionPlanReceived?.Subscribe(OnActionPlanReceived);
            eventBus.PerformanceMetric?.Subscribe(OnPerformanceMetric);
            eventBus.Error?.Subscribe(OnError);
            eventBus.TrialLifecycle?.Subscribe(OnTrialLifecycle);
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
                }
            }
        }

        private void OnFrameCaptured(FrameCapturedEventData data)
        {
            if (!saveScreenshots || data == null || !data.success) return;

            try
            {
                if (string.IsNullOrEmpty(data.imageBase64)) return;
                var bytes = Convert.FromBase64String(data.imageBase64);

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

        private void WriteCsvSummary()
        {
            try
            {
                // 仅输出 distance_compression 的结果（可扩展多任务）
                var path = Path.Combine(_sessionDir, "distance_compression_summary.csv");
                using (var sw = new StreamWriter(path, false, Encoding.UTF8))
                {
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

                if (enableJsonl)
                {
                    BufferJson(new CsvSavedLine
                    {
                        type = "csv_saved",
                        timestamp = DateTime.UtcNow.ToString("o"),
                        path = Relativize(path)
                    });
                    FlushJsonl();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ExperimentLogger] WriteCsvSummary failed: {ex.Message}");
            }
        }

        // ============ Utils ============

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
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
    }
}