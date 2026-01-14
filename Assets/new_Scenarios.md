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

## 场景 12：JND 阶梯法相对深度阈限（Staircase Relative Depth JND）

目标：用心理物理阶梯法估计人类与 AI 在两物体相对深度辨别上的最小可觉差，避免固定难度导致的天花板效应。

刺激与界面：
- 同屏两个外观一致的对象 A/B，位于视线前方同一水平面，仅沿深度方向存在微小距离差。
- 场景保持简洁（中性光照/背景），禁止使用尺寸或遮挡等额外线索；可固定左右屏幕位置，以减少随机布局带来的额外提示。

自变量（建议最小集合）：
- 初始步长 Δd0：默认 0.5 m，可配置。
- 阶梯因子 κ：默认 1.414（√2），控制缩放幅度。
- 基准距离范围 d_base：随机采样区间 [d_min, d_max]，需满足 d_base > Δd_min，防止出现负距或重合；每 trial 重新采样以防固定。
- 相机限制：固定 FOV、分辨率；禁止二次观察（one-shot）。

因变量：
- `closer`（A/B）
- `confidence`（0–1）
- 阶梯输出：`delta_at_reversal` 序列、`reversal_count`、`final_threshold`（可取最后 N 次 reversal 平均或拟合 75% 正确率阈值）

Trial 流程（1-up / 2-down 阶梯逻辑）：
1. 初始化：Δd = Δd0，consecutive_correct = 0，last_direction = "init"，reversal_count = 0。
2. 生成 trial：随机采样 d_base；设置一物体深度为 d_base，另一物体为 d_base − Δd；随机决定哪一侧更近（A/B），并保证两深度均大于最小可呈现距离。
3. 呈现：抓帧并发送；禁止 `action_plan`，仅允许单次 `snapshot`。
4. 响应：受试者/模型回答更近者（A/B），可带置信度。
5. 阶梯更新：
   - 若回答正确：consecutive_correct += 1；若 consecutive_correct == 2，则 Δd = Δd / κ，consecutive_correct = 0，若 last_direction == "up" 则 reversal_count += 1；last_direction = "down"。
   - 若回答错误：Δd = Δd * κ，consecutive_correct = 0，若 last_direction == "down" 则 reversal_count += 1；last_direction = "up"。
   - 将 Δd clamp 至 [Δd_min, Δd_max] 以避免数值溢出；记录当前 Δd 与 direction。
6. 终止条件：当 reversal_count 达到 6–8（或达到 max_trials 上限）即认为找到阈限；可取最近 N 次 reversal 的 Δd 均值或拟合 psychometric 曲线获得 `final_threshold`。
7. 记录 trial：保存 d_base、Δd、左右分配、随机种子、回答、耗时/latency、方向序列、reversal 索引。

模型输出（inference）：

```json
{ "type":"inference","taskId":"depth_jnd_staircase","trialId":3,"answer":{"closer":"B"},"confidence":0.72 }
```

推荐动作原语：
- `snapshot`（强制单次）
- `camera_set_resolution` / `camera_set_fov`（若需下采样）

评测与拟合（建议）：
- **阈限估计**：使用 staircase reversal Δd 均值或以 Δd 为自变量拟合 psychometric（logistic/cumulative normal），取 75% 正确率点为 JND。
- **AI vs 人类差异**：比较 `final_threshold` 与 Weber fraction（JND / d_base）分布；可绘制随试次的 Δd 曲线展示收敛速度。
- **稳健性诊断**：按光照/纹理/背景等条件分组阈限，检测潜在线索泄漏或策略差异。

实现对接（本项目已提供 Human-only 最小接入）：
- Task：`Assets/Scripts/Tasks/Task/DepthJndStaircaseTask.cs`（taskId=`depth_jnd_staircase`）
- PICO 左手柄输入桥：`Assets/Scripts/UI/XRDepthJndHumanInputBridge.cs`（primaryButton=>A/左物体，secondaryButton=>B/右物体）
- Playlist：`Assets/Resources/PlayLists/Scenario12_DepthJND_Human.asset`（用于通过 Orchestrator/Playlist 切换）

## 场景 13：地平线线索整合（Horizon Cue Integration）——最终版

目标：在严格执行“红球相对屏幕静止”（Camera 绝对不动，仅改变目标真实距离与地平线位置线索）的前提下，对比人类与 MLLM 的距离估计，检验地平线线索对距离判断的系统性偏置。

实验矩阵（Experimental Matrix）：
- Trial 总数：45（3 距离 × 5 角度 × 3 重复）
- 呈现顺序：完全随机（Fully Randomized）
- 因子 A：物理距离 `D`（m）：5 / 10 / 20
- 因子 B：地平线偏移 `θ`（deg）：+6 / +3 / 0 / -3 / -6
  - `+6°`（High Horizon）：显著上移（暗示更近/贴近地面）
  - `+3°`（Med Horizon）：轻微上移
  - `0°`（Baseline）：基准对照组
  - `-3°`（Med Low）：轻微下移
  - `-6°`（Low Horizon）：显著下移（暗示更远/悬浮）
- 因子 C：重复次数：3

场景实现（Scene Setup，关键：相机不动、红球屏幕位置不动）：
```text
[Experiment_Root]
 ├── Main Camera (Pose: 0, 1.7, 0)  <-- 绝对不动！
 ├── Target_Container
 │    └── RedSphere (Scale: 0.5, 0.5, 0.5) <-- 每一轮 Trial 修改 Z 轴位置 (5/10/20)
 └── Environment_Rig (Pivot: 0, 1.7, 0) <-- 锚点必须与相机位置重合
      └── Skybox_Background (Inverted Sphere, Radius: 500m)
          <-- 每一轮 Trial 修改 X 轴旋转 (Pitch)
```

