using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// WebSocket 连接器：仅负责通信与事件广播，不解析业务内容。
/// 功能：
/// - 连接/断开/自动重连（指数退避+抖动）
/// - 文本/二进制/图片(从 Texture 编码 PNG/JPEG)发送
/// - 接收文本/二进制并在主线程事件广播
/// - 心跳(KeepAliveInterval)
/// 说明：本脚本不支持 WebGL（浏览器环境需替代实现）
/// </summary>
public enum WsState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Closing
}

[Serializable]
public class StringEvent : UnityEvent<string> {}

[Serializable]
public class BytesEvent : UnityEvent<byte[]> {}

[Serializable]
public class StateEvent : UnityEvent<string> {}

[Serializable]
public class Header
{
    public string Key;
    public string Value;
}

public enum BackpressurePolicy
{
    Reject,
    DropNewest,
    DropOldest
}

public enum ImageFormat
{
    Png,
    Jpeg
}

[Serializable]
public class ImageEncodingOptions
{
    public ImageFormat Format = ImageFormat.Png;
    [Range(1, 100)] public int JpegQuality = 75;
    [Tooltip("0 表示不缩放")] public int MaxWidth = 0;
    [Tooltip("0 表示不缩放")] public int MaxHeight = 0;
}

public class Connector : MonoBehaviour
{
    [Header("Connection")]
    [SerializeField] private string serverUri = "ws://127.0.0.1:8080/ws";
    [SerializeField] private bool autoConnect = true;
    [Tooltip("可选：与服务器协商的子协议")]
    [SerializeField] private List<string> subprotocols = new List<string>();
    [Tooltip("可选：自定义请求头，例如 Authorization")]
    [SerializeField] private List<Header> headers = new List<Header>();
    [Tooltip("仅测试用途：允许自签名证书（生产环境请禁用）")]
    [SerializeField] private bool allowSelfSignedCert = false;
    [Tooltip("是否跨场景保留该对象")]
    [SerializeField] private bool dontDestroyOnLoad = false;

    [Header("KeepAlive")]
    [SerializeField] private int keepAliveSeconds = 30;

    [Header("Reconnect")]
    [SerializeField] private bool autoReconnect = true;
    [SerializeField] private float reconnectInitialDelay = 1f;
    [SerializeField] private float reconnectMaxDelay = 30f;
    [SerializeField] private float reconnectBackoff = 2f;
    [SerializeField, Range(0f, 1f)] private float reconnectJitter = 0.2f;
    [Tooltip("-1 表示无限次")]
    [SerializeField] private int maxReconnectAttempts = -1;

    [Header("Buffers")]
    [SerializeField] private int receiveBufferSize = 64 * 1024;
    [SerializeField] private int maxMessageBytes = 16 * 1024 * 1024;

    [Header("Send Queue")]
    [SerializeField] private int sendQueueLimit = 256;
    [SerializeField] private BackpressurePolicy backpressurePolicy = BackpressurePolicy.Reject;

    [Header("Image Encoding")]
    [SerializeField] private ImageEncodingOptions imageEncoding = new ImageEncodingOptions();

    [Header("Logging")]
    [SerializeField] private bool verboseLogs = false;

    [Header("Unity Events (Inspector)")]
    public UnityEvent OnConnected = new UnityEvent();
    public StringEvent OnDisconnected = new StringEvent(); // reason
    public StringEvent OnTextMessage = new StringEvent();
    public BytesEvent OnBinaryMessage = new BytesEvent();
    public StringEvent OnError = new StringEvent();
    public StateEvent OnStateChanged = new StateEvent();

    // 代码侧事件（C# 订阅）
    public event Action Connected;
    public event Action<string> Disconnected;
    public event Action<string> TextMessageReceived;
    public event Action<byte[]> BinaryMessageReceived;
    public event Action<string> ErrorReceived;
    public event Action<WsState> StateChanged;

    // 内部状态
    private ClientWebSocket _ws;
    private CancellationTokenSource _cts;
    private Task _receiveLoopTask;
    private Task _sendLoopTask;

