# VR 感知对比实验 - 脚本功能与调用关系总览

本文档汇总工程中核心脚本的职责与相互调用关系，帮助快速理解“采样/推理/动作/任务/事件/日志/UI”的整体闭环。

更新时间：UTC 2025-09-28

---

## 1. 架构鸟瞰（7+1 子系统）

- 感知（Perception）
  - 抓帧并带元数据 → 发送到 LLM Provider → 收到 inference 或 action_plan
  - 关键脚本：[`PerceptionSystem.cs`](Assets/Scripts/Perception/PerceptionSystem.cs)、[`StimulusCapture.cs`](Assets/Scripts/Perception/StimulusCapture.cs)、[`ILLMProvider.cs`](Assets/Scripts/Perception/ILLMProvider.cs)
- 执行器（AvatarAction）
  - 将 action_plan 里的原子动作交由执行器顺序执行，带超时/重试/互斥资源
  - 关键脚本：[`ActionExecutor.cs`](Assets/Scripts/AvatarAction/ActionExecutor.cs)、[`SceneOracle.cs`](Assets/Scripts/AvatarAction/SceneOracle.cs)
- 任务引擎（Tasks）
  - 组织 Trial 生命周期（布置→采样→推理→评测→记录→清理）
  - 关键脚本：[`TaskRunner.cs`](Assets/Scripts/Tasks/TaskRunner.cs)、[`ITask.cs`](Assets/Scripts/Tasks/ITask.cs)
  - 示例任务：[`DistanceCompressionTask.cs`](Assets/Scripts/Tasks/DistanceCompressionTask.cs)、[`SemanticSizeBiasTask.cs`](Assets/Scripts/Tasks/SemanticSizeBiasTask.cs)
- 事件总线（Infra/EventBus）
  - ScriptableObject 事件通道，模块解耦
  - 关键脚本：[`EventBusManager.cs`](Assets/Scripts/Infra/EventBus/EventBusManager.cs)、[`EventChannel.cs`](Assets/Scripts/Infra/EventBus/EventChannel.cs)、[`EventChannels.cs`](Assets/Scripts/Infra/EventBus/EventChannels.cs)、[`EventData.cs`](Assets/Scripts/Infra/EventBus/EventData.cs)、[`EventBusBootstrap.cs`](Assets/Scripts/Infra/EventBus/EventBusBootstrap.cs)
- 日志与评测（Infra）
  - JSONL、CSV、截图、性能指标；任务级评测工具
  - 关键脚本：[`ExperimentLogger.cs`](Assets/Scripts/Infra/ExperimentLogger.cs)、[`Evaluator.cs`](Assets/Scripts/Infra/Evaluator.cs)
- 传输与 Provider（Perception/Providers）
  - 多后端 Provider 适配；HTTP/WS 传输；Prompt 模板统一
  - 关键脚本：[`ProviderRegistry.cs`](Assets/Scripts/Perception/ProviderRegistry.cs)、[`ProviderRouter.cs`](Assets/Scripts/Perception/ProviderRouter.cs)、[`PromptTemplates.cs`](Assets/Scripts/Perception/PromptTemplates.cs)
  - 传输：[`HttpConnector.cs`](Assets/Scripts/Perception/HttpConnector.cs)、[`Connector.cs`](Assets/Scripts/Connector.cs)
  - Provider 实现：[`OpenAIProvider.cs`](Assets/Scripts/Perception/Providers/OpenAIProvider.cs)、[`AnthropicProvider.cs`](Assets/Scripts/Perception/Providers/AnthropicProvider.cs)、[`OllamaProvider.cs`](Assets/Scripts/Perception/Providers/OllamaProvider.cs)、[`CustomHttpProvider.cs`](Assets/Scripts/Perception/Providers/CustomHttpProvider.cs)、[`VLLMProvider.cs`](Assets/Scripts/Perception/Providers/VLLMProvider.cs)
- UI（UI）
  - 运行控制面板与人类被试输入
  - 关键脚本：[`ExperimentUI.cs`](Assets/Scripts/UI/ExperimentUI.cs)、[`HumanInputHandler.cs`](Assets/Scripts/UI/HumanInputHandler.cs)

---

## 2. 端到端时序（MLLM 模式）

