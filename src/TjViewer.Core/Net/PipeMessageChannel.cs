namespace TjViewer.Core.Net;

/// <summary>
/// 把命名管道流适配为 <see cref="IMessageChannel"/>，使 SecureAgent 能直接复用
/// HostVideoSender（采集→编码→发送）与既有 ChannelMux 协议。本地管道无拥塞，WaitSnd 恒 0。
/// </summary>
public sealed class PipeMessageChannel : IMessageChannel
{
    private readonly Stream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Task _readTask;

    public event Action<byte[]>? MessageReceived;
    /// <summary>读循环结束（对端关闭/出错）时触发。</summary>
    public event Action? Closed;
    public uint WaitSnd => 0;

    public PipeMessageChannel(Stream stream)
    {
        _stream = stream;
        _readTask = Task.Run(ReadLoop);
    }

    private async Task ReadLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                byte[]? msg;
                try { msg = await PipeFraming.ReadAsync(_stream, _cts.Token); }
                catch { break; }
                if (msg is null) break;
                MessageReceived?.Invoke(msg);
            }
        }
        finally { Closed?.Invoke(); }
    }

    public void Send(byte[] data)
    {
        _writeLock.Wait();
        try { PipeFraming.WriteAsync(_stream, data, _cts.Token).Wait(); }
        catch { }
        finally { _writeLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _readTask; } catch { }
        try { _stream.Dispose(); } catch { }
    }
}
