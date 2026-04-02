using System;
using UnityEngine;

namespace VRPerception.Tasks
{
    /// <summary>
    /// 标记当前 trial 相关对象，便于统一导出物体摆放清单。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TrialObjectMarker : MonoBehaviour
    {
        [SerializeField] private string taskId;
        [SerializeField] private int trialId = -1;
        [SerializeField] private string objectId;
        [SerializeField] private string kind;
        [SerializeField] private string objectRole;

        public string TaskId => taskId;
        public int TrialId => trialId;
        public string ObjectId => objectId;
        public string Kind => kind;
        public string ObjectRole => objectRole;

        public void Configure(string taskIdValue, int trialIdValue, string objectIdValue, string kindValue, string objectRoleValue)
        {
            taskId = string.IsNullOrWhiteSpace(taskIdValue) ? "unknown_task" : taskIdValue.Trim();
            trialId = trialIdValue;
            objectId = string.IsNullOrWhiteSpace(objectIdValue) ? Guid.NewGuid().ToString("N") : objectIdValue.Trim();
            kind = string.IsNullOrWhiteSpace(kindValue) ? InferKind(gameObject) : kindValue.Trim();
            objectRole = string.IsNullOrWhiteSpace(objectRoleValue) ? InferRole(gameObject != null ? gameObject.name : null) : objectRoleValue.Trim();
        }

        public static TrialObjectMarker AttachOrUpdate(GameObject target, string taskId, int trialId, string objectId, string kind, string objectRole)
        {
            if (target == null) return null;

            var marker = target.GetComponent<TrialObjectMarker>();
            if (marker == null) marker = target.AddComponent<TrialObjectMarker>();
            marker.Configure(taskId, trialId, objectId, kind, objectRole);
            return marker;
        }

        public static string InferRole(string objectName)
        {
            var name = string.IsNullOrWhiteSpace(objectName) ? string.Empty : objectName.Trim().ToLowerInvariant();
            if (name.Contains("target")) return "target";
            if (name.Contains("dist") || name.Contains("distractor")) return "distractor";
            if (name.Contains("occ") || name.Contains("occluder")) return "occluder";
            if (name.Contains("fixation")) return "fixation";
            if (name.Contains("letter") || name.Contains("flanker")) return "flanker";
            return "helper";
        }

        public static string InferKind(GameObject target)
        {
            if (target == null) return "unknown";

            var name = string.IsNullOrWhiteSpace(target.name) ? string.Empty : target.name.Trim().ToLowerInvariant();
            if (name.Contains("sphere")) return "sphere";
            if (name.Contains("cube")) return "cube";
            if (name.Contains("capsule")) return "capsule";
            if (name.Contains("cylinder")) return "cylinder";
            if (name.Contains("plane")) return "plane";
            if (name.Contains("quad")) return "quad";
            if (name.Contains("letter")) return "letter";
            if (name.Contains("fixation")) return "fixation";
            if (target.GetComponent<TextMesh>() != null) return "letter";
            return "unknown";
        }
    }
}
