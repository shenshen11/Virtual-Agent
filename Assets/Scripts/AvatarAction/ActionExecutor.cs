using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;

namespace VRPerception.AvatarAction
{
    /// <summary>
    /// 动作执行器，负责执行命令队列和状态管理
    /// </summary>
    public class ActionExecutor : MonoBehaviour
    {
        [Header("Components")]
        [SerializeField] private SceneOracle sceneOracle;
        [SerializeField] private Camera headCamera;
        [SerializeField] private Transform avatarTransform;
        
        [Header("Settings")]
        [SerializeField] private int maxQueueSize = 100;
        [SerializeField] private int maxConcurrentNonBlocking = 3;
        [SerializeField] private float defaultTimeout = 10f;
        [SerializeField] private int defaultRetries = 2;
        
        [Header("Movement")]
        [SerializeField] private float moveSpeed = 2f;
        [SerializeField] private float rotateSpeed = 90f;
        [SerializeField] private CharacterController characterController;
        
        [Header("Event Bus")]
        [SerializeField] private EventBusManager eventBus;
        
        private readonly Queue<ExecutionCommand> _commandQueue = new Queue<ExecutionCommand>();
        private readonly Dictionary<string, ExecutionCommand> _activeCommands = new Dictionary<string, ExecutionCommand>();
        private readonly HashSet<string> _mutexResources = new HashSet<string>();
        
        private ExecutorState _currentState = ExecutorState.Idle;
        private ExecutionCommand _currentBlockingCommand;
        private int _activeNonBlockingCount = 0;
        
        public ExecutorState CurrentState => _currentState;
        public int QueuedCommands => _commandQueue.Count;
        public int ActiveCommands => _activeCommands.Count;
        public bool IsBusy => _currentState != ExecutorState.Idle;
        
        private void Awake()
        {
            if (eventBus == null)
                eventBus = EventBusManager.Instance;
            
            if (sceneOracle == null)
                sceneOracle = GetComponent<SceneOracle>();
            
            if (headCamera == null)
                headCamera = Camera.main;
            
            if (avatarTransform == null)
                avatarTransform = transform;
            
            if (characterController == null)
                characterController = GetComponent<CharacterController>();
        }
        
        private void Start()
        {
            SubscribeToEvents();
            ChangeState(ExecutorState.Idle);
        }
        
        private void OnDestroy()
        {
            UnsubscribeFromEvents();
            CancelAllCommands();
        }
        
        private void Update()
        {
            ProcessQueue();
            UpdateActiveCommands();
        }
        
        /// <summary>
        /// 订阅事件
        /// </summary>
        private void SubscribeToEvents()
        {
            if (eventBus?.ActionPlanReceived != null)
            {
                eventBus.ActionPlanReceived.Subscribe(OnActionPlanReceived);
            }
        }
        
        /// <summary>
        /// 取消订阅事件
        /// </summary>
        private void UnsubscribeFromEvents()
        {
            if (eventBus?.ActionPlanReceived != null)
            {
                eventBus.ActionPlanReceived.Unsubscribe(OnActionPlanReceived);
            }
        }
        
        /// <summary>
        /// 处理动作计划接收事件
        /// </summary>
        private void OnActionPlanReceived(ActionPlanReceivedEventData eventData)
        {
            if (eventData.actions != null)
            {
                foreach (var action in eventData.actions)
                {
                    EnqueueCommand(action);
                }
            }
        }
        
        /// <summary>
        /// 将命令加入队列
        /// </summary>
        public bool EnqueueCommand(ActionCommand command)
        {
            if (command == null)
            {
                Debug.LogWarning("[ActionExecutor] Null command ignored");
                return false;
            }
            
            if (_commandQueue.Count >= maxQueueSize)
            {
                Debug.LogWarning($"[ActionExecutor] Queue full, dropping command: {command.name}");
                return false;
            }
            
            var execCommand = new ExecutionCommand(command)
            {
                QueuedTime = DateTime.UtcNow
            };
            
            _commandQueue.Enqueue(execCommand);
            
            PublishCommandEvent(execCommand, CommandLifecycleState.Queued);
            
            Debug.Log($"[ActionExecutor] Queued command: {command.name} (ID: {command.id})");
            return true;
        }
        
