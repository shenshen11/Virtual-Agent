using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VRPerception.Infra.EventBus;
using VRPerception.Tasks;

namespace VRPerception.Orchestration
{
    public interface IPlaylistCheckpointStore
    {
        Task<PlaylistCheckpoint> LoadLatestAsync(string playlistId, string participantId, CancellationToken ct = default);
        Task SaveAsync(PlaylistCheckpoint checkpoint, CancellationToken ct = default);
        Task DeleteAsync(string playlistId, string participantId, CancellationToken ct = default);
        string RootFolder { get; }
    }

    public sealed class FilePlaylistCheckpointStore : IPlaylistCheckpointStore
    {
        public string RootFolder { get; }

        public FilePlaylistCheckpointStore(string rootFolder = null)
        {
            if (string.IsNullOrEmpty(rootFolder))
            {
                rootFolder = Path.Combine(Application.persistentDataPath, "VRP_Checkpoints");
            }
            RootFolder = rootFolder;
            try
            {
                Directory.CreateDirectory(RootFolder);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FilePlaylistCheckpointStore] CreateDirectory failed: {ex.Message}");
            }
        }

        public async Task<PlaylistCheckpoint> LoadLatestAsync(string playlistId, string participantId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(playlistId) || string.IsNullOrWhiteSpace(participantId))
                return null;

            string filePath = GetPath(playlistId, participantId);
            if (!File.Exists(filePath))
                return null;

