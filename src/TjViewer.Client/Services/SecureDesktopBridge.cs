using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using TjViewer.Core;
using TjViewer.Core.Net;

namespace TjViewer.Client.Services;

/// <summary>
/// 被控端安全桌面桥接：检测到 Winlogon/UAC 安全桌面时，请求后台服务以 SYSTEM 拉起 SecureAgent，
/// 建立命名管道把代理采集的画面转发到远程通道，并把控制端键鼠/画质消息转发给代理注入。
/// 安全桌面结束或代理断开后自动回切本地采集。需已安装后台服务；否则保持本地采集（降级）。
/// </summary>
public sealed class SecureDesktopBridge : IAsyncDisposable
{
    private readonly IMessageChannel _channel;
    private readonly CancellationTokenSource _cts = new();
    private Task? _monitor;
    private NamedPipeServerStream? _pipe;
    private PipeMessageChannel? _pipeChannel;
    private volatile bool _active;
    private int _busy;

    public bool IsActive => _active;
    public event Action? EnteredSecure;
    public event Action? ExitedSecure;

    public SecureDesktopBridge(IMessageChannel channel) => _channel = channel;

    public void Start() => _monitor = Task.Run(MonitorLoop);

    private async Task MonitorLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                bool secure = SecureDesktop.IsSecureDesktopActive();
                if (secure && !_active) await TryEnterAsync();
                else if (!secure && _active) Exit();
            }
            catch { }
            try { await Task.Delay(500, _cts.Token); } catch { break; }
        }
    }

    private async Task TryEnterAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var pipeName = "TjViewer.Secure." + Guid.NewGuid().ToString("N");
            int session;
            try { session = Process.GetCurrentProcess().SessionId; } catch { session = 0; }

            if (!await RequestLaunchAsync(pipeName, session)) return; // 服务不可用：降级保持本地采集

            var ps = new PipeSecurity();
            ps.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(
                WindowsIdentity.GetCurrent().User!,
                PipeAccessRights.FullControl, AccessControlType.Allow));
            _pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, ps);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(8000);
            try { await _pipe.WaitForConnectionAsync(timeout.Token); }
            catch { CleanupPipe(); return; }

            _pipeChannel = new PipeMessageChannel(_pipe);
            _pipeChannel.Closed += () => { if (_active) Exit(); };
            _pipeChannel.MessageReceived += msg => { try { _channel.Send(msg); } catch { } };
            _active = true;
            EnteredSecure?.Invoke();
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private void Exit()
    {
        if (!_active) return;
        _active = false;
        CleanupPipe();
        ExitedSecure?.Invoke();
    }

    private void CleanupPipe()
    {
        var pc = _pipeChannel;
        _pipeChannel = null;
        _pipe = null;
        if (pc is not null) _ = pc.DisposeAsync().AsTask(); // 内部释放管道流
    }

    /// <summary>把控制端的输入/画质消息转发给 SecureAgent。</summary>
    public void SendToAgent(byte[] msg)
    {
        var pc = _pipeChannel;
        if (pc is not null && _active) { try { pc.Send(msg); } catch { } }
    }

    private static async Task<bool> RequestLaunchAsync(string pipeName, int session)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", "TjViewer.Service.Control",
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(3000);
            var req = JsonSerializer.Serialize(new { Action = "launch-secure-agent", PipeName = pipeName, SessionId = session });
            using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(req);
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync();
            using var doc = JsonDocument.Parse(line ?? "{}");
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _active = false;
        if (_monitor is not null) try { await _monitor; } catch { }
        CleanupPipe();
    }
}
