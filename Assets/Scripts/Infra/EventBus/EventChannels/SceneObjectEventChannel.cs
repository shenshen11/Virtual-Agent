using UnityEngine;
using VRPerception.Infra.EventBus;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 场景对象变化事件通道
    /// </summary>
    [CreateAssetMenu(fileName = "SceneObjectEventChannel", menuName = "VR Perception/Event Channels/Scene Object")]
    public class SceneObjectEventChannel : EventChannel<SceneObjectEventData>
    {
    }
}

