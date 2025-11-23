using UnityEngine;
using VRPerception.Infra.EventBus;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 暂停/恢复事件通道（无参数）
    /// </summary>
    [CreateAssetMenu(fileName = "PauseResumeEventChannel", menuName = "VR Perception/Event Channels/Pause Resume")]
    public class PauseResumeEventChannel : VoidEventChannel
    {
    }
}

