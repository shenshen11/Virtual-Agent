# Unity 播放后执行全流程（Play → 结束）

本文梳理当你点击 Unity 播放键后，框架从启动到完成一个或多个 Trial 的完整执行路径，覆盖模块初始化顺序、事件流、MLLM 与 Human 两种模式、动作闭环与日志评测。文中所有脚本均可点击跳转查看源码。

---

## 0. 参与脚本清单（分层）

- 感知层（Perception）
  - [`PerceptionSystem.cs`](Assets/Scripts/Perception/PerceptionSystem.cs)
  - [`StimulusCapture.cs`](Assets/Scripts/Perception/StimulusCapture.cs)
  - [`ILLMProvider.cs`](Assets/Scripts/Perception/ILLMProvider.cs)
  - [`ProviderRegistry.cs`](Assets/Scripts/Perception/ProviderRegistry.cs)
  - [`ProviderRouter.cs`](Assets/Scripts/Perception/ProviderRouter.cs)
  - [`PromptTemplates.cs`](Assets/Scripts/Perception/PromptTemplates.cs)
  - Provider 实现：[`OpenAIProvider.cs`](Assets/Scripts/Perception/Providers/OpenAIProvider.cs)、[`AnthropicProvider.cs`](Assets/Scripts/Perception/Providers/AnthropicProvider.cs)、[`OllamaProvider.cs`](Assets/Scripts/Perception/Providers/OllamaProvider.cs)、[`CustomHttpProvider.cs`](Assets/Scripts/Perception/Providers/CustomHttpProvider.cs)、[`VLLMProvider.cs`](Assets/Scripts/Perception/Providers/VLLMProvider.cs)
  - 传输：[`HttpConnector.cs`](Assets/Scripts/Perception/HttpConnector.cs)；WebSocket：[`Connector.cs`](Assets/Scripts/Connector.cs)
- 执行器层（AvatarAction）
  - [`ActionExecutor.cs`](Assets/Scripts/AvatarAction/ActionExecutor.cs)
  - [`SceneOracle.cs`](Assets/Scripts/AvatarAction/SceneOracle.cs)
- 任务层（Tasks）
  - [`TaskRunner.cs`](Assets/Scripts/Tasks/TaskRunner.cs)
  - [`ITask.cs`](Assets/Scripts/Tasks/ITask.cs)
  - 任务实现：[`DistanceCompressionTask.cs`](Assets/Scripts/Tasks/DistanceCompressionTask.cs)、[`SemanticSizeBiasTask.cs`](Assets/Scripts/Tasks/SemanticSizeBiasTask.cs)
  - 布置/环境：[`ExperimentSceneManager.cs`](Assets/Scripts/Tasks/ExperimentSceneManager.cs)、[`ObjectPlacer.cs`](Assets/Scripts/Tasks/ObjectPlacer.cs)
- 事件与基础设施（Infra）
  - [`EventBusManager.cs`](Assets/Scripts/Infra/EventBus/EventBusManager.cs)
  - [`EventChannel.cs`](Assets/Scripts/Infra/EventBus/EventChannel.cs) / [`EventChannels.cs`](Assets/Scripts/Infra/EventBus/EventChannels.cs) / [`EventData.cs`](Assets/Scripts/Infra/EventBus/EventData.cs)
  - [`EventBusBootstrap.cs`](Assets/Scripts/Infra/EventBus/EventBusBootstrap.cs)
  - 日志评测：[`ExperimentLogger.cs`](Assets/Scripts/Infra/ExperimentLogger.cs)、[`Evaluator.cs`](Assets/Scripts/Infra/Evaluator.cs)
- UI 层（UI）
  - [`ExperimentUI.cs`](Assets/Scripts/UI/ExperimentUI.cs)
  - [`HumanInputHandler.cs`](Assets/Scripts/UI/HumanInputHandler.cs)

---

## 1. 播放后脚本生命周期初始化（Awake → OnEnable → Start）

初始执行顺序会受“脚本执行顺序设置”和 GameObject 在层级中的先后影响。一般情况下（未自定义执行顺序）推荐挂载布局使以下初始化大致按此发生：

1) 事件系统
- [`EventBusManager.cs`](Assets/Scripts/Infra/EventBus/EventBusManager.cs) Awake
  - 建立单例、可选 DontDestroyOnLoad
  - 初始化/缓存各事件通道；Start 时注册全局日志转发
- [`EventBusBootstrap.cs`](Assets/Scripts/Infra/EventBus/EventBusBootstrap.cs) Awake
  - 若场景未配置通道 ScriptableObject 资产，则运行时创建，确保“开箱即用”

2) 感知与 Provider
- [`StimulusCapture.cs`](Assets/Scripts/Perception/StimulusCapture.cs) Awake/Start
  - 绑定 EventBus、查找头部相机、参数校验
- [`PerceptionSystem.cs`](Assets/Scripts/Perception/PerceptionSystem.cs) Awake/Start
  - 获取 Stimulus/Registry/Router 引用，订阅 FrameRequested，配置并发/限速
