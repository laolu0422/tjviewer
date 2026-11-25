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
        ChildProcessTracker.Track(p); // 完全关闭时统一终止，防止残留
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) FFmpegLocator.Emit(e.Data); };
        p.BeginErrorReadLine();
        return p;
    }
}

/// <summary>
/// 编解码硬件能力探测（见规格书 §11「硬件编码优先、软件兜底」）。
/// 编码按 NVIDIA NVENC → Intel QSV → AMD AMF → libx264 顺序实测探测；解码按 D3D11VA 实测探测。
/// 只用 <c>ffmpeg -encoders</c> 判断是不够的（仅证明编译进了二进制），这里用极短真实码流跑一次初始化与解码。
/// 结果按 ffmpeg 路径缓存，避免每次改档重建编码器都重新探测。
/// 环境变量应急开关：<c>TJVIEWER_ENCODER=libx264</c> 强制指定编码器；<c>TJVIEWER_HWACCEL=none</c>/<c>0</c> 禁用硬解。
/// </summary>
public static class HardwareCodecs
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> EncoderCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> DecoderCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>硬件编码器候选（按优先级）；libx264 永远兜底。</summary>
    private static readonly string[] EncoderCandidates = { "h264_nvenc", "h264_qsv", "h264_amf" };

    /// <summary>各编码器的低延迟参数。</summary>
    internal static string OptionsFor(string encoder) => encoder switch
    {
        "h264_nvenc" => "-preset p1 -tune ull -rc cbr -delay 0",
        "h264_qsv" => "-preset veryfast -async_depth 1",
        "h264_amf" => "-usage ultralowlatency -quality speed -rc cbr",
        _ => "-preset ultrafast -tune zerolatency",
    };

    /// <summary>各编码器偏好的输入像素格式（QSV 要求 nv12）。</summary>
    internal static string PixFmtFor(string encoder) => encoder == "h264_qsv" ? "nv12" : "yuv420p";

    /// <summary>选择编码器名；无可用硬件时返回 libx264。</summary>
    public static string SelectEncoder(string ffmpeg)
    {
        var forced = Environment.GetEnvironmentVariable("TJVIEWER_ENCODER");
        if (!string.IsNullOrWhiteSpace(forced)) return forced.Trim();

        lock (Gate)
        {
            if (EncoderCache.TryGetValue(ffmpeg, out var cached)) return cached;
            var chosen = "libx264";
            foreach (var cand in EncoderCandidates)
            {
                if (!ProbeEncoder(ffmpeg, cand)) continue;
                chosen = cand;
                break;
            }
            EncoderCache[ffmpeg] = chosen;
            FFmpegLocator.Emit(chosen == "libx264"
                ? "视频编码器：libx264（软件，未检测到可用硬件编码器）"
                : $"视频编码器：{chosen}（硬件加速）");
            return chosen;
        }
    }

    /// <summary>选择解码硬件加速方式；不可用时返回 null（软件解码）。</summary>
    public static string? SelectDecoderHwAccel(string ffmpeg)
    {
        var forced = Environment.GetEnvironmentVariable("TJVIEWER_HWACCEL");
        if (!string.IsNullOrWhiteSpace(forced))
            return forced.Equals("none", StringComparison.OrdinalIgnoreCase) || forced == "0" ? null : forced.Trim();

        lock (Gate)
        {
            if (DecoderCache.TryGetValue(ffmpeg, out var cached)) return cached;
            string? hw = ProbeDecoder(ffmpeg, "d3d11va") ? "d3d11va" : null;
            DecoderCache[ffmpeg] = hw;
            FFmpegLocator.Emit(hw is null ? "视频解码：软件" : "视频解码：d3d11va（硬件加速）");
            return hw;
        }
    }

    /// <summary>后台预热探测，避免首帧被探测耗时阻塞。</summary>
    public static void WarmUp(string ffmpeg) => _ = Task.Run(() =>
    {
        try { SelectEncoder(ffmpeg); SelectDecoderHwAccel(ffmpeg); } catch { }
    });

    private static bool ProbeEncoder(string ffmpeg, string encoder)
    {
        var args = $"-hide_banner -loglevel error -f lavfi -i color=black:s=256x144:r=30:d=0.2 " +
                   $"-c:v {encoder} {OptionsFor(encoder)} -b:v 800k -maxrate 800k -bufsize 1600k -g 60 -bf 0 -pix_fmt {PixFmtFor(encoder)} -an -f null -";
        var ok = Run(ffmpeg, args, 15000, out var err);
        if (!ok) FFmpegLocator.Emit($"编码器探测失败 {encoder}：{FirstLine(err)}");
        return ok;
    }

    private static bool ProbeDecoder(string ffmpeg, string hwaccel)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tjviewer-hwprobe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var src = Path.Combine(dir, "t.h264");
            var dst = Path.Combine(dir, "o.bgra");
            if (!Run(ffmpeg, $"-hide_banner -loglevel error -y -f lavfi -i testsrc2=s=320x240:r=10:d=0.2 -c:v libx264 -preset ultrafast -f h264 \"{src}\"", 15000, out _))
                return false;
            if (!Run(ffmpeg, $"-hide_banner -loglevel error -y -hwaccel {hwaccel} -f h264 -i \"{src}\" -f rawvideo -pix_fmt bgra \"{dst}\"", 15000, out var err))
            {
                FFmpegLocator.Emit($"硬解探测失败 {hwaccel}：{FirstLine(err)}");
                return false;
            }
            return new FileInfo(dst).Length > 0;
        }
        catch { return false; }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static bool Run(string ffmpeg, string args, int timeoutMs, out string stderr)
    {
        stderr = "";
        try
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
            using var p = Process.Start(psi);
            if (p is null) return false;
            var errTask = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            try { p.StandardInput.Close(); } catch { }
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return false; }
            stderr = errTask.GetAwaiter().GetResult();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "(无输出)";
        var i = s.IndexOfAny(new[] { '\r', '\n' });
        return i < 0 ? s.Trim() : s[..i].Trim();
    }
}

