# Trial Plan

- Task: `depth_jnd_staircase` (requested: `distance_compression`, resolved: `depth_jnd_staircase`)
- Seed: `12345`
- Scene: `Task`
- Scene Path: `Assets/Scenes/Task.unity`
- Trial Count: `20`

## System Prompt

```text
You are a vision agent for Staircase Relative Depth JND. ONLY output JSON. Your goal is to decide which object (A or B) is closer to the camera in the image. Inference format: {"type":"inference","taskId":"depth_jnd_staircase","trialId":<int>,"answer":{"closer":"A"|"B"},"confidence":<0..1>} Do NOT output any extra text or action_plan.
```

## Staircase Notes

- Note: `depthA/depthB/trueCloser` are generated online (adaptive).
- Note: This exporter calls `BuildTrials(seed)` only; it does not simulate staircase updates.
- defaultMaxTrials: `60`
- fovDeg: `60`
- baseDistanceRangeM: `[4, 10]`
- minPresentableDistanceM: `1`
- deltaStartM: `0.5`
- deltaMinM: `0.02`
- deltaMaxM: `2`
- kappa: `1.414214`
- reversalTargetPerGroup: `8`
- thresholdUseLastReversals: `4`

## Trials

### Trial 0

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 1

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 2

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 3

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 4

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 5

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 6

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 7

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 8

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 9

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 10

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 11

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 12

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 13

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 14

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 15

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 16

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 17

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 18

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

### Trial 19

- taskId: `depth_jnd_staircase`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- objectA: `sphere`
- objectB: `sphere`
- sizeRelation: `equal`
- depthA: `runtime`
- depthB: `runtime`
- scaleA: `1`
- scaleB: `1`
- trueCloser: `runtime`

```text
Task: Decide which object is closer to the camera (A or B).
Both objects are visually identical; only their depth differs.
Conditions: background=none, FOV=60 deg.
Object A is on the left side of the view; object B is on the right side.
Output ONLY JSON with fields: type=inference, answer.closer ('A'|'B'), confidence (0..1).
```