- [`ProviderRegistry.cs`](Assets/Scripts/Perception/ProviderRegistry.cs) Awake
  - 读取配置并构建 Provider 列表（OpenAI/Anthropic/Ollama/vLLM/自定义等）
- [`ProviderRouter.cs`](Assets/Scripts/Perception/ProviderRouter.cs) Awake
  - 设置路由/回退策略、速率限制等

3) 执行器/环境/任务
- [`ActionExecutor.cs`](Assets/Scripts/AvatarAction/ActionExecutor.cs) Awake/Start
  - 绑定 SceneOracle/HeadCamera/Avatar/CharacterController，订阅 ActionPlanReceived，置为 Idle
- [`ExperimentSceneManager.cs`](Assets/Scripts/Tasks/ExperimentSceneManager.cs) Awake
  - 准备地面/走廊/光照等预置材质
- [`TaskRunner.cs`](Assets/Scripts/Tasks/TaskRunner.cs) Awake/Start
  - 组装上下文引用；若 autoRun=true，则启动 RunAsync

4) 日志评测与 UI
- [`ExperimentLogger.cs`](Assets/Scripts/Infra/ExperimentLogger.cs) Awake/OnEnable
  - 创建会话目录/JSONL 文件；订阅 Inference/ActionPlan/Metric/Error/Trial/FrameCaptured
- [`Evaluator.cs`](Assets/Scripts/Infra/Evaluator.cs) OnEnable（若 autoSubscribe=true）
- [`ExperimentUI.cs`](Assets/Scripts/UI/ExperimentUI.cs) Awake/OnEnable
  - 反射读取/设置 TaskRunner 参数；订阅 Trial 与 Error 展示状态
- [`HumanInputHandler.cs`](Assets/Scripts/UI/HumanInputHandler.cs) OnEnable
  - 订阅 TrialLifecycle，等待 WaitingForInput 时弹出表单

---

## 2. TaskRunner 启动试验循环

当 [`TaskRunner.cs`](Assets/Scripts/Tasks/TaskRunner.cs) 的 autoRun 打开，Start 内调用 RunAsync，按以下步骤循环执行多个 Trial：

1) 任务创建与试次构建
- 根据 TaskMode 实例化具体 ITask（如 [`DistanceCompressionTask.cs`](Assets/Scripts/Tasks/DistanceCompressionTask.cs) 或 [`SemanticSizeBiasTask.cs`](Assets/Scripts/Tasks/SemanticSizeBiasTask.cs)）
- 调用 BuildTrials(seed) 生成 TrialSpec 列表（可限制 maxTrials）

2) 单个 Trial 生命周期（每条 Trial 重复）
- 发布 TrialLifecycle: Initialized
- 调用 OnBeforeTrialAsync：
  - 环境布置：[`ExperimentSceneManager.cs`](Assets/Scripts/Tasks/ExperimentSceneManager.cs) 切换开阔地/走廊、光照/纹理，按需放置遮挡
  - 目标/对象放置：[`ObjectPlacer.cs`](Assets/Scripts/Tasks/ObjectPlacer.cs) 生成目标或对象对
  - 设置相机 FOV（供被试预览）
  - 发布 TrialLifecycle: SceneSetup → Started

3) 被试模式分支
- MLLM 模式（SubjectMode.MLLM）
  - TaskRunner 调用 [`PerceptionSystem.cs`](Assets/Scripts/Perception/PerceptionSystem.cs).RequestInferenceAsync(...)：
    1. 构造 FrameCaptureOptions（FOV/分辨率/质量）
    2. 发布 FrameRequested → [`StimulusCapture.cs`](Assets/Scripts/Perception/StimulusCapture.cs) 捕获图像并编码 Base64，采集元数据
    3. 发布 FrameCaptured 返回给 PerceptionSystem
    4. 由 [`ProviderRouter.cs`](Assets/Scripts/Perception/ProviderRouter.cs) 路由到某 Provider（如 [`OpenAIProvider.cs`](Assets/Scripts/Perception/Providers/OpenAIProvider.cs)）
    5. Provider 发送 HTTP 请求（可能使用 [`HttpConnector.cs`](Assets/Scripts/Perception/HttpConnector.cs)），超时/重试由连接器与 Provider 负责
    6. Provider 返回响应：
       - inference：包含 answer 与 confidence
       - action_plan：包含动作原语序列
    7. PerceptionSystem 发布对应事件：InferenceReceived 或 ActionPlanReceived
    8. 若为 action_plan 且 TaskRunner 开启动作闭环：等待下一次 inference 作为最终答案
- Human 模式（SubjectMode.Human）
  - 发布 TrialLifecycle: WaitingForInput
  - [`HumanInputHandler.cs`](Assets/Scripts/UI/HumanInputHandler.cs) 弹出 UI，输入后主动发布 InferenceReceived（providerId="human"）

4) 评测与记录
- TaskRunner 调用 ITask.Evaluate(...) 计算误差/正确性/置信度等
- 发布 TrialLifecycle: Completed（包含 trialConfig 与 results）
- [`ExperimentLogger.cs`](Assets/Scripts/Infra/ExperimentLogger.cs) 持续记录 JSONL、截图，完成后输出 CSV 汇总（已支持 distance_compression）
- [`Evaluator.cs`](Assets/Scripts/Infra/Evaluator.cs) 可在 Completed 做二次评测/补算

