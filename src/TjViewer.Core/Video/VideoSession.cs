using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using TjViewer.Core.Net;
using TjViewer.Shared;

namespace TjViewer.Core.Video;

/// <summary>
/// 被控端视频发送：采集 → H.264 编码 → 通道。
/// 手动模式帧率/码率独立可调（受服务端 4K/120fps/10Mbps 限制）；
/// 无论画面是否变化都按设定帧率持续推流，保证操作反馈及时。
///
/// 性能关键：采集与编码解耦。采集线程只负责把画面写进固定尺寸的帧池并投递有界队列，
/// 编码线程异步消费；队列满或帧池耗尽时直接丢弃新帧（低延迟优先）。
/// 这样慢编码器（Intel QSV 首帧初始化、高负载软编）不会反压采集，
/// 从根本上消除“编码阻塞采集 → 帧率骤降/延迟持续累积”的卡顿。
/// </summary>
public sealed class HostVideoSender : IAsyncDisposable
{
    private const int FramePoolSize = 3;
    private const int EncodeQueueCapacity = 2;

    private readonly IDesktopCapturer _capturer;
    private readonly IMessageChannel _channel;
    private readonly string _ffmpeg;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _encoderLock = new();
    private readonly object _poolLock = new();
    private readonly Stack<byte[]> _freeFrames = new();
    private readonly Channel<FrameJob> _encodeQueue;
    private readonly Task _captureLoop;
    private readonly Task _encodeLoop;
    private VideoEncoder? _encoder;
    private int _encoderBitrateKbps;
    private int _outWidth;
    private int _outHeight;
    private int _inFlight;
    private int _disposed;
    private volatile bool _paused;
    private volatile bool _adaptive = true;
    private volatile bool _forceRecreate;
    private volatile int _tierIndex;
    private volatile int _fps;
    private volatile int _bitrateKbps;
    private int _stableCount;

    private readonly record struct FrameJob(byte[] Data, int Width, int Height, int Fps, int BitrateKbps);

    /// <summary>采集到的原始桌面尺寸。</summary>
    public int Width => _capturer.Width;
    public int Height => _capturer.Height;
    /// <summary>实际编码输出尺寸（超过 4K 时按比例缩小）。</summary>
    public int OutputWidth => Volatile.Read(ref _outWidth);
    public int OutputHeight => Volatile.Read(ref _outHeight);
    public int CurrentTier => _tierIndex;
    public int CurrentFps => _fps;
    public int CurrentBitrateKbps => _bitrateKbps;
    public bool IsAdaptive => _adaptive;

