using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using TjViewer.Shared;

namespace TjViewer.Server.Signaling;

/// <summary>在线连接与活跃会话的内存协调（持久化审计另入数据库）。</summary>
public class ConnectionManager
{
    public sealed record Conn(string UserId, string DeviceId, WebSocket Socket);
    public sealed record Session(string Id, string UserId, string ControllerDeviceId, string HostDeviceId, Capabilities Capabilities, SessionState State);

    private readonly ConcurrentDictionary<string, Conn> _byDevice = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public void Add(Conn c) => _byDevice[c.DeviceId] = c;
    public void Remove(string deviceId) => _byDevice.TryRemove(deviceId, out _);
    public Conn? Get(string deviceId) => _byDevice.TryGetValue(deviceId, out var c) ? c : null;
    public IReadOnlyList<Conn> ForUser(string userId) => _byDevice.Values.Where(c => c.UserId == userId).ToList();

    public void AddSession(Session s) => _sessions[s.Id] = s;
    public Session? GetSession(string id) => _sessions.TryGetValue(id, out var s) ? s : null;
    public void UpdateSessionState(string id, SessionState state)
    {
        if (_sessions.TryGetValue(id, out var s)) _sessions[id] = s with { State = state };
    }
    public void RemoveSession(string id) => _sessions.TryRemove(id, out _);
    public IReadOnlyList<Session> SessionsForDevice(string deviceId)
        => _sessions.Values.Where(s => s.ControllerDeviceId == deviceId || s.HostDeviceId == deviceId).ToList();

    public async Task SendAsync(WebSocket ws, SignalMessage msg, CancellationToken ct = default)
    {
        if (ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(SignalJson.Serialize(msg));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    public Task SendToAsync(string deviceId, SignalMessage msg, CancellationToken ct = default)
    {
        var c = Get(deviceId);
        return c is null ? Task.CompletedTask : SendAsync(c.Socket, msg, ct);
    }
}
