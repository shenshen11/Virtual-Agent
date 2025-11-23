using UnityEngine;
using VRPerception.Infra.EventBus;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 推理接收事件通道
    /// </summary>
    [CreateAssetMenu(fileName = "InferenceReceivedEventChannel", menuName = "VR Perception/Event Channels/Inference Received")]
    public class InferenceReceivedEventChannel : EventChannel<InferenceReceivedEventData>
    {
    }
}

