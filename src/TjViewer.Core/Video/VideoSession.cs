using System.Collections.Concurrent;
using System.Text.Json;
using TjViewer.Core.Net;
using TjViewer.Shared;

namespace TjViewer.Core.Video;

/// <summary>
/// 被控端视频发送：采集 → H.264 编码 → KCP 通道（见 PRD FR-05 / M2 / M3）。
/// 自适应：按发送队列拥塞升降档（降快升慢）；手动：固定档位。分辨率保持原生，调节帧率/码率。
/// </summary>
public sealed class HostVideoSender : IAsyncDisposable
{
    private readonly IDesktopCapturer _capturer;
    private readonly KcpUdpChannel _channel;
    private readonly string _ffmpeg;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _encLock = new();
    private VideoEncoder? _encoder;
    private int _tierIndex;
    private bool _adaptive = true;
    private int _stableCount;
    private readonly Task _loop;

    public int Width => _capturer.Width;
    public int Height => _capturer.Height;
    public int CurrentTier => _tierIndex;
    public bool IsAdaptive => _adaptive;

    public HostVideoSender(KcpUdpChannel channel, string ffmpeg, int initialTier = 1)
    {
        _channel = channel;
        _ffmpeg = ffmpeg;
        _capturer = CreateCapturer();
        _tierIndex = Math.Clamp(initialTier, 0, QualityProfiles.Tiers.Length - 1);
        CreateEncoder();
        var tier = QualityProfiles.Tiers[_tierIndex];
        var info = JsonSerializer.SerializeToUtf8Bytes(new VideoInfo(_capturer.Width, _capturer.Height, tier.MaxFps), SignalJson.Options);
        _channel.Send(ChannelMux.Wrap(ChannelMsgType.Control, info));
        _loop = Task.Run(CaptureLoop);
    }

    /// <summary>优先 DXGI Desktop Duplication（低延迟、自带光标合成），不可用时回退 GDI。</summary>
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
            var tier = QualityProfiles.Tiers[_tierIndex];
            _encoder = new VideoEncoder(_ffmpeg, _capturer.Width, _capturer.Height, tier.MaxFps, tier.MaxBitrateKbps);
            _encoder.EncodedChunk += data => _channel.Send(ChannelMux.Wrap(ChannelMsgType.Video, data));
        }
    }

    private async Task CaptureLoop()
    {
        var adaptWatch = System.Diagnostics.Stopwatch.StartNew();
        var frameWatch = System.Diagnostics.Stopwatch.StartNew();
        while (!_cts.IsCancellationRequested)
        {
            var tier = QualityProfiles.Tiers[_tierIndex];
            var frame = _capturer.CaptureFrame(Math.Max(1, 1000 / Math.Max(1, tier.MaxFps)));
            if (frame is not null)
            {
                lock (_encLock)
                {
                    // 分辨率变化（DXGI 显示模式切换）时按新尺寸重建编码器
                    if (_encoder is null || _encoder.Width != _capturer.Width || _encoder.Height != _capturer.Height) CreateEncoder();
                    if (_encoder is not null) { try { _encoder.EncodeFrameAsync(frame).Wait(); } catch { } }
                }
            }
            // DXGI 不会自动限帧，按档位帧率补齐间隔；GDI 采集后同样按档位节流
            var budget = Math.Max(1, 1000 / Math.Max(1, tier.MaxFps));
            var remain = budget - (int)frameWatch.ElapsedMilliseconds;
            if (remain > 0)
            {
                try { await Task.Delay(remain, _cts.Token); } catch { break; }
            }
            frameWatch.Restart();

            // 自适应：每秒评估拥塞；降档快、升档慢（连续 3 次空闲才升档）
            if (_adaptive && adaptWatch.ElapsedMilliseconds >= 1000)
            {
                adaptWatch.Restart();
                uint wait = _channel.WaitSnd;
                if (wait > 50 && _tierIndex < QualityProfiles.Tiers.Length - 1) { _tierIndex++; CreateEncoder(); _stableCount = 0; }
                else if (wait < 5) { _stableCount++; if (_stableCount >= 3 && _tierIndex > 0) { _tierIndex--; CreateEncoder(); _stableCount = 0; } }
                else _stableCount = 0;
            }
        }
    }

    /// <summary>手动固定档位（见 PRD FR-05 手动模式）。</summary>
    public void SetManualTier(int index)
    {
        _adaptive = false;
        _tierIndex = Math.Clamp(index, 0, QualityProfiles.Tiers.Length - 1);
        CreateEncoder();
    }

    public void SetAdaptive() => _adaptive = true;

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
    private readonly KcpUdpChannel _channel;
    private readonly string _ffmpeg;
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private VideoDecoder? _decoder;
    private Task? _writer;
    private readonly Action<byte[]> _onMessage;

    public event Action<byte[]>? FrameDecoded;
    public VideoInfo? Info { get; private set; }

    public ControllerVideoReceiver(KcpUdpChannel channel, string ffmpeg)
    {
        _channel = channel;
        _ffmpeg = ffmpeg;
        _onMessage = OnMessage;
        _channel.MessageReceived += _onMessage;
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
