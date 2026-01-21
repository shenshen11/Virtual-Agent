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

        [Header("Placement")]
        [SerializeField] private bool alignToFloor = true;
        [SerializeField] private float floorY = 0f;
        [SerializeField] private bool randomYaw = true;
        [SerializeField] private Vector2 scaleRange = new Vector2(0.8f, 1.2f);
        [SerializeField] private bool parentToThis = true;

        private readonly List<GameObject> _spawned = new List<GameObject>();

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

            for (int i = 0; i < count; i++)
            {
                var prefab = furniturePrefabs[rand.Next(furniturePrefabs.Count)];
                if (prefab == null) continue;

                var go = Instantiate(prefab);
                if (parentToThis)
                {
                    go.transform.SetParent(transform, true);
                }

                float offsetX = NextRange(rand, -halfX, halfX);
                float offsetZ = NextRange(rand, -halfZ, halfZ);
                var pos = basePos + centerOffset + new Vector3(offsetX, 0f, offsetZ);
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
    }
}
