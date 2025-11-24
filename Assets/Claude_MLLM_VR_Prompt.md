
请严格遵循以下角色、目标、产出、接口、任务规格与质量门槛。

## 角色

- 你是资深 Unity XR 与计算机视觉工程师，熟悉实验范式、统计采样、渲染与性能优化。
- 你编写高质量 C# 代码，面向组件与数据驱动，遵循解耦、可测试、可扩展的设计。
- 你将把所有功能组织为清晰的模块：采样/刺激、执行器、任务引擎、通信、感知编排、事件总线、记录与评测。

## 总体目标

- 在 Unity 中搭建“实验框架 + 两个示例任务”的完整工程脚本与场景资源。
- 支持人类被试与 MLLM 两种被试模式的对照与切换。
- 支持通过头像头部相机采样画面并编码发送至后端，由后端调度 MLLM 推理与动作规划。
- 支持多后端（云/本地）统一接入，便于 A/B 与回退。
- 支持实验可重复性：可配置种子、参数、试次与日志导出。
- 代码对后续任务可复用，新增任务无需改动核心框架。

## 产出物

- Unity 脚本：采样器、执行器、任务引擎、消息协议、PerceptionSystem、事件总线、日志器与两个任务的实现。
- 一个主场景内的任务配置与运行 UI，或多个示例场景。
- 与模型交互的消息格式与执行器工具清单。
- 完整的使用说明与参数表注释。

---

## 架构与解耦原则（7+1 子系统）

1) 采样/刺激（Stimulus）
- 头像头部相机（FOV 可配置：50/60/90）抓帧。
- 帧编码为 PNG/JPEG（质量可配），上限分辨率与缩放策略可配。
- 同步采集元数据：FOV、分辨率、时间戳、任务/试次 ID、光照与纹理条件、相机姿态等。

2) 执行器（ActionExecutor）
- 原子动作：视角、移动、旋转、交互，具备命令队列与状态机（后述）。
- 接收动作计划，串行/并行（非阻塞）执行，带超时、重试、失败恢复与完成回调。

3) 任务引擎（Task Runner）
- 任务 = 参数化 Trial 的集合；Trial = 场景布置 + 刺激配置 + 评价指标。
- 配置使用 ScriptableObject 或 JSON，支持批量生成试次。
- 生命周期：初始化 → 布置 → 注视/倒计时 → 采样/推理 → 应答/动作 → 记录 → 清理 → 下一 Trial。

4) 通信（Transport/Connector）
- 提供 WebSocket 与 HTTP API 两类传输方式（可并存），统一接口，负责可靠传输、心跳、重连与背压。
- 已有 WebSocket 组件可复用（见 [Connector.cs](Assets/Scripts/Connector.cs)）；补充 HTTP API 连接器。

5) PerceptionSystem（桥接器）
- 使用 Stimulus 抓帧与元数据，使用 Connector/LLM Provider 发送并等待响应/计划。
- 对上暴露“抓帧+推理/动作”一键式编排接口，保证业务无感于底层传输与后端差异。

6) 事件总线（Event Bus）
- 基于 ScriptableObject 的全局事件通道，模块间通过事件通讯，避免直接引用与 static event 紧耦合。
- 事件示例：FrameRequested、FrameCaptured、InferenceReceived、ActionPlanReceived、ExecutorStateChanged、CommandQueued/Started/Completed/Failed、ConnectionStateChanged、ErrorRaised、TrialStarted/Finished、LogFlushRequested 等。

7) 记录与评测（Logger & Evaluator）
- 记录每个 Trial 的输入、参数、模型输出/人类应答、真值与渲染截图。
- 导出 JSONL（行式 JSON）与 CSV 汇总。

+1) LLM Provider 抽象层
- 多后端统一抽象（OpenAI/Anthropic/Azure/vLLM/Ollama/TGI/HF Endpoint/自建微服务等），屏蔽差异，支持路由与回退。

---

## 多后端 API 统一接入（LLM Provider Abstraction）

