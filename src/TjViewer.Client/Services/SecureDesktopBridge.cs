using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using TjViewer.Core;
using TjViewer.Core.Net;
using TjViewer.Shared.Logging;

namespace TjViewer.Client.Services;

/// <summary>
/// 被控端安全桌面桥接：检测到 Winlogon/UAC 安全桌面时，请求后台服务以 SYSTEM 拉起 SecureAgent，
/// 建立命名管道把代理采集的画面转发到远程通道，并把控制端键鼠/画质消息转发给代理注入。
/// 安全桌面结束或代理断开后自动回切本地采集。需已安装后台服务；否则保持本地采集（降级）。
///
/// 时序要求：必须先创建管道服务端，再请求服务拉起代理，否则代理可能因找不到管道而连接超时退出。
/// 检测采用连续采样去抖，避免桌面切换瞬间误触发。
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
    private long _lastFailTicks;

    public bool IsActive => _active;
    public event Action? EnteredSecure;
    public event Action? ExitedSecure;

    public SecureDesktopBridge(IMessageChannel channel) => _channel = channel;

    public void Start() => _monitor = Task.Run(MonitorLoop);

    private async Task MonitorLoop()
    {
        var secureStreak = 0;
        var normalStreak = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // 连续两次采样一致才切换（每 300ms 一次），过滤桌面切换瞬间的抖动
                if (SecureDesktop.IsSecureDesktopActive())
                {
                    secureStreak++;
                    normalStreak = 0;
                }
                else
                {
                    normalStreak++;
                    secureStreak = 0;
                }

                if (!_active && secureStreak >= 2) await TryEnterAsync();
                else if (_active && normalStreak >= 2) Exit();
            }
            catch (Exception ex)
            {
                FileLog.Error("安全桌面监测异常", ex);
            }
            try { await Task.Delay(300, _cts.Token); } catch { break; }
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

            // 先创建管道服务端，再请求拉起代理，避免代理先连接时管道尚不存在
            try { CreatePipeServer(pipeName); }
            catch (Exception ex)
            {
                LogEnterFailure("创建安全桌面管道失败：" + ex.Message);
                return;
            }

            var (launched, launchError) = await RequestLaunchAsync(pipeName, session);
            if (!launched)
            {
                CleanupPipe();
                LogEnterFailure("后台服务不可用或拒绝拉起 SecureAgent：" + launchError);
                return;
            }

            FileLog.Info($"已请求在会话 {session} 的 winlogon 桌面拉起 SecureAgent，等待管道连接…");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(8000);
            try
            {
                await _pipe!.WaitForConnectionAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                LogEnterFailure("等待 SecureAgent 连接失败：" + ex.Message);
                CleanupPipe();
                return;
            }

            _pipeChannel = new PipeMessageChannel(_pipe);
            _pipeChannel.Closed += () =>
            {
                FileLog.Warn("SecureAgent 管道已断开");
                if (_active) Exit();
            };
            _pipeChannel.MessageReceived += msg => { try { _channel.Send(msg); } catch { } };
            _active = true;
            FileLog.Info($"安全桌面接管已建立：输入桌面={SecureDesktop.GetInputDesktopName()}");
            EnteredSecure?.Invoke();
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private void CreatePipeServer(string pipeName)
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl, AccessControlType.Allow));
        _pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, ps);
    }

    private void Exit()
    {
        if (!_active) return;
        _active = false;
        CleanupPipe();
        FileLog.Info("安全桌面已结束，回切本地采集");
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

    private static async Task<(bool Ok, string Error)> RequestLaunchAsync(string pipeName, int session)
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
            var ok = doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            var error = ok
                ? ""
                : doc.RootElement.TryGetProperty("error", out var err) ? err.GetString() ?? "服务返回失败" : "服务返回失败";
            return (ok, error);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>进入失败 10 秒内只记一次，避免安全桌面持续存在时刷爆日志。</summary>
    private void LogEnterFailure(string message)
    {
        var now = Environment.TickCount64;
        if (now - _lastFailTicks < 10_000) return;
        _lastFailTicks = now;
        FileLog.Warn("进入安全桌面失败：" + message);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _active = false;
        if (_monitor is not null) try { await _monitor; } catch { }
        CleanupPipe();
    }
}