            try
            {
                return await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    var json = File.ReadAllText(filePath, Encoding.UTF8);
                    var dto = JsonUtility.FromJson<PlaylistCheckpointDTO>(json);
                    return FromDTO(dto);
                }, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FilePlaylistCheckpointStore] Load failed: {ex.Message}");
                return null;
            }
        }

        public async Task SaveAsync(PlaylistCheckpoint checkpoint, CancellationToken ct = default)
        {
            if (checkpoint == null) return;
            if (string.IsNullOrWhiteSpace(checkpoint.playlistId) || string.IsNullOrWhiteSpace(checkpoint.participantId))
            {
                Debug.LogWarning("[FilePlaylistCheckpointStore] Save aborted: missing playlistId/participantId");
                return;
            }

            string filePath = GetPath(checkpoint.playlistId, checkpoint.participantId);
            var dto = ToDTO(checkpoint);

            try
            {
                var json = JsonUtility.ToJson(dto, true);
                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                    File.WriteAllText(filePath, json, Encoding.UTF8);
                }, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogError($"[FilePlaylistCheckpointStore] Save failed: {ex.Message}");
            }
        }

        public async Task DeleteAsync(string playlistId, string participantId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(playlistId) || string.IsNullOrWhiteSpace(participantId))
                return;

            string filePath = GetPath(playlistId, participantId);
            try
            {
                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FilePlaylistCheckpointStore] Delete failed: {ex.Message}");
            }
        }

        private string GetPath(string playlistId, string participantId)
        {
            string safePlaylist = Sanitize(playlistId);
            string safeParticipant = Sanitize(participantId);
            string folder = Path.Combine(RootFolder, safePlaylist);
            return Path.Combine(folder, $"{safeParticipant}.json");
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unknown";
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        [Serializable]
        private class TaskRunnerSnapshotDTO
        {
            public string taskId;
            public int legacyMode;
            public bool legacyModeHasValue;
            public int subjectMode;
            public bool subjectModeHasValue;
            public bool forceHumanInput;
            public int randomSeed;
            public int maxTrials;
            public bool enableActionPlanLoop;
            public int actionPlanLoopTimeoutMs;
            public string humanInputPrompt;
        }

        [Serializable]
        private class PlaylistCheckpointDTO
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
            public TaskRunnerSnapshotDTO runner;
            public string lastSaveTimeUtcIso;
            public string lastState;
            public string lastMessage;
        }

        private static PlaylistCheckpointDTO ToDTO(PlaylistCheckpoint src)
        {
            var dto = new PlaylistCheckpointDTO
            {
                checkpointId = string.IsNullOrWhiteSpace(src.checkpointId) ? Guid.NewGuid().ToString("N") : src.checkpointId,
                playlistId = src.playlistId,
                playlistName = src.playlistName,
                sessionId = string.IsNullOrWhiteSpace(src.sessionId) ? SystemInfo.deviceUniqueIdentifier : src.sessionId,
                participantId = src.participantId,
                deviceId = string.IsNullOrWhiteSpace(src.deviceId) ? SystemInfo.deviceUniqueIdentifier : src.deviceId,
                currentEntryIndex = src.currentEntryIndex,
                currentTrialIndex = src.currentTrialIndex,
                entryCompleted = src.entryCompleted,
                runner = new TaskRunnerSnapshotDTO
                {
                    taskId = src.runner?.taskId,
                    legacyMode = src.runner?.legacyMode.HasValue == true ? (int)src.runner.legacyMode.Value : 0,
                    legacyModeHasValue = src.runner?.legacyMode.HasValue == true,
                    subjectMode = src.runner?.subjectMode.HasValue == true ? (int)src.runner.subjectMode.Value : 0,
                    subjectModeHasValue = src.runner?.subjectMode.HasValue == true,
                    forceHumanInput = src.runner?.forceHumanInput ?? false,
                    randomSeed = src.runner?.randomSeed ?? 0,
                    maxTrials = src.runner?.maxTrials ?? 0,
                    enableActionPlanLoop = src.runner?.enableActionPlanLoop ?? false,
                    actionPlanLoopTimeoutMs = src.runner?.actionPlanLoopTimeoutMs ?? 0,
                    humanInputPrompt = src.runner?.humanInputPrompt
                },
                lastSaveTimeUtcIso = src.lastSaveTimeUtc.ToUniversalTime().ToString("o"),
                lastState = src.lastState.ToString(),
                lastMessage = src.lastMessage
            };
            return dto;
        }

        private static PlaylistCheckpoint FromDTO(PlaylistCheckpointDTO dto)
        {
            var result = new PlaylistCheckpoint
            {
                checkpointId = dto.checkpointId,
                playlistId = dto.playlistId,
                playlistName = dto.playlistName,
                sessionId = dto.sessionId,
                participantId = dto.participantId,
                deviceId = dto.deviceId,
                currentEntryIndex = dto.currentEntryIndex,
                currentTrialIndex = dto.currentTrialIndex,
                entryCompleted = dto.entryCompleted,
                runner = new TaskRunnerSnapshot
                {
                    taskId = dto.runner?.taskId,
                    legacyMode = (dto.runner != null && dto.runner.legacyModeHasValue) ? (TaskMode?)dto.runner.legacyMode : null,
                    subjectMode = (dto.runner != null && dto.runner.subjectModeHasValue) ? (SubjectMode?)dto.runner.subjectMode : null,
                    forceHumanInput = dto.runner?.forceHumanInput ?? false,
                    randomSeed = dto.runner?.randomSeed ?? 0,
                    maxTrials = dto.runner?.maxTrials ?? 0,
                    enableActionPlanLoop = dto.runner?.enableActionPlanLoop ?? false,
                    actionPlanLoopTimeoutMs = dto.runner?.actionPlanLoopTimeoutMs ?? 0,
                    humanInputPrompt = dto.runner?.humanInputPrompt
                },
                lastSaveTimeUtc = ParseIso(dto.lastSaveTimeUtcIso),
                lastState = ParseState(dto.lastState),
                lastMessage = dto.lastMessage
            };
            return result;
        }

        private static DateTime ParseIso(string iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return DateTime.UtcNow;
            if (DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
                return dt.ToUniversalTime();
            return DateTime.UtcNow;
        }

        private static OrchestratorLifecycleState ParseState(string s)
        {
            if (Enum.TryParse<OrchestratorLifecycleState>(s, out var st))
                return st;
            return OrchestratorLifecycleState.Idle;
        }
    }
}