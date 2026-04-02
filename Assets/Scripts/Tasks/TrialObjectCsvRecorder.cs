using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using VRPerception.Infra;

namespace VRPerception.Tasks
{
    /// <summary>
    /// 将每个 trial 的对象摆放信息追加写入 CSV。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TrialObjectCsvRecorder : MonoBehaviour
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly string[] IncludedNamePrefixes =
        {
            "dc_","ssb_","rdo_","cd_","occ_","cc_","cca_","mp_","mr_","num_","vs_","cnt_","djnd_","vc_","hci_"
        };

        [Header("Output")]
        [SerializeField] private string rootFolderName = "VRP_Logs";
        [SerializeField] private string outputFileName = "trial_objects.csv";

        private string _outputPath;
        private StreamWriter _writer;

        private void Awake()
        {
            EnsureWriter();
        }

        private void OnEnable()
        {
            EnsureWriter();
        }

        private void OnDisable()
        {
            CloseWriter();
        }

        private void OnDestroy()
        {
            CloseWriter();
        }

        public void RecordTrialObjects(string runId, SubjectMode subjectMode, int taskSeed, int trialExecutionIndex, TrialSpec trial)
        {
            if (trial == null) return;

            EnsureWriter();
            if (_writer == null) return;

            var objects = CollectObjects(trial);
            if (objects.Count == 0)
            {
                Debug.LogWarning($"[TrialObjectCsvRecorder] No trial objects found for task={trial.taskId} trial={trial.trialId}.");
            }
            for (int i = 0; i < objects.Count; i++)
            {
                WriteRecord(runId, subjectMode, taskSeed, trialExecutionIndex, trial, objects[i]);
            }

            _writer.Flush();
        }

        private void EnsureWriter()
        {
            if (_writer != null) return;

            var sessionDir = LogSessionPaths.GetOrCreateSessionDirectory(rootFolderName);
            _outputPath = Path.Combine(sessionDir, string.IsNullOrWhiteSpace(outputFileName) ? "trial_objects.csv" : outputFileName.Trim());
            bool writeHeader = !File.Exists(_outputPath) || new FileInfo(_outputPath).Length == 0;

            _writer = new StreamWriter(_outputPath, append: true, new UTF8Encoding(false));
            if (writeHeader)
            {
                _writer.WriteLine(
                    "runId,subjectMode,taskId,trialId,trialExecutionIndex,taskSeed,objectId,objectName,kind,objectRole,active,layer," +
                    "posX,posY,posZ,rotX,rotY,rotZ,scaleX,scaleY,scaleZ," +
                    "rendererBoundsCenterX,rendererBoundsCenterY,rendererBoundsCenterZ,rendererBoundsSizeX,rendererBoundsSizeY,rendererBoundsSizeZ,hasRendererBounds," +
                    "colliderBoundsCenterX,colliderBoundsCenterY,colliderBoundsCenterZ,colliderBoundsSizeX,colliderBoundsSizeY,colliderBoundsSizeZ,hasColliderBounds");
                _writer.Flush();
            }
        }

        private void CloseWriter()
        {
            if (_writer == null) return;

            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch
            {
                // Ignore close failures to keep experiment loop unaffected.
            }
            finally
            {
                _writer = null;
            }
        }

        private List<ObjectRecord> CollectObjects(TrialSpec trial)
        {
            var transforms = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            var records = new List<ObjectRecord>();
            var seen = new HashSet<int>();

            for (int i = 0; i < transforms.Length; i++)
            {
                var tr = transforms[i];
                if (tr == null) continue;
                var go = tr.gameObject;
                if (go == null || !go.scene.IsValid()) continue;
                if (!ShouldInclude(go, trial)) continue;

                int instanceId = go.GetInstanceID();
                if (!seen.Add(instanceId)) continue;

                records.Add(BuildRecord(go, trial));
            }

            records.Sort((a, b) => string.CompareOrdinal(a.objectId, b.objectId));
            return records;
        }

