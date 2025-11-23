using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRPerception.Infra.EventBus
{
    /// <summary>
    /// 基础事件通道，基于 ScriptableObject 实现全局事件通信
    /// </summary>
    /// <typeparam name="T">事件数据类型</typeparam>
    public abstract class EventChannel<T> : ScriptableObject
    {
        private readonly List<Action<T>> _listeners = new List<Action<T>>();
        private readonly List<WeakReference> _weakListeners = new List<WeakReference>();
        
        [Header("Debug")]
        [SerializeField] private bool enableDebugLogs = false;
        [SerializeField] private int maxLoggedEvents = 10;
        
        private readonly Queue<EventLogEntry> _eventLog = new Queue<EventLogEntry>();
        
        /// <summary>
        /// 订阅事件（强引用）
        /// </summary>
        public void Subscribe(Action<T> listener)
        {
            if (listener == null) return;
            
            if (!_listeners.Contains(listener))
            {
                _listeners.Add(listener);
                LogDebug($"Subscribed listener (strong): {listener.Method.Name}");
            }
        }
        
        /// <summary>
        /// 取消订阅事件（强引用）
        /// </summary>
        public void Unsubscribe(Action<T> listener)
        {
            if (listener == null) return;
            
            if (_listeners.Remove(listener))
            {
                LogDebug($"Unsubscribed listener (strong): {listener.Method.Name}");
            }
        }
        
        /// <summary>
        /// 订阅事件（弱引用，防止内存泄漏）
        /// </summary>
        public void SubscribeWeak(Action<T> listener)
        {
            if (listener == null) return;
            
            _weakListeners.Add(new WeakReference(listener));
            LogDebug($"Subscribed listener (weak): {listener.Method.Name}");
        }
        
        /// <summary>
        /// 发布事件
        /// </summary>
        public void Publish(T eventData)
        {
            LogEvent(eventData);
            
            // 调用强引用监听器
            for (int i = _listeners.Count - 1; i >= 0; i--)
            {
                try
                {
                    _listeners[i]?.Invoke(eventData);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[EventChannel] Error in listener {_listeners[i]?.Method.Name}: {ex.Message}");
                }
            }
            
            // 调用弱引用监听器并清理失效引用
            for (int i = _weakListeners.Count - 1; i >= 0; i--)
            {
                var weakRef = _weakListeners[i];
                if (weakRef.Target is Action<T> listener)
                {
                    try
                    {
                        listener.Invoke(eventData);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[EventChannel] Error in weak listener {listener.Method.Name}: {ex.Message}");
                    }
                }
                else
                {
                    // 清理失效的弱引用
                    _weakListeners.RemoveAt(i);
                }
            }
        }
        
        /// <summary>
        /// 清理所有监听器
        /// </summary>
        public void Clear()
        {
            _listeners.Clear();
            _weakListeners.Clear();
            LogDebug("Cleared all listeners");
        }
        
        /// <summary>
        /// 获取监听器数量
        /// </summary>
        public int ListenerCount => _listeners.Count + _weakListeners.Count;
        
        /// <summary>
        /// 获取事件日志
        /// </summary>
        public IReadOnlyCollection<EventLogEntry> EventLog => _eventLog;
        
        private void LogEvent(T eventData)
        {
            if (!enableDebugLogs) return;
            
            var logEntry = new EventLogEntry
            {
                Timestamp = DateTime.UtcNow,
                EventType = typeof(T).Name,
                EventData = eventData?.ToString() ?? "null",
                ListenerCount = ListenerCount
            };
            
            _eventLog.Enqueue(logEntry);
            
            // 限制日志数量
            while (_eventLog.Count > maxLoggedEvents)
            {
                _eventLog.Dequeue();
            }
            
            LogDebug($"Published event: {logEntry.EventType} to {logEntry.ListenerCount} listeners");
        }
        
        private void LogDebug(string message)
        {
            if (enableDebugLogs)
            {
                Debug.Log($"[{name}] {message}");
            }
        }
        
        private void OnDisable()
        {
            Clear();
        }
    }
    
    /// <summary>
    /// 事件日志条目
    /// </summary>
    [Serializable]
    public class EventLogEntry
    {
        public DateTime Timestamp;
        public string EventType;
        public string EventData;
        public int ListenerCount;
        
        public override string ToString()
        {
            return $"[{Timestamp:HH:mm:ss.fff}] {EventType} -> {ListenerCount} listeners: {EventData}";
        }
    }
    
    /// <summary>
    /// 无参数事件通道
    /// </summary>
    public abstract class VoidEventChannel : ScriptableObject
    {
        private readonly List<Action> _listeners = new List<Action>();
        private readonly List<WeakReference> _weakListeners = new List<WeakReference>();
        
        [Header("Debug")]
        [SerializeField] private bool enableDebugLogs = false;
        
        public void Subscribe(Action listener)
        {
            if (listener == null) return;
            
            if (!_listeners.Contains(listener))
            {
                _listeners.Add(listener);
                LogDebug($"Subscribed listener: {listener.Method.Name}");
            }
        }
        
        public void Unsubscribe(Action listener)
        {
            if (listener == null) return;
            
            if (_listeners.Remove(listener))
            {
                LogDebug($"Unsubscribed listener: {listener.Method.Name}");
            }
        }
        
        public void SubscribeWeak(Action listener)
        {
            if (listener == null) return;
            
            _weakListeners.Add(new WeakReference(listener));
            LogDebug($"Subscribed weak listener: {listener.Method.Name}");
        }
        
        public void Publish()
        {
            LogDebug($"Publishing event to {ListenerCount} listeners");
            
            // 调用强引用监听器
            for (int i = _listeners.Count - 1; i >= 0; i--)
            {
                try
                {
                    _listeners[i]?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[VoidEventChannel] Error in listener {_listeners[i]?.Method.Name}: {ex.Message}");
                }
            }
            
            // 调用弱引用监听器并清理失效引用
            for (int i = _weakListeners.Count - 1; i >= 0; i--)
            {
                var weakRef = _weakListeners[i];
                if (weakRef.Target is Action listener)
                {
                    try
                    {
                        listener.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[VoidEventChannel] Error in weak listener {listener.Method.Name}: {ex.Message}");
                    }
                }
                else
                {
                    _weakListeners.RemoveAt(i);
                }
            }
        }
        
        public void Clear()
        {
            _listeners.Clear();
            _weakListeners.Clear();
            LogDebug("Cleared all listeners");
        }
        
        public int ListenerCount => _listeners.Count + _weakListeners.Count;
        
        private void LogDebug(string message)
        {
            if (enableDebugLogs)
            {
                Debug.Log($"[{name}] {message}");
            }
        }
        
        private void OnDisable()
        {
            Clear();
        }
    }
}
