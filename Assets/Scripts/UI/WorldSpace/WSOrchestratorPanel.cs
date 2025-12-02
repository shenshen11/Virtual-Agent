using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using VRPerception.Infra.EventBus;
using VRPerception.Orchestration;

namespace VRPerception.UI
{
    /// <summary>
    /// 世界空间编排控制面板：桥接 UI 按钮与 TaskOrchestrator，并显示编排状态。
    /// 挂载在一个世界空间 Canvas 下的空物体或面板上，在 Inspector 里绑定引用。
    /// </summary>
    public sealed class WSOrchestratorPanel : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private TaskOrchestrator orchestrator;
        [SerializeField] private EventBusManager eventBus;
        [Tooltip("可选：若指定，则在开始时优先使用 PlaylistSelector 中选定的 Playlist。")]
        [SerializeField] private VRPerception.Orchestration.PlaylistSelector playlistSelector;
        [SerializeField] private Text stateText;
        [SerializeField] private Button startButton;
        [SerializeField] private Button pauseButton;
        [SerializeField] private Button resumeButton;
        [SerializeField] private Button skipRestButton;
        [SerializeField] private Button skipEntryButton;
        [SerializeField] private Button cancelButton;

        [Header("Options")]
        [SerializeField] private bool autoWireButtons = true;

        private void Awake()
        {
            if (eventBus == null) eventBus = EventBusManager.Instance;
            if (orchestrator == null) orchestrator = FindObjectOfType<TaskOrchestrator>();
        }

        private void OnEnable()
        {
            if (eventBus?.OrchestratorState != null)
                eventBus.OrchestratorState.Subscribe(OnOrchestratorState);
            if (autoWireButtons) WireButtons();
        }

        private void OnDisable()
        {
            try { eventBus?.OrchestratorState?.Unsubscribe(OnOrchestratorState); } catch {}
            UnwireButtons();
        }

        private void WireButtons()
        {
            if (startButton) startButton.onClick.AddListener(OnStartClicked);
            if (pauseButton) pauseButton.onClick.AddListener(OnPauseClicked);
            if (resumeButton) resumeButton.onClick.AddListener(OnResumeClicked);
            if (skipRestButton) skipRestButton.onClick.AddListener(OnSkipRestClicked);
            if (skipEntryButton) skipEntryButton.onClick.AddListener(OnSkipEntryClicked);
            if (cancelButton) cancelButton.onClick.AddListener(OnCancelClicked);
        }

        private void UnwireButtons()
        {
            if (startButton) startButton.onClick.RemoveListener(OnStartClicked);
            if (pauseButton) pauseButton.onClick.RemoveListener(OnPauseClicked);
            if (resumeButton) resumeButton.onClick.RemoveListener(OnResumeClicked);
            if (skipRestButton) skipRestButton.onClick.RemoveListener(OnSkipRestClicked);
            if (skipEntryButton) skipEntryButton.onClick.RemoveListener(OnSkipEntryClicked);
            if (cancelButton) cancelButton.onClick.RemoveListener(OnCancelClicked);
        }

        private async void OnStartClicked()
        {
            if (orchestrator == null || orchestrator.IsRunning)
            {
                return;
            }

            // 如有 PlaylistSelector，则优先使用其返回的 Playlist；否则退回默认 Playlist
            TaskPlaylist playlist = null;
            if (playlistSelector != null)
            {
                playlist = playlistSelector.GetSelectedPlaylist();
            }

            await orchestrator.StartPlaylistAsync(playlist);
        }

        private void OnPauseClicked()
        {
            orchestrator?.Pause();
        }

        private void OnResumeClicked()
        {
            orchestrator?.Resume();
        }

        private void OnSkipRestClicked()
        {
            orchestrator?.SkipRest();
        }

        private void OnSkipEntryClicked()
        {
            orchestrator?.SkipEntry();
        }

        private void OnCancelClicked()
        {
            orchestrator?.Cancel();
        }

        private void OnOrchestratorState(OrchestratorStateEventData e)
        {
            if (stateText == null || e == null) return;
            var playlistName = string.IsNullOrEmpty(e.playlistDisplayName) ? e.playlistId : e.playlistDisplayName;
            stateText.text = $"[{playlistName}] Entry {e.currentEntryIndex} | {e.state} | {e.message}";
        }
    }
}
