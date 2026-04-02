using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using VRPerception.Infra.EventBus;
using VRPerception.Perception;
using VRPerception.UI;

namespace VRPerception.Tasks
{
    /// <summary>
    /// 地平线线索整合任务（运行时自包含创建）
    /// - 相机不动；红球放在相机高度、正前方
    /// - 通过旋转环境根节点（俯仰）移动“地平线线索”
    /// - 距离：5/10/20 米；角度：-6/-3/0/+3/+6 度；重复 3 次，总计 45 个试次
    ///
    /// 设计要点：
    /// - “地平线线索”来自一个包裹相机的天空球（Sphere）+ 运行时生成的地平线纹理；
    /// - 通过旋转 Environment_Rig（俯仰）让地平线在视野中上下移动；
    /// - 红球始终在相机正前方、同高度，仅距离变化。
    /// </summary>
    public sealed class HorizonCueIntegrationTask : ITask, ITaskRunLifecycle
    {
        public string TaskId => "horizon_cue_integration";

        // 试次切换黑屏保持时长：避免用户看到场景/物体瞬时跳变。
        private const int InterTrialBlackoutHoldMs = 600;

        private TaskRunnerContext _ctx;
        private GameObject _runtimeRoot;
        private Transform _environmentRig;
        private Transform _skySphere;
        private Transform _redSphere;
        private TrialBlackoutOverlay _blackout;
        private bool _blackoutIsRuntimeCreated;
        private bool? _blackoutOriginalHideOnTrialEnd;
        private bool? _blackoutOriginalEnabled;
        private bool? _blackoutOriginalActiveSelf;
        private bool? _blackoutOriginalVisible;

        private Material _runtimeRedMaterial;
        private Material _runtimeSkyMaterial;
        private Texture2D _runtimeSkyTexture;
        private Mesh _runtimeSkyMesh;
        private bool? _roomWasActive;
        private GameObject _roomGo;
        private bool _didDisableRoom;
        private GameObject _openFieldGroundGo;
        private bool? _openFieldGroundWasActive;
        private bool _referenceFrameInitialized;
        private Vector3 _referenceOrigin;
        private Vector3 _referenceForward;
        private float _referenceEyeY;
        private readonly Dictionary<string, int> _snapshotObjectCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int _activeTrialId = -1;

        public HorizonCueIntegrationTask(TaskRunnerContext ctx)
        {
            _ctx = ctx;
        }

        public void Initialize(TaskRunner runner, EventBusManager eventBus)
        {
            if (_ctx == null)
            {
                _ctx = new TaskRunnerContext
                {
                    runner = runner,
                    eventBus = eventBus,
                    perception = runner ? runner.GetComponent<PerceptionSystem>() : null,
                    stimulus = runner ? runner.GetComponent<StimulusCapture>() : null,
                    humanReferenceFrame = runner ? runner.GetComponent<HumanReferenceFrameService>() : null
                };
            }
            else if (_ctx.humanReferenceFrame == null)
            {
                _ctx.humanReferenceFrame = runner ? runner.GetComponent<HumanReferenceFrameService>() : null;
            }
        }

        public Task OnRunBeginAsync(CancellationToken ct)
        {
            if (!TryUseHumanSharedReferenceFrame())
            {
                CaptureReferenceFrameIfNeeded(forceRefresh: true);
            }

            _roomWasActive = null;
            _didDisableRoom = false;
            _roomGo = GameObject.Find("Room");
            if (_roomGo != null)
            {
                _roomWasActive = _roomGo.activeSelf;
                if (_roomGo.activeSelf)
                {
                    _roomGo.SetActive(false);
                    _didDisableRoom = true;
                }
            }

            _openFieldGroundWasActive = null;
            _openFieldGroundGo = GameObject.Find("env_open_field_ground");
            if (_openFieldGroundGo != null)
            {
                _openFieldGroundWasActive = _openFieldGroundGo.activeSelf;
                if (_openFieldGroundGo.activeSelf) _openFieldGroundGo.SetActive(false);
            }

            return Task.CompletedTask;
        }