Pitch 控制（以 Unity 常用左手坐标系为参考；请以实际贴图方向在 Scene 视图校验）：
- Horizon `0°`：`Environment_Rig.rotation = (0, 0, 0)`
- Horizon `+3°`（地平线上移）：`Environment_Rig.rotation = (-3, 0, 0)`（通常抬头看天为负角度；为了让地平线上移，背景球需向后仰）
- Horizon `-6°`（地平线下移）：`Environment_Rig.rotation = (+6, 0, 0)`（背景球向前倒，天盖下来，地平线降低）

关键检查：
- 旋转 `Environment_Rig` 时，Camera 视野内地平线确实上下移动。
- 同时 `RedSphere` 在屏幕中的位置（尤其是 `screen_y`）保持稳定，用作线索隔离的校验位。

Trial 流程逻辑（建议 ExperimentManager 自动化执行）：
1. Start：生成 45 条 trial 条件并洗牌。
2. Setup：设置 `RedSphere` 的 Z 距离（5/10/20），设置 `Environment_Rig` 的 pitch（-6/-3/0/3/6，对应上文约定）。
3. Wait：0.5s（渲染稳定/防残影）。
4. Capture：抓取 RGB 截图；记录 `true_distance`、`horizon_angle`、`sphere_screen_y`（校验位）。
5. Inference：显示人类输入 UI；或发送给 MLLM（单帧推理为主）。
6. Reset：黑屏 0.5s（清除视觉残留）。

条件生成（伪代码）：
```csharp
struct Trial {
  int id;
  float distance; // 5, 10, 20
  float angle;    // -6, -3, 0, 3, 6
}
```

因变量与日志建议：
- 输出：`estimated_distance_m`（人类/模型），可选 `confidence` 与 `latencyMs`
- 必记字段：`true_distance_m`、`horizon_angle_deg`、`sphere_screen_y`、`seed`、`trial_index`

分析（预期图表）：
- X 轴：Horizon Angle（-6, -3, 0, +3, +6）
- Y 轴：Estimated Distance（人类/MLLM）
- 系列：5m 组、10m 组、20m 组（3 条折线）

## 场景 14：视觉拥挤（Visual Crowding）

目标：在严格中央注视条件下，让被试/模型用余光识别**右侧外周**字母串中间目标字母，测量拥挤效应随离心率与间距变化的心理物理曲线，并对比 Human vs MLLM 是否存在“临界间距（Critical Spacing）”。

刺激与界面：
- 屏幕中央持续显示红色注视点（或 “+”），被试要求全程盯住注视点。
- 右侧外周呈现水平 5 字母串：`F1 F2 [T] F3 F4`（`[T]` 为目标字母，`Fi` 为干扰字母，且 `Fi != T`）。

自变量（建议最小可行集合）：
- **Eccentricity（eccentricityDeg）**：目标中心距注视点的视角距离（如 6° / 10° / 14°）。
- **Spacing（spacingDeg）**：目标与最近干扰字母中心距（如 0.5° / 1.0° / 1.5° / 2.0° / 3.0°）。
- （固定项）字母集合：建议使用大写字母并移除易混淆字符（如 I/O/Q）；干扰字母与目标同集合采样。

因变量：
- `correct`（bool）
- `confidence`（0–1，仅模型端）
- RT；模型记录 `latencyMs`


模型输出（inference）：
```json
{ "type":"inference","taskId":"visual_crowding","trialId":12,"answer":{"letter":"R"},"confidence":0.72 }
```
Trial流程

推荐动作原语：
- 建议仅允许 `snapshot`（强制单次）；如需对齐不同 Provider，可在任务端明确禁用 `action_plan`。

评测与拟合（建议）：
- **准确率曲线**：按 `eccentricityDeg` 分组，绘制 Accuracy vs `spacingDeg`。
- **临界间距**：对每个 `eccentricityDeg` 拟合 psychometric（logistic/cumulative normal），取 75% 正确率对应的 `criticalSpacingDeg`。
- **Bouma 比值**：`boumaK = criticalSpacingDeg / eccentricityDeg`（人类通常相对稳定；模型可能显著偏离）。

日志字段建议（JSONL）：
- 条件：`eccentricityDeg`, `spacingDeg`, `targetLetter`, `flankers[]`, `targetIndex=2`, `seed`

## 日志与配置建议（适用于全部场景）

- JSONL 字段：`taskId`、`trialId`、条件（FOV/光照/背景/遮挡/对象分布等）、真值、模型输出、`confidence`、动作序列、`latencyMs`、`provider`/`transport`、随机种子
- 截图：可选保存关键快照（`stimulus`/`mask`/`action_k`）
- 配置：通过 ScriptableObject/JSON 批量生成试次，支持难度分层与随机种子复现

## 对接与扩展提示

- 所有场景均可复用统一动作原语与消息协议；若信息不足，优先用 `action_plan` 请求 `snapshot`/扫视（但本场景建议直接禁用以保证 one-shot）。
- 任务引擎将这些场景实现为独立 Task，沿用相同生命周期；评测器统一产出指标。
- Perception 系统与 LLM Provider 路由保持不变，可用于多后端对比。