    /// <param name="channel">远端会话通道。</param>
    /// <param name="ffmpeg">ffmpeg 路径。</param>
    /// <param name="initialTier">初始画质档位。</param>
    /// <param name="forceGdi">强制 GDI 采集（安全桌面代理必须开启：DXGI 在 winlogon 桌面不可用）。</param>
    /// <param name="gdiAttachInputDesktop">GDI 采集线程是否绑定输入桌面（安全桌面下必须开启）。</param>
    public HostVideoSender(IMessageChannel channel, string ffmpeg, int initialTier = 1,
        bool forceGdi = false, bool gdiAttachInputDesktop = false)
    {
        _channel = channel;
        _ffmpeg = ffmpeg;
        _capturer = CreateCapturer(forceGdi, gdiAttachInputDesktop);
        _tierIndex = Math.Clamp(initialTier, 0, QualityProfiles.Tiers.Length - 1);
        _fps = VideoLimits.ClampFps(QualityProfiles.Tiers[_tierIndex].MaxFps);
        _bitrateKbps = VideoLimits.ClampBitrate(QualityProfiles.Tiers[_tierIndex].MaxBitrateKbps);
        (_outWidth, _outHeight) = VideoLimits.ClampSize(_capturer.Width, _capturer.Height);

        _encodeQueue = Channel.CreateBounded<FrameJob>(new BoundedChannelOptions(EncodeQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _encodeLoop = Task.Run(EncodeLoop);
        _captureLoop = Task.Run(CaptureLoop);
        SendVideoInfo(_outWidth, _outHeight, _fps);
    }

    /// <summary>优先 DXGI Desktop Duplication，不可用时回退 GDI；安全桌面强制 GDI。</summary>
    private static IDesktopCapturer CreateCapturer(bool forceGdi, bool attachInputDesktop)
    {
        if (!forceGdi)
        {
            try { return new DxgiCapturer(); }
            catch (Exception ex)
            {
                Trace.WriteLine("DXGI 采集不可用，回退 GDI：" + ex.Message);
            }
        }
        return new GdiCapturer(attachInputDesktop);
    }

    /// <summary>编码线程：按帧携带的参数消费队列，参数变化时重建编码器（避免采集线程等待编码器初始化）。</summary>
    private async Task EncodeLoop()
    {
        try
        {
            while (await _encodeQueue.Reader.WaitToReadAsync(_cts.Token))
            {
                while (_encodeQueue.Reader.TryRead(out var job))
                {
                    try
                    {
                        VideoEncoder? encoder;
                        lock (_encoderLock)
                        {
                            if (_forceRecreate || _encoder is null || _encoder.Width != job.Width ||
                                _encoder.Height != job.Height || _encoder.Fps != job.Fps ||
                                _encoderBitrateKbps != job.BitrateKbps)
                            {
                                _forceRecreate = false;
                                CreateEncoderLocked(job.Width, job.Height, job.Fps, job.BitrateKbps);
                            }
                            encoder = _encoder;
                        }
                        if (encoder is not null) await encoder.EncodeFrameAsync(job.Data);
                    }
                    catch (Exception ex)
                    {
                        FFmpegLocator.Emit("视频编码写入失败：" + ex.Message);
                    }
                    finally
                    {
                        ReturnFrame(job.Data);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FFmpegLocator.Emit("编码循环异常：" + ex.Message); }
    }

    /// <summary>创建编码器并通知对端；必须持有 <see cref="_encoderLock"/>。</summary>
    private void CreateEncoderLocked(int width, int height, int fps, int bitrateKbps)
    {
        if (_encoder is not null)
        {
            try { _encoder.DisposeAsync().AsTask().Wait(2000); } catch { }
            _encoder = null;
        }

        var (ow, oh) = VideoLimits.ClampSize(width, height);
        Volatile.Write(ref _outWidth, ow);
        Volatile.Write(ref _outHeight, oh);
        _encoderBitrateKbps = bitrateKbps;
        _encoder = new VideoEncoder(_ffmpeg, width, height, fps, bitrateKbps, null, ow, oh);
        _encoder.EncodedChunk += data => _channel.Send(ChannelMux.Wrap(ChannelMsgType.Video, data));
        SendVideoInfo(ow, oh, fps);
    }

    /// <summary>周期性重发视频协商信息，避免对端晚加入时错过首包。</summary>
    private void SendVideoInfo(int width, int height, int fps)
    {
        try
        {
            var info = JsonSerializer.SerializeToUtf8Bytes(new VideoInfo(width, height, fps), SignalJson.Options);
            _channel.Send(ChannelMux.Wrap(ChannelMsgType.Control, info));
        }
        catch { }
    }

    private async Task CaptureLoop()
    {
        var frameWatch = Stopwatch.StartNew();
        var adaptWatch = Stopwatch.StartNew();
        var infoWatch = Stopwatch.StartNew();
        long backlogStartMs = -1, lastUpgradeMs = 0;
        while (!_cts.IsCancellationRequested)
        {
            // 安全桌面期间由 SecureAgent 接管采集，本地循环暂停
            if (_paused)
            {
                try { await Task.Delay(50, _cts.Token); } catch { break; }
                continue;
            }

            int fps = Math.Max(1, _fps);
            int w = _capturer.Width, h = _capturer.Height;
            int length = w * h * 4;

            // 帧池/编码队列已满说明编码跟不上：直接丢弃本帧，绝不阻塞采集（低延迟优先）
            if (length > 0 && Volatile.Read(ref _inFlight) < FramePoolSize)
            {
                var buffer = RentFrame(length);
                Interlocked.Increment(ref _inFlight);
                bool captured;
                try { captured = _capturer.CaptureFrameInto(buffer, Math.Clamp(1000 / fps / 4, 2, 4)); }
                catch { captured = false; }

                if (!captured)
                {
                    ReturnFrame(buffer);
                }
                else if (!_encodeQueue.Writer.TryWrite(new FrameJob(buffer, w, h, fps, _bitrateKbps)))
                {
                    ReturnFrame(buffer); // 队列满：丢弃新帧，避免延迟累积
                }
            }

            if (infoWatch.ElapsedMilliseconds >= 1000)
            {
                infoWatch.Restart();
                SendVideoInfo(OutputWidth, OutputHeight, _fps);
            }

            // 按当前帧率补齐间隔（DXGI 有新帧时立即返回，GDI 采集后同样节流）
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
        // 编码器由编码线程在下一帧按新参数重建，避免与编码写入竞争管道
    }

    public void SetAdaptive()
    {
        _adaptive = true;
        _fps = VideoLimits.ClampFps(QualityProfiles.Tiers[_tierIndex].MaxFps);
        _bitrateKbps = VideoLimits.ClampBitrate(QualityProfiles.Tiers[_tierIndex].MaxBitrateKbps);
    }

    /// <summary>暂停本地采集/编码（安全桌面由 SecureAgent 接管时调用）。</summary>
    public void Pause() => _paused = true;
    /// <summary>恢复本地采集/编码；重建编码器强制输出新关键帧，避免解码端沿用代理码流的参考帧产生花屏。</summary>
    public void Resume()
    {
        _forceRecreate = true;
        _paused = false;
    }

    private byte[] RentFrame(int length)
    {
        lock (_poolLock)
        {
            while (_freeFrames.Count > 0)
            {
                var buffer = _freeFrames.Pop();
                if (buffer.Length == length) return buffer; // 尺寸不符（分辨率变化）的直接废弃
            }
        }
        return GC.AllocateUninitializedArray<byte>(length);
    }

    private void ReturnFrame(byte[] buffer)
    {
        Interlocked.Decrement(ref _inFlight);
        lock (_poolLock)
        {
            if (_freeFrames.Count < FramePoolSize) _freeFrames.Push(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        _encodeQueue.Writer.TryComplete();
        try { await _captureLoop; } catch { }
        try { await _encodeLoop; } catch { }
        VideoEncoder? encoder;
        lock (_encoderLock) { encoder = _encoder; _encoder = null; }
        if (encoder is not null) { try { await encoder.DisposeAsync(); } catch { } }
        _capturer.Dispose();
        _cts.Dispose();
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
