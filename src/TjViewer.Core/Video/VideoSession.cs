using System.Collections.Concurrent;
using System.Text.Json;
using TjViewer.Core.Net;
using TjViewer.Shared;

namespace TjViewer.Core.Video;

/// <summary>
/// 被控端视频发送：采集 → H.264 编码 → 通道。
/// 手动模式帧率/码率独立可调（受服务端 4K/120fps/10Mbps 限制）；
/// 无论画面是否变化都按设定帧率持续推流，保证操作反馈及时。
/// </summary>
public sealed class HostVideoSender : IAsyncDisposable
{
    private readonly IDesktopCapturer _capturer;
    private readonly IMessageChannel _channel;
    private readonly string _ffmpeg;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _encLock = new();
    private VideoEncoder? _encoder;
    private int _tierIndex;
    private int _fps;
    private int _bitrateKbps;
    private int _outWidth;
    private int _outHeight;
    private bool _adaptive = true;
    private int _stableCount;
    private readonly Task _loop;

    /// <summary>采集到的原始桌面尺寸。</summary>
    public int Width => _capturer.Width;
    public int Height => _capturer.Height;
    /// <summary>实际编码输出尺寸（超过 4K 时按比例缩小）。</summary>
    public int OutputWidth => _outWidth;
    public int OutputHeight => _outHeight;
    public int CurrentTier => _tierIndex;
    public int CurrentFps => _fps;
    public int CurrentBitrateKbps => _bitrateKbps;
    public bool IsAdaptive => _adaptive;

    public HostVideoSender(IMessageChannel channel, string ffmpeg, int initialTier = 1)
    {
        _channel = channel;
        _ffmpeg = ffmpeg;
        _capturer = CreateCapturer();
        _tierIndex = Math.Clamp(initialTier, 0, QualityProfiles.Tiers.Length - 1);
        _fps = VideoLimits.ClampFps(QualityProfiles.Tiers[_tierIndex].MaxFps);
        _bitrateKbps = VideoLimits.ClampBitrate(QualityProfiles.Tiers[_tierIndex].MaxBitrateKbps);
        CreateEncoder();
        _loop = Task.Run(CaptureLoop);
    }

    /// <summary>优先 DXGI Desktop Duplication，不可用时回退 GDI。</summary>
    private static IDesktopCapturer CreateCapturer()
    {
        try { return new DxgiCapturer(); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine("DXGI 采集不可用，回退 GDI：" + ex.Message);
            return new GdiCapturer();
        }
    }

    private void CreateEncoder()
    {
        lock (_encLock)
        {
            if (_encoder is not null) { try { _encoder.DisposeAsync().AsTask().Wait(); } catch { } }
            var (ow, oh) = VideoLimits.ClampSize(_capturer.Width, _capturer.Height);
            _outWidth = ow;
            _outHeight = oh;
            _fps = VideoLimits.ClampFps(_fps);
            _bitrateKbps = VideoLimits.ClampBitrate(_bitrateKbps);
            _encoder = new VideoEncoder(_ffmpeg, _capturer.Width, _capturer.Height, _fps, _bitrateKbps, null, ow, oh);
            _encoder.EncodedChunk += data => _channel.Send(ChannelMux.Wrap(ChannelMsgType.Video, data));
            SendVideoInfo();
        }
    }

    /// <summary>周期性重发视频协商信息，避免对端晚加入时错过首包。</summary>
    private void SendVideoInfo()
    {
        var info = JsonSerializer.SerializeToUtf8Bytes(new VideoInfo(_outWidth, _outHeight, _fps), SignalJson.Options);
        _channel.Send(ChannelMux.Wrap(ChannelMsgType.Control, info));
    }