1) 任务引擎发起 Trial
   - [`TaskRunner.cs`](Assets/Scripts/Tasks/TaskRunner.cs) 调用 [`PerceptionSystem.cs`](Assets/Scripts/Perception/PerceptionSystem.cs) 的一次推理请求（包含系统/任务提示、工具定义、FOV 捕获参数）
2) 抓帧与元数据
   - [`PerceptionSystem.cs`](Assets/Scripts/Perception/PerceptionSystem.cs) 通过事件总线向 [`StimulusCapture.cs`](Assets/Scripts/Perception/StimulusCapture.cs) 发出抓帧请求，并等待 [`FrameCapturedEventData`](Assets/Scripts/Infra/EventBus/EventData.cs) 回传（带 Base64 图像与 Camera/FOV/姿态等）
3) Provider 调用
   - [`ProviderRouter.cs`](Assets/Scripts/Perception/ProviderRouter.cs) 路由到某个 Provider（如 [`OpenAIProvider.cs`](Assets/Scripts/Perception/Providers/OpenAIProvider.cs)），使用 [`HttpConnector.cs`](Assets/Scripts/Perception/HttpConnector.cs) 发起 HTTP，或通过兼容接口直连
4) 结果返回
   - Provider 返回 inference 或 action_plan
   - [`PerceptionSystem.cs`](Assets/Scripts/Perception/PerceptionSystem.cs) 向事件总线发布 [`InferenceReceivedEventData`](Assets/Scripts/Infra/EventBus/EventData.cs) 或 [`ActionPlanReceivedEventData`](Assets/Scripts/Infra/EventBus/EventData.cs)
5) 动作计划执行（可选）
   - [`ActionExecutor.cs`](Assets/Scripts/AvatarAction/ActionExecutor.cs) 订阅 action_plan，逐条执行（如 head_look_at/move/turn/snapshot）；执行期间发布命令生命周期事件与执行器状态事件
   - `snapshot` 将再次触发抓帧 → 进一步推理闭环
6) 评测与记录
   - [`TaskRunner.cs`](Assets/Scripts/Tasks/TaskRunner.cs) 收到 inference 后调用任务的 Evaluate 进行评测
   - [`ExperimentLogger.cs`](Assets/Scripts/Infra/ExperimentLogger.cs) 订阅事件写 JSONL/CSV/截图；[`Evaluator.cs`](Assets/Scripts/Infra/Evaluator.cs) 可订阅 Completed 做二次评测/纠偏

---

## 3. 端到端时序（Human 模式）

1) [`TaskRunner.cs`](Assets/Scripts/Tasks/TaskRunner.cs) 切换 SubjectMode=Human
2) Trial 运行至等待阶段时，发布 `WaitingForInput`（通过状态/文案在 UI 显示）
3) [`HumanInputHandler.cs`](Assets/Scripts/UI/HumanInputHandler.cs) 弹出输入表单：
   - Distance Compression：输入 `distance_m` 与 `confidence`
   - Semantic Size Bias：选择 `A/B` 与 `confidence`
   - 提交后人工构造 [`InferenceReceivedEventData`](Assets/Scripts/Infra/EventBus/EventData.cs) 注入流水线，后续评测/记录一致

---

## 4. 目录与脚本职责

### 4.1 AvatarAction

- [`AvatarAction/ActionExecutor.cs`](Assets/Scripts/AvatarAction/ActionExecutor.cs)
  - 职责：原子动作命令队列与状态机；互斥资源；超时/重试；命令事件发布；执行器状态发布
  - 订阅：`ActionPlanReceived`
  - 发布：`CommandLifecycle`（Queued/Started/Completed/Failed）、`ExecutorState`
  - 依赖：[`SceneOracle.cs`](Assets/Scripts/AvatarAction/SceneOracle.cs)、`Camera`、`CharacterController`、[`EventBusManager.cs`](Assets/Scripts/Infra/EventBus/EventBusManager.cs)
  - 支持原语：`camera_set_fov` / `head_look_at` / `move_forward` / `strafe` / `turn_yaw` / `set_texture_density` / `set_lighting` / `place_object` / `focus_target` / `snapshot`

- [`AvatarAction/SceneOracle.cs`](Assets/Scripts/AvatarAction/SceneOracle.cs)
  - 职责：将“字符串目标名/别名”解析为场景目标对象或世界坐标（供 `head_look_at` 等使用）
  - 能力：名称/标签索引、可见性/距离筛选、模糊匹配策略（具体实现可扩展）

### 4.2 Perception