LLM Provider 作为策略/适配器集合，为不同大模型/平台提供统一推理接口：
- Provider 类型示例：
  - cloud_openai: OpenAI/兼容（包含 vLLM/自建 OpenAI-compatible）
  - cloud_anthropic: Anthropic Claude
  - cloud_azure_openai: Azure OpenAI
  - local_ollama: Ollama （http://localhost:11434）
  - local_tgi: Text Generation Inference
  - local_vllm: vLLM OpenAI-compatible
  - custom_http: 自建 HTTP 微服务（统一 JSON 契约）
- 统一方法：infer(framePayload, toolSpec?, systemPrompt, taskPrompt, stop?, temperature?, top_p?, timeout?)
- 可选流式：SSE/Chunked（按 provider 能力开启）

建议在 Unity 侧实现：
- ProviderRegistry（注册/发现可用 Provider）
- ProviderRouter（按任务/健康度/延迟/配额进行路由或 A/B）
- CredentialsStore（基于 PlayerPrefs/环境变量/加密文件存储 API Key）
- RateLimiter 与 RetryWithBackoff（指数退避+抖动）
- 可观测性：请求 ID、延迟直方图、错误码聚合、采样日志（不含敏感字段）

后端请求携带图像方式：
- Base64 内嵌 JSON（通用，体积较大）
- URL（预先上载到对象存储/静态文件服务）
- Multi-part（部分私有/定制 API 支持）

接口封装建议：尽量将“提示工程/工具定义/少样本/系统提示”在 Unity 侧转为结构化 JSON（toolSpec + taskGoal + constraints），避免拼接自然语言导致脆弱性。

---

## PerceptionSystem：桥接 Stimulus 与 Connector

职责：
- 调度 Stimulus 抓帧（尊重 FPS 上限、节流与快照）
- 打包元数据（FOV/分辨率/任务/试次/条件/时间戳/姿态等）
- 调用 LLM Provider：支持 inference 或 action_plan 工作流
- 若收到 action_plan：将命令序列投喂 ActionExecutor，并在必要时再次抓帧闭环

流程（典型一次推理）：
1) 接到 FrameRequested（来源：任务引擎或 Executor 的 snapshot）
2) Stimulus 抓帧 → FrameCaptured（携带纹理与元数据）
3) PerceptionSystem 编码图像与元数据 → 调用 Provider.infer()
4) 收到 inference → InferenceReceived（或 ActionPlanReceived）
5) 若为 action_plan → 丢入执行器命令队列；若为 inference → 任务引擎进入评测记录

注意：
- PerceptionSystem 不关心具体 WS/HTTP，只依赖 LLM Provider 抽象
- 可配置：超时、并发度、速率限制、失败回退（切 Provider）

---

## 事件总线（ScriptableObject Event Bus）

事件通道示例（ScriptableObject 资产）：
- FrameRequestedEventChannel
- FrameCapturedEventChannel
- InferenceReceivedEventChannel
- ActionPlanReceivedEventChannel
- ExecutorStateEventChannel
- CommandLifecycleEventChannel（Queued/Started/Completed/Failed）
- ConnectionStateEventChannel
- ErrorEventChannel
- TrialLifecycleEventChannel（Started/Completed）
- LogFlushEventChannel

实践要点：
- 事件负载使用轻量结构（避免直接传 Texture，传引用/ID）
- 渠道名称与命名空间规范，集中管理 ScriptableObject 资产
- 支持编辑器与运行时订阅，避免隐藏耦合
- 保持幂等订阅与弱引用，防止生命周期导致的泄漏

---

## 执行器（ActionExecutor）：命令队列与状态机

命令格式（与下行 action_plan 对齐）：
- id: string
- name: string（camera_set_fov / head_look_at / move_forward / strafe / turn_yaw / set_texture_density / set_lighting / place_object / focus_target / snapshot）
- params: dict
- wait: bool（阻塞语义）
- timeoutMs: int?（可选）
- retries: int?（可选）

内部组件：
- Queue<ActionCommand> 命令队列（先入先出，支持优先级可选）
- 状态机：
  - Idle：空闲
  - ExecutingBlocking：执行阻塞命令（wait=true，串行）
  - ExecutingNonBlocking：执行非阻塞命令（wait=false，可能并发/排队）
  - Paused：外部暂停（如 Trial 切换）
  - Error：不可恢复错误（等待清理或回退）