        public Task OnRunEndAsync(CancellationToken ct)
        {
            _referenceFrameInitialized = false;
            if (_roomWasActive.HasValue && _didDisableRoom)
            {
                // 注意：Room 在被 SetActive(false) 后，GameObject.Find 找不到它；因此必须用缓存引用恢复。
                if (_roomGo != null) _roomGo.SetActive(_roomWasActive.Value);
                _roomWasActive = null;
            }

            _didDisableRoom = false;
            _roomGo = null;

            if (_openFieldGroundWasActive.HasValue)
            {
                if (_openFieldGroundGo != null) _openFieldGroundGo.SetActive(_openFieldGroundWasActive.Value);
                _openFieldGroundWasActive = null;
            }
            _openFieldGroundGo = null;

            RestoreSceneBlackoutIfNeeded();

            _environmentRig = null;
            _skySphere = null;
            _redSphere = null;
            _blackout = null;
            _blackoutIsRuntimeCreated = false;

            if (_runtimeRedMaterial != null)
            {
                UnityEngine.Object.Destroy(_runtimeRedMaterial);
                _runtimeRedMaterial = null;
            }

            if (_runtimeSkyMaterial != null)
            {
                UnityEngine.Object.Destroy(_runtimeSkyMaterial);
                _runtimeSkyMaterial = null;
            }

            if (_runtimeSkyTexture != null)
            {
                UnityEngine.Object.Destroy(_runtimeSkyTexture);
                _runtimeSkyTexture = null;
            }

            if (_runtimeSkyMesh != null)
            {
                UnityEngine.Object.Destroy(_runtimeSkyMesh);
                _runtimeSkyMesh = null;
            }

            if (_runtimeRoot != null)
            {
                UnityEngine.Object.Destroy(_runtimeRoot);
                _runtimeRoot = null;
            }

            return Task.CompletedTask;
        }

        public TrialSpec[] BuildTrials(int seed)
        {
            // 45 个试次：3 个距离 × 5 个地平线角度 × 3 次重复。
            var distances = new[] { 5f, 10f, 20f };
            var angles = new[] { -6f, -3f, 0f, 3f, 6f };
            const int repetitions = 3;

            var trials = new List<TrialSpec>(distances.Length * angles.Length * repetitions);
            for (int di = 0; di < distances.Length; di++)
            {
                var distance = distances[di];
                for (int rep = 0; rep < repetitions; rep++)
                {
                    foreach (var angle in angles)
                    {
                        trials.Add(new TrialSpec
                        {
                            taskId = TaskId,
                            fovDeg = 60f,
                            trueDistanceM = distance,
                            horizonAngleDeg = angle,
                            repetitionIndex = rep + 1
                        });
                    }
                }
            }

            // 使用 seed 打乱试次顺序，避免固定序列带来顺序效应。
            Shuffle(trials, new System.Random(seed));
            return trials.ToArray();
        }

        public string GetSystemPrompt()
        {
            return PromptTemplates.GetSystemPrompt(TaskId);
        }

        public ToolSpec[] GetTools()
        {
            return null;
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            return PromptTemplates.BuildHorizonCueIntegrationPrompt(trial.trialId);
        }

        public async Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            _snapshotObjectCounts.Clear();
            _activeTrialId = trial != null ? trial.trialId : -1;
            EnsureRuntimeObjects();

            // 使用 StimulusCapture 的头部相机；若不可用则回退到 Camera.main。
            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null)
            {
                Debug.LogWarning("[HorizonCueIntegrationTask] No camera found (StimulusCapture.HeadCamera/Camera.main).");
                return;
            }

            if (!TryUseHumanSharedReferenceFrame())
            {
                CaptureReferenceFrameIfNeeded(forceRefresh: false);
            }

            ResolvePlacementReference(cam, out var referenceOrigin, out var referenceForward, out var referenceEyeY);

