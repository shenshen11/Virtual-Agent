using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRPerception.Orchestration;

namespace VRPerception.UI
{
    /// <summary>
    /// MainMenu scene controller: stores playlist selection and loads Task scene.
    /// </summary>
    public class MainMenuSceneController : MonoBehaviour
    {
        [Header("Scene")]
        [SerializeField] private string taskSceneName = "Task";

        [Header("Playlist")]
        [SerializeField] private PlaylistSelector playlistSelector;

        [Header("PXR Video Seethrough")]
        [SerializeField] private PXRVideoSeethroughToggle videoSeethroughToggle;
        [SerializeField] private bool enableSeethroughOnEnable = true;
        [SerializeField] private bool disableSeethroughBeforeLoad = true;
        [SerializeField] private float seethroughApplyDelaySeconds = 0.2f;

        private Coroutine _seethroughRoutine;

        private void OnEnable()
        {
            if (enableSeethroughOnEnable && videoSeethroughToggle != null)
            {
                videoSeethroughToggle.SetEnabled(true);
                RestartSeethroughRoutine();
            }
        }

        private void OnDisable()
        {
            if (_seethroughRoutine != null)
            {
                StopCoroutine(_seethroughRoutine);
                _seethroughRoutine = null;
            }
        }

        /// <summary>
        /// UI button hook: cache playlist selection and load Task scene.
        /// </summary>
        public void StartExperiment()
        {
            var playlist = playlistSelector != null ? playlistSelector.GetSelectedPlaylist() : null;
            PlaylistLaunchState.SetSelectedPlaylist(playlist);

            if (_seethroughRoutine != null)
            {
                StopCoroutine(_seethroughRoutine);
                _seethroughRoutine = null;
            }

            if (disableSeethroughBeforeLoad && videoSeethroughToggle != null)
            {
                videoSeethroughToggle.SetEnabled(false);
            }

            if (string.IsNullOrWhiteSpace(taskSceneName))
            {
                Debug.LogError("[MainMenuSceneController] Task scene name is empty.");
                return;
            }

            SceneManager.LoadScene(taskSceneName);
        }

        private void RestartSeethroughRoutine()
        {
            if (seethroughApplyDelaySeconds <= 0f)
            {
                return;
            }

            if (_seethroughRoutine != null)
            {
                StopCoroutine(_seethroughRoutine);
            }

            _seethroughRoutine = StartCoroutine(ApplySeethroughDelayed());
        }

        private IEnumerator ApplySeethroughDelayed()
        {
            yield return new WaitForSeconds(seethroughApplyDelaySeconds);

            if (enableSeethroughOnEnable && videoSeethroughToggle != null)
            {
                videoSeethroughToggle.SetEnabled(true);
            }

            _seethroughRoutine = null;
        }
    }
}
