using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TjViewer.Service;

/// <summary>
/// 控制管道：客户端检测到安全桌面后，经此请求服务以 SYSTEM 在 winlogon 桌面拉起 SecureAgent。
/// 仅处理活动控制台会话的请求；管道允许已认证用户与 SYSTEM 访问。
/// </summary>
public sealed class ControlPipeListener : BackgroundService
{
    private const string PipeName = "TjViewer.Service.Control";
    private readonly ILogger<ControlPipeListener> _log;

    public ControlPipeListener(ILogger<ControlPipeListener> log) => _log = log;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, ps);
                await server.WaitForConnectionAsync(stoppingToken);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(stoppingToken);
                var resp = Handle(line);
                using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(resp);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning("控制管道异常：{Message}", ex.Message);
            }
            finally
            {
                if (server is not null)
                {
                    try { if (server.IsConnected) server.Disconnect(); } catch { }
                    server.Dispose();
                }
            }
        }
    }

    private string Handle(string? line)
    {
        try
        {
            var req = JsonSerializer.Deserialize<ControlRequest>(line ?? "");
            if (req is null || req.Action != "launch-secure-agent" || string.IsNullOrEmpty(req.PipeName))
                return Fail("未知请求");

            uint session = Native.WTSGetActiveConsoleSessionId();
            if (req.SessionId != session) return Fail("会话不匹配");

            if (SecureAgentLauncher.Launch(req.PipeName, session, out var err))
            {
                _log.LogInformation("已在会话 {Session} 的 winlogon 桌面拉起 SecureAgent", session);
                return "{\"ok\":true}";
            }
            _log.LogWarning("拉起 SecureAgent 失败：{Error}", err);
            return Fail(err);
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    private static string Fail(string error)
        => JsonSerializer.Serialize(new { ok = false, error });

    private sealed record ControlRequest(string Action, string? PipeName, uint SessionId);
}
