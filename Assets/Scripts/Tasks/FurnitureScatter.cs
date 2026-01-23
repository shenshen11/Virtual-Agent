using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRPerception.Tasks
{
    /// <summary>
    /// 随机摆放家具 Prefab（用于颜色恒常任务的环境参照）。
    /// </summary>
    public sealed class FurnitureScatter : MonoBehaviour
    {
        [Header("Prefabs")]
        [SerializeField] private List<GameObject> furniturePrefabs = new List<GameObject>();

        [Header("Spawn Count")]
        [SerializeField] private int minCount = 6;
        [SerializeField] private int maxCount = 10;

        [Header("Spawn Area")]
        [Tooltip("相对于锚点/相机的中心偏移（米）。")]
        [SerializeField] private Vector3 centerOffset = new Vector3(0f, 0f, 3f);
        [Tooltip("生成区域尺寸（X/Z），Y 会被忽略或作为固定高度。")]
        [SerializeField] private Vector3 areaSize = new Vector3(6f, 0f, 6f);

        [Header("Distance Constraints")]
        [Tooltip("与锚点/相机的最小水平距离（米），避免贴脸。")]
        [SerializeField] private float minDistanceFromAnchor = 2.5f;
        [Tooltip("家具之间的最小水平间距（米），尽量避免重叠。")]
        [SerializeField] private float minSeparation = 1.2f;
        [Tooltip("每个物体的放置尝试次数，越大越容易满足间距限制。")]
        [SerializeField] private int maxPlacementAttempts = 24;

        [Header("Placement")]
        [SerializeField] private bool alignToFloor = true;
        [SerializeField] private float floorY = 0f;
        [SerializeField] private bool randomYaw = true;
        [SerializeField] private Vector2 scaleRange = new Vector2(0.8f, 1.2f);
        [SerializeField] private bool parentToThis = true;

        private readonly List<GameObject> _spawned = new List<GameObject>();

        public bool HasSpawned => _spawned.Count > 0;

        public void Spawn(System.Random rand, Transform anchor = null, int? countOverride = null)
        {
            Clear();

            if (furniturePrefabs == null || furniturePrefabs.Count == 0)
            {
                return;
            }

            if (rand == null)
            {
                rand = new System.Random(Environment.TickCount);
            }

            int count = countOverride ?? ResolveSpawnCount(rand);
            if (count <= 0) return;

            var basePos = anchor != null ? anchor.position : Vector3.zero;
            var halfX = Mathf.Max(0.01f, areaSize.x) * 0.5f;
            var halfZ = Mathf.Max(0.01f, areaSize.z) * 0.5f;
            var placed = new List<Vector3>(count);
            float minAnchorDist = Mathf.Max(0f, minDistanceFromAnchor);
            float minSep = Mathf.Max(0f, minSeparation);
            int attempts = Mathf.Max(1, maxPlacementAttempts);

            if (minAnchorDist > 0f)
            {
                var center = new Vector2(basePos.x + centerOffset.x, basePos.z + centerOffset.z);
                var anchorXZ = new Vector2(basePos.x, basePos.z);
                float maxDx = Mathf.Abs(center.x - anchorXZ.x) + halfX;
                float maxDz = Mathf.Abs(center.y - anchorXZ.y) + halfZ;
                float maxPossible = Mathf.Sqrt(maxDx * maxDx + maxDz * maxDz);
                if (maxPossible > 0f)
                {
                    minAnchorDist = Mathf.Min(minAnchorDist, maxPossible);
                }
            }

            for (int i = 0; i < count; i++)
            {
                var prefab = furniturePrefabs[rand.Next(furniturePrefabs.Count)];
                if (prefab == null) continue;

                var go = Instantiate(prefab);
                if (parentToThis)
                {
                    go.transform.SetParent(transform, true);
                }

                var pos = ResolvePlacement(rand, basePos, halfX, halfZ, placed, minAnchorDist, minSep, attempts);
                if (alignToFloor)
                {
                    pos.y = floorY;
                }

                go.transform.position = pos;

                if (randomYaw)
                {
                    float yaw = NextRange(rand, 0f, 360f);
                    go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                }

                float scale = NextRange(rand, scaleRange.x, scaleRange.y);
                if (Mathf.Abs(scale - 1f) > 0.001f)
                {
                    go.transform.localScale = go.transform.localScale * scale;
                }

                _spawned.Add(go);
                placed.Add(pos);
            }
        }

        public void Clear()
        {
            for (int i = _spawned.Count - 1; i >= 0; i--)
            {
                var go = _spawned[i];
                if (go == null) { _spawned.RemoveAt(i); continue; }
#if UNITY_EDITOR
                DestroyImmediate(go);
#else
                Destroy(go);
#endif
                _spawned.RemoveAt(i);
            }
        }

        public void SetActive(bool active)
        {
            for (int i = _spawned.Count - 1; i >= 0; i--)
            {
                var go = _spawned[i];
                if (go == null) { _spawned.RemoveAt(i); continue; }
                if (go.activeSelf != active)
                {
                    go.SetActive(active);
                }
            }
        }

        private int ResolveSpawnCount(System.Random rand)
        {
            int min = Mathf.Max(0, minCount);
            int max = Mathf.Max(min, maxCount);
            if (max == min) return min;
            return rand.Next(min, max + 1);
        }

        private static float NextRange(System.Random rand, float min, float max)
        {
            if (rand == null) return min;
            if (max < min)
            {
                (min, max) = (max, min);
            }
            return (float)(min + rand.NextDouble() * (max - min));
        }

        private Vector3 ResolvePlacement(System.Random rand, Vector3 basePos, float halfX, float halfZ, List<Vector3> placed, float minAnchorDist, float minSep, int attempts)
        {
            Vector3 bestPos = basePos + centerOffset;
            float bestScore = -1f;
            var anchorXZ = new Vector2(basePos.x, basePos.z);

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                float offsetX = NextRange(rand, -halfX, halfX);
                float offsetZ = NextRange(rand, -halfZ, halfZ);
                var candidate = basePos + centerOffset + new Vector3(offsetX, 0f, offsetZ);

                if (minAnchorDist > 0f)
                {
                    var candXZ = new Vector2(candidate.x, candidate.z);
                    float anchorDist = Vector2.Distance(candXZ, anchorXZ);
                    if (anchorDist < minAnchorDist)
                    {
                        continue;
                    }
                }

                float nearest = float.PositiveInfinity;
                if (placed.Count > 0)
                {
                    for (int i = 0; i < placed.Count; i++)
                    {
                        var p = placed[i];
                        float dx = candidate.x - p.x;
                        float dz = candidate.z - p.z;
                        float dist = Mathf.Sqrt(dx * dx + dz * dz);
                        if (dist < nearest) nearest = dist;
                    }
                }

                if (minSep <= 0f || placed.Count == 0 || nearest >= minSep)
                {
                    return candidate;
                }

                if (nearest > bestScore)
                {
                    bestScore = nearest;
                    bestPos = candidate;
                }
            }

            return bestPos;
        }
    }
}
