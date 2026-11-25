using System.Collections.Concurrent;
using System.Text.Json;
using TjViewer.Core.Net;
using TjViewer.Shared;

namespace TjViewer.Core.Video;

/// <summary>被控端视频发送：采集 → H.264 编码 → KCP 通道（见 PRD FR-05 / M2）。</summary>
public sealed class HostVideoSender : IAsyncDisposable
{
    private readonly IDesktopCapturer _capturer;
    private readonly VideoEncoder _encoder;
    private readonly KcpUdpChannel _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public int Width => _capturer.Width;
    public int Height => _capturer.Height;

    public HostVideoSender(KcpUdpChannel channel, string ffmpeg, int fps = 30, int bitrateKbps = 1500)
    {
        _channel = channel;
        _capturer = new GdiCapturer();
        _encoder = new VideoEncoder(ffmpeg, _capturer.Width, _capturer.Height, fps, bitrateKbps);
        _encoder.EncodedChunk += data => _channel.Send(ChannelMux.Wrap(ChannelMsgType.Video, data));

        // 先发送视频协商信息
        var info = JsonSerializer.SerializeToUtf8Bytes(new VideoInfo(_capturer.Width, _capturer.Height, fps), SignalJson.Options);
        _channel.Send(ChannelMux.Wrap(ChannelMsgType.Control, info));

        _loop = Task.Run(async () =>
        {
            int delay = Math.Max(1, 1000 / fps);
            while (!_cts.IsCancellationRequested)
            {
                var frame = _capturer.CaptureFrame();
                if (frame is not null)
                {
                    try { await _encoder.EncodeFrameAsync(frame); } catch { break; }
                }
                try { await Task.Delay(delay, _cts.Token); } catch { break; }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop; } catch { }
        await _encoder.DisposeAsync();
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
            if (info is not null && _decoder is null)
            {
                Info = info;
                _decoder = new VideoDecoder(_ffmpeg, info.Width, info.Height);
                _decoder.FrameDecoded += f => FrameDecoded?.Invoke(f);
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
