using UnityEngine;
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
            if (playlist == null && !autoStartIfNoPlaylist)
            {
                return;
            }

            _ = orchestrator.StartPlaylistAsync(playlist);
        }
    }
}