        private static bool ShouldInclude(GameObject go, TrialSpec trial)
        {
            var marker = go.GetComponent<TrialObjectMarker>();
            if (marker != null)
            {
                return marker.TrialId == trial.trialId &&
                       string.Equals(marker.TaskId, trial.taskId, StringComparison.OrdinalIgnoreCase);
            }

            var name = go.name ?? string.Empty;
            for (int i = 0; i < IncludedNamePrefixes.Length; i++)
            {
                if (name.StartsWith(IncludedNamePrefixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static ObjectRecord BuildRecord(GameObject go, TrialSpec trial)
        {
            var marker = go.GetComponent<TrialObjectMarker>();
            TryGetCombinedRendererBounds(go, out var rendererCenter, out var rendererSize, out var hasRendererBounds);
            TryGetCombinedColliderBounds(go, out var colliderCenter, out var colliderSize, out var hasColliderBounds);

            return new ObjectRecord
            {
                objectId = marker != null ? marker.ObjectId : $"{trial.taskId}_{trial.trialId}_{go.name}_{go.GetInstanceID()}",
                objectName = go.name,
                kind = marker != null ? marker.Kind : TrialObjectMarker.InferKind(go),
                objectRole = marker != null ? marker.ObjectRole : TrialObjectMarker.InferRole(go.name),
                active = go.activeInHierarchy,
                layer = go.layer,
                position = go.transform.position,
                rotation = go.transform.rotation.eulerAngles,
                scale = go.transform.lossyScale,
                rendererBoundsCenter = rendererCenter,
                rendererBoundsSize = rendererSize,
                hasRendererBounds = hasRendererBounds,
                colliderBoundsCenter = colliderCenter,
                colliderBoundsSize = colliderSize,
                hasColliderBounds = hasColliderBounds
            };
        }

        private void WriteRecord(string runId, SubjectMode subjectMode, int taskSeed, int trialExecutionIndex, TrialSpec trial, ObjectRecord record)
        {
            _writer.WriteLine(string.Join(",",
                Escape(runId),
                Escape(subjectMode.ToString()),
                Escape(trial.taskId),
                trial.trialId.ToString(Invariant),
                trialExecutionIndex.ToString(Invariant),
                taskSeed.ToString(Invariant),
                Escape(record.objectId),
                Escape(record.objectName),
                Escape(record.kind),
                Escape(record.objectRole),
                FormatBool(record.active),
                record.layer.ToString(Invariant),
                FormatFloat(record.position.x),
                FormatFloat(record.position.y),
                FormatFloat(record.position.z),
                FormatFloat(record.rotation.x),
                FormatFloat(record.rotation.y),
                FormatFloat(record.rotation.z),
                FormatFloat(record.scale.x),
                FormatFloat(record.scale.y),
                FormatFloat(record.scale.z),
                FormatVector(record.rendererBoundsCenter, 0),
                FormatVector(record.rendererBoundsCenter, 1),
                FormatVector(record.rendererBoundsCenter, 2),
                FormatVector(record.rendererBoundsSize, 0),
                FormatVector(record.rendererBoundsSize, 1),
                FormatVector(record.rendererBoundsSize, 2),
                FormatBool(record.hasRendererBounds),
                FormatVector(record.colliderBoundsCenter, 0),
                FormatVector(record.colliderBoundsCenter, 1),
                FormatVector(record.colliderBoundsCenter, 2),
                FormatVector(record.colliderBoundsSize, 0),
                FormatVector(record.colliderBoundsSize, 1),
                FormatVector(record.colliderBoundsSize, 2),
                FormatBool(record.hasColliderBounds)));
        }

        private static bool TryGetCombinedRendererBounds(GameObject root, out Vector3 center, out Vector3 size, out bool hasBounds)
        {
            center = Vector3.zero;
            size = Vector3.zero;
            hasBounds = false;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            Bounds combined = default;
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;
                if (!hasBounds)
                {
                    combined = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    combined.Encapsulate(renderer.bounds);
                }
            }

            if (!hasBounds) return false;
            center = combined.center;
            size = combined.size;
            return true;
        }

        private static bool TryGetCombinedColliderBounds(GameObject root, out Vector3 center, out Vector3 size, out bool hasBounds)
        {
            center = Vector3.zero;
            size = Vector3.zero;
            hasBounds = false;

            var colliders = root.GetComponentsInChildren<Collider>(true);
            Bounds combined = default;
            for (int i = 0; i < colliders.Length; i++)
            {
                var collider = colliders[i];
                if (collider == null) continue;
                if (!hasBounds)
                {
                    combined = collider.bounds;
                    hasBounds = true;
                }
                else
                {
                    combined.Encapsulate(collider.bounds);
                }
            }

            if (!hasBounds) return false;
            center = combined.center;
            size = combined.size;
            return true;
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n") || value.Contains("\r"))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }

            return value;
        }

        private static string FormatFloat(float value)
        {
            return value.ToString("F6", Invariant);
        }

        private static string FormatVector(Vector3 value, int index)
        {
            return index switch
            {
                0 => FormatFloat(value.x),
                1 => FormatFloat(value.y),
                _ => FormatFloat(value.z)
            };
        }

        private static string FormatBool(bool value)
        {
            return value ? "true" : "false";
        }

        private sealed class ObjectRecord
        {
            public string objectId;
            public string objectName;
            public string kind;
            public string objectRole;
            public bool active;
            public int layer;
            public Vector3 position;
            public Vector3 rotation;
            public Vector3 scale;
            public Vector3 rendererBoundsCenter;
            public Vector3 rendererBoundsSize;
            public bool hasRendererBounds;
            public Vector3 colliderBoundsCenter;
            public Vector3 colliderBoundsSize;
            public bool hasColliderBounds;
        }
    }
}