- 超时与重试：失败进入重试；重试耗尽 → Error/跳过（按策略）
- 并发策略：非阻塞命令可并行（限制最大并发），但需定义互斥资源（如相机 FOV/位姿）避免冲突
- 快照命令（snapshot）：通过 Event Bus 请求 PerceptionSystem 抓帧并异步回传

---

## SceneOracle：字符串 → 场景对象/位置解析

用途：支持 head_look_at: { target: string } 与 focus_target 等语义，从字符串名称映射到 GameObject 或世界坐标。

建议功能：
- 名称/标签/层级索引（启动时构建与增量维护）
- 自定义字典：别名 → 对象引用
- 距离/可见性/射线检测过滤
- 模糊匹配策略：完全匹配 → 前缀/后缀 → 近似编辑距离（可选）
- 返回策略：最近的匹配项、或按优先规则
- 缓存与失效：对象销毁时清理索引

找不到目标时：
- 返回错误并建议可用名称列表
- 执行器记录失败并由上层决定回退（例如请求模型换用 {x,y,z} 坐标）

---

## 消息与数据协议

上行（Unity → 后端/模型）统一格式：
```json
{
  "type": "frame",
  "timestamp": 1710000000.123,
  "taskId": "distance_compression",
  "trialId": 12,
  "camera": { "fov": 60, "resolution": [1280, 720], "pose": {"position":[0,1.6,0], "rotation_euler":[0,0,0]} },
  "conditions": { "textureDensity": 1.5, "lighting": "bright", "occlusion": false, "environment": "corridor" },
  "objects": [
    { "name": "target", "kind": "cube", "position": {"x":0,"y":1,"z":10}, "trueDistance": 10.0 }
  ],
  "image": "data:image/jpeg;base64, ...",
  "meta": { "seed": 12345, "fps_cap": 8, "transport": "http|ws", "provider": "cloud_anthropic" }
}
```

下行（后端/模型 → Unity）两类：
- inference：
```json
{
  "type": "inference",
  "taskId": "distance_compression",
  "trialId": 12,
  "answer": { "distance_m": 9.8 },
  "confidence": 0.62,
  "explanation": "可选"
}
```
- action_plan：
```json
{
  "type": "action_plan",
  "taskId": "distance_compression",
  "trialId": 12,
  "actions": [
    { "id": "a1", "name": "camera_set_fov", "params": {"fov_deg": 60}, "wait": true },
    { "id": "a2", "name": "snapshot", "params": {"label": "after_fov"}, "wait": true }
  ]
}
```

错误：
```json
{ "type": "error", "code": "E_TIMEOUT", "message": "...", "taskId": "...", "trialId": 12 }
```

传输信封（可选）：
```json
{ "protocol": "vr-perception-v1", "payload": { ...上述对象... } }
```

---

## HTTP API 与本地后端示例（供 LLM Provider 适配用）

- OpenAI 兼容（含 vLLM/Azure-OpenAI 兼容模式）：
  - POST /v1/chat/completions 或 /v1/responses
  - 输入：system + user（含图像 URL/base64）+ tools（动作原语规范）
  - 输出：严格 JSON，仅 inference 或 action_plan

- Anthropic Claude（Messages API）：
  - POST /v1/messages
  - 输入：system + messages（包含图像 content 与工具定义）
  - 输出：解析 JSON，有则转发为 inference 或 action_plan

- 本地 Ollama：
  - POST /api/generate 或 /api/chat
  - 输入：prompt（含 JSON 指令）+ images（base64/URL）
  - 输出：从文本中提取 JSON，有歧义则要求模型严格 “ONLY JSON”

- TGI/HF Endpoint：
  - POST /generate 或 endpoint 特定路径
  - 输入：prompt/inputs + images/urls + 参数
  - 输出：同上，统一解析与验证

适配层职责：
- 模型提示模板化（系统提示 + 任务提示 + 工具规范）
- 安全 JSON 解析与验证（Schema 验证、字段容错/默认值）
- 严格 “ONLY JSON” 约束 + 失败重试（带 content filter）
- 失败回退：切换 Provider 或降级策略

---

## 执行器工具集 API（模型可调用的动作原语）

