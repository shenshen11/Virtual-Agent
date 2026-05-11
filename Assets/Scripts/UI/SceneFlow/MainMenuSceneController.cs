using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRPerception.Orchestration;
using VRPerception.Tasks;

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

        [Header("Human Session Identity")]
        [SerializeField] private TMP_InputField experimentIdInput;
        [SerializeField] private TMP_InputField participantIdInput;
        [SerializeField] private TMP_Text validationMessageText;

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
            bool isHumanPlaylist = IsHumanPlaylist(playlist);
            string experimentId = experimentIdInput != null ? experimentIdInput.text?.Trim() : null;
            string participantId = participantIdInput != null ? participantIdInput.text?.Trim() : null;

            if (isHumanPlaylist && !TryParsePositiveExperimentId(experimentId))
            {
                SetValidationMessage("请询问实验员您的实验编号。");
                return;
            }

            if (isHumanPlaylist && string.IsNullOrWhiteSpace(participantId))
            {
                SetValidationMessage("请填写学号。");
                return;
            }

            SetValidationMessage(string.Empty);
            PlaylistLaunchState.SetSelectedPlaylist(playlist);
            PlaylistLaunchState.SetSessionIdentity(experimentId, participantId);

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

        private static bool TryParsePositiveExperimentId(string experimentId)
        {
            if (string.IsNullOrWhiteSpace(experimentId)) return false;
            var value = experimentId.Trim();
            for (int i = 0; i < value.Length; i++)
            {
                if (!char.IsDigit(value[i])) return false;
            }

            return int.TryParse(value, out var experimentNumber) && experimentNumber >= 1;
        }

        private void SetValidationMessage(string message)
        {
            if (validationMessageText == null) return;
            validationMessageText.text = message ?? string.Empty;
            validationMessageText.gameObject.SetActive(!string.IsNullOrWhiteSpace(message));
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