        /// <summary>
        /// 处理命令队列
        /// </summary>
        private void ProcessQueue()
        {
            if (_commandQueue.Count == 0) return;
            
            // 如果有阻塞命令在执行，等待完成
            if (_currentBlockingCommand != null) return;
            
            var command = _commandQueue.Peek();
            
            // 检查资源互斥
            if (HasResourceConflict(command))
            {
                return; // 等待资源释放
            }
            
            // 检查并发限制
            if (!command.Command.wait && _activeNonBlockingCount >= maxConcurrentNonBlocking)
            {
                return; // 等待非阻塞命令完成
            }
            
            // 开始执行命令
            _commandQueue.Dequeue();
            StartCommandExecution(command);
        }
        
        /// <summary>
        /// 开始执行命令
        /// </summary>
        private void StartCommandExecution(ExecutionCommand command)
        {
            command.StartTime = DateTime.UtcNow;
            command.State = CommandExecutionState.Executing;
            
            _activeCommands[command.Command.id] = command;
            
            if (command.Command.wait)
            {
                _currentBlockingCommand = command;
                ChangeState(ExecutorState.ExecutingBlocking);
            }
            else
            {
                _activeNonBlockingCount++;
                if (_currentState == ExecutorState.Idle)
                {
                    ChangeState(ExecutorState.ExecutingNonBlocking);
                }
            }
            
            // 锁定资源
            LockResources(command);
            
            PublishCommandEvent(command, CommandLifecycleState.Started);
            
            // 启动执行协程
            StartCoroutine(ExecuteCommandCoroutine(command));
        }
        
        /// <summary>
        /// 执行命令协程
        /// </summary>
        private IEnumerator ExecuteCommandCoroutine(ExecutionCommand command)
        {
            string errorMessage = null;
            // Run inner coroutine safely; capture exceptions thrown during iteration
            yield return StartCoroutine(RunSafely(ExecuteSpecificCommand(command), ex => errorMessage = ex.Message));

            if (string.IsNullOrEmpty(errorMessage))
            {
                CompleteCommand(command, true);
            }
            else
            {
                Debug.LogError($"[ActionExecutor] Command execution failed: {command.Command.name}, Error: {errorMessage}");
                CompleteCommand(command, false, errorMessage);
            }
        }

        // Iterate an IEnumerator and capture exceptions without yielding inside a try/catch block
        private IEnumerator RunSafely(IEnumerator routine, Action<Exception> onException)
        {
            if (routine == null) yield break;

            while (true)
            {
                bool moved = false;
                object current = null;
                try
                {
                    moved = routine.MoveNext();
                    if (moved)
                    {
                        current = routine.Current;
                    }
                }
                catch (Exception ex)
                {
                    onException?.Invoke(ex);
                    yield break;
                }

                if (!moved) yield break;
                yield return current;
            }
        }
        
        /// <summary>
        /// 执行具体命令
        /// </summary>
        private IEnumerator ExecuteSpecificCommand(ExecutionCommand command)
        {
            var cmd = command.Command;
            
            switch (cmd.name)
            {
                case "camera_set_fov":
                    yield return ExecuteCameraSetFOV(cmd);
                    break;
                    
                case "head_look_at":
                    yield return ExecuteHeadLookAt(cmd);
                    break;
                    
                case "move_forward":
                    yield return ExecuteMoveForward(cmd);
                    break;
                    
                case "strafe":
                    yield return ExecuteStrafe(cmd);
                    break;
                    
                case "turn_yaw":
                    yield return ExecuteTurnYaw(cmd);
                    break;
                    
                case "set_texture_density":
                    yield return ExecuteSetTextureDensity(cmd);
                    break;
                    
                case "set_lighting":
                    yield return ExecuteSetLighting(cmd);
                    break;
                    
                case "place_object":
                    yield return ExecutePlaceObject(cmd);
                    break;
                    
                case "focus_target":
                    yield return ExecuteFocusTarget(cmd);
                    break;
                    
                case "snapshot":
                    yield return ExecuteSnapshot(cmd);
                    break;
                    
                default:
                    Debug.LogWarning($"[ActionExecutor] Unknown command: {cmd.name}");
                    break;
            }
        }
        
        /// <summary>
        /// 执行相机FOV设置
        /// </summary>
        private IEnumerator ExecuteCameraSetFOV(ActionCommand cmd)
        {
            if (headCamera == null)
            {
                throw new InvalidOperationException("Head camera not available");
            }
            
            var parameters = JsonUtility.FromJson<CameraSetFOVParams>(JsonUtility.ToJson(cmd.parameters));
            headCamera.fieldOfView = parameters.fov_deg;
            
            Debug.Log($"[ActionExecutor] Set camera FOV to {parameters.fov_deg}");
            yield return null;
        }
        