5) 循环下一 Trial，直至达到 maxTrials 或被 CancelRun 中断

---

## 3. 动作计划闭环（ActionExecutor）

当模型返回 action_plan 时，[`ActionExecutor.cs`](Assets/Scripts/AvatarAction/ActionExecutor.cs) 处理流程如下：

1) 订阅与入队
- 接收 `ActionPlanReceived`，将每个 `ActionCommand` 入队（包含 id/name/params/wait/timeout/retries）

2) 调度与互斥
- 队列调度：wait=true 的阻塞命令串行执行；wait=false 的非阻塞命令可并发（受 maxConcurrentNonBlocking 限制）
- 互斥资源：如 camera/movement，避免相机与位姿冲突

3) 执行与事件
- 启动命令协程，发布 CommandLifecycle: Started
- 超时监控：超过 timeoutMs 标记失败；可按策略重试
- 完成/失败后：释放资源，发布 CommandLifecycle: Completed/Failed，并更新 ExecutorState

4) 典型原语
- camera_set_fov：设置相机 FOV
- head_look_at：使用 [`SceneOracle.cs`](Assets/Scripts/AvatarAction/SceneOracle.cs) 将目标名解析为坐标，平滑朝向
- move_forward/strafe/turn_yaw：移动与转向（支持 CharacterController 或直接插值 Transform）
- snapshot：通过 EventBus 发布 FrameRequested 触发一次抓帧，形成“动作→观察”闭环

---

## 4. 事件与日志时间线（按先后）

1) 初始化期
- EventBusManager.Awake → InitializeChannels
- EventBusBootstrap.Awake → Ensure Channels
- StimulusCapture.Awake/Start → Validate
- PerceptionSystem.Start → Subscribe FrameRequested
- ActionExecutor.Start → Subscribe ActionPlanReceived
- ExperimentLogger.OnEnable → Subscribe X
- TaskRunner.Start → autoRun? RunAsync

2) 单次推理（MLLM）
- TaskRunner 发起 RequestInferenceAsync
- PerceptionSystem 发布 FrameRequested
- StimulusCapture 抓帧 → 发布 FrameCaptured
- ProviderRouter → Provider（HTTP/SSE/…）
- PerceptionSystem 发布 InferenceReceived 或 ActionPlanReceived
- 若 ActionPlan：ActionExecutor 执行 → snapshot → 再次 FrameRequested → …
- InferenceReceived → TaskRunner 评测 → Trial Completed
- ExperimentLogger 写 JSONL / 截图 / CSV

3) 人类模式
- Trial WaitingForInput
- HumanInputHandler 提交 → 发布 InferenceReceived
- TaskRunner 评测 → Trial Completed

---

## 5. 错误与超时回退

- 抓帧失败/超时：[`StimulusCapture.cs`](Assets/Scripts/Perception/StimulusCapture.cs) 发布错误事件；PerceptionSystem 记录并回退
- Provider 请求失败：[`HttpConnector.cs`](Assets/Scripts/Perception/HttpConnector.cs) 408/429/5xx 指数退避重试，发布 ConnectionState/错误
- 动作执行超时：[`ActionExecutor.cs`](Assets/Scripts/AvatarAction/ActionExecutor.cs) 监控并 Complete 失败，资源释放，状态更新
- 全局错误：[`EventBusManager.cs`](Assets/Scripts/Infra/EventBus/EventBusManager.cs) 将 Unity 日志转发为 ErrorEventData，便于统一记录

---

## 6. 性能与并发控制（要点）

- 抓帧限速：[`PerceptionSystem.cs`](Assets/Scripts/Perception/PerceptionSystem.cs) 的 maxFPS/节流
- 请求并发：SemaphoreSlim 控制 maxConcurrentRequests
- 日志批量落盘：[`ExperimentLogger.cs`](Assets/Scripts/Infra/ExperimentLogger.cs) buffer + flushEveryNLines
- Connector 重试回退：初始延迟/指数退避/抖动上限，保护 P95

---

## 7. 退出与收尾

- 试验结束：TaskRunner 取消或完成全部 Trial
- 取消当前命令：[`ActionExecutor.cs`](Assets/Scripts/AvatarAction/ActionExecutor.cs) 清理队列与互斥资源，状态回到 Idle
- 组件 OnDisable/OnDestroy：
  - Logger Flush JSONL、输出 CSV
  - EventBus 取消订阅
  - Provider/Connector 释放请求（若需要）

---

## 8. 最小化验证步骤

1) 在 PerceptionRig 上启用 TaskRunner.autoRun，选择 TaskMode=DistanceCompression，Subject=MLLM
2) 点击播放后，观察 Console 与 VRP_Logs 下的 JSONL/截图/CSV
3) 切换 Subject=Human，点击播放，待 WaitingForInput 时在 HumanInputHandler 面板提交，检查日志与评测

---

若需更详细的脚本功能与相互依赖，请参阅：[`Scripts_Overview.md`](Assets/Scripts/Scripts_Overview.md)