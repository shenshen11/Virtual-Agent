using System;
using System.Collections;
using UnityEngine;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;

namespace VRPerception.UI
{
    /// <summary>
    /// Trial 黑屏遮罩（用于 Human 短时曝光后阻断二次观察）
    /// - 任务可调用 BeginBlackoutAfterMs(exposureMs) 来延迟黑屏
    /// - Trial 结束（Completed/Failed/Cancelled）自动隐藏
    /// </summary>
    public class TrialBlackoutOverlay : MonoBehaviour
    {
        [Header("Event Bus")]
        [SerializeField] private EventBusManager eventBus;
        [SerializeField] private bool autoFindEventBus = true;

        [Header("Behavior")]
        [SerializeField] private bool hideOnTrialEnd = true;

        [Header("Rendering")]
        [SerializeField] private int guiDepth = -2000;
        [Header("World Space")]
        [SerializeField] private bool useWorldSpaceQuadForBlackout = true;
        [SerializeField] private bool autoFindHeadCamera = true;
        [SerializeField] private Camera headCamera;
        [SerializeField] private float quadOverscan = 1.1f;
        [SerializeField] private float quadNearOffset = 0.02f;

        private bool _visible;
        private Texture2D _tex;
        private Coroutine _pending;
        private GameObject _quad;
        private Renderer _quadRenderer;
        private Material _quadMaterial;

        public bool IsVisible => _visible;

        private void Awake()
        {
            if (autoFindEventBus && eventBus == null)
                eventBus = EventBusManager.Instance;

            EnsureTexture();
        }

        private void OnEnable()
        {
            EnsureTexture();
            if (eventBus != null)
            {
                eventBus.TrialLifecycle?.Subscribe(OnTrialLifecycle);
            }
        }

        private void OnDisable()
        {
            try { eventBus?.TrialLifecycle?.Unsubscribe(OnTrialLifecycle); } catch { }
            CancelPending();
            Hide();
            DestroyQuad();
        }

        private void OnDestroy()
        {
            CancelPending();
            if (_tex != null)
            {
                Destroy(_tex);
                _tex = null;
            }
            DestroyQuad();
        }

        public void Hide()
        {
            _visible = false;
            if (_quad != null) _quad.SetActive(false);
        }

        public void Show()
        {
            _visible = true;
            EnsureQuadIfNeeded();
            if (_quad != null) _quad.SetActive(true);
        }

        public void BeginBlackoutAfterMs(int delayMs)
        {
            CancelPending();
            Hide();

            if (delayMs <= 0)
            {
                Show();
                return;
            }

            _pending = StartCoroutine(DelayShow(delayMs));
        }

        private IEnumerator DelayShow(int delayMs)
        {
            float t = 0f;
            float target = delayMs / 1000f;
            while (t < target)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            Show();
            _pending = null;
        }

        private void CancelPending()
        {
            if (_pending != null)
            {
                try { StopCoroutine(_pending); } catch { }
                _pending = null;
            }
        }

        private void OnTrialLifecycle(TrialLifecycleEventData data)
        {
            if (!hideOnTrialEnd || data == null) return;
            if (data.state == TrialLifecycleState.Completed ||
                data.state == TrialLifecycleState.Failed ||
                data.state == TrialLifecycleState.Cancelled)
            {
                CancelPending();
                Hide();
            }
        }

        private void LateUpdate()
        {
            UpdateQuadTransform();
        }

        private void OnGUI()
        {
            if (!_visible) return;
            EnsureTexture();
            GUI.depth = guiDepth;
            var c = GUI.color;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _tex, ScaleMode.StretchToFill);
            GUI.color = c;
        }

        private void EnsureTexture()
        {
            if (_tex != null) return;
            _tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _tex.SetPixel(0, 0, Color.white);
            _tex.Apply();
        }

        private void EnsureQuadIfNeeded()
        {
            if (!useWorldSpaceQuadForBlackout) return;
            if (_quad != null)
            {
                UpdateQuadTransform();
                return;
            }

            if (headCamera == null && autoFindHeadCamera)
            {
                var stim = FindObjectOfType<StimulusCapture>();
                headCamera = stim != null ? stim.HeadCamera : null;
                if (headCamera == null) headCamera = Camera.main;
                if (headCamera == null)
                {
                    // 优先寻找用于立体渲染的相机（XR 头显渲染）
                    var cams = Camera.allCameras;
                    for (int i = 0; i < cams.Length; i++)
                    {
                        var cam = cams[i];
                        if (cam != null && cam.stereoTargetEye != StereoTargetEyeMask.None)
                        {
                            headCamera = cam;
                            break;
                        }
                    }
                }
                if (headCamera == null) headCamera = FindObjectOfType<Camera>();
            }

            if (headCamera == null) return;

            _quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _quad.name = "trial_blackout_quad";
            var col = _quad.GetComponent<Collider>();
            if (col != null) Destroy(col);

            _quadRenderer = _quad.GetComponent<Renderer>();
            if (_quadRenderer != null)
            {
                var shader =
                    Shader.Find("Universal Render Pipeline/Unlit") ??
                    Shader.Find("Unlit/Color") ??
                    Shader.Find("Sprites/Default");

                if (shader != null)
                {
                    _quadMaterial = new Material(shader);
                    if (_quadMaterial.HasProperty("_BaseColor")) _quadMaterial.SetColor("_BaseColor", Color.black);
                    else if (_quadMaterial.HasProperty("_Color")) _quadMaterial.SetColor("_Color", Color.black);
                    else _quadMaterial.color = Color.black;

                    // Render late; still relies on distance to camera for occlusion ordering.
                    _quadMaterial.renderQueue = 5000;
                    _quadMaterial.SetInt("_ZWrite", 0);
                    _quadMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                    _quadRenderer.material = _quadMaterial;
                }
                if (_quadRenderer != null)
                {
                    _quadRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    _quadRenderer.receiveShadows = false;
                }
            }

            _quad.transform.SetParent(headCamera.transform, worldPositionStays: false);
            _quad.SetActive(_visible);
            UpdateQuadTransform();
        }

        private void UpdateQuadTransform()
        {
            if (_quad == null || headCamera == null) return;

            float dist = Mathf.Max(0.01f, headCamera.nearClipPlane + Mathf.Max(0f, quadNearOffset));
            float halfH = Mathf.Tan(headCamera.fieldOfView * Mathf.Deg2Rad * 0.5f) * dist;
            float height = 2f * halfH;
            float width = height * Mathf.Max(0.1f, headCamera.aspect);

            _quad.transform.localPosition = new Vector3(0f, 0f, dist);
            _quad.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            _quad.transform.localScale = new Vector3(width * quadOverscan, height * quadOverscan, 1f);
        }

        private void DestroyQuad()
        {
            if (_quad != null)
            {
                Destroy(_quad);
                _quad = null;
            }
            if (_quadMaterial != null)
            {
                Destroy(_quadMaterial);
                _quadMaterial = null;
            }
            _quadRenderer = null;
        }
    }
}
