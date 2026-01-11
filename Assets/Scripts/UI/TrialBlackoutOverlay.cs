using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR;
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
        // 推荐开启：用一个“贴在相机前方的黑色 Quad”做遮罩，在 XR/头显上更稳定。
        // 关闭时会退回到 OnGUI 画全屏黑色纹理（某些 XR 模式下可能只影响电脑镜像）。
        [SerializeField] private bool useWorldSpaceQuadForBlackout = true;
        [SerializeField] private bool autoFindHeadCamera = true;
        // 用于挂遮罩的相机（优先 StimulusCapture.HeadCamera，其次 Camera.main；XR 下会尝试找 stereo 相机）。
        [SerializeField] private Camera headCamera;
        // 遮罩缩放冗余（>1 可避免边缘漏光/视锥差异导致的黑边没盖住）。
        [SerializeField] private float quadOverscan = 2.0f;
        // 遮罩离 near clip 的额外偏移（避免与 near clip/其它近处几何发生 Z-fighting）。
        [SerializeField] private float quadNearOffset = 0.02f;

        private bool _visible;
        private Texture2D _tex;
        private Coroutine _pending;
        private GameObject _overlayRoot;
        private GameObject _overlayQuad;
        private MeshRenderer _overlayQuadRenderer;
        private Material _overlayQuadMaterial;

        public bool IsVisible => _visible;

        /// <summary>
        /// 是否在 Trial 结束事件（Completed/Failed/Cancelled）时自动隐藏遮罩。
        /// 某些任务需要把黑屏延续到下一试次再手动关闭，此时可设为 false。
        /// </summary>
        public bool HideOnTrialEnd
        {
            get => hideOnTrialEnd;
            set => hideOnTrialEnd = value;
        }

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
            DestroyOverlay();
        }

        private void OnDestroy()
        {
            CancelPending();
            if (_tex != null)
            {
                Destroy(_tex);
                _tex = null;
            }
            DestroyOverlay();
        }

        public void Hide()
        {
            _visible = false;
            if (_overlayRoot != null) _overlayRoot.SetActive(false);
        }

        public void Show()
        {
            _visible = true;
            EnsureOverlayIfNeeded();
            if (_overlayRoot != null) _overlayRoot.SetActive(true);
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
            // 相机参数/分辨率可能在运行时变化（尤其是 XR），每帧校准遮罩位置/大小。
            UpdateOverlayPlacement();
        }

        private void OnGUI()
        {
            if (!_visible) return;
            // 当启用 world-space quad 时，不使用 OnGUI 路径，避免双重绘制。
            if (useWorldSpaceQuadForBlackout) return;
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
            _tex.SetPixel(0, 0, Color.black);
            _tex.Apply();
        }

        private void EnsureOverlayIfNeeded()
        {
            if (!useWorldSpaceQuadForBlackout) return;
            if (_overlayRoot != null)
            {
                UpdateOverlayPlacement();
                return;
            }

            if (headCamera == null && autoFindHeadCamera)
            {
                // 优先取 StimulusCapture 的头部相机（任务采集用），其次回退到主相机。
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

            // XR/头显环境下常见多相机（UI/镜像/采集）。若当前相机不是立体渲染相机，遮罩会只出现在电脑端镜像。
            if (XRSettings.isDeviceActive && headCamera != null && headCamera.stereoTargetEye == StereoTargetEyeMask.None)
            {
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

            if (headCamera == null) return;

            // 在相机下创建一个根节点，便于整体启用/禁用与清理。
            _overlayRoot = new GameObject("trial_blackout_overlay");
            _overlayRoot.transform.SetParent(headCamera.transform, worldPositionStays: false);
            SetLayerRecursively(_overlayRoot, headCamera.gameObject.layer);

            // A simple black "2D image": an Unlit textured quad placed in front of the camera.
            // 最简遮罩：一个带黑色贴图的 Quad，贴在相机前方，覆盖整个视野。
            _overlayQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _overlayQuad.name = "trial_blackout_quad";
            _overlayQuad.transform.SetParent(_overlayRoot.transform, worldPositionStays: false);
            SetLayerRecursively(_overlayQuad, headCamera.gameObject.layer);

            var col = _overlayQuad.GetComponent<Collider>();
            if (col != null) Destroy(col);

            _overlayQuadRenderer = _overlayQuad.GetComponent<MeshRenderer>();
            if (_overlayQuadRenderer != null)
            {
                _overlayQuadRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _overlayQuadRenderer.receiveShadows = false;
            }

            EnsureTexture();
            var shader =
                Shader.Find("Unlit/Texture") ??
                Shader.Find("Unlit/Color") ??
                Shader.Find("Universal Render Pipeline/Unlit") ??
                Shader.Find("HDRP/Unlit") ??
                Shader.Find("Standard");

            if (shader != null)
            {
                _overlayQuadMaterial = new Material(shader);

                if (_overlayQuadMaterial.HasProperty("_BaseMap")) _overlayQuadMaterial.SetTexture("_BaseMap", _tex);
                if (_overlayQuadMaterial.HasProperty("_MainTex")) _overlayQuadMaterial.SetTexture("_MainTex", _tex);

                if (_overlayQuadMaterial.HasProperty("_BaseColor")) _overlayQuadMaterial.SetColor("_BaseColor", Color.black);
                if (_overlayQuadMaterial.HasProperty("_Color")) _overlayQuadMaterial.SetColor("_Color", Color.black);
                _overlayQuadMaterial.color = Color.black;

                // Try to keep it above other geometry in most pipelines.
                // 注：不同渲染管线/后处理下不保证 100% 置顶，但通常足够作为“黑屏遮罩”。
                _overlayQuadMaterial.renderQueue = 5000;

                if (_overlayQuadRenderer != null) _overlayQuadRenderer.sharedMaterial = _overlayQuadMaterial;
            }

            _overlayRoot.SetActive(_visible);
            UpdateOverlayPlacement();
        }

        private void UpdateOverlayPlacement()
        {
            if (_overlayQuad == null || headCamera == null) return;

            float dist = Mathf.Max(0.01f, headCamera.nearClipPlane + Mathf.Max(0f, quadNearOffset));

            // 放在相机本地 +Z 方向（相机前方）。
            _overlayQuad.transform.localPosition = new Vector3(0f, 0f, dist);
            _overlayQuad.transform.localRotation = Quaternion.identity;

            // Scale from camera FOV/aspect; overscan to avoid edge leaks.
            float fovDeg = Mathf.Clamp(headCamera.fieldOfView, 1f, 179f);
            float halfFovRad = 0.5f * fovDeg * Mathf.Deg2Rad;
            float height = 2f * dist * Mathf.Tan(halfFovRad);
            float width = height * Mathf.Max(0.01f, headCamera.aspect);

            float overscan = Mathf.Max(1f, quadOverscan);
            // XR 双眼视锥可能与 mono 近似计算略有差异，给一点额外冗余更稳。
            if (headCamera.stereoEnabled) overscan *= 1.25f;

            _overlayQuad.transform.localScale = new Vector3(width * overscan, height * overscan, 1f);
        }

        private void DestroyOverlay()
        {
            if (_overlayRoot != null)
            {
                Destroy(_overlayRoot);
                _overlayRoot = null;
            }

            _overlayQuad = null;
            _overlayQuadRenderer = null;

            if (_overlayQuadMaterial != null)
            {
                Destroy(_overlayQuadMaterial);
                _overlayQuadMaterial = null;
            }
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            if (root == null) return;
            root.layer = layer;
            var t = root.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c != null) SetLayerRecursively(c.gameObject, layer);
            }
        }
    }
}
