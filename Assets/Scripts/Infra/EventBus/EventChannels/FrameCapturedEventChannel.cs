using UnityEngine;
using VRPerception.Infra.EventBus;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 帧捕获完成事件通道
    /// </summary>
    [CreateAssetMenu(fileName = "FrameCapturedEventChannel", menuName = "VR Perception/Event Channels/Frame Captured")]
    public class FrameCapturedEventChannel : EventChannel<FrameCapturedEventData>
    {
    }
}