- [`Perception/PerceptionSystem.cs`](Assets/Scripts/Perception/PerceptionSystem.cs)
  - 职责：桥接 Stimulus 与 LLM Provider；封装“一次抓帧+推理/动作”的编排
  - 输入：任务 Id/Trial Id、系统/任务提示、工具定义、抓帧参数（FOV/分辨率）
  - 与事件总线交互：发布 `FrameRequested`，等待 `FrameCaptured`；发布 `InferenceReceived` / `ActionPlanReceived`

- [`Perception/StimulusCapture.cs`](Assets/Scripts/Perception/StimulusCapture.cs)
  - 职责：从头部相机抓帧并编码为 Base64（JPEG/PNG），收集 FOV/姿态/分辨率等元数据
  - 输入：`FrameRequestedEventData`
  - 输出：`FrameCapturedEventData`，并发布性能指标（抓帧耗时等）

- [`Perception/ILLMProvider.cs`](Assets/Scripts/Perception/ILLMProvider.cs)
  - 定义：Provider 抽象与基元数据结构（`LLMRequest/LLMResponse`、`ToolSpec`、`ActionCommand`、`FrameMetadata` 等）

- [`Perception/ProviderRegistry.cs`](Assets/Scripts/Perception/ProviderRegistry.cs)
  - 职责：基于配置实例化各 Provider，实现健康检查与可用性管理

- [`Perception/ProviderRouter.cs`](Assets/Scripts/Perception/ProviderRouter.cs)
  - 职责：在可用 Provider 中进行路由与回退（策略可依据健康度/延迟/配额）

- [`Perception/PromptTemplates.cs`](Assets/Scripts/Perception/PromptTemplates.cs)
  - 职责：统一 System Prompt/Task Prompt 与工具定义模板，尽量保证多后端一致性及“ONLY JSON”输出约束

- Provider 实现（调用 OpenAI/Anthropic/Ollama/vLLM/自定义 HTTP 等）：
  - [`Perception/Providers/OpenAIProvider.cs`](Assets/Scripts/Perception/Providers/OpenAIProvider.cs)
  - [`Perception/Providers/AnthropicProvider.cs`](Assets/Scripts/Perception/Providers/AnthropicProvider.cs)
  - [`Perception/Providers/OllamaProvider.cs`](Assets/Scripts/Perception/Providers/OllamaProvider.cs)
  - [`Perception/Providers/CustomHttpProvider.cs`](Assets/Scripts/Perception/Providers/CustomHttpProvider.cs)
  - [`Perception/Providers/VLLMProvider.cs`](Assets/Scripts/Perception/Providers/VLLMProvider.cs)

- 传输：
  - [`Perception/HttpConnector.cs`](Assets/Scripts/Perception/HttpConnector.cs)：统一 POST JSON/超时/重试/错误事件/性能指标
  - [`Connector.cs`](Assets/Scripts/Connector.cs)：通用 WebSocket 连接器（心跳/重连/二进制发送，WS 路径可按需接入）

### 4.3 Tasks

- [`Tasks/ITask.cs`](Assets/Scripts/Tasks/ITask.cs)
  - 定义：任务接口（BuildTrials/GetSystemPrompt/GetTools/OnBeforeTrialAsync/OnAfterTrialAsync/Evaluate），以及通用 `TrialSpec/TrialEvaluation/TaskRunnerContext`

- [`Tasks/TaskRunner.cs`](Assets/Scripts/Tasks/TaskRunner.cs)
  - 职责：任务生命周期管理与 Trial 循环；调用 PerceptionSystem；集成人类/MLLM 两模式
  - 发布：`TrialLifecycle`（Initialized/SceneSetup/Started/WaitingForInput/Completed/Failed/Cancelled）

- [`Tasks/DistanceCompressionTask.cs`](Assets/Scripts/Tasks/DistanceCompressionTask.cs)
  - 职责：距离估计任务；布置环境/目标，设置相机 FOV，构造提示词，评测误差（绝对/相对）
  - 依赖辅助：[`ExperimentSceneManager.cs`](Assets/Scripts/Tasks/ExperimentSceneManager.cs)、[`ObjectPlacer.cs`](Assets/Scripts/Tasks/ObjectPlacer.cs)

- [`Tasks/SemanticSizeBiasTask.cs`](Assets/Scripts/Tasks/SemanticSizeBiasTask.cs)
  - 职责：语义大小偏差任务；放置对象对（A/B）、背景与尺寸关系；从模型输出解析 A/B 并评测正确性

