using UnityEngine;
using VRPerception.Infra.EventBus;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 命令生命周期事件通道
    /// </summary>
    [CreateAssetMenu(fileName = "CommandLifecycleEventChannel", menuName = "VR Perception/Event Channels/Command Lifecycle")]
    public class CommandLifecycleEventChannel : EventChannel<CommandLifecycleEventData>
    {
    }
}

