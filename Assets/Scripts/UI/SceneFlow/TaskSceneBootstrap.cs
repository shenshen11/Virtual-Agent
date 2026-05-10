using UnityEngine;
using VRPerception.Infra;
using VRPerception.Orchestration;
using VRPerception.Tasks;

namespace VRPerception.UI
{
    /// <summary>
    /// Task scene bootstrap: consumes playlist selection and starts the orchestrator.
    /// </summary>
    public class TaskSceneBootstrap : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private TaskOrchestrator orchestrator;
        [SerializeField] private TaskRunner taskRunner;

        [Header("Behavior")]
        [Tooltip("If no playlist was provided by MainMenu, should we auto-start with default playlist?")]
        [SerializeField] private bool autoStartIfNoPlaylist = false;

        private void Awake()
        {
            if (orchestrator == null)
            {
                orchestrator = FindObjectOfType<TaskOrchestrator>();
            }

            if (taskRunner == null)
            {
                taskRunner = FindObjectOfType<TaskRunner>();
            }

            if (taskRunner != null)
            {
                taskRunner.AutoRun = false;
            }
        }

        private void Start()
        {
            if (orchestrator == null)
            {
                Debug.LogError("[TaskSceneBootstrap] TaskOrchestrator not found.");
                return;
            }

            var playlist = PlaylistLaunchState.ConsumeSelectedPlaylist();
            PlaylistLaunchState.ConsumeSessionIdentity(out var experimentId, out var participantId);
            if (playlist == null && !autoStartIfNoPlaylist)
            {
                return;
            }

            ConfigureLogSessionIfNeeded(playlist, experimentId, participantId);
            _ = orchestrator.StartPlaylistAsync(playlist);
        }

        private static void ConfigureLogSessionIfNeeded(TaskPlaylist playlist, string experimentId, string participantId)
        {
            if (!IsHumanPlaylist(playlist))
            {
                LogSessionPaths.ClearConfiguredSession("VRP_Logs");
                return;
            }

            LogSessionPaths.ConfigureHumanSessionIdentity("VRP_Logs", experimentId, participantId);
        }

        private static bool IsHumanPlaylist(TaskPlaylist playlist)
        {
            if (playlist == null) return false;
            var entries = playlist.Entries;
            if (entries == null) return false;

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null) continue;
                if (entry.subjectMode == SubjectMode.Human || entry.requireHumanInput)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
