using UnityEngine;
using VRPerception.Infra.EventBus;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 应用程序退出事件通道（无参数）
    /// </summary>
    [CreateAssetMenu(fileName = "ApplicationQuitEventChannel", menuName = "VR Perception/Event Channels/Application Quit")]
    public class ApplicationQuitEventChannel : VoidEventChannel
    {
    }
}

