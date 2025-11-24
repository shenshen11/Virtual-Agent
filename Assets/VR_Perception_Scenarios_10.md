# VR 感知实验场景库（新增 10 项，可直接接入框架）

本文给出 10 个可在 VR 中对比 MLLM 与人类的感知实验场景。每个场景均符合既有架构（采样/刺激、执行器、任务引擎、通信、Perception 系统、事件总线、记录评测），可用同一消息协议与动作原语快速落地。

统一规范要点：
- 输入：由 Perception 系统抓帧，附带元数据（taskId、trialId、FOV、分辨率、姿态、条件等）。
- 输出：模型仅返回 inference 或 action_plan 的标准 JSON；必要时通过动作原语请求额外快照。
- 变量：每个场景列出自变量与因变量，Trial 流程清晰，可批量生成。
- 评测：提供指标定义（准确率/MAE/F1/AUC/耗时等）与日志字段。

## 场景 1：相对深度排序（Relative Depth Ordering）

目标：判断同屏对象 A/B 哪个更靠近观察者。

自变量（示例）：FOV（50/60/90）、光照（bright/dim/hdr）、背景（none/indoor/street）、纹理密度（0.5/1.0/1.5/2.0）、对象尺寸（等大/不同）、遮挡（true/false）。
因变量：更近者（A/B）、置信度。

Trial 流程：
1) 布置两对象 A/B 于正前方不同距离；设定条件。
2) 抓帧并发送。
3) 模型返回较近者或下发 action_plan（如请求微小平移以利用运动视差）。
4) 记录结果并与真值比对。

模型输出（inference）：

```json
{ "type":"inference","taskId":"relative_depth_order","trialId":1,"answer":{"closer":"A"},"confidence":0.8 }
```

推荐动作原语：snapshot、strafe、turn_yaw、head_look_at、camera_set_fov。
评测：准确率、置信度-准确率校准（ECE）、按条件分组的混淆矩阵。

## 场景 2：遮挡推理与计数（Occlusion Reasoning & Counting）

目标：部分遮挡条件下，判断目标是否存在/类别，或估计可见同类物体数量。

自变量：遮挡率（0–0.8）、遮挡体类型（墙/植物/行人）、背景、光照、目标类别。
因变量：存在性（bool）或数量（int）。

Trial 流程：
1) 布置目标与遮挡体，设定遮挡率。
2) 抓帧发送。
3) 模型给出存在/数量；不足时请求 head_look_at 或 turn_yaw 获取不同角度。
4) 记录并比对真值。

模型输出（示例）：

```json
{ "type":"inference","taskId":"occlusion_reasoning","trialId":7,"answer":{"present":true,"count":3},"confidence":0.74 }
```

推荐动作原语：head_look_at、turn_yaw、snapshot、focus_target。
评测：存在性准确率、计数 MAE/MAPE、遮挡率分层曲线。

## 场景 3：颜色恒常与照明（Color Constancy）

目标：在不同光色/强度下判断物体表面“感知颜色”。

自变量：光照色温/强度（多档）、环境类型、材质、阴影（true/false）。
因变量：颜色类别（red/green/...）或近似 RGB 值。

Trial 流程：
1) 放置标准色卡或彩色物体，设置光照条件。
2) 抓帧发送。
3) 模型输出颜色类别/RGB；必要时请求 snapshot 在不同曝光/角度。
4) 记录并对比真值（或接近度 ΔE）。

模型输出（示例）：

```json
{ "type":"inference","taskId":"color_constancy","trialId":3,"answer":{"color_name":"red","rgb":[220,40,30]},"confidence":0.67 }
```

推荐动作原语：set_lighting、snapshot、camera_set_fov、head_look_at。
评测：类别准确率或 ΔE2000、在各光照下的稳健性曲线。

## 场景 4：材质识别（Material Perception）

目标：基于高光/法线/反射等线索判断材质类型（metal/glass/wood/plastic/fabric）。

自变量：光照（方向/强度/HDR）、对象几何形状、背景、相机角度。
因变量：材质类别与置信度。

Trial 流程：
1) 放置对象并设置光照与角度。
2) 抓帧发送；如不确定，可请求 turn_yaw 或 head_look_at 获取高光位。
3) 输出材质类别并记录。

模型输出：

```json
{ "type":"inference","taskId":"material_perception","trialId":5,"answer":{"material":"metal"},"confidence":0.76 }
```

推荐动作原语：set_lighting、turn_yaw、head_look_at、snapshot。
评测：准确率、混淆矩阵、不同光照下的鲁棒性。

## 场景 5：运动视差深度比（Motion Parallax Depth Ratio）

目标：通过轻微横移/转头，估计前景与背景的相对深度比或前景距离。

自变量：横移距离（0.2–1.0 m）、转头角度（5–20°）、背景纹理密度、光照。
因变量：深度比（r ∈ (0,1]）或距离估计。

Trial 流程：
1) 布置前景/背景标定物。
2) 模型可下发 action_plan：strafe/turn_yaw + snapshot 两三次。
3) 输出深度比或距离，记录序列。