- [`Tasks/ExperimentSceneManager.cs`](Assets/Scripts/Tasks/ExperimentSceneManager.cs)
  - 职责：动态布置开阔地/走廊、光照预设、纹理密度与遮挡体；发布环境变更事件

- [`Tasks/ObjectPlacer.cs`](Assets/Scripts/Tasks/ObjectPlacer.cs)
  - 职责：生成/清理原始 3D 基元（Cube/Sphere/Capsule 等），设置位置/缩放/材质

### 4.4 Infra / EventBus

- [`Infra/EventBus/EventBusManager.cs`](Assets/Scripts/Infra/EventBus/EventBusManager.cs)
  - 职责：集中管理所有事件通道的引用（FrameRequested/FrameCaptured/InferenceReceived/ActionPlanReceived/...）；提供 PublishError/PublishMetric 便捷方法
  - 单例模式；可跨场景保留；带全局错误日志转发

- [`Infra/EventBus/EventChannel.cs`](Assets/Scripts/Infra/EventBus/EventChannel.cs)
  - 职责：通用事件通道基类（订阅/发布/清理），基于 ScriptableObject

- [`Infra/EventBus/EventChannels/`](Assets/Scripts/Infra/EventBus/EventChannels/)、[`Infra/EventBus/EventData.cs`](Assets/Scripts/Infra/EventBus/EventData.cs)
  - 定义：所有具体通道类型与事件数据结构体（每个通道类在单独的文件中）

- [`Infra/EventBus/EventBusBootstrap.cs`](Assets/Scripts/Infra/EventBus/EventBusBootstrap.cs)
  - 职责：运行时若未配置 Resources/Events 的通道资产，则自动创建通道 ScriptableObject 实例（非持久化），保证“开箱即用”

### 4.5 Infra / Logging & Evaluation

- [`Infra/ExperimentLogger.cs`](Assets/Scripts/Infra/ExperimentLogger.cs)
  - 职责：订阅关键事件并写入 JSONL（inference/action_plan/metrics/error/trial）、可选保存截图、输出任务 CSV 汇总（现支持 distance_compression）
  - 输出目录：`Application.persistentDataPath/VRP_Logs/<session>/...`

- [`Infra/Evaluator.cs`](Assets/Scripts/Infra/Evaluator.cs)
  - 职责：静态评测工具（距离与大小偏差）；可按需订阅 Trial Completed 做二次评测或补算指标

### 4.6 UI

- [`UI/ExperimentUI.cs`](Assets/Scripts/UI/ExperimentUI.cs)
  - 职责：IMGUI 控制台；可设置任务模式/被试模式/随机种子/最大试次数；开始/取消运行；显示 Trial 状态与错误

- [`UI/HumanInputHandler.cs`](Assets/Scripts/UI/HumanInputHandler.cs)
  - 职责：在 `WaitingForInput` 时弹出输入面板，收集人类被试答案并注入 `InferenceReceived` 事件

---

## 5. 互相调用/事件关系（要点）

- 任务引擎 ⇄ 感知
  - [`TaskRunner.cs`](Assets/Scripts/Tasks/TaskRunner.cs) → 调用 → [`PerceptionSystem.cs`](Assets/Scripts/Perception/PerceptionSystem.cs)
  - Perception → 发布 `InferenceReceived/ActionPlanReceived` → TaskRunner 处理/评测/推进 Trial

- 感知 ⇄ 采样
  - [`PerceptionSystem.cs`](Assets/Scripts/Perception/PerceptionSystem.cs) → 发布 `FrameRequested` → [`StimulusCapture.cs`](Assets/Scripts/Perception/StimulusCapture.cs)
  - StimulusCapture → 发布 `FrameCaptured` → PerceptionSystem

- 感知 ⇄ Provider/Connector
  - [`ProviderRouter.cs`](Assets/Scripts/Perception/ProviderRouter.cs) → 选中具体 Provider（如 [`OpenAIProvider.cs`](Assets/Scripts/Perception/Providers/OpenAIProvider.cs)）
  - Provider → 使用 [`HttpConnector.cs`](Assets/Scripts/Perception/HttpConnector.cs) POST JSON 或自带 UnityWebRequest 调用