        /// <summary>
        /// 执行头部看向目标
        /// </summary>
        private IEnumerator ExecuteHeadLookAt(ActionCommand cmd)
        {
            if (headCamera == null)
            {
                throw new InvalidOperationException("Head camera not available");
            }
            
            var parameters = JsonUtility.FromJson<HeadLookAtParams>(JsonUtility.ToJson(cmd.parameters));
            Vector3 targetPosition;
            
            if (!string.IsNullOrEmpty(parameters.target))
            {
                // 使用SceneOracle解析目标
                var targetPos = sceneOracle?.ResolvePosition(parameters.target);
                if (!targetPos.HasValue)
                {
                    throw new InvalidOperationException($"Cannot resolve target: {parameters.target}");
                }
                targetPosition = targetPos.Value;
            }
            else if (parameters.position != null)
            {
                targetPosition = new Vector3(parameters.position.x, parameters.position.y, parameters.position.z);
            }
            else
            {
                throw new InvalidOperationException("No target specified for head_look_at");
            }
            
            // 平滑转向目标
            var startRotation = headCamera.transform.rotation;
            var direction = (targetPosition - headCamera.transform.position).normalized;
            var targetRotation = Quaternion.LookRotation(direction);
            
            float elapsed = 0f;
            float duration = 1f; // 1秒转向时间
            
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                var t = elapsed / duration;
                headCamera.transform.rotation = Quaternion.Slerp(startRotation, targetRotation, t);
                yield return null;
            }
            
            headCamera.transform.rotation = targetRotation;
            Debug.Log($"[ActionExecutor] Head looked at target: {targetPosition}");
        }
        
        /// <summary>
        /// 执行前进移动
        /// </summary>
        private IEnumerator ExecuteMoveForward(ActionCommand cmd)
        {
            var parameters = JsonUtility.FromJson<MoveForwardParams>(JsonUtility.ToJson(cmd.parameters));
            var distance = parameters.meters;
            var speed = parameters.speed > 0 ? parameters.speed : moveSpeed;
            
            var startPosition = avatarTransform.position;
            var direction = avatarTransform.forward;
            var targetPosition = startPosition + direction * distance;
            
            yield return StartCoroutine(MoveToPosition(targetPosition, speed));
            
            Debug.Log($"[ActionExecutor] Moved forward {distance}m");
        }
        
        /// <summary>
        /// 执行侧移
        /// </summary>
        private IEnumerator ExecuteStrafe(ActionCommand cmd)
        {
            var parameters = JsonUtility.FromJson<StrafeParams>(JsonUtility.ToJson(cmd.parameters));
            var distance = parameters.meters;
            var speed = parameters.speed > 0 ? parameters.speed : moveSpeed;
            var direction = parameters.direction == "left" ? -avatarTransform.right : avatarTransform.right;
            
            var startPosition = avatarTransform.position;
            var targetPosition = startPosition + direction * distance;
            
            yield return StartCoroutine(MoveToPosition(targetPosition, speed));
            
            Debug.Log($"[ActionExecutor] Strafed {parameters.direction} {distance}m");
        }
        
        /// <summary>
        /// 执行转向
        /// </summary>
        private IEnumerator ExecuteTurnYaw(ActionCommand cmd)
        {
            var parameters = JsonUtility.FromJson<TurnYawParams>(JsonUtility.ToJson(cmd.parameters));
            var angle = parameters.deg;
            var speed = parameters.speed > 0 ? parameters.speed : rotateSpeed;
            
            var startRotation = avatarTransform.rotation;
            var targetRotation = startRotation * Quaternion.Euler(0, angle, 0);
            
            float elapsed = 0f;
            float duration = Mathf.Abs(angle) / speed;
            
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                var t = elapsed / duration;
                avatarTransform.rotation = Quaternion.Slerp(startRotation, targetRotation, t);
                yield return null;
            }
            