            // 若上一试次刚把黑场打开，这里先不要立刻关闭。
            // 做法：在黑场下更新场景，然后保持一小段时间，最后再解除黑场。
            bool wasBlackoutVisible = _blackout != null && _blackout.IsVisible;

            // 确保天空球不会被相机 far clip 裁剪（默认 Primitive.Sphere 半径约为 0.5，scale=1 表示直径 1）。
            if (_skySphere != null)
            {
                var targetScale = Mathf.Clamp(cam.farClipPlane * 1.8f, 50f, 5000f);
                _skySphere.localScale = Vector3.one * targetScale;
            }

            if (_environmentRig != null)
            {
                // 关键：让环境 Rig 跟随相机位置，并用俯仰角旋转它，从而使“地平线线索”相对视野上下移动。
                _environmentRig.position = referenceOrigin;
                _environmentRig.localRotation = Quaternion.Euler(-trial.horizonAngleDeg, 0f, 0f);
            }

            if (_redSphere != null)
            {
                // 红球放在相机正前方、同高度；只改变距离，不改变高度/方位。
                var pos = referenceOrigin + referenceForward * Mathf.Max(0.01f, trial.trueDistanceM);
                pos.y = referenceEyeY;
                _redSphere.position = pos;

                // 记录红球在屏幕上的纵向位置（0..1），用于事后分析。
                var vp = cam.WorldToViewportPoint(_redSphere.position);
                trial.sphereScreenY01 = vp.y;
            }

            if (wasBlackoutVisible)
            {
                await Task.Delay(InterTrialBlackoutHoldMs, ct);
                _blackout?.Hide();
            }

