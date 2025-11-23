using UnityEngine;
using VRPerception.Infra.EventBus;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 连接状态变化事件通道
    /// </summary>
    [CreateAssetMenu(fileName = "ConnectionStateEventChannel", menuName = "VR Perception/Event Channels/Connection State")]
    public class ConnectionStateEventChannel : EventChannel<ConnectionStateEventData>
    {
    }
}

