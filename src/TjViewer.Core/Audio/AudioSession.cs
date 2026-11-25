using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TjViewer.Core.Net;
using TjViewer.Shared;

namespace TjViewer.Core.Audio;

/// <summary>
/// 音频发送（见 PRD FR-07）：WASAPI 采集（系统声音 loopback 或麦克风）→ 重采样 48k 单声道 → Opus 编码 → KCP。
/// direction: 0=被控→控制（系统声），1=控制→被控（麦克风）。
/// 注：回声消除（AEC）建议配合耳机使用；软件 AEC 为后续增强。
/// </summary>
public sealed class AudioSender : IDisposable
{
    private readonly IWaveIn _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly MediaFoundationResampler _resampler;
    private readonly IOpusEncoder _encoder;
    private readonly IMessageChannel _channel;
    private readonly byte _direction;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly short[] _frame = new short[960];
    private int _framePos;

    public AudioSender(IMessageChannel channel, byte direction, bool loopback)
    {
        _channel = channel;
        _direction = direction;
        _capture = loopback ? new WasapiLoopbackCapture() : new WasapiCapture();
        _buffer = new BufferedWaveProvider(_capture.WaveFormat) { DiscardOnBufferOverflow = true };
        _resampler = new MediaFoundationResampler(_buffer, new WaveFormat(48000, 16, 1));
        _encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _capture.DataAvailable += (_, e) => _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        _capture.StartRecording();
        _loop = Task.Run(ReadLoop);
    }

    private void ReadLoop()
    {
        var buf = new byte[960 * 2];
        while (!_cts.IsCancellationRequested)
        {
            int n;
            try { n = _resampler.Read(buf, 0, buf.Length); }
            catch { break; }
            if (n <= 0) { Thread.Sleep(5); continue; }
            for (int i = 0; i + 1 < n; i += 2)
            {
                _frame[_framePos++] = BitConverter.ToInt16(buf, i);
                if (_framePos == 960) { EncodeFrame(); _framePos = 0; }
            }
        }
    }

    private void EncodeFrame()
    {
        var outBuf = new byte[1276];
        int len = _encoder.Encode(_frame.AsSpan(), 960, outBuf.AsSpan(), outBuf.Length);
        if (len <= 0) return;
        var packet = new byte[len + 1];
        packet[0] = _direction;
        Array.Copy(outBuf, 0, packet, 1, len);
        _channel.Send(ChannelMux.Wrap(ChannelMsgType.Audio, packet));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _capture.StopRecording(); } catch { }
        try { _loop.Wait(500); } catch { }
        _capture.Dispose();
        _resampler.Dispose();
    }
}

/// <summary>音频接收：KCP → Opus 解码 → WASAPI 播放（见 PRD FR-07）。</summary>
public sealed class AudioReceiver : IDisposable
{
    private readonly IOpusDecoder _decoder;
    private readonly BufferedWaveProvider _playBuffer;
    private readonly WasapiOut _out;
    private readonly IMessageChannel _channel;
    private readonly byte _direction;
    private readonly Action<byte[]> _onMessage;

    public AudioReceiver(IMessageChannel channel, byte direction)
    {
        _channel = channel;
        _direction = direction;
        _decoder = OpusCodecFactory.CreateDecoder(48000, 1);
        _playBuffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 1)) { DiscardOnBufferOverflow = true };
        _out = new WasapiOut();
        _out.Init(_playBuffer);
        _out.Play();
        _onMessage = OnMessage;
        _channel.MessageReceived += _onMessage;
    }

    private void OnMessage(byte[] msg)
    {
        if (msg.Length < 2 || msg[0] != ChannelMsgType.Audio) return;
        var payload = msg[1..];
        if (payload.Length < 2 || payload[0] != _direction) return;
        var opus = payload[1..];
        var pcm = new short[960];
        int samples;
        try { samples = _decoder.Decode(opus.AsSpan(), pcm.AsSpan(), 960, false); }
        catch { return; }
        if (samples <= 0) return;
        var bytes = new byte[samples * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, samples * 2);
        _playBuffer.AddSamples(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        _channel.MessageReceived -= _onMessage;
        try { _out.Stop(); } catch { }
        _out.Dispose();
    }
}
