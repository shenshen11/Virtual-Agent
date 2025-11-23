using System;
using System.Collections.Generic;
using UnityEngine;
using VRPerception.Tasks;

namespace VRPerception.Orchestration
{
    /// <summary>
    /// Playlist ScriptableObject：定义任务编排条目顺序及 TaskRunner 覆盖项。
    /// </summary>
    [CreateAssetMenu(fileName = "TaskPlaylist", menuName = "VR Perception/Task Playlist")]
    public sealed class TaskPlaylist : ScriptableObject
    {
        [SerializeField]
        [Tooltip("Playlist 唯一标识（默认生成 Guid）。")]
        private string playlistId = Guid.NewGuid().ToString("N");

        [SerializeField]
        [Tooltip("用于 UI 展示的名称。")]
        private string displayName;

        [SerializeField]
        [Tooltip("用于操作员/参与者的简介说明。")]
        [TextArea]
        private string description;

        [SerializeField]
        [Tooltip("任务执行条目列表，顺序即执行顺序。")]
        private List<TaskPlaylistEntry> entries = new List<TaskPlaylistEntry>();

        [SerializeField]
        [Tooltip("默认参与者 ID（可由 Orchestrator 覆写或运行时输入）。")]
        private string defaultParticipantId;

        [SerializeField]
        [Tooltip("任务间默认休息时长（秒），单条目可覆写。")]
        private float defaultRestSeconds = 0f;

        [SerializeField]
        [Tooltip("默认随机种子，条目 randomSeed ≤ 0 时回退于此。")]
        private int defaultRandomSeed = 12345;

        /// <summary>
        /// Playlist 唯一标识。
        /// </summary>
        public string PlaylistId => playlistId;

        /// <summary>
        /// 展示名称。
        /// </summary>
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? name : displayName;

        /// <summary>
        /// 简介。
        /// </summary>
        public string Description => description;

        /// <summary>
        /// 默认参与者 ID。
        /// </summary>
        public string DefaultParticipantId => defaultParticipantId;

        /// <summary>
        /// 默认休息时长（秒）。
        /// </summary>
        public float DefaultRestSeconds => defaultRestSeconds;

        /// <summary>
        /// 默认随机种子。
        /// </summary>
        public int DefaultRandomSeed => defaultRandomSeed;

        /// <summary>
        /// 只读条目集合。
        /// </summary>
        public IReadOnlyList<TaskPlaylistEntry> Entries => entries;

        /// <summary>
        /// 按索引获取条目。
        /// </summary>
        public TaskPlaylistEntry GetEntry(int index) => entries[index];

        /// <summary>
        /// 尝试获取条目。
        /// </summary>
        public bool TryGetEntry(int index, out TaskPlaylistEntry entry)
        {
            if (index < 0 || index >= entries.Count)
            {
                entry = null;
                return false;
            }

            entry = entries[index];
            return true;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(playlistId))
            {
                playlistId = Guid.NewGuid().ToString("N");
            }
        }
#endif
    }

    /// <summary>
    /// Playlist 条目定义：对应一次 TaskRunner 执行的配置项。
    /// </summary>
    [Serializable]
    public class TaskPlaylistEntry
    {
        [Tooltip("TaskRegistry 注册的任务 ID，留空则使用 legacyMode 推导。")]
        public string taskId;

        [Tooltip("向后兼容 TaskMode 枚举，当 taskId 为空时生效。")]
        public TaskMode legacyMode = TaskMode.DistanceCompression;

        [Tooltip("UI 展示名称，留空则使用 taskId/legacyMode。")]
        public string displayName;

        [Tooltip("对参与者/操作员的描述说明。")]
        [TextArea]
        public string description;

        [Tooltip("执行主体模式：MLLM 或 Human。")]
        public SubjectMode subjectMode = SubjectMode.MLLM;

        [Tooltip("随机种子，≤ 0 时采用 Playlist 默认值。")]
        public int randomSeed = 0;

        [Tooltip("最大试次，0 表示使用任务默认配置。")]
        public int maxTrials = 0;

        [Tooltip("是否启用 action_plan 闭环等待。")]
        public bool enableActionPlanLoop = true;

        [Tooltip("action_plan 闭环等待的超时时间（毫秒）。")]
        public int actionPlanLoopTimeoutMs = 20000;

        [Tooltip("是否强制要求开启人类输入 UI（覆盖 subjectMode）。")]
        public bool requireHumanInput = false;

        [Tooltip("场景预设标识，对接 ExperimentSceneManager。")]
        public string scenePreset;

        [Tooltip("任务前引导文本。")]
        [TextArea]
        public string preTaskMessage;

        [Tooltip("任务后总结文本。")]
        [TextArea]
        public string postTaskMessage;

        [Tooltip("执行完成后的休息时长（秒），负值代表使用 Playlist 默认值。")]
        public float restSeconds = -1f;

        [Tooltip("仅供操作员查看的备注。")]
        [TextArea]
        public string operatorNotes;

        [Tooltip("Human 模式下显示给用户的任务提示文本（描述该任务需要用户做什么输入或选择）。")]
        [TextArea]
        public string humanInputPrompt;

        /// <summary>
        /// 解析最终任务 ID（taskId 优先，fallback legacyMode）。
        /// </summary>
        public string ResolveTaskId()
        {
            if (!string.IsNullOrWhiteSpace(taskId))
            {
                return taskId;
            }

            return legacyMode switch
            {
                TaskMode.DistanceCompression => "distance_compression",
                TaskMode.SemanticSizeBias => "semantic_size_bias",
                _ => null
            };
        }

        /// <summary>
        /// 计算休息时长，负值回退 Playlist 默认值。
        /// </summary>
        public float ResolveRestSeconds(float playlistDefault)
        {
            return restSeconds >= 0f ? restSeconds : playlistDefault;
        }

        /// <summary>
        /// 计算随机种子，≤0 回退 Playlist 默认值。
        /// </summary>
        public int ResolveRandomSeed(int playlistDefault)
        {
            return randomSeed > 0 ? randomSeed : playlistDefault;
        }
    }
}