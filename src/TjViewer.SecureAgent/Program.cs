using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using TjViewer.Core;
using TjViewer.Core.Input;
using TjViewer.Core.Net;
using TjViewer.Core.Video;
using TjViewer.Shared;

namespace TjViewer.SecureAgent;

/// <summary>
/// 安全桌面代理：由后台服务以 SYSTEM 身份在 winsta0\winlogon 桌面拉起（uiAccess=true，须签名）。
/// 复用 HostVideoSender 采集→编码→经命名管道回传客户端；同时接收客户端转发的键鼠/画质消息并注入。
/// 安全桌面结束（输入桌面回到 Default）或管道断开即退出。
/// </summary>
public static class Program
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TjViewer", "logs");

    public static async Task<int> Main(string[] args)
    {
        string? pipeName = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--pipe") pipeName = args[i + 1];
        if (string.IsNullOrEmpty(pipeName)) { Log("缺少 --pipe 参数"); return 1; }

        RuntimeTuning.Apply();
        Log($"SecureAgent 启动：桌面={SecureDesktop.GetInputDesktopName()} 管道={pipeName} 进程={Environment.ProcessId}");

        using var cts = new CancellationTokenSource();
        var exit = new TaskCompletionSource();
        cts.Token.Register(() => exit.TrySetResult());

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        PipeMessageChannel? channel = null;
        HostVideoSender? sender = null;
        try
        {
            await pipe.ConnectAsync(8000, cts.Token);
            Log("管道已连接");
            channel = new PipeMessageChannel(pipe);
            channel.Closed += () => cts.Cancel();

            // 先接收消息：代理初始化编码器期间到达的画质/输入消息先缓存，避免丢失
            var pending = new ConcurrentQueue<byte[]>();
            channel.MessageReceived += msg =>
            {
                var s = sender;
                if (s is null) { pending.Enqueue(msg); return; }
                OnMessage(msg, s);
            };

            var ffmpeg = FFmpegLocator.Find();
            if (ffmpeg is null) { Log("未找到 ffmpeg"); return 1; }

            // 安全桌面强制使用 GDI 采集：DXGI Desktop Duplication 在 winlogon 安全桌面通常
            // 返回 AccessLost 或采集到黑屏，GDI 才能稳定抓取 UAC/锁屏画面。
            // 生命周期只跟随管道：安全桌面结束时由客户端关闭管道触发退出，
            // 不再周期性判断输入桌面（切换瞬间误判会导致代理提前退出、画面黑屏）。
            sender = new HostVideoSender(channel, ffmpeg, forceGdi: true, gdiAttachInputDesktop: true);
            while (pending.TryDequeue(out var queued)) OnMessage(queued, sender);
            Log($"采集已启动：{sender.Width}x{sender.Height}（GDI，绑定输入桌面）");

            await exit.Task;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log("异常：" + ex); }
        finally
        {
            if (sender is not null) await sender.DisposeAsync();
            if (channel is not null) await channel.DisposeAsync();
            Log("SecureAgent 退出");
        }
        return 0;
    }

    private static void OnMessage(byte[] msg, HostVideoSender sender)
    {
        if (msg.Length < 1) return;
        var (type, payload) = ChannelMux.Unwrap(msg);
        if (type == ChannelMsgType.Input)
        {
            var ev = JsonSerializer.Deserialize<InputEvent>(payload, SignalJson.Options);
            if (ev is not null) ApplyInput(ev, sender);
        }
        else if (type == ChannelMsgType.Quality)
        {
            var q = JsonSerializer.Deserialize<QualityRequest>(payload, SignalJson.Options);
            if (q is null) return;
            if (q.Mode == "adaptive") sender.SetAdaptive();
            else sender.SetManual(q.Tier, q.Fps, q.BitrateKbps);
        }
    }

    private static void ApplyInput(InputEvent ev, HostVideoSender sender)
    {
        int vw = sender.OutputWidth, vh = sender.OutputHeight;
        int sw = sender.Width, sh = sender.Height;
        switch (ev.Kind)
        {
            case "move":
                var (x, y) = Map(ev.X, ev.Y, vw, vh, sw, sh);
                InputInjector.MoveMouseAbsolute(x, y, sw, sh);
                break;
            case "button": InputInjector.MouseButtonEvent((MouseButton)ev.Button, ev.Down); break;
            case "wheel": InputInjector.Wheel(ev.Delta); break;
            case "key": InputInjector.ScanCode(ev.Scan, ev.Down, ev.Extended); break;
            case "text": if (ev.Text is not null) InputInjector.Text(ev.Text); break;
        }
    }

    /// <summary>视频可能因 4K 上限被缩放，坐标换算回安全桌面原生像素。</summary>
    private static (int, int) Map(int x, int y, int vw, int vh, int sw, int sh)
    {
        if (vw > 0 && vh > 0 && (vw != sw || vh != sh))
            return ((int)((long)x * sw / vw), (int)((long)y * sh / vh));
        return (x, y);
    }

    private static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(Path.Combine(LogDir, $"secure-agent-{DateTime.Now:yyyyMMdd}.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch { }
    }
}
