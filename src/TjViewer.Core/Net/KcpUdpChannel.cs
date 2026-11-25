using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace TjViewer.Core.Net;

/// <summary>UDP + KCP 可靠通道（见 PRD FR-09 / M1）。绑定本地 UDP，KCP 输出经 UDP 发往对端。</summary>
public class KcpUdpChannel : IMessageChannel
{
    private readonly UdpClient _udp;
    private readonly Kcp _kcp;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private IPEndPoint? _remote;
    private Task? _recvTask, _updateTask;

    public event Action<byte[]>? MessageReceived;
    public uint Conv { get; }
    public IPEndPoint LocalEndpoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    public KcpUdpChannel(uint conv, int localPort = 0)
    {
        Conv = conv;
        _udp = new UdpClient(localPort);
        // 大接收/发送缓冲：120fps 高码率与突发重传时减少内核丢包
        try
        {
            _udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
            _udp.Client.SendBufferSize = 4 * 1024 * 1024;
        }
        catch { }
        _kcp = new Kcp(conv);
        // 5ms 快节拍 + 关闭拥塞控制，配合 512 段窗口降低视频排队时延
        _kcp.NoDelay(1, 5, 2, 1);
        _kcp.WndSize(512, 512);
        _kcp.SetOutput((buf, len) => { if (_remote is not null) _udp.Send(buf, len, _remote); });
    }

    public void SetRemote(IPEndPoint remote) => _remote = remote;

    /// <summary>经本通道 UDP socket 查询 STUN 公网映射。</summary>
    public Task<IPEndPoint?> QueryStunAsync(string stunHost, int stunPort = 19302)
        => StunClient.GetPublicEndpointAsync(_udp, stunHost, stunPort);

    public void Start()
    {
        _recvTask = Task.Run(ReceiveLoop);
        _updateTask = Task.Run(UpdateLoop);
    }

    /// <summary>NAT 打洞：向对端发送若干 UDP 包以打开映射（见 PRD FR-09）。</summary>
    public async Task PunchAsync(int attempts = 5, int intervalMs = 200)
    {
        if (_remote is null) return;
        var probe = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        for (int i = 0; i < attempts; i++)
        {
            try { await _udp.SendAsync(probe, probe.Length, _remote); } catch { }
            await Task.Delay(intervalMs);
        }
    }

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _udp.ReceiveAsync(_cts.Token);
                if (r.Buffer.Length == 4 && r.Buffer[0] == 0xFF) continue; // 打洞探测包
                lock (_lock) _kcp.Input(r.Buffer, r.Buffer.Length);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { }
            catch (ObjectDisposedException) { break; }
        }
    }

    private async Task UpdateLoop()
    {
        var sw = Stopwatch.StartNew();
        var buf = new byte[1 << 20];
        while (!_cts.IsCancellationRequested)
        {
            uint ts = (uint)sw.ElapsedMilliseconds;
            List<byte[]>? received = null;
            lock (_lock)
            {
                _kcp.Update(ts);
                int n;
                while ((n = _kcp.Recv(buf)) > 0)
                {
                    (received ??= new()).Add(buf[..n]);
                }
            }
            if (received is not null)
                foreach (var msg in received) MessageReceived?.Invoke(msg);
            // 与 NoDelay 的 5ms 区间对齐；客户端启动时已调用 timeBeginPeriod(1) 保证精度
            try { await Task.Delay(5, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Send(byte[] data)
    {
        lock (_lock) _kcp.Send(data);
    }

    /// <summary>发送队列深度（拥塞信号，用于自适应码率，见 PRD FR-05/M3）。</summary>
    public uint WaitSnd { get { lock (_lock) return _kcp.WaitSnd; } }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _udp.Close(); } catch { }
        if (_recvTask is not null) try { await _recvTask; } catch { }
        if (_updateTask is not null) try { await _updateTask; } catch { }
        _udp.Dispose();
    }
}
