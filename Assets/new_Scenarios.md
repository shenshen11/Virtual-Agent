# VR 感知实验场景库（新扩展任务，可直接接入框架）

本文给出新扩展的可在 VR 中对比 MLLM 与人类的感知实验场景。每个场景均遵循统一结构（刺激生成、执行器、任务引擎、通信、Perception 系统、事件总线、记录评测），可复用同一消息协议与动作原语快速落地。

统一规范要点：
- 输入：由 Perception 系统抓帧，并附带元数据（taskId、trialId、FOV、分辨率、姿态、条件等）。
- 输出：模型仅返回 `inference` 或 `action_plan` 的标准 JSON；必要时通过动作原语请求额外快照。
- 变量：每个场景列出自变量与因变量；Trial 流程清晰，可批量生成。
- 评测：提供指标定义（准确率/MAE/F1/AUC/耗时等）与日志字段建议。

## 场景 11：韦伯定律数量对比（Rapid Numerosity Comparison）

目标：在严格禁止“逐个计数”的条件下（人类：短时曝光；AI：单帧、低分辨率/模糊），完成左右两侧**数量二选一**判断，并用心理物理学方法估计阈限，检验韦伯定律（JND 与标准量 N 近似成比例，Weber fraction k≈JND/N 近似恒定）。

刺激与界面：
- 视野被一条竖直分割线分成左右两半；每半区内呈现同类点阵（建议 2D 圆点或 3D 小球，统一外观材质）。
- 每个 trial 仅呈现一次刺激（One-shot），不允许通过移动视角/放大来二次观察。

自变量（建议最小可行集合，便于消融）：
- **基准数量 N（Base Count）**：10 / 50 / 100 / 200 / 500
- **比例 r（Ratio = max/min）**：在 1.1–2.0 内离散取值1.1 / 1.2 / 1.3 / 1.5 / 2.0，并保证左右两侧“较多”随机均衡
- **线索控制模式（Cue Control）**：
  - `equal_dot_size_random_pos`：点大小固定，位置随机
- **AI 感知限制**：分辨率（如 224×224 / 320×320）、可选轻度模糊（如 σ=0/1/2），并强制单帧输入。
- （人类被试）**曝光时长**：0.5s（或 200–800ms 分层），随后遮罩/黑屏

因变量：
- `more_side`（Left/Right）、`confidence`（0–1）
- 反应时/推理耗时（人类 RT；AI latencyMs）

Trial 流程（与现有 Task 生命周期对齐）：
1. 生成 trial 条件：采样 N 与 r；设定“较少”一侧为 N，“较多”一侧为 round(N·r)（必要时对 r 做微调以保证整数与可呈现性），并随机决定左右分配。
2. 场景布置：左右半区随机散点（无重叠、与边界保持最小间距）；按 `Cue Control` 决定点大小/凸包/密度约束。
3. 呈现与采样：
   - AI：固定相机、禁止 `action_plan`；仅允许一次 `snapshot`，并由任务端对图像做下采样/模糊后送入模型。
   - 人类：刺激显示 0.5s 后立即遮罩（黑屏或噪声 mask）。
4. 模型输出二选一：`more_side`。
5. 记录 trial（N、r、左右数量、线索控制、分辨率/模糊、seed）与结果，用于拟合心理物理曲线。

模型输出（inference）：
```json
{ "type":"inference","taskId":"numerosity_comparison","trialId":12,"answer":{"more_side":"right"},"confidence":0.85 }
```

推荐动作原语（若允许，仍建议在任务端强制 one-shot）：
- `snapshot`（强制单次）
- `camera_set_resolution`（或 StimulusCapture 侧下采样）

评测与拟合（建议）：
- **准确率曲线**：按 N 分组，绘制 Accuracy vs. r（或 log(r)）并拟合心理物理函数（logistic / cumulative normal）
- **阈限与 Weber fraction**：对每个 N 计算达到 75% 正确率的阈限 r*，令 JND ≈ (r*−1)·N，k=JND/N；检验 k 是否随 N 近似恒定
- **线索泄漏诊断**：对比不同 `Cue Control` 模式下的性能差异（若差异巨大，说明模型可能利用了非数目线索）

## 日志与配置建议（适用于全部场景）

- JSONL 字段：`taskId`、`trialId`、条件（FOV/光照/背景/遮挡/对象分布等）、真值、模型输出、`confidence`、动作序列、`latencyMs`、`provider`/`transport`、随机种子
- 截图：可选保存关键快照（`stimulus`/`mask`/`action_k`）
- 配置：通过 ScriptableObject/JSON 批量生成试次，支持难度分层与随机种子复现

## 对接与扩展提示

- 所有场景均可复用统一动作原语与消息协议；若信息不足，优先用 `action_plan` 请求 `snapshot`/扫视（但本场景建议直接禁用以保证 one-shot）。
- 任务引擎将这些场景实现为独立 Task，沿用相同生命周期；评测器统一产出指标。
- Perception 系统与 LLM Provider 路由保持不变，可用于多后端对比。
