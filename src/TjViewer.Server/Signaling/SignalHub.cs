using System.IdentityModel.Tokens.Jwt;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TjViewer.Server.Data;
using TjViewer.Server.Services;
using TjViewer.Shared;

namespace TjViewer.Server.Signaling;

/// <summary>WebSocket 信令：鉴权握手、在线状态、连接授权协调、对端信令中继（见 PRD FR-04 / FR-09）。</summary>
public class SignalHub
{
    private readonly ConnectionManager _cm;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TokenService _tokens;
    private readonly ILogger<SignalHub> _log;

    public SignalHub(ConnectionManager cm, IServiceScopeFactory scopeFactory, TokenService tokens, ILogger<SignalHub> log)
    {
        _cm = cm;
        _scopeFactory = scopeFactory;
        _tokens = tokens;
        _log = log;
    }

    public async Task HandleAsync(HttpContext ctx, WebSocket ws)
    {
        var ct = ctx.RequestAborted;
        var hello = await ReadMessageAsync(ws, ct);
        if (hello is null || hello.Type != SignalTypes.Hello) { await CloseAsync(ws); return; }
        var hp = SignalJson.Parse<HelloPayload>(hello);
        if (hp is null || string.IsNullOrEmpty(hp.DeviceId)) { await CloseAsync(ws); return; }

        var principal = _tokens.Validate(hp.Token ?? "");
        var uid = principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (uid is null)
        {
            await _cm.SendAsync(ws, SignalJson.Create(SignalTypes.Error, new ErrorPayload("unauthorized", "令牌无效或已过期")), ct);
            await CloseAsync(ws);
            return;
        }

        string deviceName;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dev = await db.Devices.FirstOrDefaultAsync(d => d.Id == hp.DeviceId && d.OwnerId == uid, ct);
            if (dev is null)
            {
                await _cm.SendAsync(ws, SignalJson.Create(SignalTypes.Error, new ErrorPayload("forbidden", "设备不存在或不属于本账户")), ct);
                await CloseAsync(ws);
                return;
            }
            dev.Status = DeviceStatus.Online;
            dev.LastSeen = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            deviceName = dev.Name;
        }

