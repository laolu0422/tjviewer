using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using TjViewer.Core.Audio;
using TjViewer.Core.Files;
using TjViewer.Core.Input;
using TjViewer.Core.Net;
using TjViewer.Core.Video;
using TjViewer.Shared;

namespace TjViewer.Client.Services;

public sealed record EndpointExchange(int LocalPort, string? PublicIp, int PublicPort, string? LocalIp);

/// <summary>
/// 远程会话编排（见 PRD FR-04/05/06/09）：先经 STUN + 信令交换端点 + UDP 打洞尝试 KCP 直连，
/// 握手探测确认双向可达；超时或对端请求时自动回退服务器中转（WebSocket 承载全部会话消息）。
/// </summary>
public sealed class RemoteSession : IAsyncDisposable
{
    private readonly SignalClient _signal;
    private readonly AppSession _app;
    private readonly string _sessionId;
    private readonly bool _isController;
    private readonly string? _ffmpeg;
    private readonly CancellationTokenSource _cts = new();
    private IMessageChannel? _channel;
    private KcpUdpChannel? _kcp;
    private RelayChannel? _relay;
    private HostVideoSender? _hostSender;
    private ControllerVideoReceiver? _ctrlReceiver;
    private AudioSender? _audioSender;
    private AudioReceiver? _audioReceiver;
    private AudioSender? _micSender;
    private FileReceiver? _fileReceiver;
    private bool _mediaStarted;
    private int _relaySwitching;
    private ConnectionPath _path = ConnectionPath.Unknown;
    private readonly Action<PeerSignalPayload> _onPeer;
    private readonly Action<RelaySwitchPayload> _onRelaySwitch;
    private int _hostW = 1920, _hostH = 1080;

    public event Action<byte[]>? FrameDecoded;
    public event Action<string>? Status;
    public event Action? ChannelReady;
    public event Action<ConnectionPath>? PathChanged;
    public event Action<string, double>? FileProgress;
    public event Action<string, string>? FileCompleted;
    public event Action<string, string>? FileFailed;

    public bool IsController => _isController;
    public ConnectionPath Path => _path;
    public VideoInfo? VideoInfo => _ctrlReceiver?.Info;

    public RemoteSession(SignalClient signal, AppSession app, string sessionId, bool isController)
    {
        _signal = signal;
        _app = app;
        _sessionId = sessionId;
        _isController = isController;
        _ffmpeg = FFmpegLocator.Find();
        _onPeer = OnPeerSignal;
        _onRelaySwitch = p => { if (p.SessionId == _sessionId) _ = SwitchToRelayAsync("对端请求中转"); };
        _signal.PeerSignal += _onPeer;
        _signal.RelaySwitch += _onRelaySwitch;
    }

    public async Task StartAsync()
    {
        if (_ffmpeg is null) { Status?.Invoke("未找到 ffmpeg，无法建立视频"); return; }
        uint conv = (uint)Math.Abs(_sessionId.GetHashCode());
        var kcp = new KcpUdpChannel(conv);
        _kcp = kcp;
        var pub = await kcp.QueryStunAsync("stun.l.google.com");
        var local = kcp.LocalEndpoint;
        var info = new EndpointExchange(local.Port, pub?.Address.ToString(), pub?.Port ?? 0, local.Address.ToString());
        await _signal.SendPeerSignal(_sessionId, JsonSerializer.Serialize(info, SignalJson.Options));
        Status?.Invoke(pub is null ? "STUN 未响应，尝试局域网直连…" : "已获取公网映射，等待对端…");
    }

    private async void OnPeerSignal(PeerSignalPayload p)
    {
        if (p.SessionId != _sessionId || _kcp is null) return;
        EndpointExchange? info;
        try { info = JsonSerializer.Deserialize<EndpointExchange>(p.Data, SignalJson.Options); }
        catch { return; }
        if (info is null) return;

        IPEndPoint? remote = null;
        if (info.PublicIp is not null && info.PublicPort > 0 && IPAddress.TryParse(info.PublicIp, out var pip))
            remote = new IPEndPoint(pip, info.PublicPort);
        else if (info.LocalIp is not null && IPAddress.TryParse(info.LocalIp, out var lip))
            remote = new IPEndPoint(lip, info.LocalPort);
        if (remote is null) return;

        var kcp = _kcp;
        kcp.SetRemote(remote);
        kcp.Start();
        await kcp.PunchAsync();
        _ = HandshakeAsync(kcp);
    }

