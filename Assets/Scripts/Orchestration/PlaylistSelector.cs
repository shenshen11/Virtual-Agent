using System;
using System.Linq;
using UnityEngine;
using TMPro;

namespace VRPerception.Orchestration
{
    /// <summary>
    /// Playlist 选择器：
    /// - 在运行时从 Resources/PlayLists 或显式列表中加载多个 TaskPlaylist
    /// - 通过 Dropdown 展示可选 playlist 名称
    /// - 提供 GetSelectedPlaylist() 给其它 UI（例如 WSOrchestratorPanel）调用
    /// 
    /// 设计目标：不直接启动 Orchestrator，只负责“选哪一个 playlist”，
    /// 避免与现有 WSOrchestratorPanel 的开始按钮产生逻辑冲突。
    /// </summary>
    public sealed class PlaylistSelector : MonoBehaviour
    {
        [Header("Sources")]
        [Tooltip("如不为空，将优先使用此列表中的 Playlist；否则从 Resources/PlayLists 加载。")]
        [SerializeField] private TaskPlaylist[] explicitPlaylists;

        [Tooltip("当 explicitPlaylists 为空时，是否从 Resources/PlayLists 加载全部 TaskPlaylist。")]
        [SerializeField] private bool autoLoadFromResources = true;

        [Header("UI")]
        [Tooltip("用于展示可选 Playlist 名称的 TMP 下拉框（可选）。")]
        [SerializeField] private TMP_Dropdown playlistDropdown;

        private TaskPlaylist[] _playlists = Array.Empty<TaskPlaylist>();

        private void Awake()
        {
            LoadPlaylists();
            InitDropdownOptions();
        }

        /// <summary>
        /// 返回当前选择的 Playlist；若未找到则返回 null。
        /// </summary>
        public TaskPlaylist GetSelectedPlaylist()
        {
            if (_playlists == null || _playlists.Length == 0)
            {
                Debug.LogWarning("[PlaylistSelector] No playlists available.");
                return null;
            }

            if (playlistDropdown == null)
            {
                // 若没有 UI，下标默认 0
                return _playlists[0];
            }

            var index = Mathf.Clamp(playlistDropdown.value, 0, _playlists.Length - 1);
            return _playlists[index];
        }

        private void LoadPlaylists()
        {
            if (explicitPlaylists != null && explicitPlaylists.Length > 0)
            {
                _playlists = explicitPlaylists.Where(p => p != null).ToArray();
            }
            else if (autoLoadFromResources)
            {
                _playlists = Resources.LoadAll<TaskPlaylist>("PlayLists") ?? Array.Empty<TaskPlaylist>();
            }
            else
            {
                _playlists = Array.Empty<TaskPlaylist>();
            }

            if (_playlists.Length == 0)
            {
                Debug.LogWarning("[PlaylistSelector] No TaskPlaylist found (explicitPlaylists empty and Resources load disabled or empty).");
            }
        }

        private void InitDropdownOptions()
        {
            if (playlistDropdown == null)
            {
                return;
            }

            playlistDropdown.ClearOptions();

            if (_playlists.Length == 0)
            {
                playlistDropdown.AddOptions(new[] { "No Playlists" }.ToList());
                playlistDropdown.interactable = false;
                return;
            }

            var options = _playlists
                .Select(p => string.IsNullOrWhiteSpace(p.DisplayName) ? p.name : p.DisplayName)
                .ToList();

            playlistDropdown.AddOptions(options);
            playlistDropdown.value = 0;
            playlistDropdown.RefreshShownValue();
            playlistDropdown.interactable = true;
        }
    }
}
