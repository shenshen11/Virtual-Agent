using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using VRPerception.Infra.EventBus;
using VRPerception.Orchestration;

namespace VRPerception.UI
{
    /// <summary>
    /// 世界空间任务消息面板
    /// - 订阅 OrchestratorStateEventData，显示 preTaskMessage 和 postTaskMessage
    /// - 在任务开始前显示引导文本，任务完成后显示总结文本
    /// - 支持自动隐藏或手动确认
    /// </summary>
    [RequireComponent(typeof(Canvas))]
    public class WSTaskMessagePanel : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private EventBusManager eventBus;
        [SerializeField] private TaskPlaylist playlist;
        [SerializeField] private bool autoFindEventBus = true;

        [Header("UI Components")]
        [Tooltip("消息面板根节点")]
        [SerializeField] private GameObject panelRoot;
        [Tooltip("消息标题文本")]
        [SerializeField] private TMP_Text titleText;
        [Tooltip("消息内容文本")]
        [SerializeField] private TMP_Text messageText;
        [Tooltip("确认按钮（可选，如果为空则自动隐藏）")]
        [SerializeField] private Button confirmButton;

        [Header("Display Settings")]
        [Tooltip("preTaskMessage 显示时长（秒），0 表示需要手动确认")]
        [SerializeField] private float preTaskDisplayDuration = 5f;
        [Tooltip("postTaskMessage 显示时长（秒），0 表示需要手动确认")]
        [SerializeField] private float postTaskDisplayDuration = 3f;
        [Tooltip("是否在 Playlist 完成时显示最后一个任务的 postTaskMessage")]
        [SerializeField] private bool showPostMessageOnCompletion = true;

        [Header("Rendering Settings")]
        [SerializeField] private int canvasSortingOrder = 50;

        private Canvas _canvas;
        private Coroutine _autoHideRoutine;
        private int _lastEntryIndex = -1;
        private string _lastPostTaskMessage = string.Empty;

        private void Awake()
        {
            _canvas = GetComponent<Canvas>();
            if (_canvas != null)
            {
                _canvas.renderMode = RenderMode.WorldSpace;
                if (_canvas.worldCamera == null && Camera.main != null)
                    _canvas.worldCamera = Camera.main;
                _canvas.sortingOrder = canvasSortingOrder;
            }

            if (autoFindEventBus && eventBus == null)
                eventBus = EventBusManager.Instance;

            HidePanel();

            if (confirmButton != null)
                confirmButton.onClick.AddListener(OnConfirmClicked);
        }

        private void OnEnable()
        {
            SubscribeEvents();
        }

        private void OnDisable()
        {
            UnsubscribeEvents();
            if (_autoHideRoutine != null)
            {
                StopCoroutine(_autoHideRoutine);
                _autoHideRoutine = null;
            }
        }

        private void OnDestroy()
        {
            if (confirmButton != null)
                confirmButton.onClick.RemoveListener(OnConfirmClicked);
        }

        private void SubscribeEvents()
        {
            eventBus?.OrchestratorState?.Subscribe(OnOrchestratorState);
        }

        private void UnsubscribeEvents()
        {
            eventBus?.OrchestratorState?.Unsubscribe(OnOrchestratorState);
        }

        private void OnOrchestratorState(OrchestratorStateEventData data)
        {
            if (data == null) return;

            // 当开始新的 Entry 时，显示 preTaskMessage
            if (data.state == OrchestratorLifecycleState.RunningEntry)
            {
                // 准备显示当前任务的 preTaskMessage
                if (playlist != null && data.currentEntryIndex >= 0 && data.currentEntryIndex < playlist.Entries.Count)
                {
                    var entry = playlist.Entries[data.currentEntryIndex];
                    if (!string.IsNullOrWhiteSpace(entry.preTaskMessage))
                    {
                        ShowPreTaskMessage(entry.preTaskMessage, entry.ResolveTaskId());
                    }

                    // 保存 postTaskMessage 以便任务完成后（休息时）显示
                    _lastPostTaskMessage = entry.postTaskMessage;
                    _lastEntryIndex = data.currentEntryIndex;
                }
            }
            // 当进入休息阶段时，显示刚完成任务的 postTaskMessage
            else if (data.state == OrchestratorLifecycleState.WaitingForRest)
            {
                if (!string.IsNullOrWhiteSpace(_lastPostTaskMessage))
                {
                    ShowPostTaskMessage(_lastPostTaskMessage);
                    _lastPostTaskMessage = string.Empty; // 清空，避免重复显示
                }
            }
            // 当 Playlist 完成时，显示最后一个任务的 postTaskMessage（如果还没显示过）
            else if (data.state == OrchestratorLifecycleState.Completed && showPostMessageOnCompletion)
            {
                if (!string.IsNullOrWhiteSpace(_lastPostTaskMessage))
                {
                    ShowPostTaskMessage(_lastPostTaskMessage);
                    _lastPostTaskMessage = string.Empty;
                }
            }
            // 当 Playlist 被取消时，隐藏面板
            else if (data.state == OrchestratorLifecycleState.Cancelled)
            {
                HidePanel();
                _lastPostTaskMessage = string.Empty;
            }
        }

        private void ShowPreTaskMessage(string message, string taskId)
        {
            if (titleText != null)
                titleText.text = "任务引导";
            if (messageText != null)
                messageText.text = message;

            ShowPanel(preTaskDisplayDuration);
        }

        private void ShowPostTaskMessage(string message)
        {
            if (titleText != null)
                titleText.text = "任务总结";
            if (messageText != null)
                messageText.text = message;

            ShowPanel(postTaskDisplayDuration);
        }

        private void ShowPanel(float autoDuration)
        {
            if (panelRoot != null)
                panelRoot.SetActive(true);

            // 如果有确认按钮且需要手动确认，显示按钮
            if (confirmButton != null)
            {
                confirmButton.gameObject.SetActive(autoDuration <= 0f);
            }

            // 如果设置了自动隐藏时长，启动自动隐藏
            if (autoDuration > 0f)
            {
                if (_autoHideRoutine != null)
                    StopCoroutine(_autoHideRoutine);
                _autoHideRoutine = StartCoroutine(AutoHideAfterDelay(autoDuration));
            }
        }

        private void HidePanel()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);

            if (_autoHideRoutine != null)
            {
                StopCoroutine(_autoHideRoutine);
                _autoHideRoutine = null;
            }
        }

        private IEnumerator AutoHideAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            HidePanel();
            _autoHideRoutine = null;
        }

        private void OnConfirmClicked()
        {
            HidePanel();
        }
    }
}