            // 给渲染/采集留出稳定时间窗口（解除黑场后也需要一段时间确保画面稳定）。
            await Task.Delay(250, ct);
        }

        private void CaptureReferenceFrameIfNeeded(bool forceRefresh)
        {
            if (_referenceFrameInitialized && !forceRefresh) return;

            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null)
            {
                _referenceFrameInitialized = false;
                return;
            }

            _referenceOrigin = cam.transform.position;
            _referenceForward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (_referenceForward.sqrMagnitude < 1e-6f) _referenceForward = Vector3.forward;
            _referenceForward.Normalize();
            _referenceEyeY = cam.transform.position.y;
            _referenceFrameInitialized = true;
        }

        private bool TryUseHumanSharedReferenceFrame()
        {
            if (!IsHumanMode()) return false;

            var humanRef = _ctx?.humanReferenceFrame;
            if (humanRef == null || !humanRef.HasReferenceFrame) return false;

            _referenceOrigin = humanRef.Origin;
            _referenceForward = humanRef.Forward;
            if (_referenceForward.sqrMagnitude < 1e-6f) _referenceForward = Vector3.forward;
            _referenceForward.Normalize();
            _referenceEyeY = humanRef.EyeY;
            _referenceFrameInitialized = true;
            return true;
        }

        private void ResolvePlacementReference(Camera cam, out Vector3 origin, out Vector3 forward, out float eyeY)
        {
            if (TryUseHumanSharedReferenceFrame())
            {
                origin = _referenceOrigin;
                forward = _referenceForward;
                eyeY = _referenceEyeY;
                return;
            }

            origin = _referenceFrameInitialized ? _referenceOrigin : cam.transform.position;
            forward = _referenceFrameInitialized ? _referenceForward : Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();
            eyeY = _referenceFrameInitialized ? _referenceEyeY : cam.transform.position.y;
        }

        private bool IsHumanMode()
        {
            return _ctx?.runner != null && _ctx.runner.CurrentSubjectMode == SubjectMode.Human;
        }

        public async Task OnAfterTrialAsync(TrialSpec trial, LLMResponse response, CancellationToken ct)
        {
            // 试次结束立即黑场，减少上一次刺激对下一次的影响。
            if (_blackout != null) _blackout.BeginBlackoutAfterMs(0);
            _snapshotObjectCounts.Clear();
            _activeTrialId = -1;
            await Task.Delay(250, ct);
        }

        public TrialEvaluation Evaluate(TrialSpec trial, LLMResponse response)
        {
            var eval = new TrialEvaluation
            {
                responseType = response?.type,
                providerId = response?.providerId,
                latencyMs = response?.latencyMs ?? 0,
                confidence = response?.confidence ?? 0
            };

            // 只接受 type=inference 的响应。
            if (response == null || !string.Equals(response.type, "inference", StringComparison.OrdinalIgnoreCase))
            {
                eval.success = false;
                eval.failureReason = "Invalid response type";
                return eval;
            }

            // 从 response.answer 中容错提取 distance_m（也兼容 distance）。
            if (!TryExtractDistanceM(response.answer, out var predicted))
            {
                eval.success = false;
                eval.failureReason = "No answer.distance_m found";
                return eval;
            }

            eval.predictedDistanceM = predicted;
            eval.absError = Mathf.Abs(predicted - trial.trueDistanceM);
            eval.relError = trial.trueDistanceM > 0.0001f ? eval.absError / trial.trueDistanceM : 0f;
            eval.success = true;
            return eval;
        }

        private void EnsureRuntimeObjects()
        {
            // 运行时对象在任务运行期间常驻；每个试次只更新位置/旋转。
            if (_runtimeRoot == null) _runtimeRoot = new GameObject("HorizonCueIntegration_RuntimeRoot");
            AttachSnapshotMarker(_runtimeRoot, "runtime_root", "helper");

            if (_environmentRig == null)
            {
                // 用于承载“环境线索”（天空球）；通过旋转它改变地平线相对视野的位置。
                var go = new GameObject("Environment_Rig");
                go.transform.SetParent(_runtimeRoot.transform, worldPositionStays: true);
                _environmentRig = go.transform;
            }
            AttachSnapshotMarker(_environmentRig != null ? _environmentRig.gameObject : null, "environment_rig", "helper");

            if (_skySphere == null)
            {
                _skySphere = CreateSkySphere(_environmentRig);
            }
            AttachSnapshotMarker(_skySphere != null ? _skySphere.gameObject : null, "sky_sphere", "helper");

            if (_redSphere == null)
            {
                _redSphere = CreateRedSphere(_runtimeRoot.transform);
            }
            AttachSnapshotMarker(_redSphere != null ? _redSphere.gameObject : null, "sphere", "target");

            EnsureBlackoutOverlay();
        }

        private void EnsureBlackoutOverlay()
        {
            if (_blackout != null) return;

            if (TryFindSceneTrialBlackoutOverlay(out var overlay))
            {
                _blackout = overlay;
                _blackoutIsRuntimeCreated = false;

                // 记录原状态，方便任务结束后恢复。
                _blackoutOriginalHideOnTrialEnd = overlay.HideOnTrialEnd;
                _blackoutOriginalEnabled = overlay.enabled;
                _blackoutOriginalActiveSelf = overlay.gameObject != null ? overlay.gameObject.activeSelf : null;
                _blackoutOriginalVisible = overlay.IsVisible;

                if (!overlay.enabled) overlay.enabled = true;
                if (overlay.gameObject != null && !overlay.gameObject.activeInHierarchy) overlay.gameObject.SetActive(true);

                // 该任务需要把黑屏延续到下一试次再解除，不能在 TrialEnd 事件时自动隐藏。
                overlay.HideOnTrialEnd = false;
                return;
            }

            // 找不到场景遮罩时，回退到运行时创建，避免任务不可用。
            Debug.LogWarning("[HorizonCueIntegrationTask] TrialBlackoutOverlay not found in scene; creating a runtime one.");
            var blackoutGo = new GameObject("TrialBlackoutOverlay");
            blackoutGo.transform.SetParent(_runtimeRoot != null ? _runtimeRoot.transform : null, worldPositionStays: false);
            _blackout = blackoutGo.AddComponent<TrialBlackoutOverlay>();
            AttachSnapshotMarker(blackoutGo, "blackout_overlay", "helper");
            _blackoutIsRuntimeCreated = true;
            _blackout.HideOnTrialEnd = false;
        }

        private void RestoreSceneBlackoutIfNeeded()
        {
            if (_blackout == null) return;
            if (_blackoutIsRuntimeCreated) return;

            try
            {
                var go = _blackout.gameObject;
                if (go != null && !go.activeInHierarchy) go.SetActive(true);

                if (_blackoutOriginalHideOnTrialEnd.HasValue) _blackout.HideOnTrialEnd = _blackoutOriginalHideOnTrialEnd.Value;

                if (_blackoutOriginalVisible.HasValue)
                {
                    if (_blackoutOriginalVisible.Value) _blackout.Show();
                    else _blackout.Hide();
                }

                if (_blackoutOriginalEnabled.HasValue) _blackout.enabled = _blackoutOriginalEnabled.Value;
                if (go != null && _blackoutOriginalActiveSelf.HasValue) go.SetActive(_blackoutOriginalActiveSelf.Value);
            }
            catch
            {
                // ignore restore failures
            }
            finally
            {
                _blackoutOriginalHideOnTrialEnd = null;
                _blackoutOriginalEnabled = null;
                _blackoutOriginalActiveSelf = null;
                _blackoutOriginalVisible = null;
            }
        }

        private static bool TryFindSceneTrialBlackoutOverlay(out TrialBlackoutOverlay overlay)
        {
            overlay = null;
            try
            {
                overlay = UnityEngine.Object.FindObjectOfType<TrialBlackoutOverlay>();
                if (overlay != null) return true;

                var all = Resources.FindObjectsOfTypeAll<TrialBlackoutOverlay>();
                if (all == null) return false;
                for (int i = 0; i < all.Length; i++)
                {
                    var candidate = all[i];
                    if (candidate == null) continue;
                    var go = candidate.gameObject;
                    if (go == null) continue;
                    if (!go.scene.IsValid()) continue; // exclude prefab assets
                    overlay = candidate;
                    return true;
                }
            }
            catch
            {
                overlay = null;
            }
            return overlay != null;
        }

        private Transform CreateSkySphere(Transform parent)
        {
            // 天空球：从球内部可见（负缩放反转法线 + 关闭剔除），贴一张带地平线的纹理作为背景线索。
            var sky = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sky.name = "Skybox_Background";
            sky.transform.SetParent(parent, worldPositionStays: false);
            sky.transform.localPosition = Vector3.zero;
            sky.transform.localRotation = Quaternion.identity;

            var collider = sky.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);

            // 通过翻转网格绕序/法线，让球内壁变为“正面”，避免不同渲染管线下材质剔除属性不生效导致球内不可见。
            sky.transform.localScale = new Vector3(500f, 500f, 500f);
            MakeSphereMeshInsideFacing(sky);

            EnsureRuntimeSkyMaterial();
            var r = sky.GetComponent<Renderer>();
            if (r != null && _runtimeSkyMaterial != null)
            {
                r.sharedMaterial = _runtimeSkyMaterial;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
            }

            return sky.transform;
        }

        private void MakeSphereMeshInsideFacing(GameObject sphere)
        {
            if (sphere == null) return;

            var mf = sphere.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;

            if (_runtimeSkyMesh == null)
            {
                _runtimeSkyMesh = UnityEngine.Object.Instantiate(mf.sharedMesh);
                _runtimeSkyMesh.name = mf.sharedMesh.name + "_InsideFacing";

                var tris = _runtimeSkyMesh.triangles;
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    var t = tris[i];
                    tris[i] = tris[i + 1];
                    tris[i + 1] = t;
                }
                _runtimeSkyMesh.triangles = tris;

                var normals = _runtimeSkyMesh.normals;
                if (normals != null && normals.Length > 0)
                {
                    for (int i = 0; i < normals.Length; i++) normals[i] = -normals[i];
                    _runtimeSkyMesh.normals = normals;
                }
                else
                {
                    _runtimeSkyMesh.RecalculateNormals();
                }

                _runtimeSkyMesh.RecalculateBounds();
            }

            mf.sharedMesh = _runtimeSkyMesh;
        }

        private Transform CreateRedSphere(Transform parent)
        {
            // 红球：使用 Unlit 材质，尽量不受场景光照影响，作为距离判断目标物。
            var sphereGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphereGo.name = "RedSphere";
            sphereGo.transform.SetParent(parent, worldPositionStays: true);
            sphereGo.transform.localScale = Vector3.one * 0.5f;

            var collider = sphereGo.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);

            EnsureRuntimeRedMaterial();
            var r = sphereGo.GetComponent<Renderer>();
            if (r != null && _runtimeRedMaterial != null)
            {
                r.sharedMaterial = _runtimeRedMaterial;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
            }

            return sphereGo.transform;
        }

        private void AttachSnapshotMarker(GameObject go, string kind, string role)
        {
            if (go == null) return;

            string taskId = _ctx?.runner?.CurrentConfiguredTaskId ?? TaskId;
            string baseName = string.IsNullOrWhiteSpace(go.name) ? "unnamed" : go.name.Trim();
            if (!_snapshotObjectCounts.TryGetValue(baseName, out var count)) count = 0;
            count++;
            _snapshotObjectCounts[baseName] = count;

            string objectId = count <= 1
                ? $"{taskId}_{_activeTrialId}_{baseName}"
                : $"{taskId}_{_activeTrialId}_{baseName}_{count}";

            TrialObjectMarker.AttachOrUpdate(go, taskId, _activeTrialId, objectId, kind, role);
        }

        private void EnsureRuntimeRedMaterial()
        {
            if (_runtimeRedMaterial != null) return;

            var shader =
                Shader.Find("Universal Render Pipeline/Unlit") ??
                Shader.Find("Unlit/Color") ??
                Shader.Find("Sprites/Default") ??
                Shader.Find("Standard");
            if (shader == null) return;

            _runtimeRedMaterial = new Material(shader);
            if (_runtimeRedMaterial.HasProperty("_BaseColor")) _runtimeRedMaterial.SetColor("_BaseColor", Color.red);
            else if (_runtimeRedMaterial.HasProperty("_Color")) _runtimeRedMaterial.SetColor("_Color", Color.red);
            else _runtimeRedMaterial.color = Color.red;
        }

        private void EnsureRuntimeSkyMaterial()
        {
            if (_runtimeSkyMaterial != null) return;

            // 使用程序生成的高分辨率渐变纹理作为天空盒
            CreateImprovedGradientSkybox();
        }

        private void CreateImprovedGradientSkybox()
        {
            // Android/PICO 打包时，纯靠字符串查找的 shader 可能被裁剪。
            // 这里优先选取内置管线和项目里更稳定可用的纹理 shader，避免天空球退回默认白材质。
            var shader =
                Shader.Find("Unlit/Texture") ??
                Shader.Find("Sprites/Default") ??
                Shader.Find("Mobile/Unlit (Supports Lightmap)") ??
                Shader.Find("Standard") ??
                Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                Debug.LogError("[HorizonCueIntegrationTask] No suitable shader found for runtime sky material.");
                return;
            }

            // 使用更高分辨率（2048x1024）以获得更好效果
            const int w = 2048;
            const int h = 1024;

            _runtimeSkyTexture = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: true, linear: false)
            {
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4
            };

            // 更自然的天空渐变
            var skyTop = new Color(0.5f, 0.7f, 1.0f, 1f); // 深蓝色
            var skyNearHorizon = new Color(0.7f, 0.85f, 1.0f, 1f); // 浅蓝色
            var ground = new Color(0.2f, 0.23f, 0.2f, 1f); // 深绿色
            var horizonLine = new Color(0.95f, 0.95f, 1.0f, 1f); // 亮白色

            for (int y = 0; y < h; y++)
            {
                var v = (float)y / (h - 1);
                Color rowColor;

                // 使用非线性渐变，更接近真实天空
                if (v >= 0.5f)
                {
                    var t = Mathf.Pow((v - 0.5f) * 2f, 0.6f); // 指数渐变
                    rowColor = Color.Lerp(skyNearHorizon, skyTop, t);
                }
                else
                {
                    rowColor = ground;
                }

                // 添加地平线光晕效果
                var horizonDist = Mathf.Abs(v - 0.5f);
                if (horizonDist < 0.1f)
                {
                    var halo = 1f - (horizonDist / 0.1f);
                    rowColor = Color.Lerp(rowColor, horizonLine, halo * 0.5f);
                }

                for (int x = 0; x < w; x++)
                {
                    _runtimeSkyTexture.SetPixel(x, y, rowColor);
                }
            }

            _runtimeSkyTexture.Apply(true, false);

            _runtimeSkyMaterial = new Material(shader);

            if (_runtimeSkyMaterial.HasProperty("_BaseColor")) _runtimeSkyMaterial.SetColor("_BaseColor", Color.white);
            else if (_runtimeSkyMaterial.HasProperty("_Color")) _runtimeSkyMaterial.SetColor("_Color", Color.white);
            else _runtimeSkyMaterial.color = Color.white;

            if (_runtimeSkyMaterial.HasProperty("_BaseMap"))
                _runtimeSkyMaterial.SetTexture("_BaseMap", _runtimeSkyTexture);
            else if (_runtimeSkyMaterial.HasProperty("_MainTex"))
                _runtimeSkyMaterial.SetTexture("_MainTex", _runtimeSkyTexture);

            // 关闭剔除，确保从球内部也能看到
            if (_runtimeSkyMaterial.HasProperty("_Cull"))
                _runtimeSkyMaterial.SetFloat("_Cull", (float)CullMode.Off);
            if (_runtimeSkyMaterial.HasProperty("_CullMode"))
                _runtimeSkyMaterial.SetFloat("_CullMode", (float)CullMode.Off);

            Debug.Log($"[HorizonCueIntegrationTask] Using runtime sky material shader: {shader.name}");
        }

        private static bool TryExtractDistanceM(object answerObj, out float distanceM)
        {
            distanceM = float.NaN;
            if (answerObj == null) return false;

            if (answerObj is IDictionary dict)
            {
                if (TryGetDictNumber(dict, "distance_m", out distanceM)) return true;
                if (TryGetDictNumber(dict, "distance", out distanceM)) return true;
            }

            try
            {
                var t = answerObj.GetType();

                var prop = t.GetProperty("distance_m", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && TryToFloat(prop.GetValue(answerObj, null), out distanceM)) return true;

                var field = t.GetField("distance_m", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (field != null && TryToFloat(field.GetValue(answerObj), out distanceM)) return true;

                var prop2 = t.GetProperty("distance", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (prop2 != null && TryToFloat(prop2.GetValue(answerObj, null), out distanceM)) return true;
            }
            catch { }

            if (answerObj is string s && TryToFloat(s, out distanceM)) return true;
            return false;
        }

        private static bool TryGetDictNumber(IDictionary dict, string key, out float value)
        {
            value = float.NaN;
            if (dict == null || string.IsNullOrWhiteSpace(key)) return false;
            try
            {
                if (!dict.Contains(key)) return false;
                return TryToFloat(dict[key], out value);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryToFloat(object v, out float f)
        {
            f = float.NaN;
            if (v == null) return false;
            switch (v)
            {
                case float fv: f = fv; return true;
                case double dv: f = (float)dv; return true;
                case int iv: f = iv; return true;
                case long lv: f = lv; return true;
                case decimal dec: f = (float)dec; return true;
                case string sv:
                {
                    if (float.TryParse(sv, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
                        float.TryParse(sv, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed))
                    {
                        f = parsed;
                        return true;
                    }

                    return false;
                }
                default: return false;
            }
        }

        private static void Shuffle<T>(IList<T> list, System.Random rand)
        {
            if (list == null || rand == null) return;
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}