    private async Task CaptureLoop()
    {
        var frameWatch = System.Diagnostics.Stopwatch.StartNew();
        var adaptWatch = System.Diagnostics.Stopwatch.StartNew();
        var infoWatch = System.Diagnostics.Stopwatch.StartNew();
        long backlogStartMs = -1, lastUpgradeMs = 0;
        while (!_cts.IsCancellationRequested)
        {
            int fps = Math.Max(1, _fps);
            // 只等待极短时间：无更新时立刻复用上一帧，避免采集阻塞编码节拍（排队时延最小化）
            var frame = _capturer.CaptureFrame(Math.Clamp(1000 / fps / 4, 2, 4));
            if (frame is not null)
            {
                lock (_encLock)
                {
                    // 分辨率变化（DXGI 显示模式切换）时按新尺寸重建编码器
                    if (_encoder is null || _encoder.Width != _capturer.Width || _encoder.Height != _capturer.Height) CreateEncoder();
                    if (_encoder is not null) { try { _encoder.EncodeFrameAsync(frame).Wait(); } catch { } }
                }
            }
            if (infoWatch.ElapsedMilliseconds >= 1000)
            {
                infoWatch.Restart();
                lock (_encLock) SendVideoInfo();
            }
            // DXGI 不会自动限帧，按当前帧率补齐间隔；GDI 采集后同样节流
            var budget = Math.Max(1, 1000 / fps);
            var remain = budget - (int)frameWatch.ElapsedMilliseconds;
            if (remain > 0)
            {
                try { await Task.Delay(remain, _cts.Token); } catch { break; }
            }
            frameWatch.Restart();

            if (!_adaptive) continue;
            long now = adaptWatch.ElapsedMilliseconds;
            uint wait = _channel.WaitSnd;
            if (wait > 50)
            {
                // 积压须持续满 1 秒才降档，避免瞬时抖动导致画质骤降
                if (backlogStartMs < 0) backlogStartMs = now;
                if (now - backlogStartMs >= 1000 && _tierIndex < QualityProfiles.Tiers.Length - 1)
                {
                    _tierIndex++;
                    _fps = VideoLimits.ClampFps(QualityProfiles.Tiers[_tierIndex].MaxFps);
                    _bitrateKbps = VideoLimits.ClampBitrate(QualityProfiles.Tiers[_tierIndex].MaxBitrateKbps);
                    CreateEncoder();
                    backlogStartMs = now;
                    _stableCount = 0;
                    lastUpgradeMs = now;
                }
            }
            else
            {
                backlogStartMs = -1;
                // 升档慢：每秒评估一次，连续 3 次空闲才升一档
                if (now - lastUpgradeMs >= 1000)
                {
                    lastUpgradeMs = now;
                    if (wait < 5)
                    {
                        _stableCount++;
                        if (_stableCount >= 3 && _tierIndex > 0)
                        {
                            _tierIndex--;
                            _fps = VideoLimits.ClampFps(QualityProfiles.Tiers[_tierIndex].MaxFps);
                            _bitrateKbps = VideoLimits.ClampBitrate(QualityProfiles.Tiers[_tierIndex].MaxBitrateKbps);
                            CreateEncoder();
                            _stableCount = 0;
                        }
                    }
                    else _stableCount = 0;
                }
            }
        }
    }

    /// <summary>手动固定：帧率与码率独立设置，统一按服务端上限钳制。</summary>
    public void SetManual(int tier, int fps, int bitrateKbps)
    {
        _adaptive = false;
        _tierIndex = Math.Clamp(tier, 0, QualityProfiles.Tiers.Length - 1);
        _fps = VideoLimits.ClampFps(fps);
        _bitrateKbps = VideoLimits.ClampBitrate(bitrateKbps);
        CreateEncoder();
    }

    public void SetAdaptive()
    {
        _adaptive = true;
        _fps = VideoLimits.ClampFps(QualityProfiles.Tiers[_tierIndex].MaxFps);
        _bitrateKbps = VideoLimits.ClampBitrate(QualityProfiles.Tiers[_tierIndex].MaxBitrateKbps);
        CreateEncoder();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop; } catch { }
        VideoEncoder? enc;
        lock (_encLock) { enc = _encoder; _encoder = null; }
        if (enc is not null) await enc.DisposeAsync();
        _capturer.Dispose();
    }
}

/// <summary>控制端视频接收：KCP 通道 → H.264 解码 → BGRA 帧事件（供渲染）。</summary>
public sealed class ControllerVideoReceiver : IAsyncDisposable
{
    private readonly IMessageChannel _channel;
    private readonly string _ffmpeg;
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private VideoDecoder? _decoder;
    private Task? _writer;
    private readonly Action<byte[]> _onMessage;

    public event Action<byte[]>? FrameDecoded;
    public VideoInfo? Info { get; private set; }

    public ControllerVideoReceiver(IMessageChannel channel, string ffmpeg)
    {
        _channel = channel;
        _ffmpeg = ffmpeg;
        _onMessage = OnMessage;
        _channel.MessageReceived += _onMessage;
        HardwareCodecs.WarmUp(ffmpeg); // 后台预热编解码探测，避免首帧被探测耗时阻塞
        _writer = Task.Run(WriterLoop);
    }

    private void OnMessage(byte[] msg)
    {
        if (msg.Length < 1) return;
        var (type, payload) = ChannelMux.Unwrap(msg);
        if (type == ChannelMsgType.Control)
        {
            var info = JsonSerializer.Deserialize<VideoInfo>(payload, SignalJson.Options);
            if (info is not null && (_decoder is null || _decoder.Width != info.Width || _decoder.Height != info.Height))
            {
                Info = info;
                var old = _decoder;
                _decoder = new VideoDecoder(_ffmpeg, info.Width, info.Height);
                _decoder.FrameDecoded += f => FrameDecoded?.Invoke(f);
                if (old is not null) _ = old.DisposeAsync().AsTask(); // 分辨率变化：异步替换解码器
            }
        }
        else if (type == ChannelMsgType.Video)
        {
            _queue.Enqueue(payload);
        }
    }

    private async Task WriterLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (_decoder is not null && _queue.TryDequeue(out var data))
            {
                try { await _decoder.WriteEncodedAsync(data); } catch { }
            }
            else
            {
                try { await Task.Delay(2, _cts.Token); } catch { break; }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.MessageReceived -= _onMessage;
        _cts.Cancel();
        if (_writer is not null) try { await _writer; } catch { }
        if (_decoder is not null) await _decoder.DisposeAsync();
    }
}
