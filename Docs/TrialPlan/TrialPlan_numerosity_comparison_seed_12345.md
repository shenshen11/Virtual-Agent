# Trial Plan

- Task: `numerosity_comparison` (requested: `distance_compression`, resolved: `numerosity_comparison`)
- Seed: `12345`
- Scene: `Task`
- Scene Path: `Assets/Scenes/Task.unity`
- Trial Count: `50`

## System Prompt

```text
You are a vision agent for Numerosity Comparison. ONLY output JSON. Determine which side (left or right) has MORE dots/objects. Inference format: {"type":"inference","taskId":"numerosity_comparison","trialId":<int>,"answer":{"more_side":"left"|"right"},"confidence":<0..1>} Do NOT output any extra text or action_plan.
```

## Trials

### Trial 0

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `200`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `241`
- baseCountN: `200`
- ratioR: `1.20`
- leftCount: `200`
- rightCount: `241`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 1

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `400`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `200`
- baseCountN: `200`
- ratioR: `2.00`
- leftCount: `400`
- rightCount: `200`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 2

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `61`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `50`
- baseCountN: `50`
- ratioR: `1.20`
- leftCount: `61`
- rightCount: `50`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 3

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `100`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `110`
- baseCountN: `100`
- ratioR: `1.10`
- leftCount: `100`
- rightCount: `110`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 4

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `12`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `10`
- baseCountN: `10`
- ratioR: `1.20`
- leftCount: `12`
- rightCount: `10`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 5

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `1000`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `500`
- baseCountN: `500`
- ratioR: `2.00`
- leftCount: `1000`
- rightCount: `500`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 6

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `105`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `100`
- baseCountN: `100`
- ratioR: `1.05`
- leftCount: `105`
- rightCount: `100`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 7

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `11`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `10`
- baseCountN: `10`
- ratioR: `1.10`
- leftCount: `11`
- rightCount: `10`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 8

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `100`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `105`
- baseCountN: `100`
- ratioR: `1.05`
- leftCount: `100`
- rightCount: `105`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 9

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `550`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `500`
- baseCountN: `500`
- ratioR: `1.10`
- leftCount: `550`
- rightCount: `500`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 10

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `525`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `500`
- baseCountN: `500`
- ratioR: `1.05`
- leftCount: `525`
- rightCount: `500`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 11

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `250`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `200`
- baseCountN: `200`
- ratioR: `1.25`
- leftCount: `250`
- rightCount: `200`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 12

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `575`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `500`
- baseCountN: `500`
- ratioR: `1.15`
- leftCount: `575`
- rightCount: `500`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 13

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `100`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `121`
- baseCountN: `100`
- ratioR: `1.20`
- leftCount: `100`
- rightCount: `121`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 14

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `241`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `200`
- baseCountN: `200`
- ratioR: `1.20`
- leftCount: `241`
- rightCount: `200`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 15

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `600`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `500`
- baseCountN: `500`
- ratioR: `1.20`
- leftCount: `600`
- rightCount: `500`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 16

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `50`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `63`
- baseCountN: `50`
- ratioR: `1.25`
- leftCount: `50`
- rightCount: `63`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 17

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `75`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `50`
- baseCountN: `50`
- ratioR: `1.50`
- leftCount: `75`
- rightCount: `50`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 18

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `10`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `15`
- baseCountN: `10`
- ratioR: `1.50`
- leftCount: `10`
- rightCount: `15`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 19

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `500`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `525`
- baseCountN: `500`
- ratioR: `1.05`
- leftCount: `500`
- rightCount: `525`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 20

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `125`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `100`
- baseCountN: `100`
- ratioR: `1.25`
- leftCount: `125`
- rightCount: `100`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 21

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `10`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `11`
- baseCountN: `10`
- ratioR: `1.10`
- leftCount: `10`
- rightCount: `11`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 22

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `100`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `125`
- baseCountN: `100`
- ratioR: `1.25`
- leftCount: `100`
- rightCount: `125`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 23

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `150`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `100`
- baseCountN: `100`
- ratioR: `1.50`
- leftCount: `150`
- rightCount: `100`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 24

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `750`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `500`
- baseCountN: `500`
- ratioR: `1.50`
- leftCount: `750`
- rightCount: `500`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 25

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `100`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `200`
- baseCountN: `100`
- ratioR: `2.00`
- leftCount: `100`
- rightCount: `200`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 26

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `100`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `150`
- baseCountN: `100`
- ratioR: `1.50`
- leftCount: `100`
- rightCount: `150`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 27

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `200`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `400`
- baseCountN: `200`
- ratioR: `2.00`
- leftCount: `200`
- rightCount: `400`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 28

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `500`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `550`
- baseCountN: `500`
- ratioR: `1.10`
- leftCount: `500`
- rightCount: `550`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 29

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `50`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `61`
- baseCountN: `50`
- ratioR: `1.20`
- leftCount: `50`
- rightCount: `61`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 30

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `210`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `200`
- baseCountN: `200`
- ratioR: `1.05`
- leftCount: `210`
- rightCount: `200`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 31

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `53`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `50`
- baseCountN: `50`
- ratioR: `1.05`
- leftCount: `53`
- rightCount: `50`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 32

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `50`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `53`
- baseCountN: `50`
- ratioR: `1.05`
- leftCount: `50`
- rightCount: `53`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 33

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `11`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `10`
- baseCountN: `10`
- ratioR: `1.05`
- leftCount: `11`
- rightCount: `10`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 34

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `200`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `220`
- baseCountN: `200`
- ratioR: `1.10`
- leftCount: `200`
- rightCount: `220`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 35

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `10`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `13`
- baseCountN: `10`
- ratioR: `1.25`
- leftCount: `10`
- rightCount: `13`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 36

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `500`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `625`
- baseCountN: `500`
- ratioR: `1.25`
- leftCount: `500`
- rightCount: `625`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 37

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `50`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `58`
- baseCountN: `50`
- ratioR: `1.15`
- leftCount: `50`
- rightCount: `58`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 38

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `13`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `10`
- baseCountN: `10`
- ratioR: `1.25`
- leftCount: `13`
- rightCount: `10`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 39

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `200`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `250`
- baseCountN: `200`
- ratioR: `1.25`
- leftCount: `200`
- rightCount: `250`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 40

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `625`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `500`
- baseCountN: `500`
- ratioR: `1.25`
- leftCount: `625`
- rightCount: `500`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 41

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `500`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `575`
- baseCountN: `500`
- ratioR: `1.15`
- leftCount: `500`
- rightCount: `575`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 42

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `200`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `210`
- baseCountN: `200`
- ratioR: `1.05`
- leftCount: `200`
- rightCount: `210`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 43

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `115`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `100`
- baseCountN: `100`
- ratioR: `1.15`
- leftCount: `115`
- rightCount: `100`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 44

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `50`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `100`
- baseCountN: `50`
- ratioR: `2.00`
- leftCount: `50`
- rightCount: `100`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 45

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `100`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `50`
- baseCountN: `50`
- ratioR: `2.00`
- leftCount: `100`
- rightCount: `50`
- trueMoreSide: `left`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 46

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `100`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `115`
- baseCountN: `100`
- ratioR: `1.15`
- leftCount: `100`
- rightCount: `115`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 47

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `50`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `55`
- baseCountN: `50`
- ratioR: `1.10`
- leftCount: `50`
- rightCount: `55`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 48

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `50`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `75`
- baseCountN: `50`
- ratioR: `1.50`
- leftCount: `50`
- rightCount: `75`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

### Trial 49

- taskId: `numerosity_comparison`
- environment: `open_field`
- fovDeg: `60`
- textureDensity: `1`
- lighting: `bright`
- occlusion: `False`
- background: `none`
- occlusionRatio: `0`
- targetPresent: `False`
- trueCount: `10`
- countingMode: `compare`
- layoutPattern: `random_scatter`
- targetCount: `12`
- baseCountN: `10`
- ratioR: `1.15`
- leftCount: `10`
- rightCount: `12`
- trueMoreSide: `right`
- exposureDurationMs: `500`
- dotRadius: `0.2`

```text
Task: Rapid Numerosity Comparison. Decide which side has MORE items (left/right). FOV=60 deg.
```

