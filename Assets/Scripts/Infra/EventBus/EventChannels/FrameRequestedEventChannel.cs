using UnityEngine;
using VRPerception.Infra.EventBus;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 帧请求事件通道
    /// </summary>
    [CreateAssetMenu(fileName = "FrameRequestedEventChannel", menuName = "VR Perception/Event Channels/Frame Requested")]
    public class FrameRequestedEventChannel : EventChannel<FrameRequestedEventData>
    {
    }
}

