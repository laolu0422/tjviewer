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
/// 远程会话编排（见 PRD FR-04/05/06/09）：经 STUN + 信令交换端点 + UDP 打洞建立 KCP 直连，
/// 控制端接收视频并发送输入；被控端发送视频并注入输入。
/// </summary>
public sealed class RemoteSession : IAsyncDisposable
{
    private readonly SignalClient _signal;
    private readonly string _sessionId;
    private readonly bool _isController;
    private readonly string? _ffmpeg;
    private KcpUdpChannel? _channel;
    private HostVideoSender? _hostSender;
    private ControllerVideoReceiver? _ctrlReceiver;
    private AudioSender? _audioSender;
    private AudioReceiver? _audioReceiver;
    private AudioSender? _micSender;
    private FileReceiver? _fileReceiver;

    public event Action<string, double>? FileProgress;
    public event Action<string, string>? FileCompleted;
    public event Action<string, string>? FileFailed;
    private readonly Action<PeerSignalPayload> _onPeer;
    private int _hostW = 1920, _hostH = 1080;

    public event Action<byte[]>? FrameDecoded;
    public event Action<string>? Status;
    public event Action? ChannelReady;
    public bool IsController => _isController;
    public VideoInfo? VideoInfo => _ctrlReceiver?.Info;

    public RemoteSession(SignalClient signal, string sessionId, bool isController)
    {
        _signal = signal;
        _sessionId = sessionId;
        _isController = isController;
        _ffmpeg = FFmpegLocator.Find();
        _onPeer = OnPeerSignal;
        _signal.PeerSignal += _onPeer;
    }

    public async Task StartAsync()
    {
        if (_ffmpeg is null) { Status?.Invoke("未找到 ffmpeg，无法建立视频"); return; }
        uint conv = (uint)Math.Abs(_sessionId.GetHashCode());
        _channel = new KcpUdpChannel(conv);
        var pub = await _channel.QueryStunAsync("stun.l.google.com");
        var local = _channel.LocalEndpoint;
        var info = new EndpointExchange(local.Port, pub?.Address.ToString(), pub?.Port ?? 0, local.Address.ToString());
        await _signal.SendPeerSignal(_sessionId, JsonSerializer.Serialize(info, SignalJson.Options));
        Status?.Invoke(pub is null ? "STUN 未响应，尝试局域网直连…" : "已获取公网映射，等待对端…");
    }

    private async void OnPeerSignal(PeerSignalPayload p)
    {
        if (p.SessionId != _sessionId || _channel is null) return;
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

        _channel.SetRemote(remote);
        _channel.Start();
        await _channel.PunchAsync();

        if (_isController)
        {
            _ctrlReceiver = new ControllerVideoReceiver(_channel, _ffmpeg!);
            _ctrlReceiver.FrameDecoded += f => FrameDecoded?.Invoke(f);
        }
        else
        {
            _hostSender = new HostVideoSender(_channel, _ffmpeg!);
            _hostW = _hostSender.Width;
            _hostH = _hostSender.Height;
            _channel.MessageReceived += OnHostMessage;
        }
        // 双向音频（设备不可用时不影响视频，见 PRD FR-07）
        try
        {
            if (_isController)
            {
                _audioReceiver = new AudioReceiver(_channel, 0); // 收听被控系统声音
            }
            else
            {
                _audioSender = new AudioSender(_channel, 0, loopback: true); // 发送系统声音
                _audioReceiver = new AudioReceiver(_channel, 1); // 收听控制端麦克风
            }
        }
        catch { Status?.Invoke("音频设备不可用，仅视频"); }

        // 文件接收（保存到 下载/TjViewer，见 PRD FR-08）
        try
        {
            var saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "TjViewer");
            _fileReceiver = new FileReceiver(_channel, saveDir);
            _fileReceiver.Progress += (n, p) => FileProgress?.Invoke(n, p);
            _fileReceiver.Completed += (n, path) => FileCompleted?.Invoke(n, path);
            _fileReceiver.Failed += (n, r) => FileFailed?.Invoke(n, r);
        }
        catch { }

        ChannelReady?.Invoke();
        Status?.Invoke("直连已建立");
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
            else _hostSender.SetManualTier(q.Tier);
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
        _micSender?.Dispose();
        _audioSender?.Dispose();
        _audioReceiver?.Dispose();
        _fileReceiver?.Dispose();
        if (_ctrlReceiver is not null) await _ctrlReceiver.DisposeAsync();
        if (_hostSender is not null) await _hostSender.DisposeAsync();
        if (_channel is not null) await _channel.DisposeAsync();
    }
}
