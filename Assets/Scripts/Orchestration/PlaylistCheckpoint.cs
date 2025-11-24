using System;
using VRPerception.Infra.EventBus;
using VRPerception.Tasks;

namespace VRPerception.Orchestration
{
    /// <summary>
    /// TaskRunner 运行配置快照，用于断点续跑时恢复。
    /// </summary>
    [Serializable]
    public sealed class TaskRunnerSnapshot
    {
        public string taskId;
        public SubjectMode? subjectMode;
        public bool forceHumanInput;
        public int randomSeed;
        public int maxTrials;
        public bool enableActionPlanLoop;
        public int actionPlanLoopTimeoutMs;
        public string humanInputPrompt;
    }

    /// <summary>
    /// Playlist 级别的断点信息。
    /// </summary>
    [Serializable]
    public sealed class PlaylistCheckpoint
    {
        public string checkpointId;
        public string playlistId;
        public string playlistName;
        public string sessionId;
        public string participantId;
        public string deviceId;

        public int currentEntryIndex;
        public int currentTrialIndex;
        public bool entryCompleted;

        public TaskRunnerSnapshot runner;
        public DateTime lastSaveTimeUtc;
        public OrchestratorLifecycleState lastState;
        public string lastMessage;
    }
}
