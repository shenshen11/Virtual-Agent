using UnityEngine;
using VRPerception.Infra.EventBus;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 日志刷新请求事件通道
    /// </summary>
    [CreateAssetMenu(fileName = "LogFlushEventChannel", menuName = "VR Perception/Event Channels/Log Flush")]
    public class LogFlushEventChannel : EventChannel<LogFlushEventData>
    {
    }
}

