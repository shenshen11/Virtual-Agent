using UnityEngine;
using VRPerception.Infra.EventBus;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 编排器状态事件通道
    /// </summary>
    [CreateAssetMenu(fileName = "OrchestratorStateEventChannel", menuName = "VR Perception/Event Channels/Orchestrator State")]
    public class OrchestratorStateEventChannel : EventChannel<OrchestratorStateEventData>
    {
    }
}