模型输出：

```json
{ "type":"inference","taskId":"motion_parallax","trialId":4,"answer":{"depth_ratio":0.42},"confidence":0.6 }
```

推荐动作原语：strafe、turn_yaw、snapshot、camera_set_fov。
评测：MAE/MAPE，相对顺序正确率；动作次数与耗时。

## 场景 6：对象朝向估计（Object Orientation）

目标：估计目标对象的水平朝向（yaw 度数）或朝向区间（如 8 向）。

自变量：对象类别与几何、光照、背景、距离、部分遮挡。
因变量：yaw_deg（-180..180）或离散方向标签。

Trial 流程：
1) 布置对象，随机 yaw。
2) 抓帧发送；需要时 head_look_at 或绕行观察。
3) 输出角度或方向标签。

模型输出：

```json
{ "type":"inference","taskId":"object_orientation","trialId":9,"answer":{"yaw_deg":-35.0},"confidence":0.58 }
```

推荐动作原语：head_look_at、turn_yaw、snapshot、focus_target。
评测：角度 MAE/MedAE、分箱准确率。

## 场景 7：杂乱视觉搜索（Visual Search in Clutter）

目标：在干扰项中寻找特定目标（如红色杯子/字母 T）。

自变量：干扰项数量、相似度、背景、光照、目标位置分布。
因变量：是否找到（bool），可选返回定位名/索引。

Trial 流程：
1) 生成包含干扰物的场景。
2) 抓帧；需要时 action_plan 通过 head_look_at/turn_yaw 进行扫视。
3) 输出 found 状态与可选 target_name。

模型输出：

```json
{ "type":"inference","taskId":"visual_search","trialId":11,"answer":{"found":true,"target":"red_cup"},"confidence":0.69 }
```

推荐动作原语：head_look_at、turn_yaw、snapshot、focus_target。
评测：准确率、平均搜索步数/时间、难度-性能曲线。

## 场景 8：变化检测（Change Detection）

目标：两次观测间，判断场景是否发生变化（对象替换/位移/出现消失）。

自变量：变化类型与幅度、时间间隔、干扰动作、背景。
因变量：是否变化（bool），可选变化类别。

Trial 流程：
1) 抓帧 A 并发送。
2) 变更场景（或由任务引擎自动切换）。
3) 抓帧 B 并发送，或让模型请求 snapshot。
4) 输出变化判断与类别。

模型输出：

```json
{ "type":"inference","taskId":"change_detection","trialId":6,"answer":{"changed":true,"category":"disappearance"},"confidence":0.77 }
```

推荐动作原语：snapshot、head_look_at、focus_target。
评测：准确率、ROC/AUC、反应时。

## 场景 9：数量与密度估计（Counting & Density Estimation）

目标：估计视野内同类对象数量或密度（如球体/立方体群）。

自变量：对象数量（1–50）、排列方式（规则/随机/簇）、遮挡率、背景、光照。
因变量：count（int）或 density（objects/m²）。

Trial 流程：
1) 生成对象集与条件。
2) 抓帧；必要时转动或抬头/低头。
3) 输出数量/密度并记录。

模型输出：

```json
{ "type":"inference","taskId":"object_counting","trialId":10,"answer":{"count":17},"confidence":0.64 }
```

推荐动作原语：turn_yaw、head_look_at、snapshot、camera_set_fov。
评测：MAE/MAPE、在遮挡/密度分层下的误差曲线。

## 场景 10：场景结构分类（Scene Layout Classification）

目标：根据几何与纹理特征对场景类型分类（open_field/corridor/room_with_obstacles/stairs）。

自变量：几何布局、障碍物密度、纹理密度、光照、FOV。
因变量：场景类别标签。

Trial 流程：
1) 选择/生成场景布局并设定条件。
2) 抓帧；必要时请求转头/步行以观察全局结构。
3) 输出场景类别。

模型输出：

```json
{ "type":"inference","taskId":"scene_layout","trialId":2,"answer":{"category":"corridor"},"confidence":0.83 }
```

推荐动作原语：turn_yaw、move_forward、snapshot、camera_set_fov。
评测：准确率、每类召回率、混淆矩阵。

## 日志与配置建议（适用于全部 10 场景）

- JSONL 字段：taskId、trialId、条件（FOV/光照/背景/遮挡/对象分布等）、真值、模型输出、置信度、动作序列、耗时、provider 与 transport、随机种子。
- 截图：可选保存关键快照（before/after/action_k）。
- 配置：通过 ScriptableObject/JSON 批量生成试次，支持难度分层与随机种子复现。

## 对接与扩展提示

- 所有场景均可使用统一动作原语与消息协议；模型如遇信息不足，优先下发 action_plan 请求 snapshot/扫视。
- 任务引擎将这些场景实现为独立 Task，沿用相同生命周期；评测器统一产出指标。
- Perception 系统与 LLM Provider 路由保持不变，可用于云/本地多后端对比。