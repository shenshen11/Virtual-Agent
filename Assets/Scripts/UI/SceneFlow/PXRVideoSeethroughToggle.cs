using UnityEngine;
using Unity.XR.PXR;

namespace VRPerception.UI
{
    /// <summary>
    /// Toggles PXR video see-through using official PICO API.
    /// Attach this to any active GameObject in the scene.
    /// </summary>
    public class PXRVideoSeethroughToggle : MonoBehaviour
    {
        [Header("Behavior")]
        [SerializeField] private bool applyOnEnable = false;
        [SerializeField] private bool enableOnEnable = true;
        [SerializeField] private bool disableOnDisable = false;
        [SerializeField] private bool disableOnDestroy = true;
        [SerializeField] private bool listenStatus = false;
        [SerializeField] private bool verboseLog = false;

        private PxrVstStatus _lastStatus = PxrVstStatus.Disabled;

        private void OnEnable()
        {
            if (listenStatus)
            {
                PXR_Manager.VstDisplayStatusChanged += OnVstDisplayStatusChanged;
            }

            if (applyOnEnable)
            {
                SetEnabled(enableOnEnable);
            }
        }

        private void OnDisable()
        {
            if (listenStatus)
            {
                PXR_Manager.VstDisplayStatusChanged -= OnVstDisplayStatusChanged;
            }

            if (disableOnDisable)
            {
                SetEnabled(false);
            }
        }

        private void OnDestroy()
        {
            if (listenStatus)
            {
                PXR_Manager.VstDisplayStatusChanged -= OnVstDisplayStatusChanged;
            }

            if (disableOnDestroy)
            {
                SetEnabled(false);
            }
        }

        /// <summary>
        /// Enable/disable PXR video see-through.
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            PXR_Manager.EnableVideoSeeThrough = enabled;
            if (verboseLog)
            {
                Debug.Log($"[PXRVideoSeethroughToggle] EnableVideoSeeThrough = {enabled}");
            }
        }

        private void OnVstDisplayStatusChanged(PxrVstStatus status)
        {
            _lastStatus = status;
            if (verboseLog)
            {
                Debug.Log($"[PXRVideoSeethroughToggle] VST status = {status}");
            }
        }
    }
}