    private readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();

    private struct OutboundMessage
    {
        public WebSocketMessageType Type;
        public byte[] Data;
    }

    private readonly ConcurrentQueue<OutboundMessage> _sendQueue = new ConcurrentQueue<OutboundMessage>();
    private readonly SemaphoreSlim _sendSignal = new SemaphoreSlim(0);

    private volatile WsState _state = WsState.Disconnected;
    private readonly System.Object _stateLock = new System.Object();
    private volatile bool _userRequestedClose = false;
    private int _reconnectAttempts = 0;

    public bool IsConnected => _ws != null && _ws.State == WebSocketState.Open;
    public WsState State => _state;

    private void Awake()
    {
#if UNITY_WEBGL
        Debug.LogError("[Connector] WebGL 平台请改用浏览器端 WebSocket 实现，本脚本当前不支持。");
        enabled = false;
        return;
#endif
        if (dontDestroyOnLoad)
        {
            DontDestroyOnLoad(gameObject);
        }
        if (receiveBufferSize < 4 * 1024) receiveBufferSize = 4 * 1024;
        if (maxMessageBytes < receiveBufferSize) maxMessageBytes = receiveBufferSize;
    }

    private void Start()
    {
        if (autoConnect)
        {
            Connect();
        }
    }

    private void Update()
    {
        while (_mainThreadActions.TryDequeue(out var a))
        {
            try { a?.Invoke(); }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
    }

    private void OnDisable()
    {
        _ = DisconnectAsync("Component disabled");
    }

    private void OnDestroy()
    {
        _ = DisconnectAsync("Component destroyed");
    }

    // ============ Public APIs ============

    public void Connect()
    {
        _ = ConnectAsync();
    }

    public async Task ConnectAsync(string overrideUri = null)
    {
        if (Application.platform == RuntimePlatform.WebGLPlayer)
        {
            return;
        }

        if (_state == WsState.Connecting || _state == WsState.Connected || _state == WsState.Reconnecting)
        {
            Log($"[Connector] Already in state {_state}, skip Connect.");
            return;
        }

        _userRequestedClose = false;
        _reconnectAttempts = 0;

        await EstablishConnectionAsync(overrideUri ?? serverUri, false);
    }

    public void Disconnect()
    {
        _ = DisconnectAsync("User requested");
    }

    public async Task DisconnectAsync(string reason)
    {
        _userRequestedClose = true;
        ChangeState(WsState.Closing);

        try { _cts?.Cancel(); } catch { }

        if (_ws != null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", closeCts.Token);
                }
                else
                {
                    _ws.Abort();
                }
            }
            catch (Exception ex)
            {
                Log($"[Connector] Close exception: {ex.Message}");
            }
        }

        await CleanupAsync();

