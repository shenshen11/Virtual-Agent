# VRP Stimulus Kind & Prefab 规范

> 目标：统一所有任务中使用的“语义对象 ID（kind）→ Prefab/模型”规则，便于跨任务复用与分析。  
> 适用：现有 4 个任务 + `VR_Perception_Scenarios_10` 中的全部未来任务。

---

## 1. 基本原则

- Task 只关心 **kind 字符串**（如 `"chair"`, `"apple"`），不直接 `Instantiate` 模型。
- 所有对象生成统一通过 **`ObjectPlacer.Place(kind, ...)`** 完成：
  - 若在 `Prefab Overrides` 里配置了该 kind → 使用对应 Prefab。
  - 否则回退到 Unity Primitive（Cube/Sphere/Capsule 等），保持兼容。
- 同一个 kind 在所有任务中语义保持一致，方便日志与结果对齐。

---

## 2. kind 命名规则

- 全小写，单词用下划线分隔：`chair`, `toy_car`, `color_patch_red`。
- 推荐模式：`<类别>[_子类][_属性]`，例如：
  - `color_patch_red`, `material_sphere_metal`, `occluder_wall`。
- 已在代码中使用的 kind（不要改名）：
  - 几何：`cube`, `sphere`, `cylinder`, `quad`, `plane`
  - 人物：`human`
  - 日常物体：`chair`, `cup`, `toy_car`, `apple`

---

## 3. 推荐 Stimulus 词表（全局复用）

> 建议在 `Assets/Prefabs/VRP_Stimuli/` 下建立对应 Prefab，并在 `ObjectPlacer` 中配置映射。

### 3.1 基础几何（通常直接用 Primitive）

- `cube` / `sphere` / `cylinder` / `quad` / `plane`

### 3.2 日常物体

- `chair`：椅子模型  
- `cup`：普通杯子  
- `toy_car`：玩具车  
- `apple`：苹果  

### 3.3 人物与遮挡体

- `human`：人形（可用 Capsule 近似）  
- `occluder_pedestrian`：行人遮挡  
- `occluder_wall`：墙体/板  
- `occluder_plant`：植物遮挡  
- `occluder_fence`：栅栏（可选）  
- `obstacle_box`：障碍方箱  

### 3.4 颜色/材质 Stimulus

颜色恒常（色块或小卡片）：

- `color_patch_red` / `color_patch_green` / `color_patch_blue`  
- `color_patch_yellow` / `color_patch_white` / `color_patch_gray`

材质感知（球体 + 不同材质）：

- `material_sphere_metal` / `material_sphere_glass`  
- `material_sphere_wood` / `material_sphere_plastic` / `material_sphere_fabric`

### 3.5 深度/视差/计数标记

- `depth_marker`：通用深度标记物  
- `parallax_marker_foreground`：视差前景标记  
- `parallax_marker_background`：视差背景标记  
- `count_ball`：数量/密度估计用球  

### 3.6 视觉搜索目标/干扰

- `red_cup` / `blue_cup` / `green_cup`：不同颜色杯子  
- 或直接复用 `color_patch_*` 作为搜索目标/干扰项。

---

## 4. 场景与 kind 对应（`VR_Perception_Scenarios_10`）

仅列推荐主用 kind，具体布局由各 Task 控制。

1. **相对深度排序（relative_depth_order）**  
   - 主体：`cube`, `sphere`, `human`, `toy_car`。

2. **遮挡推理与计数（occlusion_reasoning）**  
   - 目标：`apple`, `cup`, `toy_car`, `chair`, `human`, `count_ball`。  
   - 遮挡：`occluder_wall`, `occluder_plant`, `occluder_pedestrian`, `occluder_fence`。

3. **颜色恒常（color_constancy）**  
   - 主体：`color_patch_red/green/blue/yellow/white/gray`。  
   - 可选：`apple`, `red_cup`, `green_cup` 等。

4. **材质识别（material_perception）**  
   - 主体：`material_sphere_metal/glass/wood/plastic/fabric`。

5. **运动视差深度比（motion_parallax）**  
   - 主体：`parallax_marker_foreground`, `parallax_marker_background`（或两个 `depth_marker`）。

6. **对象朝向估计（object_orientation）**  
   - 主体：`toy_car`, `chair`（可扩展箭头牌等）。

7. **杂乱视觉搜索（visual_search）**  
   - 目标：`red_cup` 或 `color_patch_red`。  
   - 干扰：`blue_cup`, `green_cup`, 以及若干 `cube/sphere`。

8. **变化检测（change_detection）**  
   - A/B 场景复用：`cube`, `sphere`, `chair`, `apple`, `toy_car`, `count_ball` 等，修改出现/消失/移动/替换。

9. **数量与密度估计（object_counting）**  
   - 主体：`count_ball`（必要时扩展 `count_ball_red/blue`）。

10. **场景结构分类（scene_layout）**  
    - 主要依赖 `ExperimentSceneManager.environment`（`open_field/corridor/room_with_obstacles/stairs`）。  
    - 可选辅助物体：`obstacle_box`、楼梯台阶类 Prefab。

---

## 5. ObjectPlacer 使用规范

组件：`Assets/Scripts/Tasks/ObjectPlacer.cs`

- 在场景中找到挂有 `ObjectPlacer` 的对象（通常与 `TaskRunner` 同挂）。
- 在 Inspector 中配置：

  ```text
  Prefab Overrides (List<KindPrefab>)
    - Element 0: kind = "chair",   prefab = Chair.prefab
    - Element 1: kind = "apple",   prefab = Apple.prefab
    - Element 2: kind = "toy_car", prefab = ToyCar.prefab
    - ...
  ```

- 运行时行为：
  - 若指定 kind 在列表中：`Place` 使用对应 Prefab；
  - 未配置：退回 `CreatePrimitive(kind)`，保持旧任务行为。

Task 侧统一调用：

```csharp
_placer.Place(kind, position, uniformScale, null, name);
```

> 建议所有新任务都只操作 kind 字符串与空间参数，不自行 `Instantiate` 模型。

---

## 6. 新增 Stimulus 的流程

1. **确定 kind 名称**  
   - 按本规范命名，例如：`occluder_pillar`, `count_ball_red`。

2. **导入模型**  
   - 将 FBX/GLB 放入：`Assets/Models/VRP_Stimuli/`。  
   - 在 Import Settings 中调整 `Scale Factor`，保证在 `localScale=(1,1,1)` 时尺寸合理；关闭不需要的动画导入。

3. **制作 Prefab**  
   - 将模型拖入场景，调整缩放/材质/碰撞器；  
   - 从 Hierarchy 拖回 `Assets/Prefabs/VRP_Stimuli/` 生成 Prefab。

4. **在 ObjectPlacer 中配置映射**  
   - 在 `Prefab Overrides` 中添加一条：`kind = "<your_kind>"`, `prefab = 对应 Prefab`。

5. **在任务中使用**  
   - 在 `TrialSpec` 或 Task 脚本中使用该 kind（如 `trial.objectA = "occluder_pillar";`）；  
   - 通过 `_placer.Place("occluder_pillar", ...)` 实例化。

6. **更新本规范（可选）**  
   - 若新增 kind 是通用 Stimulus（多任务共享），建议在本文件中追加一条说明，保持文档与工程一致。