/// <summary>H.264 编码器：BGRA 帧入 → H.264 字节流出（见 PRD FR-05）。默认自动选择硬件编码器，失败回退 libx264。</summary>
public sealed class VideoEncoder : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readTask;

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    /// <summary>实际使用的编码器名（h264_nvenc / h264_qsv / h264_amf / libx264）。</summary>
    public string EncoderName { get; }
    public event Action<byte[]>? EncodedChunk;

    public VideoEncoder(string ffmpeg, int width, int height, int fps, int bitrateKbps, string? encoder = null, int outputWidth = 0, int outputHeight = 0)
    {
        Width = width; Height = height; Fps = fps;
        EncoderName = string.IsNullOrWhiteSpace(encoder) ? HardwareCodecs.SelectEncoder(ffmpeg) : encoder.Trim();
        // 超过服务端 4K 上限时由 ffmpeg 缩放输出
        var scale = outputWidth > 0 && outputHeight > 0 && (outputWidth != width || outputHeight != height)
            ? $"-vf scale={outputWidth}:{outputHeight}:flags=fast_bilinear "
            : "";
        var args = $"-hide_banner -loglevel error -f rawvideo -pix_fmt bgra -video_size {width}x{height} -framerate {fps} -i pipe:0 " +
                   $"{scale}-c:v {EncoderName} {HardwareCodecs.OptionsFor(EncoderName)} -flags low_delay -b:v {bitrateKbps}k -maxrate {bitrateKbps}k -bufsize {bitrateKbps * 2}k -g {fps * 2} -bf 0 -pix_fmt {HardwareCodecs.PixFmtFor(EncoderName)} -flush_packets 1 -f h264 pipe:1";
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

/// <summary>H.264 解码器：H.264 字节流入 → BGRA 帧出（固定输出尺寸）。默认 D3D11VA 硬解，不可用回退软件解码。</summary>
public sealed class VideoDecoder : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readTask;
    private readonly int _frameSize;

    public int Width { get; }
    public int Height { get; }
    /// <summary>实际使用的硬件加速方式；null 表示软件解码。</summary>
    public string? HwAccel { get; }
    public event Action<byte[]>? FrameDecoded;

    public VideoDecoder(string ffmpeg, int width, int height)
    {
        Width = width; Height = height;
        _frameSize = width * height * 4;
        HwAccel = HardwareCodecs.SelectDecoderHwAccel(ffmpeg);
        var hw = HwAccel is null ? "" : $"-hwaccel {HwAccel} ";
        // low_delay + nobuffer 消除解码侧帧缓存；软件解码用 slice 线程避免 frame 线程引入整帧时延
        var threads = HwAccel is null ? "-thread_type slice " : "";
        var args = $"-hide_banner -loglevel error -probesize 32768 -analyzeduration 0 {hw}-fflags nobuffer -flags low_delay {threads}-f h264 -i pipe:0 -f rawvideo -pix_fmt bgra -s {width}x{height} -flush_packets 1 pipe:1";
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