        _cm.Add(new ConnectionManager.Conn(uid, hp.DeviceId, ws));
        await _cm.SendAsync(ws, SignalJson.Create(SignalTypes.HelloAck, new HelloAckPayload(hp.DeviceId, DateTimeOffset.UtcNow)), ct);
        await BroadcastPresenceAsync(uid, hp.DeviceId, DeviceStatus.Online, ct);
        _log.LogInformation("设备 {Device} 上线（账户 {User}）", hp.DeviceId, uid);

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var msg = await ReadMessageAsync(ws, ct);
                if (msg is null) break;
                await DispatchAsync(uid, hp.DeviceId, deviceName, msg, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            _cm.Remove(hp.DeviceId);
            await EndSessionsForDeviceAsync(hp.DeviceId, "设备离线", ct);
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dev = await db.Devices.FindAsync(new object?[] { hp.DeviceId }, CancellationToken.None);
            if (dev is not null)
            {
                dev.Status = DeviceStatus.Offline;
                dev.LastSeen = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
            }
            await BroadcastPresenceAsync(uid, hp.DeviceId, DeviceStatus.Offline, CancellationToken.None);
            _log.LogInformation("设备 {Device} 离线", hp.DeviceId);
        }
    }

    private async Task DispatchAsync(string uid, string deviceId, string deviceName, SignalMessage msg, CancellationToken ct)
    {
        switch (msg.Type)
        {
            case SignalTypes.ConnectRequest:
                await OnConnectRequest(uid, deviceId, deviceName, SignalJson.Parse<ConnectRequestPayload>(msg), ct);
                break;
            case SignalTypes.ConnectAccept:
                await OnConnectAccept(deviceId, SignalJson.Parse<ConnectAcceptPayload>(msg), ct);
                break;
            case SignalTypes.ConnectReject:
                await OnConnectReject(deviceId, SignalJson.Parse<ConnectRejectPayload>(msg), ct);
                break;
            case SignalTypes.PeerSignal:
                await OnPeerSignal(deviceId, SignalJson.Parse<PeerSignalPayload>(msg), ct);
                break;
            case SignalTypes.SessionEnd:
                await OnSessionEnd(deviceId, SignalJson.Parse<SessionEndPayload>(msg), ct);
                break;
            case SignalTypes.Heartbeat:
                await TouchDevice(deviceId, ct);
                break;
        }
    }

    private async Task OnConnectRequest(string uid, string deviceId, string deviceName, ConnectRequestPayload? p, CancellationToken ct)
    {
        if (p is null) return;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // 授权：目标设备必须同账户（跨账户本期排除，见 PRD §5.2）
        var target = await db.Devices.FirstOrDefaultAsync(d => d.Id == p.TargetDeviceId && d.OwnerId == uid, ct);
        if (target is null) { await Fail(deviceId, null, "设备不存在或不属于本账户", ct); return; }
        if (target.Status == DeviceStatus.Offline || _cm.Get(p.TargetDeviceId) is null) { await Fail(deviceId, null, "设备离线", ct); return; }

        var session = new ConnectionManager.Session(Guid.NewGuid().ToString("N"), uid, deviceId, p.TargetDeviceId, p.Capabilities, SessionState.PendingAuth);
        _cm.AddSession(session);
        db.Sessions.Add(new SessionRecord
        {
            Id = session.Id,
            ControllerDeviceId = deviceId,
            HostDeviceId = p.TargetDeviceId,
            State = SessionState.PendingAuth,
            Capabilities = p.Capabilities,
        });
        await db.SaveChangesAsync(ct);

        await _cm.SendToAsync(p.TargetDeviceId, SignalJson.Create(SignalTypes.ConnectIncoming,
            new ConnectIncomingPayload(session.Id, deviceId, deviceName, p.Capabilities)), ct);
    }

    private async Task OnConnectAccept(string deviceId, ConnectAcceptPayload? p, CancellationToken ct)
    {
        if (p is null) return;
        var s = _cm.GetSession(p.SessionId);
        if (s is null || s.HostDeviceId != deviceId) return; // 授权校验
        _cm.UpdateSessionState(s.Id, SessionState.Connected);
        await UpdateSessionRecord(s.Id, SessionState.Connected, ct);

        string hostName = deviceId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dev = await db.Devices.FindAsync(new object?[] { s.HostDeviceId }, ct);
            if (dev is not null) { hostName = dev.Name; dev.Status = DeviceStatus.Controlled; await db.SaveChangesAsync(ct); }
        }
        await _cm.SendToAsync(s.ControllerDeviceId, SignalJson.Create(SignalTypes.ConnectReady,
            new ConnectReadyPayload(s.Id, s.HostDeviceId, hostName, s.Capabilities)), ct);
    }

    private async Task OnConnectReject(string deviceId, ConnectRejectPayload? p, CancellationToken ct)
    {
        if (p is null) return;
        var s = _cm.GetSession(p.SessionId);
        if (s is null || s.HostDeviceId != deviceId) return;
        _cm.RemoveSession(s.Id);
        await UpdateSessionRecord(s.Id, SessionState.Ended, ct);
        await _cm.SendToAsync(s.ControllerDeviceId, SignalJson.Create(SignalTypes.ConnectFailed,
            new ConnectFailedPayload(s.Id, p.Reason ?? "对方拒绝了连接")), ct);
    }

    private async Task OnPeerSignal(string deviceId, PeerSignalPayload? p, CancellationToken ct)
    {
        if (p is null) return;
        var s = _cm.GetSession(p.SessionId);
        if (s is null) return;
        if (s.ControllerDeviceId != deviceId && s.HostDeviceId != deviceId) return; // 仅会话成员可中继
        var other = deviceId == s.ControllerDeviceId ? s.HostDeviceId : s.ControllerDeviceId;
        await _cm.SendToAsync(other, SignalJson.Create(SignalTypes.PeerSignal, p), ct);
    }

    private async Task OnSessionEnd(string deviceId, SessionEndPayload? p, CancellationToken ct)
    {
        if (p is null) return;
        var s = _cm.GetSession(p.SessionId);
        if (s is null) return;
        if (s.ControllerDeviceId != deviceId && s.HostDeviceId != deviceId) return;
        _cm.RemoveSession(s.Id);
        await UpdateSessionRecord(s.Id, SessionState.Ended, ct);
        var other = deviceId == s.ControllerDeviceId ? s.HostDeviceId : s.ControllerDeviceId;
        await _cm.SendToAsync(other, SignalJson.Create(SignalTypes.SessionEnd, p), ct);
        await ResetHostStatus(s.HostDeviceId, ct);
    }

    private async Task EndSessionsForDeviceAsync(string deviceId, string reason, CancellationToken ct)
    {
        foreach (var s in _cm.SessionsForDevice(deviceId))
        {
            _cm.RemoveSession(s.Id);
            await UpdateSessionRecord(s.Id, SessionState.Ended, CancellationToken.None);
            var other = deviceId == s.ControllerDeviceId ? s.HostDeviceId : s.ControllerDeviceId;
            await _cm.SendToAsync(other, SignalJson.Create(SignalTypes.SessionEnd, new SessionEndPayload(s.Id, reason)), CancellationToken.None);
        }
    }

    private async Task ResetHostStatus(string hostDeviceId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dev = await db.Devices.FindAsync(new object?[] { hostDeviceId }, ct);
        if (dev is not null && dev.Status == DeviceStatus.Controlled)
        {
            dev.Status = DeviceStatus.Online;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task TouchDevice(string deviceId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dev = await db.Devices.FindAsync(new object?[] { deviceId }, ct);
        if (dev is not null) { dev.LastSeen = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); }
    }

    private async Task UpdateSessionRecord(string id, SessionState state, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rec = await db.Sessions.FindAsync(new object?[] { id }, ct);
        if (rec is not null)
        {
            rec.State = state;
            if (state is SessionState.Ended or SessionState.Failed) rec.EndedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private Task Fail(string deviceId, string? sessionId, string reason, CancellationToken ct)
        => _cm.SendToAsync(deviceId, SignalJson.Create(SignalTypes.ConnectFailed, new ConnectFailedPayload(sessionId, reason)), ct);

    private async Task BroadcastPresenceAsync(string uid, string deviceId, DeviceStatus status, CancellationToken ct)
    {
        var msg = SignalJson.Create(SignalTypes.Presence, new PresencePayload(deviceId, status));
        foreach (var c in _cm.ForUser(uid))
            if (c.DeviceId != deviceId)
                await _cm.SendAsync(c.Socket, msg, ct);
    }

    private static async Task<SignalMessage?> ReadMessageAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (ms.Length > 1_000_000) return null; // 信令消息大小上限
        } while (!result.EndOfMessage);
        return SignalJson.Deserialize(Encoding.UTF8.GetString(ms.ToArray()));
    }

    private static async Task CloseAsync(WebSocket ws)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
    }
}