        ChangeState(WsState.Disconnected);
        DispatchMain(() =>
        {
            SafeInvoke(() => OnDisconnected?.Invoke(reason ?? "Closed"));
            SafeInvoke(() => Disconnected?.Invoke(reason ?? "Closed"));
        });
    }

    public bool TrySendText(string text)
    {
        if (text == null) text = string.Empty;
        var bytes = Encoding.UTF8.GetBytes(text);
        return EnqueueToSend(new OutboundMessage { Type = WebSocketMessageType.Text, Data = bytes });
    }

    public bool TrySendBinary(byte[] data)
    {
        if (data == null) data = Array.Empty<byte>();
        return EnqueueToSend(new OutboundMessage { Type = WebSocketMessageType.Binary, Data = data });
    }

    public bool TrySendJson(object obj)
    {
        try
        {
            var json = JsonUtility.ToJson(obj ?? new object());
            return TrySendText(json);
        }
        catch (Exception ex)
        {
            ReportError($"JSON 序列化失败: {ex.Message}");
            return false;
        }
    }

    public bool TrySendTexture(Texture texture, ImageEncodingOptions options = null)
    {
        if (texture == null)
        {
            ReportError("发送纹理失败：texture 为 null");
            return false;
        }
        options ??= imageEncoding;

        Texture2D tex2D = null;
        RenderTexture tempRT = null;
        try
        {
            tex2D = ConvertToReadableTexture2D(texture, options.MaxWidth, options.MaxHeight, out tempRT);

            byte[] payload = options.Format == ImageFormat.Png
                ? tex2D.EncodeToPNG()
                : tex2D.EncodeToJPG(Mathf.Clamp(options.JpegQuality, 1, 100));

            return TrySendBinary(payload);
        }
        catch (Exception ex)
        {
            ReportError($"纹理编码失败: {ex.Message}");
            return false;
        }
        finally
        {
            if (tempRT != null)
            {
                RenderTexture.ReleaseTemporary(tempRT);
            }
            if (tex2D != null)
            {
                Destroy(tex2D);
            }
        }
    }

    // ============ Internals ============

    private Texture2D ConvertToReadableTexture2D(Texture src, int maxW, int maxH, out RenderTexture tempRT)
    {
        tempRT = null;

        int srcW = src.width;
        int srcH = src.height;

        int dstW = srcW;
        int dstH = srcH;

        if (maxW > 0 || maxH > 0)
        {
            float scaleW = maxW > 0 ? (float)maxW / srcW : 1f;
            float scaleH = maxH > 0 ? (float)maxH / srcH : 1f;
            float scale = Mathf.Min(scaleW, scaleH);
            if (scale < 1f)
            {
                dstW = Mathf.Max(1, Mathf.RoundToInt(srcW * scale));
                dstH = Mathf.Max(1, Mathf.RoundToInt(srcH * scale));
            }
        }

        var prev = RenderTexture.active;
        tempRT = RenderTexture.GetTemporary(dstW, dstH, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);

        // Blit source to temp
        Graphics.Blit(src, tempRT);
        RenderTexture.active = tempRT;

        var tex = new Texture2D(dstW, dstH, TextureFormat.RGB24, false, true);
        tex.ReadPixels(new Rect(0, 0, dstW, dstH), 0, 0);
        tex.Apply(false, false);

        RenderTexture.active = prev;
        return tex;
    }

    private bool EnqueueToSend(OutboundMessage msg)
    {
        if (!IsConnected)
        {
            ReportError("尚未连接，无法发送消息");
            return false;
        }

        if (sendQueueLimit > 0 && _sendQueue.Count >= sendQueueLimit)
        {
            switch (backpressurePolicy)
            {
                case BackpressurePolicy.Reject:
                    ReportError("发送队列已满，拒绝入队");
                    return false;
                case BackpressurePolicy.DropNewest:
                    // 丢弃本条
                    return false;
                case BackpressurePolicy.DropOldest:
                    _sendQueue.TryDequeue(out _);
                    break;
            }
        }

        _sendQueue.Enqueue(msg);
        _sendSignal.Release();
        return true;
    }

    private async Task EstablishConnectionAsync(string uri, bool isReconnect)
    {
        ChangeState(isReconnect ? WsState.Reconnecting : WsState.Connecting);

        try { _cts?.Cancel(); } catch { }
        _cts = new CancellationTokenSource();

        _ws = new ClientWebSocket();

        // KeepAlive
        if (keepAliveSeconds > 0)
        {
            try { _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(keepAliveSeconds); } catch { }
        }

        // Subprotocols
        if (subprotocols != null)
        {
            foreach (var sp in subprotocols)
            {
                if (!string.IsNullOrWhiteSpace(sp))
                {
                    try { _ws.Options.AddSubProtocol(sp.Trim()); } catch { }
                }
            }
        }

        // Headers
        if (headers != null)
        {
            foreach (var h in headers)
            {
                if (!string.IsNullOrEmpty(h?.Key))
                {
                    try { _ws.Options.SetRequestHeader(h.Key, h.Value ?? ""); } catch { }
                }
            }
        }

        // Self-signed cert (test only)
        RemoteCertificateValidationCallback originalCallback = null;
        if (allowSelfSignedCert)
        {
            try
            {
                originalCallback = ServicePointManager.ServerCertificateValidationCallback;
                ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;
            }
            catch (Exception ex)
            {
                Log($"[Connector] 设置证书忽略失败: {ex.Message}");
            }
        }

        try
        {
            Log($"[Connector] Connecting to {uri} ...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await _ws.ConnectAsync(new Uri(uri), _cts.Token);
            sw.Stop();
            Log($"[Connector] Connected in {sw.ElapsedMilliseconds} ms");

            ChangeState(WsState.Connected);

            DispatchMain(() =>
            {
                SafeInvoke(() => OnConnected?.Invoke());
                SafeInvoke(() => Connected?.Invoke());
            });

            // 启动收发循环
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            _sendLoopTask = Task.Run(() => SendLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            ReportError($"连接失败: {ex.Message}");
            await HandleConnectionLostAsync("Connect failure", null, ex, duringConnect: true);
        }
        finally
        {
            if (allowSelfSignedCert)
            {
                try { ServicePointManager.ServerCertificateValidationCallback = originalCallback; } catch { }
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var buffer = new byte[Mathf.Max(1024, receiveBufferSize)];
        var ms = new MemoryStream();

        try
        {
            while (!token.IsCancellationRequested && _ws != null && _ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    await HandleConnectionLostAsync("Receive exception", null, ex, duringConnect: false);
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await HandleConnectionLostAsync("Server requested close", result.CloseStatus, null, duringConnect: false);
                    return;
                }

                if (result.Count > 0)
                {
                    if (ms.Length + result.Count > maxMessageBytes)
                    {
                        // 超限，主动断开并重连
                        ReportError($"收到消息超出上限: {ms.Length + result.Count} > {maxMessageBytes}");
                        try
                        {
                            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            await _ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too big", closeCts.Token);
                        }
                        catch { }
                        await HandleConnectionLostAsync("Message too big", WebSocketCloseStatus.MessageTooBig, null, duringConnect: false);
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                }

                if (result.EndOfMessage)
                {
                    var data = ms.ToArray();
                    ms.SetLength(0);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string text = null;
                        try { text = Encoding.UTF8.GetString(data); }
                        catch (Exception ex)
                        {
                            ReportError($"文本解码失败: {ex.Message}");
                            continue;
                        }

                        DispatchMain(() =>
                        {
                            SafeInvoke(() => OnTextMessage?.Invoke(text));
                            SafeInvoke(() => TextMessageReceived?.Invoke(text));
                        });
                    }
                    else if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        DispatchMain(() =>
                        {
                            SafeInvoke(() => OnBinaryMessage?.Invoke(data));
                            SafeInvoke(() => BinaryMessageReceived?.Invoke(data));
                        });
                    }
                }
            }
        }
        finally
        {
            ms.Dispose();
        }
    }

    private async Task SendLoopAsync(CancellationToken token)
    {
        const int chunkSize = 16 * 1024;

        try
        {
            while (!token.IsCancellationRequested)
            {
                // 等待至少一条消息
                await _sendSignal.WaitAsync(token);

                // 尽可能多地发送已排队消息
                while (_sendQueue.TryDequeue(out var msg))
                {
                    if (_ws == null || _ws.State != WebSocketState.Open)
                    {
                        // 连接不可用，等待重连
                        break;
                    }

                    try
                    {
                        int offset = 0;
                        while (offset < msg.Data.Length)
                        {
                            int size = Math.Min(chunkSize, msg.Data.Length - offset);
                            bool end = (offset + size) >= msg.Data.Length;
                            var seg = new ArraySegment<byte>(msg.Data, offset, size);
                            await _ws.SendAsync(seg, msg.Type, end, token);
                            offset += size;
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        await HandleConnectionLostAsync("Send exception", null, ex, duringConnect: false);
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
    }

    private async Task HandleConnectionLostAsync(string reason, WebSocketCloseStatus? closeStatus, Exception ex, bool duringConnect)
    {
        var humanReason = new StringBuilder();
        humanReason.Append(reason);
        if (closeStatus.HasValue) humanReason.Append($", CloseStatus={(int)closeStatus.Value}/{closeStatus.Value}");
        if (ex != null) humanReason.Append($", Exception={ex.GetType().Name}: {ex.Message}");

        Log($"[Connector] Connection lost: {humanReason}");

        try { _cts?.Cancel(); } catch { }
        await CleanupAsync();

        // 广播断开
        DispatchMain(() =>
        {
            SafeInvoke(() => OnDisconnected?.Invoke(humanReason.ToString()));
            SafeInvoke(() => Disconnected?.Invoke(humanReason.ToString()));
        });

        if (_userRequestedClose || !autoReconnect || !isActiveAndEnabled)
        {
            ChangeState(WsState.Disconnected);
            return;
        }

        if (maxReconnectAttempts >= 0 && _reconnectAttempts >= maxReconnectAttempts)
        {
            ReportError("重连次数已达上限，停止重连。");
            ChangeState(WsState.Disconnected);
            return;
        }

        _reconnectAttempts++;
        var delay = ComputeReconnectDelay(_reconnectAttempts, reconnectInitialDelay, reconnectBackoff, reconnectMaxDelay, reconnectJitter);

        ChangeState(WsState.Reconnecting);
        Log($"[Connector] Reconnecting in {delay:F2}s (attempt #{_reconnectAttempts}) ...");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(delay));
        }
        catch { }

        if (_userRequestedClose || !autoReconnect || !isActiveAndEnabled)
        {
            ChangeState(WsState.Disconnected);
            return;
        }

        await EstablishConnectionAsync(serverUri, true);
    }

    private async Task CleanupAsync()
    {
        // 停止任务
        var recvTask = _receiveLoopTask;
        var sendTask = _sendLoopTask;

        _receiveLoopTask = null;
        _sendLoopTask = null;

        try { _cts?.Cancel(); } catch { }

        if (recvTask != null)
        {
            try { await recvTask; } catch { }
        }
        if (sendTask != null)
        {
            try { await sendTask; } catch { }
        }

        // 关闭套接字
        if (_ws != null)
        {
            try { _ws.Abort(); } catch { }
            try { _ws.Dispose(); } catch { }
            _ws = null;
        }

        try { _cts?.Dispose(); } catch { }
        _cts = null;

        // 清空发送信号，避免泄漏
        while (_sendSignal.CurrentCount > 0)
        {
            try { _sendSignal.Wait(0); } catch { break; }
        }
        // 发送队列保留由业务决定是否丢弃，这里不清空，避免短线期间丢失重要消息
    }

    private static float ComputeReconnectDelay(int attempt, float initial, float backoff, float max, float jitterFraction)
    {
        if (attempt <= 1) return Mathf.Min(initial, max);
        double delay = initial * Math.Pow(backoff, attempt - 1);
        delay = Math.Min(delay, max);

        // 抖动 ±jitterFraction
        var rnd = UnityEngine.Random.Range(-jitterFraction, jitterFraction);
        delay = Math.Max(0.05, delay * (1.0 + rnd));
        return (float)delay;
    }

    private void ChangeState(WsState newState)
    {
        lock (_stateLock)
        {
            if (_state == newState) return;
            _state = newState;
        }

        Log($"[Connector] State => {newState}");

        DispatchMain(() =>
        {
            SafeInvoke(() => OnStateChanged?.Invoke(newState.ToString()));
            SafeInvoke(() => StateChanged?.Invoke(newState));
        });
    }

    private void ReportError(string message)
    {
        Debug.LogError($"[Connector] {message}");
        DispatchMain(() =>
        {
            SafeInvoke(() => OnError?.Invoke(message));
            SafeInvoke(() => ErrorReceived?.Invoke(message));
        });
    }

    private void Log(string message)
    {
        if (verboseLogs)
        {
            Debug.Log(message);
        }
    }

    private void DispatchMain(Action a)
    {
        if (a == null) return;
        _mainThreadActions.Enqueue(a);
    }

    private void SafeInvoke(Action a)
    {
        try { a?.Invoke(); } catch (Exception ex) { Debug.LogException(ex); }
    }
}
