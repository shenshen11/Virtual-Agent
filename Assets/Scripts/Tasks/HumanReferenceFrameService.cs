using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VRPerception.Tasks
{
    /// <summary>
    /// Human 模式专用的头部参考系标定服务。
    /// 仅在任务开始前采样一次稳定头位姿，并在任务期间作为公共参考系提供给任务读取。
    /// </summary>
    public sealed class HumanReferenceFrameService : MonoBehaviour
    {
        [Header("Calibration")]
        [SerializeField] private float fixationDistanceM = 2.0f;
        [SerializeField] private float stableWindowMs = 3000f;
        [SerializeField] private float yawThresholdDeg = 2f;
        [SerializeField] private float positionThresholdM = 0.03f;
        [SerializeField] private float timeoutSeconds = 10f;
        [SerializeField] private float calibrationStartDelaySeconds = 5f;

        [Header("Fixation UI")]
        [SerializeField] private float fixationScaleM = 0.025f;
        [SerializeField] private Color fixationColor = Color.red;

        private HumanFixationPresenter _presenter;

        public bool HasReferenceFrame { get; private set; }
        public Vector3 Origin { get; private set; }
        public Vector3 Forward { get; private set; } = Vector3.forward;
        public Vector3 Right => Vector3.Cross(Vector3.up, Forward).normalized;
        public float EyeY { get; private set; }

        public async Task<bool> CalibrateAsync(Camera headCamera, Transform xrRigTransform, CancellationToken ct, Action<string> onSubphaseChanged = null)
        {
            Clear();

            if (headCamera == null)
            {
                Debug.LogWarning("[HumanReferenceFrameService] Calibration skipped: HeadCamera is missing.");
                return false;
            }

            EnsurePresenter();
            onSubphaseChanged?.Invoke("pre_delay");
            if (calibrationStartDelaySeconds > 0f)
            {
                Debug.Log($"[HumanReferenceFrameService] Waiting {calibrationStartDelaySeconds:F2}s before calibration to let XR pose stabilize.");
                float delayDeadline = Time.unscaledTime + calibrationStartDelaySeconds;
                while (Time.unscaledTime < delayDeadline)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
            }

            Vector3 referenceForward = ResolveReferenceForward(xrRigTransform, headCamera.transform);
            Vector3 fixationWorldPosition = ResolveFixationWorldPosition(xrRigTransform, headCamera.transform.position, referenceForward);
            int renderLayer = headCamera.gameObject.layer;
            onSubphaseChanged?.Invoke("fixation");
            LogCalibrationState("pre-show", headCamera, xrRigTransform, fixationWorldPosition, referenceForward);
            _presenter?.Show(fixationWorldPosition, headCamera.transform.position, fixationScaleM, fixationColor, renderLayer);

            try
            {
                float startedAt = Time.unscaledTime;
                float stableStartAt = startedAt;

                PoseSample lastSample = CaptureSample(headCamera, referenceForward);
                PoseSample stableAnchor = lastSample;

                while ((Time.unscaledTime - startedAt) < timeoutSeconds)
                {
                    ct.ThrowIfCancellationRequested();

                    PoseSample sample = CaptureSample(headCamera, referenceForward);
                    lastSample = sample;

                    if (IsStable(stableAnchor, sample))
                    {
                        if ((Time.unscaledTime - stableStartAt) * 1000f >= stableWindowMs)
                        {
                            ApplySample(sample);
                            LogCalibrationState("stable-sample", headCamera, xrRigTransform, fixationWorldPosition, referenceForward);
                            return true;
                        }
                    }
                    else
                    {
                        stableAnchor = sample;
                        stableStartAt = Time.unscaledTime;
                    }

                    await Task.Yield();
                }

                ApplySample(lastSample);
                LogCalibrationState("timeout-sample", headCamera, xrRigTransform, fixationWorldPosition, referenceForward);
                Debug.LogWarning("[HumanReferenceFrameService] Calibration timed out. Using last sampled head pose.");
                return true;
            }
            finally
            {
                _presenter?.Hide();
            }
        }

        public void Clear()
        {
            HasReferenceFrame = false;
            Origin = Vector3.zero;
            Forward = Vector3.forward;
            EyeY = 0f;
            _presenter?.Hide();
        }

        private void EnsurePresenter()
        {
            if (_presenter != null) return;

            _presenter = GetComponentInChildren<HumanFixationPresenter>(includeInactive: true);
            if (_presenter != null) return;

            var go = new GameObject("HumanFixationPresenter");
            go.transform.SetParent(transform, false);
            _presenter = go.AddComponent<HumanFixationPresenter>();
        }

        private void ApplySample(PoseSample sample)
        {
            Origin = sample.position;
            Forward = sample.forward;
            EyeY = sample.position.y;
            HasReferenceFrame = true;
        }

        private bool IsStable(PoseSample anchor, PoseSample current)
        {
            float positionDelta = Vector3.Distance(anchor.position, current.position);
            float yawDelta = Vector3.Angle(anchor.forward, current.forward);
            return positionDelta <= positionThresholdM && yawDelta <= yawThresholdDeg;
        }

        private Vector3 ResolveFixationWorldPosition(Transform xrRigTransform, Vector3 fallbackPosition, Vector3 referenceForward)
        {
            return fallbackPosition + referenceForward * Mathf.Max(0.1f, fixationDistanceM);
        }

        private static Vector3 ResolveReferenceForward(Transform xrRigTransform, Transform headTransform)
        {
            Vector3 forward = xrRigTransform != null ? xrRigTransform.forward : headTransform.forward;
            forward = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f) forward = headTransform.forward;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();
            return forward;
        }

        private static PoseSample CaptureSample(Camera headCamera, Vector3 referenceForward)
        {
            Vector3 forward = referenceForward;
            if (forward.sqrMagnitude < 1e-6f)
            {
                forward = Vector3.ProjectOnPlane(headCamera.transform.forward, Vector3.up);
                if (forward.sqrMagnitude < 1e-6f) forward = headCamera.transform.forward;
                if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
                forward.Normalize();
            }

            return new PoseSample
            {
                position = headCamera.transform.position,
                forward = forward
            };
        }

        private void LogCalibrationState(string phase, Camera headCamera, Transform xrRigTransform, Vector3 fixationWorldPosition, Vector3 referenceForward)
        {
            if (headCamera == null) return;

            Vector3 headPosition = headCamera.transform.position;
            Vector3 rigPosition = xrRigTransform != null ? xrRigTransform.position : Vector3.zero;
            string rigName = xrRigTransform != null ? xrRigTransform.name : "null";

            Debug.Log(
                "[HumanReferenceFrameService] Calibration " + phase +
                $" | head={headPosition:F3}" +
                $" | headY={headPosition.y:F3}" +
                $" | rig={rigName}@{rigPosition:F3}" +
                $" | fixation={fixationWorldPosition:F3}" +
                $" | fixationY={fixationWorldPosition.y:F3}" +
                $" | forward={referenceForward:F3}");
        }

        private struct PoseSample
        {
            public Vector3 position;
            public Vector3 forward;
        }
    }

    /// <summary>
    /// 极简 fixation 点显示器：在世界坐标中显示固定的十字注视点。
    /// </summary>
    public sealed class HumanFixationPresenter : MonoBehaviour
    {
        private GameObject _crossRoot;
        private Renderer[] _renderers;

        public void Show(Vector3 worldPosition, Vector3 headPosition, float scaleM, Color color, int layer)
        {
            EnsureCross();
            transform.SetParent(null, true);
            transform.position = worldPosition;
            transform.rotation = Quaternion.identity;
            transform.localScale = Vector3.one;
            SetLayerRecursively(gameObject, layer);

            if (_crossRoot != null)
            {
                float size = Mathf.Max(0.001f, scaleM * 3f);
                float armLength = size;
                float armThickness = Mathf.Max(0.001f, size * 0.18f);

                _crossRoot.transform.localPosition = Vector3.zero;
                _crossRoot.transform.localRotation = Quaternion.identity;
                _crossRoot.transform.localScale = Vector3.one;

                Transform horizontal = _crossRoot.transform.Find("Horizontal");
                Transform vertical = _crossRoot.transform.Find("Vertical");

                if (horizontal != null)
                {
                    horizontal.localPosition = Vector3.zero;
                    horizontal.localRotation = Quaternion.identity;
                    horizontal.localScale = new Vector3(armLength, armThickness, armThickness);
                }

                if (vertical != null)
                {
                    vertical.localPosition = Vector3.zero;
                    vertical.localRotation = Quaternion.identity;
                    vertical.localScale = new Vector3(armThickness, armLength, armThickness);
                }

                _crossRoot.SetActive(true);
            }

            if (_renderers != null)
            {
                foreach (var renderer in _renderers)
                {
                    if (renderer != null && renderer.sharedMaterial != null)
                    {
                        renderer.sharedMaterial.color = color;
                    }
                }
            }

            gameObject.SetActive(true);
        }

        public void Hide()
        {
            if (_crossRoot != null) _crossRoot.SetActive(false);
            gameObject.SetActive(false);
        }

        private void EnsureCross()
        {
            if (_crossRoot != null) return;

            _crossRoot = new GameObject("FixationCross");
            _crossRoot.transform.SetParent(transform, false);

            var horizontal = CreateCrossArm("Horizontal");
            var vertical = CreateCrossArm("Vertical");
            horizontal.transform.SetParent(_crossRoot.transform, false);
            vertical.transform.SetParent(_crossRoot.transform, false);

            _renderers = new[]
            {
                horizontal.GetComponent<Renderer>(),
                vertical.GetComponent<Renderer>()
            };
        }

        private static GameObject CreateCrossArm(string name)
        {
            var arm = GameObject.CreatePrimitive(PrimitiveType.Cube);
            arm.name = name;

            var collider = arm.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.Destroy(collider);
            }

            var renderer = arm.GetComponent<Renderer>();
            if (renderer != null)
            {
                var shader =
                    Shader.Find("Unlit/Color") ??
                    Shader.Find("Universal Render Pipeline/Unlit") ??
                    Shader.Find("HDRP/Unlit") ??
                    Shader.Find("Standard");

                if (shader != null)
                {
                    var material = new Material(shader);
                    if (material.HasProperty("_Color")) material.color = Color.red;
                    material.renderQueue = 4000;
                    renderer.sharedMaterial = material;
                }

                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            return arm;
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            if (root == null) return;

            root.layer = layer;
            for (int i = 0; i < root.transform.childCount; i++)
            {
                var child = root.transform.GetChild(i);
                if (child != null)
                {
                    SetLayerRecursively(child.gameObject, layer);
                }
            }
        }
    }
}