    /// <summary>P2P 握手：双向互发探测包，5 秒内收到对端探测才确认直连，否则回退中转。</summary>
    private async Task HandshakeAsync(KcpUdpChannel kcp)
    {
        var probe = ChannelMux.Wrap(ChannelMsgType.Handshake, new byte[] { 1 });
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<byte[]> onMsg = m => { if (m.Length >= 1 && m[0] == ChannelMsgType.Handshake) tcs.TrySetResult(); };
        kcp.MessageReceived += onMsg;
        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000 && !_cts.IsCancellationRequested)
            {
                try { kcp.Send(probe); } catch { break; }
                var done = await Task.WhenAny(tcs.Task, Task.Delay(300, _cts.Token));
                if (done == tcs.Task)
                {
                    kcp.MessageReceived -= onMsg;
                    SetupMedia(kcp, ConnectionPath.P2P);
                    return;
                }
            }
            kcp.MessageReceived -= onMsg;
            if (!_cts.IsCancellationRequested) await SwitchToRelayAsync("P2P 打洞超时");
        }
        catch (OperationCanceledException) { }
        catch { kcp.MessageReceived -= onMsg; }
    }

    /// <summary>回退服务器中转：拆除现有媒体 → 建立中转 WebSocket → 双方就绪后重建媒体。</summary>
    private async Task SwitchToRelayAsync(string reason)
    {
        if (Interlocked.Exchange(ref _relaySwitching, 1) == 1) return;
        try
        {
            Status?.Invoke($"正在切换服务器中转（{reason}）…");
            await TeardownMediaAsync();
            var kcp = _kcp;
            _kcp = null;
            if (kcp is not null) await kcp.DisposeAsync();

            try { await _signal.SendRelaySwitch(_sessionId); } catch { } // 通知对端同步切换（覆盖单向可达的不对称 NAT）

            var relay = new RelayChannel(_app.RelayUrl, _app.AccessToken ?? "", _app.DeviceId ?? "", _sessionId);
            _relay = relay;
            relay.Closed += r => Status?.Invoke("中转断开：" + r);
            await relay.ConnectAsync();
            var ready = await Task.WhenAny(relay.PeerReadyTask, Task.Delay(10000, _cts.Token));
            if (ready != relay.PeerReadyTask) { Status?.Invoke("中转建立失败：对端未就绪"); return; }
            SetupMedia(relay, ConnectionPath.Relay);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status?.Invoke("中转建立失败：" + ex.Message); }
    }

    /// <summary>在给定通道上建立媒体管线（视频/音频/文件），P2P 与中转共用。</summary>
    private void SetupMedia(IMessageChannel channel, ConnectionPath connPath)
    {
        if (_mediaStarted || _cts.IsCancellationRequested) return;
        _channel = channel;
        _path = connPath;
        _mediaStarted = true;

        if (_isController)
        {
            _ctrlReceiver = new ControllerVideoReceiver(channel, _ffmpeg!);
            _ctrlReceiver.FrameDecoded += f => FrameDecoded?.Invoke(f);
        }
        else
        {
            _hostSender = new HostVideoSender(channel, _ffmpeg!);
            _hostW = _hostSender.Width;
            _hostH = _hostSender.Height;
            channel.MessageReceived += OnHostMessage;
        }

        // 双向音频（设备不可用时不影响视频，见 PRD FR-07）
        try
        {
            if (_isController)
            {
                _audioReceiver = new AudioReceiver(channel, 0); // 收听被控系统声音
            }
            else
            {
                _audioSender = new AudioSender(channel, 0, loopback: true); // 发送系统声音
                _audioReceiver = new AudioReceiver(channel, 1); // 收听控制端麦克风
            }
        }
        catch { Status?.Invoke("音频设备不可用，仅视频"); }

        // 文件接收（保存到 下载/TjViewer，见 PRD FR-08）
        try
        {
            var saveDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "TjViewer");
            _fileReceiver = new FileReceiver(channel, saveDir);
            _fileReceiver.Progress += (n, p) => FileProgress?.Invoke(n, p);
            _fileReceiver.Completed += (n, path2) => FileCompleted?.Invoke(n, path2);
            _fileReceiver.Failed += (n, r) => FileFailed?.Invoke(n, r);
        }
        catch { }

        ChannelReady?.Invoke();
        PathChanged?.Invoke(connPath);
        Status?.Invoke(connPath == ConnectionPath.P2P ? "P2P 直连已建立" : "服务器中转已建立");
    }

    private async Task TeardownMediaAsync()
    {
        _mediaStarted = false;
        if (_channel is not null) _channel.MessageReceived -= OnHostMessage;
        _micSender?.Dispose(); _micSender = null;
        _audioSender?.Dispose(); _audioSender = null;
        _audioReceiver?.Dispose(); _audioReceiver = null;
        _fileReceiver?.Dispose(); _fileReceiver = null;
        if (_ctrlReceiver is not null) { await _ctrlReceiver.DisposeAsync(); _ctrlReceiver = null; }
        if (_hostSender is not null) { await _hostSender.DisposeAsync(); _hostSender = null; }
        _channel = null;
    }

    public Task SendFileAsync(string path)
        => _channel is null ? Task.CompletedTask : FileSender.SendFileAsync(path, _channel);

    /// <summary>控制端开启麦克风回传（须用户主动同意，见 PRD §5.5）。</summary>
    public void StartMic()
    {
        if (!_isController || _channel is null || _micSender is not null) return;
        try { _micSender = new AudioSender(_channel, 1, loopback: false); }
        catch { Status?.Invoke("麦克风不可用"); }
    }

    public void StopMic()
    {
        _micSender?.Dispose();
        _micSender = null;
    }

    private void OnHostMessage(byte[] msg)
    {
        if (msg.Length < 1) return;
        var (type, payload) = ChannelMux.Unwrap(msg);
        if (type == ChannelMsgType.Input)
        {
            var ev = JsonSerializer.Deserialize<InputEvent>(payload, SignalJson.Options);
            if (ev is not null) ApplyInput(ev);
        }
        else if (type == ChannelMsgType.Quality && _hostSender is not null)
        {
            var q = JsonSerializer.Deserialize<QualityRequest>(payload, SignalJson.Options);
            if (q is null) return;
            if (q.Mode == "adaptive") _hostSender.SetAdaptive();
            else _hostSender.SetManual(q.Tier, q.Fps);
        }
    }

    private void ApplyInput(InputEvent ev)
    {
        switch (ev.Kind)
        {
            case "move": InputInjector.MoveMouseAbsolute(ev.X, ev.Y, _hostW, _hostH); break;
            case "button": InputInjector.MouseButtonEvent((MouseButton)ev.Button, ev.Down); break;
            case "wheel": InputInjector.Wheel(ev.Delta); break;
            case "key": InputInjector.ScanCode(ev.Scan, ev.Down, ev.Extended); break;
            case "text": if (ev.Text is not null) InputInjector.Text(ev.Text); break;
        }
    }

    public void SendInput(InputEvent ev)
    {
        if (_channel is null) return;
        _channel.Send(ChannelMux.Wrap(ChannelMsgType.Input, JsonSerializer.SerializeToUtf8Bytes(ev, SignalJson.Options)));
    }

    public void SendQuality(QualityRequest q)
    {
        if (_channel is null) return;
        _channel.Send(ChannelMux.Wrap(ChannelMsgType.Quality, JsonSerializer.SerializeToUtf8Bytes(q, SignalJson.Options)));
    }

    public async ValueTask DisposeAsync()
    {
        _signal.PeerSignal -= _onPeer;
        _signal.RelaySwitch -= _onRelaySwitch;
        _cts.Cancel();
        await TeardownMediaAsync();
        if (_relay is not null) await _relay.DisposeAsync();
        if (_kcp is not null) await _kcp.DisposeAsync();
    }
}
