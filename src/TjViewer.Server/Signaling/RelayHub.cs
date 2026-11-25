using System.IdentityModel.Tokens.Jwt;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TjViewer.Server.Data;
using TjViewer.Server.Services;
using TjViewer.Shared;

namespace TjViewer.Server.Signaling;

/// <summary>
/// 全功能会话中转（见 PRD FR-09）：P2P 不可用时，视频/音频/输入/文件/控制消息经此 WebSocket 二进制转发。
/// 仅允许会话成员接入；双方就绪后通知 relay_ready；单帧上限 2MB；TCP 保证可靠有序。
/// </summary>
public class RelayHub
{
    private sealed class Peer
    {
        public WebSocket Socket = null!;
        public string DeviceId = "";
        public readonly SemaphoreSlim Gate = new(1, 1); // WebSocket 不允许并发发送
    }

    private sealed class Pair
    {
        public Peer? Controller;
        public Peer? Host;
    }

    private readonly ConnectionManager _cm;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TokenService _tokens;
    private readonly ILogger<RelayHub> _log;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Pair> _pairs = new();

    public RelayHub(ConnectionManager cm, IServiceScopeFactory scopeFactory, TokenService tokens, ILogger<RelayHub> log)
    {
        _cm = cm;
        _scopeFactory = scopeFactory;
        _tokens = tokens;
        _log = log;
    }

    public async Task HandleAsync(HttpContext ctx, WebSocket ws)
    {
        var ct = ctx.RequestAborted;
        var hello = await ReadTextAsync(ws, 65536, ct);
        var hp = hello is null ? null : JsonSerializer.Deserialize<RelayHelloPayload>(hello, SignalJson.Options);
        if (hp is null || string.IsNullOrEmpty(hp.SessionId) || string.IsNullOrEmpty(hp.DeviceId)) { await CloseAsync(ws); return; }

        var principal = _tokens.Validate(hp.Token ?? "");
        var uid = principal is null ? null : ClaimsHelper.UserId(principal);
        if (uid is null) { await CloseAsync(ws); return; }

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dev = await db.Devices.FirstOrDefaultAsync(d => d.Id == hp.DeviceId && d.OwnerId == uid, ct);
            if (dev is null) { await CloseAsync(ws); return; }
        }

        var session = _cm.GetSession(hp.SessionId);
        if (session is null || (session.ControllerDeviceId != hp.DeviceId && session.HostDeviceId != hp.DeviceId))
        {
            await CloseAsync(ws);
            return;
        }
        bool isController = session.ControllerDeviceId == hp.DeviceId;

        var peer = new Peer { Socket = ws, DeviceId = hp.DeviceId };
        var pair = _pairs.GetOrAdd(hp.SessionId, _ => new Pair());
        Peer? other;
        lock (pair)
        {
            if (isController) pair.Controller = peer; else pair.Host = peer;
            other = isController ? pair.Host : pair.Controller;
        }
        _log.LogInformation("中转接入：会话 {Session} 设备 {Device}（{Role}）", hp.SessionId, hp.DeviceId, isController ? "控制端" : "被控端");

        if (other is not null)
        {
            await SendTextAsync(peer, RelayText.Ready);
            await SendTextAsync(other, RelayText.Ready);
        }

        var buf = new byte[65536];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close) goto done;
                    ms.Write(buf, 0, r.Count);
                    if (ms.Length > 2_000_000) goto done;
                } while (!r.EndOfMessage);

                if (r.MessageType != WebSocketMessageType.Binary) continue;
                Peer? target;
                lock (pair) { target = isController ? pair.Host : pair.Controller; }
                if (target is not null)
                    await SendBinaryAsync(target, ms.ToArray()); // 背压：对端发送慢时此处阻塞，经 TCP 传导至发送端
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        done:
        Peer? left;
        lock (pair)
        {
            if (isController) pair.Controller = null; else pair.Host = null;
            left = isController ? pair.Host : pair.Controller;
            if (pair.Controller is null && pair.Host is null) _pairs.TryRemove(hp.SessionId, out _);
        }
        if (left is not null) await SendTextAsync(left, RelayText.PeerLeft);
        await CloseAsync(ws);
        _log.LogInformation("中转离开：会话 {Session} 设备 {Device}", hp.SessionId, hp.DeviceId);
    }

    private static async Task SendBinaryAsync(Peer p, byte[] data)
    {
        await p.Gate.WaitAsync();
        try { if (p.Socket.State == WebSocketState.Open) await p.Socket.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None); }
        catch { }
        finally { p.Gate.Release(); }
    }

    private static async Task SendTextAsync(Peer p, string text)
    {
        await p.Gate.WaitAsync();
        try { if (p.Socket.State == WebSocketState.Open) await p.Socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { }
        finally { p.Gate.Release(); }
    }

    private static async Task<string?> ReadTextAsync(WebSocket ws, int limit, CancellationToken ct)
    {
        var buf = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType != WebSocketMessageType.Text) return null;
            ms.Write(buf, 0, r.Count);
            if (ms.Length > limit) return null;
        } while (!r.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task CloseAsync(WebSocket ws)
    {
        try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
        catch { }
    }
}
