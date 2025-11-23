using UnityEngine;
using VRPerception.Infra.EventBus;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 动作计划接收事件通道
    /// </summary>
    [CreateAssetMenu(fileName = "ActionPlanReceivedEventChannel", menuName = "VR Perception/Event Channels/Action Plan Received")]
    public class ActionPlanReceivedEventChannel : EventChannel<ActionPlanReceivedEventData>
    {
    }
}