- 执行器 ⇄ 感知（闭环）
  - [`ActionExecutor.cs`](Assets/Scripts/AvatarAction/ActionExecutor.cs) 接收 `ActionPlanReceived`，执行 `snapshot` 时通过事件总线触发抓帧，Perception 再走一次推理

- UI ⇄ 任务/事件
  - [`ExperimentUI.cs`](Assets/Scripts/UI/ExperimentUI.cs) 反射修改 [`TaskRunner.cs`](Assets/Scripts/Tasks/TaskRunner.cs) 序列化字段（taskMode/subjectMode/seed/maxTrials），订阅 Trial/错误事件展示状态
  - [`HumanInputHandler.cs`](Assets/Scripts/UI/HumanInputHandler.cs) 在 `WaitingForInput` 时注入 `InferenceReceived` 等价事件

- 日志/评测 ⇄ 事件
  - [`ExperimentLogger.cs`](Assets/Scripts/Infra/ExperimentLogger.cs)、[`Evaluator.cs`](Assets/Scripts/Infra/Evaluator.cs) 通过订阅事件进行记录与评测

---

## 6. 事件通道（常用）

- `FrameRequestedEventChannel` / `FrameCapturedEventChannel`
- `InferenceReceivedEventChannel` / `ActionPlanReceivedEventChannel`
- `CommandLifecycleEventChannel` / `ExecutorStateEventChannel`
- `ConnectionStateEventChannel` / `ErrorEventChannel`
- `TrialLifecycleEventChannel` / `PerformanceMetricEventChannel` / `SceneObjectEventChannel`
- 详见：[`EventChannels/`](Assets/Scripts/Infra/EventBus/EventChannels/)、[`EventData.cs`](Assets/Scripts/Infra/EventBus/EventData.cs)

---

## 7. 快速使用建议

1) 在场景创建一个空对象 PerceptionRig，挂载：
   - 事件总线：[`EventBusManager.cs`](Assets/Scripts/Infra/EventBus/EventBusManager.cs)、[`EventBusBootstrap.cs`](Assets/Scripts/Infra/EventBus/EventBusBootstrap.cs)
   - 感知：[`StimulusCapture.cs`](Assets/Scripts/Perception/StimulusCapture.cs)、[`PerceptionSystem.cs`](Assets/Scripts/Perception/PerceptionSystem.cs)
   - Provider：[`ProviderRegistry.cs`](Assets/Scripts/Perception/ProviderRegistry.cs)、[`ProviderRouter.cs`](Assets/Scripts/Perception/ProviderRouter.cs)，（可选）[`HttpConnector.cs`](Assets/Scripts/Perception/HttpConnector.cs)
   - 执行器：[`ActionExecutor.cs`](Assets/Scripts/AvatarAction/ActionExecutor.cs)、[`SceneOracle.cs`](Assets/Scripts/AvatarAction/SceneOracle.cs)
   - 任务/布置：[`TaskRunner.cs`](Assets/Scripts/Tasks/TaskRunner.cs)、[`ExperimentSceneManager.cs`](Assets/Scripts/Tasks/ExperimentSceneManager.cs)、[`ObjectPlacer.cs`](Assets/Scripts/Tasks/ObjectPlacer.cs)
   - 日志/评测：[`ExperimentLogger.cs`](Assets/Scripts/Infra/ExperimentLogger.cs)、[`Evaluator.cs`](Assets/Scripts/Infra/Evaluator.cs)
   - UI：[`ExperimentUI.cs`](Assets/Scripts/UI/ExperimentUI.cs)、[`HumanInputHandler.cs`](Assets/Scripts/UI/HumanInputHandler.cs)

2) 在 UI 面板选择 TaskMode（DistanceCompression / SemanticSizeBias）与 SubjectMode（MLLM / Human），设置随机种子与最大试次数，点击“开始运行”。

---

## 8. 设计要点与扩展位

- 事件驱动：所有核心路径通过事件解耦，便于测试与替换
- Provider 适配：新增后端仅需实现 `ILLMProvider` 并注册到 `ProviderRegistry`
- 动作原语：在 `ActionExecutor` 侧保持原子化与幂等；复杂行为由上层组合
- Prompt 统一：`PromptTemplates` 保障不同后端提示工程的一致性（系统提示/任务提示/工具定义）
- 资源兜底：`EventBusBootstrap` 在未配置 SO 资产时自动创建运行时通道，保证开箱即用
