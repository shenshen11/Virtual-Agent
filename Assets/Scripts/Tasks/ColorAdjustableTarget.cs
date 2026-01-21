using UnityEngine;

namespace VRPerception.Tasks
{
    /// <summary>
    /// 可被人类输入面板调节颜色的目标物体。
    /// </summary>
    public sealed class ColorAdjustableTarget : MonoBehaviour
    {
        [SerializeField] private Renderer targetRenderer;
        [SerializeField] private Color initialColor = Color.gray;

        private Material _runtimeMaterial;
        private Color _currentColor = Color.gray;

        public Color CurrentColor => _currentColor;

        private void Awake()
        {
            if (targetRenderer == null)
            {
                targetRenderer = GetComponentInChildren<Renderer>();
            }

            if (targetRenderer != null)
            {
                var source = targetRenderer.sharedMaterial;
                _runtimeMaterial = source != null ? new Material(source) : new Material(Shader.Find("Standard"));
                targetRenderer.material = _runtimeMaterial;
            }

            SetColor(initialColor);
        }

        public void SetColor(Color color)
        {
            _currentColor = color;
            ApplyColor(color);
        }

        private void ApplyColor(Color color)
        {
            if (_runtimeMaterial != null)
            {
                _runtimeMaterial.color = color;
            }
            else if (targetRenderer != null)
            {
                targetRenderer.material.color = color;
            }
        }
    }
}
