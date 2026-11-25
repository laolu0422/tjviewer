using System.IO;
using System.Net.WebSockets;
using System.Text;
using TjViewer.Shared;

namespace TjViewer.Client.Services;

/// <summary>WebSocket 信令客户端（见 PRD FR-04 / FR-09）。事件在后台线程触发，UI 需自行调度。</summary>
public class SignalClient : IAsyncDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private readonly AppSession _s;

    public event Action<PresencePayload>? Presence;
    public event Action<ConnectIncomingPayload>? ConnectIncoming;
    public event Action<ConnectReadyPayload>? ConnectReady;
    public event Action<ConnectFailedPayload>? ConnectFailed;
    public event Action<PeerSignalPayload>? PeerSignal;
    public event Action<RelaySwitchPayload>? RelaySwitch;
    public event Action<CodesUpdatedPayload>? CodesUpdated;
    public event Action<SessionEndPayload>? SessionEnd;
    public event Action<string>? Disconnected;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public SignalClient(AppSession s) => _s = s;

    public async Task ConnectAsync()
    {
        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(_s.WsUrl), _cts.Token);
        await SendAsync(SignalJson.Create(SignalTypes.Hello, new HelloPayload(_s.AccessToken ?? "", _s.DeviceId ?? "")));
        _ = ReceiveLoop(_cts.Token);
    }

    public Task SendAsync(SignalMessage msg)
        => _ws!.SendAsync(Encoding.UTF8.GetBytes(SignalJson.Serialize(msg)), WebSocketMessageType.Text, true, _cts!.Token);

    public Task RequestConnect(string targetDeviceId, Capabilities caps)
        => SendAsync(SignalJson.Create(SignalTypes.ConnectRequest, new ConnectRequestPayload(targetDeviceId, caps)));

    public Task Accept(string sessionId)
        => SendAsync(SignalJson.Create(SignalTypes.ConnectAccept, new ConnectAcceptPayload(sessionId)));

    public Task Reject(string sessionId, string? reason)
        => SendAsync(SignalJson.Create(SignalTypes.ConnectReject, new ConnectRejectPayload(sessionId, reason)));

    public Task EndSession(string sessionId, string? reason)
        => SendAsync(SignalJson.Create(SignalTypes.SessionEnd, new SessionEndPayload(sessionId, reason)));

    public Task SendPeerSignal(string sessionId, string data)
        => SendAsync(SignalJson.Create(SignalTypes.PeerSignal, new PeerSignalPayload(sessionId, data)));

    public Task SendRelaySwitch(string sessionId)
        => SendAsync(SignalJson.Create(SignalTypes.RelaySwitch, new RelaySwitchPayload(sessionId)));

    public Task SendCodeConnect(string code, string password, string mode)
        => SendAsync(SignalJson.Create(SignalTypes.CodeConnect, new CodeConnectPayload(code, password, mode)));

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (_ws!.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, ct);
                    if (r.MessageType == WebSocketMessageType.Close) { Disconnected?.Invoke("连接已关闭"); return; }
                    ms.Write(buffer, 0, r.Count);
                    if (ms.Length > 1_000_000) return;
                } while (!r.EndOfMessage);

                var msg = SignalJson.Deserialize(Encoding.UTF8.GetString(ms.ToArray()));
                if (msg is not null) Dispatch(msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Disconnected?.Invoke(ex.Message); }
    }

    private void Dispatch(SignalMessage msg)
    {
        switch (msg.Type)
        {
            case SignalTypes.Presence: Presence?.Invoke(SignalJson.Parse<PresencePayload>(msg)!); break;
            case SignalTypes.ConnectIncoming: ConnectIncoming?.Invoke(SignalJson.Parse<ConnectIncomingPayload>(msg)!); break;
            case SignalTypes.ConnectReady: ConnectReady?.Invoke(SignalJson.Parse<ConnectReadyPayload>(msg)!); break;
            case SignalTypes.ConnectFailed: ConnectFailed?.Invoke(SignalJson.Parse<ConnectFailedPayload>(msg)!); break;
            case SignalTypes.PeerSignal: PeerSignal?.Invoke(SignalJson.Parse<PeerSignalPayload>(msg)!); break;
            case SignalTypes.RelaySwitch: RelaySwitch?.Invoke(SignalJson.Parse<RelaySwitchPayload>(msg)!); break;
            case SignalTypes.CodesUpdated: CodesUpdated?.Invoke(SignalJson.Parse<CodesUpdatedPayload>(msg)!); break;
            case SignalTypes.SessionEnd: SessionEnd?.Invoke(SignalJson.Parse<SessionEndPayload>(msg)!); break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_ws is not null)
        {
            try { if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { }
            _ws.Dispose();
        }
    }
}
