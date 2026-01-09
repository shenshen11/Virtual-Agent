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

        private TaskRunnerContext _ctx;

        private GameObject _runtimeRoot;
        private Transform _environmentRig;
        private Transform _skySphere;
        private Transform _redSphere;
        private TrialBlackoutOverlay _blackout;

        private Material _runtimeRedMaterial;
        private Material _runtimeSkyMaterial;
        private Texture2D _runtimeSkyTexture;
        private Mesh _runtimeSkyMesh;
        private bool? _roomWasActive;
        private GameObject _roomGo;
        private bool _didDisableRoom;

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
                    stimulus = runner ? runner.GetComponent<StimulusCapture>() : null
                };
            }
        }

        public Task OnRunBeginAsync(CancellationToken ct)
        {
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

            // 任务运行时自建一套最小场景（根节点/环境Rig/天空球/红球/黑场遮罩）。
            EnsureRuntimeObjects();
            return Task.CompletedTask;
        }

        public Task OnRunEndAsync(CancellationToken ct)
        {
            if (_roomWasActive.HasValue && _didDisableRoom)
            {
                // 注意：Room 在被 SetActive(false) 后，GameObject.Find 找不到它；因此必须用缓存引用恢复。
                if (_roomGo != null) _roomGo.SetActive(_roomWasActive.Value);
                _roomWasActive = null;
            }

            _didDisableRoom = false;
            _roomGo = null;

            _environmentRig = null;
            _skySphere = null;
            _redSphere = null;
            _blackout = null;

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
            return
                "You are a vision agent. ONLY output JSON according to task rules. " +
                "Format: {\"type\":\"inference\",\"taskId\":\"horizon_cue_integration\",\"trialId\":<int>," +
                "\"answer\":{\"distance_m\":<number>},\"confidence\":<0..1>}. " +
                "No extra text.";
        }

        public ToolSpec[] GetTools()
        {
            return null;
        }

        public string BuildTaskPrompt(TrialSpec trial)
        {
            return
                "Task: Estimate the distance to the red sphere in meters.\n" +
                "Output ONLY JSON with fields: type=inference, answer.distance_m (float), confidence (0..1).";
        }

        public async Task OnBeforeTrialAsync(TrialSpec trial, CancellationToken ct)
        {
            EnsureRuntimeObjects();

            // 使用 StimulusCapture 的头部相机；若不可用则回退到 Camera.main。
            var cam = _ctx?.stimulus?.HeadCamera ?? Camera.main;
            if (cam == null)
            {
                Debug.LogWarning("[HorizonCueIntegrationTask] No camera found (StimulusCapture.HeadCamera/Camera.main).");
                return;
            }

            // 防御：确保新试次开始时不是黑场。
            _blackout?.Hide();

            // 确保天空球不会被相机 far clip 裁剪（默认 Primitive.Sphere 半径约为 0.5，scale=1 表示直径 1）。
            if (_skySphere != null)
            {
                var targetScale = Mathf.Clamp(cam.farClipPlane * 1.8f, 50f, 5000f);
                _skySphere.localScale = Vector3.one * targetScale;
            }

            if (_environmentRig != null)
            {
                // 关键：让环境 Rig 跟随相机位置，并用俯仰角旋转它，从而使“地平线线索”相对视野上下移动。
                _environmentRig.position = cam.transform.position;
                _environmentRig.localRotation = Quaternion.Euler(-trial.horizonAngleDeg, 0f, 0f);
            }

            if (_redSphere != null)
            {
                // 红球放在相机正前方、同高度；只改变距离，不改变高度/方位。
                var origin = cam.transform.position;

                // 使用水平前向，避免相机俯仰导致“同高度”校正后距离失真。
                var forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
                if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
                forward.Normalize();

                var pos = origin + forward * Mathf.Max(0.01f, trial.trueDistanceM);
                pos.y = origin.y;
                _redSphere.position = pos;

                // 记录红球在屏幕上的纵向位置（0..1），用于事后分析。
                var vp = cam.WorldToViewportPoint(_redSphere.position);
                trial.sphereScreenY01 = vp.y;
            }

            // 给渲染/采集留出稳定时间窗口。
            await Task.Delay(250, ct);
        }

        public async Task OnAfterTrialAsync(TrialSpec trial, LLMResponse response, CancellationToken ct)
        {
            // 试次结束立即黑场，减少上一次刺激对下一次的影响。
            if (_blackout != null) _blackout.BeginBlackoutAfterMs(0);
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

            if (_environmentRig == null)
            {
                // 用于承载“环境线索”（天空球）；通过旋转它改变地平线相对视野的位置。
                var go = new GameObject("Environment_Rig");
                go.transform.SetParent(_runtimeRoot.transform, worldPositionStays: true);
                _environmentRig = go.transform;
            }

            if (_skySphere == null)
            {
                _skySphere = CreateSkySphere(_environmentRig);
            }

            if (_redSphere == null)
            {
                _redSphere = CreateRedSphere(_runtimeRoot.transform);
            }

            if (_blackout == null)
            {
                var blackoutGo = new GameObject("TrialBlackoutOverlay");
                blackoutGo.transform.SetParent(_runtimeRoot.transform, worldPositionStays: false);
                _blackout = blackoutGo.AddComponent<TrialBlackoutOverlay>();
            }
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

            var shader =
                Shader.Find("Universal Render Pipeline/Unlit") ??
                Shader.Find("Unlit/Texture") ??
                Shader.Find("Sprites/Default") ??
                Shader.Find("Standard");
            if (shader == null) return;

            const int w = 256;
            const int h = 128;

            // 运行时生成一张“天空-地面渐变 + 明显地平线”的纹理，用作可控的地平线线索。
            _runtimeSkyTexture = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false, linear: false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var skyTop = new Color(0.55f, 0.75f, 1.0f, 1f);
            var skyNearHorizon = new Color(0.75f, 0.85f, 1.0f, 1f);
            var ground = new Color(0.18f, 0.20f, 0.18f, 1f);
            var horizonLine = new Color(0.95f, 0.95f, 0.95f, 1f);

            const float horizonV = 0.5f;
            const float band = 0.04f;
            const float line = 0.006f;

            for (int y = 0; y < h; y++)
            {
                var v = h <= 1 ? 0f : (float)y / (h - 1);
                Color rowColor;

                if (v >= horizonV + band * 0.5f)
                {
                    var t = Mathf.InverseLerp(horizonV + band * 0.5f, 1f, v);
                    rowColor = Color.Lerp(skyNearHorizon, skyTop, Mathf.Clamp01(t));
                }
                else if (v <= horizonV - band * 0.5f)
                {
                    rowColor = ground;
                }
                else
                {
                    var t = Mathf.InverseLerp(horizonV - band * 0.5f, horizonV + band * 0.5f, v);
                    rowColor = Color.Lerp(ground, skyNearHorizon, Mathf.Clamp01(t));
                }

                if (Mathf.Abs(v - horizonV) <= line * 0.5f)
                {
                    rowColor = Color.Lerp(rowColor, horizonLine, 0.65f);
                }

                for (int x = 0; x < w; x++)
                {
                    _runtimeSkyTexture.SetPixel(x, y, rowColor);
                }
            }

            _runtimeSkyTexture.Apply(updateMipmaps: false, makeNoLongerReadable: false);

            _runtimeSkyMaterial = new Material(shader);
            if (_runtimeSkyMaterial.HasProperty("_BaseColor")) _runtimeSkyMaterial.SetColor("_BaseColor", Color.white);
            else if (_runtimeSkyMaterial.HasProperty("_Color")) _runtimeSkyMaterial.SetColor("_Color", Color.white);
            else _runtimeSkyMaterial.color = Color.white;

            if (_runtimeSkyMaterial.HasProperty("_BaseMap")) _runtimeSkyMaterial.SetTexture("_BaseMap", _runtimeSkyTexture);
            else if (_runtimeSkyMaterial.HasProperty("_MainTex")) _runtimeSkyMaterial.SetTexture("_MainTex", _runtimeSkyTexture);

            // 关闭剔除，确保从球内部也能看到纹理。
            if (_runtimeSkyMaterial.HasProperty("_Cull")) _runtimeSkyMaterial.SetFloat("_Cull", (float)CullMode.Off);
            if (_runtimeSkyMaterial.HasProperty("_CullMode")) _runtimeSkyMaterial.SetFloat("_CullMode", (float)CullMode.Off);
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

