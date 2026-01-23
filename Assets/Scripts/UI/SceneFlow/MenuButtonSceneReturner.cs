using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;
using VRPerception.Orchestration;
using VRPerception.Tasks;

namespace VRPerception.UI
{
    /// <summary>
    /// Returns to MainMenu when left controller menu button is pressed.
    /// </summary>
    public class MenuButtonSceneReturner : MonoBehaviour
    {
        [Header("Scene")]
        [SerializeField] private string mainMenuSceneName = "MainMenu";

        [Header("References")]
        [SerializeField] private TaskOrchestrator orchestrator;
        [SerializeField] private TaskRunner taskRunner;

        [Header("Debug")]
        [SerializeField] private bool enableKeyboardFallback = false;
        [SerializeField] private KeyCode keyboardKey = KeyCode.Escape;

        private InputDevice _leftController;
        private bool _wasPressed;
        private readonly List<InputDevice> _devices = new List<InputDevice>(2);

        private void Awake()
        {
            if (orchestrator == null)
            {
                orchestrator = FindObjectOfType<TaskOrchestrator>();
            }

            if (taskRunner == null)
            {
                taskRunner = FindObjectOfType<TaskRunner>();
            }
        }

        private void Update()
        {
            if (enableKeyboardFallback && Input.GetKeyDown(keyboardKey))
            {
                ReturnToMainMenu();
                return;
            }

            if (!_leftController.isValid)
            {
                TryResolveLeftController();
            }

            if (!_leftController.isValid)
            {
                return;
            }

            if (_leftController.TryGetFeatureValue(CommonUsages.menuButton, out var pressed))
            {
                if (pressed && !_wasPressed)
                {
                    ReturnToMainMenu();
                }

                _wasPressed = pressed;
            }
        }

        private void TryResolveLeftController()
        {
            _devices.Clear();
            InputDevices.GetDevicesWithCharacteristics(
                InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller,
                _devices);

            if (_devices.Count > 0)
            {
                _leftController = _devices[0];
            }
        }

        private void ReturnToMainMenu()
        {
            orchestrator?.Cancel();
            taskRunner?.CancelRun();

            if (string.IsNullOrWhiteSpace(mainMenuSceneName))
            {
                Debug.LogError("[MenuButtonSceneReturner] MainMenu scene name is empty.");
                return;
            }

            SceneManager.LoadScene(mainMenuSceneName);
        }
    }
}
