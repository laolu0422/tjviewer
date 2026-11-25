using System.Diagnostics;

namespace TjViewer.Core.Video;

/// <summary>定位 ffmpeg.exe（应用目录 / tools / 用户目录 / PATH）。</summary>
public static class FFmpegLocator
{
    /// <summary>诊断：ffmpeg stderr 输出（排查用）。</summary>
    public static event Action<string>? Diagnostics;
    internal static void Emit(string line) => Diagnostics?.Invoke(line);

    public static string? Find()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var cand in new[] { Path.Combine(baseDir, "ffmpeg.exe"), Path.Combine(baseDir, "tools", "ffmpeg.exe") })
            if (File.Exists(cand)) return cand;

        var userShare = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "ffmpeg");
        if (Directory.Exists(userShare))
        {
            var f = Directory.GetFiles(userShare, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (f is not null) return f;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = Path.Combine(p.Trim(), "ffmpeg.exe");
            if (File.Exists(f)) return f;
        }
        return null;
    }
}

internal static class ProcHelper
{
    public static Process Start(string ffmpeg, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ffmpeg");
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) FFmpegLocator.Emit(e.Data); };
        p.BeginErrorReadLine();
        return p;
    }
}

/// <summary>H.264 编码器：BGRA 帧入 → H.264 字节流出（见 PRD FR-05）。默认 libx264 ultrafast+zerolatency，可换硬件编码器。</summary>
public sealed class VideoEncoder : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readTask;

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public event Action<byte[]>? EncodedChunk;

    public VideoEncoder(string ffmpeg, int width, int height, int fps, int bitrateKbps, string encoder = "libx264")
    {
        Width = width; Height = height; Fps = fps;
        var encArgs = encoder == "libx264" ? "-preset ultrafast -tune zerolatency" : "-preset p1 -tune ll";
        var args = $"-hide_banner -loglevel error -f rawvideo -pix_fmt bgra -video_size {width}x{height} -framerate {fps} -i pipe:0 " +
                   $"-c:v {encoder} {encArgs} -b:v {bitrateKbps}k -maxrate {bitrateKbps}k -bufsize {bitrateKbps * 2}k -g {fps * 2} -bf 0 -pix_fmt yuv420p -flush_packets 1 -f h264 pipe:1";
        _proc = ProcHelper.Start(ffmpeg, args);
        _readTask = Task.Run(ReadLoop);
    }

    public async Task EncodeFrameAsync(byte[] bgra)
    {
        var s = _proc.StandardInput.BaseStream;
        await s.WriteAsync(bgra);
        await s.FlushAsync();
    }

    private async Task ReadLoop()
    {
        var buf = new byte[65536];
        var s = _proc.StandardOutput.BaseStream;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await s.ReadAsync(buf, _cts.Token);
                if (n <= 0) break;
                EncodedChunk?.Invoke(buf[..n]);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _proc.StandardInput.Close(); } catch { }
        try { if (!_proc.HasExited) _proc.Kill(); } catch { }
        if (_readTask is not null) try { await _readTask; } catch { }
        _proc.Dispose();
    }
}

/// <summary>H.264 解码器：H.264 字节流入 → BGRA 帧出（固定输出尺寸）。</summary>
public sealed class VideoDecoder : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readTask;
    private readonly int _frameSize;

    public int Width { get; }
    public int Height { get; }
    public event Action<byte[]>? FrameDecoded;

    public VideoDecoder(string ffmpeg, int width, int height)
    {
        Width = width; Height = height;
        _frameSize = width * height * 4;
        var args = $"-hide_banner -loglevel error -probesize 32768 -analyzeduration 100000 -f h264 -i pipe:0 -f rawvideo -pix_fmt bgra -s {width}x{height} -flush_packets 1 pipe:1";
        _proc = ProcHelper.Start(ffmpeg, args);
        _readTask = Task.Run(ReadLoop);
    }

    public async Task WriteEncodedAsync(byte[] data)
    {
        var s = _proc.StandardInput.BaseStream;
        await s.WriteAsync(data);
        await s.FlushAsync();
    }

    /// <summary>关闭输入流（EOF），促使解码器 flush 尾部缓冲帧。用于有限流结束；实时流不调用。</summary>
    public void CompleteInput()
    {
        try { _proc.StandardInput.Close(); } catch { }
    }

    private async Task ReadLoop()
    {
        var s = _proc.StandardOutput.BaseStream;
        var frame = new byte[_frameSize];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int off = 0;
                while (off < _frameSize)
                {
                    int n = await s.ReadAsync(frame.AsMemory(off, _frameSize - off), _cts.Token);
                    if (n <= 0) return;
                    off += n;
                }
                FrameDecoded?.Invoke((byte[])frame.Clone());
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _proc.StandardInput.Close(); } catch { }
        try { if (!_proc.HasExited) _proc.Kill(); } catch { }
        if (_readTask is not null) try { await _readTask; } catch { }
        _proc.Dispose();
    }
}
