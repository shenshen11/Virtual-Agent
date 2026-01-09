using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.UI;
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
        private GameObject _overlayRoot;
        private Canvas _overlayCanvas;
        private Image _overlayImage;

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
            UpdateOverlayPlacement();
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

            _overlayRoot = new GameObject("trial_blackout_overlay");
            _overlayRoot.transform.SetParent(headCamera.transform, worldPositionStays: false);
            SetLayerRecursively(_overlayRoot, headCamera.gameObject.layer);

            _overlayCanvas = _overlayRoot.AddComponent<Canvas>();
            _overlayCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            _overlayCanvas.worldCamera = headCamera;
            _overlayCanvas.overrideSorting = true;
            _overlayCanvas.sortingOrder = short.MaxValue;

            // Do not add GraphicRaycaster; this overlay should never block UI input.
            var scaler = _overlayRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            var imageGo = new GameObject("BlackImage", typeof(RectTransform), typeof(Image));
            imageGo.transform.SetParent(_overlayRoot.transform, worldPositionStays: false);
            SetLayerRecursively(imageGo, headCamera.gameObject.layer);

            _overlayImage = imageGo.GetComponent<Image>();
            _overlayImage.color = Color.black;
            _overlayImage.raycastTarget = false;

            var rt = (RectTransform)imageGo.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = Vector2.zero;

            _overlayRoot.SetActive(_visible);
            UpdateOverlayPlacement();
        }

        private void UpdateOverlayPlacement()
        {
            if (_overlayCanvas == null || headCamera == null) return;

            float dist = Mathf.Max(0.01f, headCamera.nearClipPlane + Mathf.Max(0f, quadNearOffset));
            _overlayCanvas.planeDistance = dist;

            var overlayRt = _overlayRoot != null ? _overlayRoot.GetComponent<RectTransform>() : null;
            if (overlayRt != null)
            {
                overlayRt.localPosition = Vector3.zero;
                overlayRt.localRotation = Quaternion.Euler(0f, 180f, 0f);
                overlayRt.localScale = Vector3.one;
                // For ScreenSpaceCamera + stretched anchors, sizeDelta=0 means full viewport.
                overlayRt.sizeDelta = Vector2.zero;
            }
        }

        private void DestroyOverlay()
        {
            if (_overlayRoot != null)
            {
                Destroy(_overlayRoot);
                _overlayRoot = null;
            }
            _overlayCanvas = null;
            _overlayImage = null;
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
