using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRPerception.Tasks
{
    /// <summary>
    /// 以固定槽位摆放家具 Prefab：每个槽位显式指定 prefab、相对锚点的偏移、yaw 与缩放。
    /// 同一份配置在任何 run / seed 下都得到完全一致的家具布局，便于复现。
    /// </summary>
    public sealed class FurnitureScatter : MonoBehaviour
    {
        [Serializable]
        public struct FurnitureSlot
        {
            [Tooltip("要实例化的家具 Prefab。")]
            public GameObject prefab;

            [Tooltip("相对锚点前向坐标系的偏移（米）：x=右, y=高度增量, z=前。useAnchorFrame=false 时按世界坐标解释。")]
            public Vector3 localOffset;

            [Tooltip("相对锚点前向的 yaw（度，绕世界 Y）。faceAnchor=true 时忽略。")]
            public float yawDegrees;

            [Tooltip("统一缩放系数，<=0 视为 1。")]
            public float uniformScale;

            [Tooltip("true 时朝向锚点（投影到水平面），忽略 yawDegrees。")]
            public bool faceAnchor;
        }

        [Header("Slots (Fixed Layout)")]
        [Tooltip("固定槽位列表：按列表顺序实例化，位姿完全可复现。")]
        [SerializeField] private List<FurnitureSlot> slots = new List<FurnitureSlot>();

        [Header("Layout")]
        [Tooltip("true 时使用锚点位置 + 投影前向作为局部坐标系；false 时直接以世界坐标解释 localOffset。")]
        [SerializeField] private bool useAnchorFrame = true;
        [Tooltip("true 时将每个家具的 y 强制对齐到 floorY。")]
        [SerializeField] private bool alignToFloor = true;
        [SerializeField] private float floorY = 0f;
        [Tooltip("true 时把生成的家具父挂在本组件 transform 下。")]
        [SerializeField] private bool parentToThis = true;

        // === Legacy fields (deprecated) ===
        // 仅为保持现有 .unity 序列化兼容而保留，运行时不再消费。
        // 若未来需要彻底清理，可在确认所有场景配置已迁移到 slots 后删除。
#pragma warning disable 0414
        [HideInInspector] [SerializeField] private List<GameObject> furniturePrefabs = new List<GameObject>();
        [HideInInspector] [SerializeField] private int minCount = 0;
        [HideInInspector] [SerializeField] private int maxCount = 0;
        [HideInInspector] [SerializeField] private Vector3 centerOffset = Vector3.zero;
        [HideInInspector] [SerializeField] private Vector3 areaSize = Vector3.zero;
        [HideInInspector] [SerializeField] private float minDistanceFromAnchor = 0f;
        [HideInInspector] [SerializeField] private float minSeparation = 0f;
        [HideInInspector] [SerializeField] private int maxPlacementAttempts = 0;
        [HideInInspector] [SerializeField] private bool randomYaw = false;
        [HideInInspector] [SerializeField] private Vector2 scaleRange = new Vector2(1f, 1f);
#pragma warning restore 0414

        private readonly List<GameObject> _spawned = new List<GameObject>();

        public bool HasSpawned => _spawned.Count > 0;

        /// <summary>
        /// 按固定槽位列表实例化家具。幂等：若已生成则直接返回，不会重复实例化。
        /// 参数 <paramref name="rand"/> 与 <paramref name="countOverride"/> 仅为 API 兼容保留，已忽略。
        /// </summary>
        public void Spawn(System.Random rand = null, Transform anchor = null, int? countOverride = null)
        {
            if (HasSpawned) return;
            if (slots == null || slots.Count == 0) return;

            ResolveAnchorFrame(anchor, out var origin, out var forward, out var right);
            float baseYawDeg = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;

            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (slot.prefab == null) continue;

                var go = Instantiate(slot.prefab);
                if (parentToThis)
                {
                    go.transform.SetParent(transform, true);
                }

                var pos = origin
                          + right * slot.localOffset.x
                          + Vector3.up * slot.localOffset.y
                          + forward * slot.localOffset.z;
                if (alignToFloor)
                {
                    pos.y = floorY;
                }
                go.transform.position = pos;

                Quaternion rot;
                if (slot.faceAnchor)
                {
                    var toAnchor = origin - pos;
                    toAnchor.y = 0f;
                    if (toAnchor.sqrMagnitude < 1e-6f)
                    {
                        rot = Quaternion.Euler(0f, baseYawDeg, 0f);
                    }
                    else
                    {
                        rot = Quaternion.LookRotation(toAnchor.normalized, Vector3.up);
                    }
                }
                else
                {
                    rot = Quaternion.Euler(0f, baseYawDeg + slot.yawDegrees, 0f);
                }
                go.transform.rotation = rot;

                float scale = slot.uniformScale > 0.0001f ? slot.uniformScale : 1f;
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

        private void ResolveAnchorFrame(Transform anchor, out Vector3 origin, out Vector3 forward, out Vector3 right)
        {
            if (useAnchorFrame && anchor != null)
            {
                origin = anchor.position;
                forward = Vector3.ProjectOnPlane(anchor.forward, Vector3.up);
                if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
                forward.Normalize();
            }
            else
            {
                origin = Vector3.zero;
                forward = Vector3.forward;
            }

            right = Vector3.Cross(Vector3.up, forward);
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            right.Normalize();
        }
    }
}
