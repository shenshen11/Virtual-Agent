using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 挂到任意 prefab/实例根节点，用于测量 Renderer 包围盒并给出 Scale Factor 建议。
/// 默认目标是让最大边约等于 1 米，便于与 SemanticSizeBiasTask 的基准尺寸配合。
/// </summary>
public class MeasureAndSuggest : MonoBehaviour
{
    [Tooltip("希望物体最大边变成的目标尺寸（米）")]
    public float targetMaxSize = 1f;

    [Tooltip("进入 Play 时自动打印一次信息")]
    public bool logOnStart = true;

    private void Start()
    {
        if (logOnStart)
            PrintInfo();
    }

#if UNITY_EDITOR
    [ContextMenu("Print Bounds & Suggested Scale")]
#endif
    public void PrintInfo()
    {
        var renderers = GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            Debug.Log($"{name}: 无 Renderer");
            return;
        }

        var bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        var size = bounds.size;
        float maxDim = Mathf.Max(size.x, size.y, size.z);
        float suggested = maxDim > 1e-4f ? targetMaxSize / maxDim : 1f;

        Debug.Log($"{name}: 尺寸 {size} (最大边 {maxDim}), " +
                  $"若想最大边≈{targetMaxSize}m, 可将模型 Importer 的 Scale Factor 设为 ≈ {suggested:0.###}");
    }
}
