using UnityEngine;
using VRPerception.Infra.EventBus;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 性能指标事件通道
    /// </summary>
    [CreateAssetMenu(fileName = "PerformanceMetricEventChannel", menuName = "VR Perception/Event Channels/Performance Metric")]
    public class PerformanceMetricEventChannel : EventChannel<PerformanceMetricEventData>
    {
    }
}

