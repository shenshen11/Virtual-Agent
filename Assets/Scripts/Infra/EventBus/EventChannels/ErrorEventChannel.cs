using UnityEngine;
using VRPerception.Infra.EventBus;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 错误事件通道
    /// </summary>
    [CreateAssetMenu(fileName = "ErrorEventChannel", menuName = "VR Perception/Event Channels/Error")]
    public class ErrorEventChannel : EventChannel<ErrorEventData>
    {
    }
}