动作以 JSON 数组形式下发，每个动作包含 name、params、id、可选 wait。建议原语：
- camera_set_fov: { fov_deg: number }
- head_look_at: { target: string | {x:number,y:number,z:number} }  // 使用 SceneOracle 解析字符串
- move_forward: { meters: number, speed: number? }
- strafe: { meters: number, direction: "left"|"right", speed: number? }
- turn_yaw: { deg: number, speed: number? }
- set_texture_density: { scale: number }
- set_lighting: { preset: "bright"|"dim"|"hdr" }
- place_object: { kind: "cube"|"sphere"|"human"|"chair"|"cup"|"toy_car"|"apple", position:{x,y,z}, scale:number }
- focus_target: { name: string }
- snapshot: { label: string? }  // 立即抓帧并回传（通过 Event Bus → PerceptionSystem）

执行策略：
- 原子动作容错+幂等，失败返回 error 与可恢复提示
- 队列化；wait=false 则并行或排队执行，保证最终一致
- 进度/完成事件发往 Event Bus，任务引擎监听

---

## 任务规格 A：Distance Compression（距离压缩）

目标：在开阔地与长走廊两类环境下，测量被试对正前方目标的距离估计误差，并与真值对比。

环境：
- 两套环境：空旷平地、长走廊
- 地面/墙面纹理密度可调（0.5/1.0/1.5/2.0）
- 光照预设（bright/dim/hdr）
- 目标类型：cube/sphere/human；距离范围 2–30 m 分层采样

自变量：
- FOV：50 / 60 / 90
- 纹理密度、光照、目标类型

因变量：
- 估计距离（米）、绝对误差与相对误差

流程（每 Trial）：
1) 初始化场景参数，布置目标
2) 设置 FOV → 抓帧 → 发送
3) 返回距离估计或 action_plan（可能再次抓帧）
4) 写日志：参数、真值、输出、人类对照（若有人类回合）

消融：默认不提供场景语义提示，仅提供图像与元数据，可切换开启/关闭提示做对照。

---

## 任务规格 B：Semantic Size Bias（语义大小偏差）

目标：同屏展示一对日常物体（如 椅子/杯子、玩具车/苹果），真实物理尺寸可相等或反转，考察被试“谁更大”的判断与语义偏差。

环境：
- 背景：none / indoor / street，可开关部分遮挡
- 两对象在视野内，控制距离与投影尺寸，形成等大或反转条件

自变量：
- 物体对类型（chair/cup, toy_car/apple, ...）
- 物理尺寸关系：相等 / 反转
- 背景/遮挡

因变量：
- 更大者（A/B）与置信度

流程（每 Trial）：
1) 初始化并布置两对象，应用背景/遮挡
2) 抓帧并发送
3) 返回较大者或 action_plan
4) 写日志并与真值比对

模型输出：
```json
{
  "type": "inference",
  "taskId": "semantic_size_bias",
  "trialId": 8,
  "answer": { "larger": "A" },
  "confidence": 0.71
}
```

---

## 参数化与可扩展性

- 所有任务参数由 ScriptableObject/JSON 配置，支持批量生成试次。
- 任务引擎依赖抽象接口，新增任务仅需实现 ITask 与 Trial 布置器。
- 执行器只暴露动作原语；复杂行为在上层组合。
- LLM Provider 通过注册适配器扩展，无需动业务层。

---

## 性能与稳定性

- 采样限速（如 5–10 FPS）与背压策略（Reject/DropNewest/DropOldest）
- PNG/JPEG 编码与缩放可配置；纹理转码在主线程最短驻留（拷贝后异步编码）
- 传输断线自动重连（指数退避+抖动），状态可观察
- 日志落盘批量/异步，避免阻塞主线程
- LLM 请求超时/重试/回退，保护指标（失败率、P95 延迟）可监控

---

## 安全与合规

- API Key 存储于系统安全存储/环境变量（不写入日志）
- 传输日志脱敏；图像可开关持久化
- 记录随机种子与配置，确保复现
- 不收集/外传个人隐私数据

---

## 给模型的系统提示（System Prompt）建议

你是能读取单帧图像并依据给定任务目标返回结构化答案或动作计划的代理。

要求：
- 仅输出指定 JSON，禁止多余文本
- 如遇信息不足，优先输出 action_plan 请求更多视角或抓帧
- 始终附带 confidence ∈ [0,1]