            avatarTransform.rotation = targetRotation;
            Debug.Log($"[ActionExecutor] Turned {angle} degrees");
        }
        
        /// <summary>
        /// 移动到指定位置
        /// </summary>
        private IEnumerator MoveToPosition(Vector3 targetPosition, float speed)
        {
            if (characterController != null)
            {
                // 使用CharacterController移动
                while (Vector3.Distance(avatarTransform.position, targetPosition) > 0.1f)
                {
                    var direction = (targetPosition - avatarTransform.position).normalized;
                    var movement = direction * speed * Time.deltaTime;
                    characterController.Move(movement);
                    yield return null;
                }
            }
            else
            {
                // 直接设置Transform位置
                var startPosition = avatarTransform.position;
                float elapsed = 0f;
                float duration = Vector3.Distance(startPosition, targetPosition) / speed;
                
                while (elapsed < duration)
                {
                    elapsed += Time.deltaTime;
                    var t = elapsed / duration;
                    avatarTransform.position = Vector3.Lerp(startPosition, targetPosition, t);
                    yield return null;
                }
                
                avatarTransform.position = targetPosition;
            }
        }
        
        /// <summary>
        /// 执行快照
        /// </summary>
        private IEnumerator ExecuteSnapshot(ActionCommand cmd)
        {
            var parameters = JsonUtility.FromJson<SnapshotParams>(JsonUtility.ToJson(cmd.parameters));
            
            // 通过事件总线请求抓帧
            var requestId = Guid.NewGuid().ToString();
            var frameRequest = new FrameRequestedEventData
            {
                requestId = requestId,
                taskId = "snapshot",
                trialId = 0,
                requester = "ActionExecutor",
                timestamp = DateTime.UtcNow,
                options = new FrameCaptureOptions
                {
                    label = parameters.label
                }
            };
            
            eventBus.FrameRequested?.Publish(frameRequest);
            
            Debug.Log($"[ActionExecutor] Snapshot requested with label: {parameters.label}");
            yield return null;
        }
        
        // 其他命令的实现方法...
        private IEnumerator ExecuteSetTextureDensity(ActionCommand cmd) { yield return null; }
        private IEnumerator ExecuteSetLighting(ActionCommand cmd) { yield return null; }
        private IEnumerator ExecutePlaceObject(ActionCommand cmd) { yield return null; }
        private IEnumerator ExecuteFocusTarget(ActionCommand cmd) { yield return null; }
        
        /// <summary>
        /// 完成命令执行
        /// </summary>
        private void CompleteCommand(ExecutionCommand command, bool success, string errorMessage = null)
        {
            command.EndTime = DateTime.UtcNow;
            command.State = success ? CommandExecutionState.Completed : CommandExecutionState.Failed;
            command.ErrorMessage = errorMessage;
            
            // 释放资源
            UnlockResources(command);
            
            // 更新状态
            if (command == _currentBlockingCommand)
            {
                _currentBlockingCommand = null;
                ChangeState(_activeNonBlockingCount > 0 ? ExecutorState.ExecutingNonBlocking : ExecutorState.Idle);
            }
            else if (!command.Command.wait)
            {
                _activeNonBlockingCount--;
                if (_activeNonBlockingCount == 0 && _currentBlockingCommand == null)
                {
                    ChangeState(ExecutorState.Idle);
                }
            }
            
            _activeCommands.Remove(command.Command.id);
            
            var state = success ? CommandLifecycleState.Completed : CommandLifecycleState.Failed;
            PublishCommandEvent(command, state);
            
            Debug.Log($"[ActionExecutor] Command {(success ? "completed" : "failed")}: {command.Command.name}");
        }
        
        /// <summary>
        /// 检查资源冲突
        /// </summary>
        private bool HasResourceConflict(ExecutionCommand command)
        {
            var resources = GetRequiredResources(command.Command.name);
            return resources.Any(resource => _mutexResources.Contains(resource));
        }
        
        /// <summary>
        /// 锁定资源
        /// </summary>
        private void LockResources(ExecutionCommand command)
        {
            var resources = GetRequiredResources(command.Command.name);
            foreach (var resource in resources)
            {
                _mutexResources.Add(resource);
            }
        }
        
        /// <summary>
        /// 解锁资源
        /// </summary>
        private void UnlockResources(ExecutionCommand command)
        {
            var resources = GetRequiredResources(command.Command.name);
            foreach (var resource in resources)
            {
                _mutexResources.Remove(resource);
            }
        }
        
        /// <summary>
        /// 获取命令所需资源
        /// </summary>
        private string[] GetRequiredResources(string commandName)
        {
            return commandName switch
            {
                "camera_set_fov" => new[] { "camera" },
                "head_look_at" => new[] { "camera" },
                "move_forward" => new[] { "movement" },
                "strafe" => new[] { "movement" },
                "turn_yaw" => new[] { "movement" },
                "snapshot" => new[] { "camera" },
                _ => new string[0]
            };
        }
        
        /// <summary>
        /// 更新活动命令
        /// </summary>
        private void UpdateActiveCommands()
        {
            var now = DateTime.UtcNow;
            var commandsToTimeout = new List<ExecutionCommand>();
            
            foreach (var command in _activeCommands.Values)
            {
                var timeout = command.Command.timeoutMs > 0 ? command.Command.timeoutMs : (defaultTimeout * 1000);
                var elapsed = (now - command.StartTime).TotalMilliseconds;
                
                if (elapsed > timeout)
                {
                    commandsToTimeout.Add(command);
                }
            }
            
            foreach (var command in commandsToTimeout)
            {
                Debug.LogWarning($"[ActionExecutor] Command timeout: {command.Command.name}");
                CompleteCommand(command, false, "Timeout");
            }
        }
        
        /// <summary>
        /// 改变状态
        /// </summary>
        private void ChangeState(ExecutorState newState)
        {
            if (_currentState == newState) return;
            
            var previousState = _currentState;
            _currentState = newState;
            
            var stateData = new ExecutorStateEventData
            {
                executorId = gameObject.name,
                previousState = previousState,
                currentState = newState,
                timestamp = DateTime.UtcNow,
                reason = $"State changed from {previousState} to {newState}"
            };
            
            eventBus.ExecutorState?.Publish(stateData);
            
            Debug.Log($"[ActionExecutor] State changed: {previousState} -> {newState}");
        }
        
        /// <summary>
        /// 发布命令事件
        /// </summary>
        private void PublishCommandEvent(ExecutionCommand command, CommandLifecycleState state)
        {
            var eventData = new CommandLifecycleEventData
            {
                commandId = command.Command.id,
                commandName = command.Command.name,
                state = state,
                timestamp = DateTime.UtcNow,
                parameters = command.Command.parameters,
                errorMessage = command.ErrorMessage,
                executionTimeMs = command.EndTime.HasValue ? 
                    (long)(command.EndTime.Value - command.StartTime).TotalMilliseconds : 0
            };
            
            eventBus.CommandLifecycle?.Publish(eventData);
        }
        
        /// <summary>
        /// 取消所有命令
        /// </summary>
        private void CancelAllCommands()
        {
            _commandQueue.Clear();
            
            foreach (var command in _activeCommands.Values.ToList())
            {
                CompleteCommand(command, false, "Cancelled");
            }
            
            _mutexResources.Clear();
            _currentBlockingCommand = null;
            _activeNonBlockingCount = 0;
            ChangeState(ExecutorState.Idle);
        }
        
        /// <summary>
        /// 暂停执行
        /// </summary>
        public void Pause()
        {
            if (_currentState != ExecutorState.Error)
            {
                ChangeState(ExecutorState.Paused);
            }
        }
        
        /// <summary>
        /// 恢复执行
        /// </summary>
        public void Resume()
        {
            if (_currentState == ExecutorState.Paused)
            {
                ChangeState(_activeCommands.Count > 0 ? 
                    (_currentBlockingCommand != null ? ExecutorState.ExecutingBlocking : ExecutorState.ExecutingNonBlocking) : 
                    ExecutorState.Idle);
            }
        }
    }
    
    /// <summary>
    /// 执行命令包装器
    /// </summary>
    public class ExecutionCommand
    {
        public ActionCommand Command { get; }
        public DateTime QueuedTime { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public CommandExecutionState State { get; set; }
        public string ErrorMessage { get; set; }
        public int RetryCount { get; set; }
        
        public ExecutionCommand(ActionCommand command)
        {
            Command = command ?? throw new ArgumentNullException(nameof(command));
            State = CommandExecutionState.Queued;
        }
    }
    
    /// <summary>
    /// 命令执行状态
    /// </summary>
    public enum CommandExecutionState
    {
        Queued,
        Executing,
        Completed,
        Failed,
        Cancelled
    }
    
    // 命令参数结构
    [Serializable] public class CameraSetFOVParams { public float fov_deg; }
    [Serializable] public class HeadLookAtParams { public string target; public PositionParams position; }
    [Serializable] public class PositionParams { public float x, y, z; }
    [Serializable] public class MoveForwardParams { public float meters; public float speed; }
    [Serializable] public class StrafeParams { public float meters; public string direction; public float speed; }
    [Serializable] public class TurnYawParams { public float deg; public float speed; }
    [Serializable] public class SnapshotParams { public string label; }
}
