using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VRPerception.AvatarAction
{
    /// <summary>
    /// 场景预言者，负责将字符串名称解析为场景对象或位置
    /// </summary>
    public class SceneOracle : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private bool autoIndexOnStart = true;
        [SerializeField] private bool enableFuzzyMatching = true;
        [SerializeField] private float maxSearchDistance = 50f;
        [SerializeField] private LayerMask searchLayerMask = -1;
        
        [Header("Custom Mappings")]
        [SerializeField] private NamedObjectMapping[] customMappings;
        
        private readonly Dictionary<string, GameObject> _objectIndex = new Dictionary<string, GameObject>();
        private readonly Dictionary<string, Vector3> _positionIndex = new Dictionary<string, Vector3>();
        private readonly Dictionary<string, string> _aliasIndex = new Dictionary<string, string>();
        
        public int IndexedObjectCount => _objectIndex.Count;
        public int IndexedPositionCount => _positionIndex.Count;
        
        private void Start()
        {
            if (autoIndexOnStart)
            {
                RebuildIndex();
            }
        }
        
        /// <summary>
        /// 重建索引
        /// </summary>
        public void RebuildIndex()
        {
            ClearIndex();
            IndexSceneObjects();
            IndexCustomMappings();
            
            Debug.Log($"[SceneOracle] Indexed {_objectIndex.Count} objects and {_positionIndex.Count} positions");
        }
        
        /// <summary>
        /// 清空索引
        /// </summary>
        private void ClearIndex()
        {
            _objectIndex.Clear();
            _positionIndex.Clear();
            _aliasIndex.Clear();
        }
        
        /// <summary>
        /// 索引场景对象
        /// </summary>
        private void IndexSceneObjects()
        {
            var allObjects = FindObjectsOfType<GameObject>();
            
            foreach (var obj in allObjects)
            {
                if (obj == null || !obj.activeInHierarchy) continue;
                
                // 按名称索引
                var name = obj.name.ToLower();
                if (!_objectIndex.ContainsKey(name))
                {
                    _objectIndex[name] = obj;
                }
                
                // 按标签索引
                if (!string.IsNullOrEmpty(obj.tag) && obj.tag != "Untagged")
                {
                    var tag = obj.tag.ToLower();
                    if (!_objectIndex.ContainsKey(tag))
                    {
                        _objectIndex[tag] = obj;
                    }
                }
                
                // 索引特殊组件
                IndexSpecialComponents(obj);
            }
        }
        
        /// <summary>
        /// 索引特殊组件
        /// </summary>
        private void IndexSpecialComponents(GameObject obj)
        {
            // 索引带有特定组件的对象
            if (obj.GetComponent<Collider>() != null)
            {
                var colliderName = $"{obj.name.ToLower()}_collider";
                _objectIndex[colliderName] = obj;
            }
            
            if (obj.GetComponent<Renderer>() != null)
            {
                var rendererName = $"{obj.name.ToLower()}_renderer";
                _objectIndex[rendererName] = obj;
            }
            
            // 可以根据需要添加更多组件类型
        }
        
        /// <summary>
        /// 索引自定义映射
        /// </summary>
        private void IndexCustomMappings()
        {
            if (customMappings == null) return;
            
            foreach (var mapping in customMappings)
            {
                if (string.IsNullOrEmpty(mapping.name)) continue;
                
                var key = mapping.name.ToLower();
                
                if (mapping.targetObject != null)
                {
                    _objectIndex[key] = mapping.targetObject;
                }
                
                if (mapping.usePosition)
                {
                    _positionIndex[key] = mapping.position;
                }
                
                // 添加别名
                if (mapping.aliases != null)
                {
                    foreach (var alias in mapping.aliases)
                    {
                        if (!string.IsNullOrEmpty(alias))
                        {
                            _aliasIndex[alias.ToLower()] = key;
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// 解析目标对象
        /// </summary>
        public GameObject ResolveObject(string targetName)
        {
            if (string.IsNullOrEmpty(targetName))
                return null;
            
            var key = targetName.ToLower();
            
            // 1. 直接匹配
            if (_objectIndex.TryGetValue(key, out var directMatch))
            {
                return ValidateObject(directMatch) ? directMatch : null;
            }
            
            // 2. 别名匹配
            if (_aliasIndex.TryGetValue(key, out var aliasKey) && 
                _objectIndex.TryGetValue(aliasKey, out var aliasMatch))
            {
                return ValidateObject(aliasMatch) ? aliasMatch : null;
            }
            
            // 3. 模糊匹配
            if (enableFuzzyMatching)
            {
                var fuzzyMatch = FindFuzzyMatch(key);
                if (fuzzyMatch != null && ValidateObject(fuzzyMatch))
                {
                    return fuzzyMatch;
                }
            }
            
            // 4. 运行时搜索
            var runtimeMatch = FindObjectByNameRuntime(targetName);
            if (runtimeMatch != null)
            {
                // 添加到索引以便下次快速查找
                _objectIndex[key] = runtimeMatch;
                return runtimeMatch;
            }
            
            return null;
        }
        
        /// <summary>
        /// 解析目标位置
        /// </summary>
        public Vector3? ResolvePosition(string targetName)
        {
            if (string.IsNullOrEmpty(targetName))
                return null;
            
            var key = targetName.ToLower();
            
            // 1. 直接位置匹配
            if (_positionIndex.TryGetValue(key, out var position))
            {
                return position;
            }
            
            // 2. 对象位置
            var obj = ResolveObject(targetName);
            if (obj != null)
            {
                return obj.transform.position;
            }
            
            // 3. 尝试解析坐标字符串 (x,y,z)
            if (TryParseCoordinates(targetName, out var coordinates))
            {
                return coordinates;
            }
            
            return null;
        }
        
        /// <summary>
        /// 获取可见对象列表
        /// </summary>
        public List<string> GetVisibleObjects(Vector3 fromPosition, float maxDistance = 0f)
        {
            var visibleObjects = new List<string>();
            var searchDistance = maxDistance > 0 ? maxDistance : maxSearchDistance;
            
            foreach (var kvp in _objectIndex)
            {
                var obj = kvp.Value;
                if (!ValidateObject(obj)) continue;
                
                var distance = Vector3.Distance(fromPosition, obj.transform.position);
                if (distance <= searchDistance)
                {
                    // 简单的可见性检查（可以扩展为更复杂的射线检测）
                    if (IsObjectVisible(fromPosition, obj))
                    {
                        visibleObjects.Add(kvp.Key);
                    }
                }
            }
            
            return visibleObjects;
        }
        
        /// <summary>
        /// 获取最近的对象
        /// </summary>
        public GameObject GetNearestObject(Vector3 fromPosition, string nameFilter = null)
        {
            GameObject nearest = null;
            float nearestDistance = float.MaxValue;
            
            foreach (var kvp in _objectIndex)
            {
                var obj = kvp.Value;
                if (!ValidateObject(obj)) continue;
                
                // 名称过滤
                if (!string.IsNullOrEmpty(nameFilter) && 
                    !kvp.Key.Contains(nameFilter.ToLower()))
                    continue;
                
                var distance = Vector3.Distance(fromPosition, obj.transform.position);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = obj;
                }
            }
            
            return nearest;
        }
        
        /// <summary>
        /// 模糊匹配
        /// </summary>
        private GameObject FindFuzzyMatch(string targetName)
        {
            var bestMatch = "";
            var bestScore = 0f;
            
            foreach (var key in _objectIndex.Keys)
            {
                var score = CalculateSimilarity(targetName, key);
                if (score > bestScore && score > 0.6f) // 相似度阈值
                {
                    bestScore = score;
                    bestMatch = key;
                }
            }
            
            return !string.IsNullOrEmpty(bestMatch) ? _objectIndex[bestMatch] : null;
        }
        
        /// <summary>
        /// 计算字符串相似度
        /// </summary>
        private float CalculateSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return 0f;
            
            // 简单的相似度计算（可以使用更复杂的算法如编辑距离）
            if (a.Contains(b) || b.Contains(a))
                return 0.8f;
            
            var commonChars = a.Intersect(b).Count();
            var maxLength = Mathf.Max(a.Length, b.Length);
            
            return (float)commonChars / maxLength;
        }
        
        /// <summary>
        /// 运行时查找对象
        /// </summary>
        private GameObject FindObjectByNameRuntime(string name)
        {
            // 尝试直接查找
            var obj = GameObject.Find(name);
            if (obj != null) return obj;
            
            // 尝试查找所有对象中的匹配
            var allObjects = FindObjectsOfType<GameObject>();
            return allObjects.FirstOrDefault(o => 
                o.name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        
        /// <summary>
        /// 验证对象是否有效
        /// </summary>
        private bool ValidateObject(GameObject obj)
        {
            return obj != null && obj.activeInHierarchy;
        }
        
        /// <summary>
        /// 检查对象是否可见
        /// </summary>
        private bool IsObjectVisible(Vector3 fromPosition, GameObject obj)
        {
            // 简单的射线检测
            var direction = (obj.transform.position - fromPosition).normalized;
            var distance = Vector3.Distance(fromPosition, obj.transform.position);
            
            if (Physics.Raycast(fromPosition, direction, out var hit, distance, searchLayerMask))
            {
                return hit.collider.gameObject == obj;
            }
            
            return true; // 如果没有碰撞，假设可见
        }
        
        /// <summary>
        /// 尝试解析坐标字符串
        /// </summary>
        private bool TryParseCoordinates(string coordString, out Vector3 coordinates)
        {
            coordinates = Vector3.zero;
            
            // 移除括号和空格
            coordString = coordString.Trim('(', ')', '[', ']', '{', '}').Replace(" ", "");
            
            var parts = coordString.Split(',');
            if (parts.Length == 3)
            {
                if (float.TryParse(parts[0], out var x) &&
                    float.TryParse(parts[1], out var y) &&
                    float.TryParse(parts[2], out var z))
                {
                    coordinates = new Vector3(x, y, z);
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// 获取建议的对象名称列表
        /// </summary>
        public List<string> GetSuggestedNames(string partialName = null, int maxResults = 10)
        {
            var suggestions = new List<string>();
            
            if (string.IsNullOrEmpty(partialName))
            {
                suggestions.AddRange(_objectIndex.Keys.Take(maxResults));
            }
            else
            {
                var partial = partialName.ToLower();
                suggestions.AddRange(_objectIndex.Keys
                    .Where(key => key.Contains(partial))
                    .Take(maxResults));
            }
            
            return suggestions;
        }
        
        /// <summary>
        /// 添加运行时映射
        /// </summary>
        public void AddMapping(string name, GameObject obj, Vector3? position = null)
        {
            if (string.IsNullOrEmpty(name)) return;
            
            var key = name.ToLower();
            
            if (obj != null)
            {
                _objectIndex[key] = obj;
            }
            
            if (position.HasValue)
            {
                _positionIndex[key] = position.Value;
            }
        }
        
        /// <summary>
        /// 移除映射
        /// </summary>
        public void RemoveMapping(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            
            var key = name.ToLower();
            _objectIndex.Remove(key);
            _positionIndex.Remove(key);
        }
    }
    
    /// <summary>
    /// 命名对象映射配置
    /// </summary>
    [Serializable]
    public class NamedObjectMapping
    {
        public string name;
        public GameObject targetObject;
        public bool usePosition;
        public Vector3 position;
        public string[] aliases;
    }
}
