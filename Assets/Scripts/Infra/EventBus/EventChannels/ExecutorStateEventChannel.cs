using UnityEngine;
using VRPerception.Infra.EventBus;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 执行器状态变化事件通道
    /// </summary>
    [CreateAssetMenu(fileName = "ExecutorStateEventChannel", menuName = "VR Perception/Event Channels/Executor State")]
    public class ExecutorStateEventChannel : EventChannel<ExecutorStateEventData>
    {
    }
}

