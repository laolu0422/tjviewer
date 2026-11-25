using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TjViewer.Shared;

namespace TjViewer.Core.Net;

/// <summary>
/// 服务器中转通道：WebSocket 二进制帧经服务器转发至对端，承载全部会话消息（音视频/输入/文件/控制）。
/// TCP 保证可靠有序；WaitSnd 为待发送队列深度，语义与 KCP 通道一致，供自适应码率与文件节流使用。
/// </summary>
public sealed class RelayChannel : IMessageChannel
{
    private readonly ClientWebSocket _ws = new();
    private readonly string _url, _token, _deviceId, _sessionId;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _sendLoop, _recvLoop;
    private int _pending;

    public event Action<byte[]>? MessageReceived;
    public event Action<string>? Closed;
    /// <summary>对端也已接入中转（双方就绪后才开始媒体协商，避免首包丢失）。</summary>
    public Task PeerReadyTask => _ready.Task;
    public uint WaitSnd => (uint)Volatile.Read(ref _pending);

    public RelayChannel(string url, string token, string deviceId, string sessionId)
    {
        _url = url; _token = token; _deviceId = deviceId; _sessionId = sessionId;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _ws.ConnectAsync(new Uri(_url), ct);
        var hello = JsonSerializer.SerializeToUtf8Bytes(new RelayHelloPayload(_token, _deviceId, _sessionId), SignalJson.Options);
        await _ws.SendAsync(hello, WebSocketMessageType.Text, true, ct);
        _sendLoop = Task.Run(SendLoop);
        _recvLoop = Task.Run(RecvLoop);
    }

    public void Send(byte[] data)
    {
        Interlocked.Increment(ref _pending);
        _outbox.Writer.TryWrite(data);
    }

    private async Task SendLoop()
    {
        try
        {
            await foreach (var data in _outbox.Reader.ReadAllAsync(_cts.Token))
            {
                await _ws.SendAsync(data, WebSocketMessageType.Binary, true, _cts.Token);
                Interlocked.Decrement(ref _pending);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Closed?.Invoke(ex.Message); }
    }

    private async Task RecvLoop()
    {
        var buf = new byte[65536];
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buf, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) { Closed?.Invoke("中转连接已关闭"); return; }
                    ms.Write(buf, 0, r.Count);
                    if (ms.Length > 2_000_000) return; // 单帧上限
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    if (text == RelayText.Ready) _ready.TrySetResult();
                    else if (text == RelayText.PeerLeft) Closed?.Invoke("对端已离开中转");
                }
                else
                {
                    MessageReceived?.Invoke(ms.ToArray());
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Closed?.Invoke(ex.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _outbox.Writer.TryComplete();
        try { if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        if (_sendLoop is not null) try { await _sendLoop; } catch { }
        if (_recvLoop is not null) try { await _recvLoop; } catch { }
        _ws.Dispose();
    }
}