任务约束：
- Distance Compression：answer = { "distance_m": number }
- Semantic Size Bias：answer = { "larger": "A"|"B" }

---

## 开发步骤（含新增组件）

1) LLM Provider 抽象与适配器实现（cloud/local 多后端）
2) HTTP API 连接器与现有 WebSocket 连接器统一封装（路由/回退）
3) 事件总线 ScriptableObject 资产与订阅框架
4) PerceptionSystem：桥接 Stimulus 与 Provider，完成一次推理/动作闭环
5) ActionExecutor：命令队列、状态机与互斥资源管理；集成 SceneOracle
6) Stimulus：相机抓帧、编码与元数据采集；支持 snapshot/节流
7) 消息协议序列化/反序列化与 Schema 验证；严格 ONLY JSON
8) 任务引擎接口与 Trial 生命周期；两个示例任务布置器与评测器
9) 日志器：JSONL 与 CSV 汇总；截图可选
10) 演示 UI：被试模式切换（人类/模型）、参数面板、状态/错误显示
11) 集成测试：伪后端/回放器；性能与稳定性验证；A/B 路由与回退测试

---

## 质量门槛（Definition of Done）

- 两个任务可运行，能循环多个 Trial 并导出日志
- 自变量（FOV/纹理/光照等）体现在元数据与日志里
- MLLM 模式下完整收发：抓帧 → 推理/动作 → 结果记录
- 人类模式下 UI 可输入答案并记录
- 新增任务仅新增布置器与配置，不改核心框架
- 至少对接 1 个闭源云（如 OpenAI/Anthropic）与 1 个本地开源（如 Ollama/vLLM），并通过 ProviderRouter 可切换
- 事件总线驱动关键流程（抓帧、命令生命周期、连接状态、错误）
- 执行器具备命令队列与状态机，支持超时/重试/并发与互斥
- SceneOracle 能解析字符串目标并给出清晰回退策略

---

## 附：与现有组件的映射与扩展建议

- 现有 WebSocket 连接器可复用（见 Connector 脚本），其编码/背压/重连可直接用于 WS 传输。
  - 编码纹理 → 发送二进制/文本：同类功能参照（无需强绑定业务逻辑）
- 新增 HTTP API 连接器与 LLM Provider 抽象层，将提示工程与工具规范从传输层剥离
- 强烈建议以事件总线替代模块间直接引用，便于测试与扩展

---

## 速查：动作原语与依赖

- camera_set_fov → 相机系统（互斥：与 turn/head_look_at）
- head_look_at → SceneOracle（字符串解析）/相机控制
- move_forward/strafe/turn_yaw → Avatar 控制器（物理或 CharacterController）
- set_texture_density/set_lighting/place_object → 场景布置器
- focus_target → SceneOracle（目标聚焦策略）
- snapshot → PerceptionSystem（抓帧）→ Provider

---

## 评测与日志

- JSONL：taskId、trialId、自变量、真值、模型/人类输出、置信度、耗时、随机种子、后端 provider 与 transport、请求/响应 ID
- 截图：每 Trial 可选存一张；命名：<task>_<trial>_<timestamp>.png
- CSV 汇总：误差/正确率/置信度分布、分条件聚合

---

## 提示工程补充（多后端一致性）

- 系统提示：明确只输出 JSON；不满足信息时用 action_plan 请求快照；禁止自由文本
- 工具定义：列出动作原语 schema（name/params/wait），描述限制/边界条件
- 任务提示：Distance/Size 目标与度量口径；样例 I/O（少样本）按 JSON 提供
- 对齐策略：不同后端使用相同模板骨架，减少模型间差异导致的行为漂移

---

以上规范即为“Ultrathink”版提示词，请按此实现并输出脚本、场景与文档。建议目录：
- Scripts/Perception（PerceptionSystem、Provider、Router、Adapters）
- Scripts/Infra（EventBus、Schema、Logging）
- Scripts/AvatarAction（ActionExecutor、ActionCommand、SceneOracle）
- Scripts/Tasks（Runner、Task/Trial/Builders）
- Resources/Events（ScriptableObject 事件通道资产）
- Resources/Configs（任务/后端/参数配置）