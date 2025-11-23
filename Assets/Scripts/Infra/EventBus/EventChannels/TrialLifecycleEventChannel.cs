using UnityEngine;
using VRPerception.Infra.EventBus;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 试验生命周期事件通道
    /// </summary>
    [CreateAssetMenu(fileName = "TrialLifecycleEventChannel", menuName = "VR Perception/Event Channels/Trial Lifecycle")]
    public class TrialLifecycleEventChannel : EventChannel<TrialLifecycleEventData>
    {
    }
}

