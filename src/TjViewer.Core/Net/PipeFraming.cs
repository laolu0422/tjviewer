namespace TjViewer.Core.Net;

/// <summary>命名管道消息分帧：4 字节小端长度 + 一条 ChannelMux 消息。SecureAgent 与客户端共用。</summary>
public static class PipeFraming
{
    private const int MaxMessage = 64 * 1024 * 1024;

    public static async Task WriteAsync(Stream s, byte[] msg, CancellationToken ct)
    {
        var len = BitConverter.GetBytes(msg.Length);
        await s.WriteAsync(len, ct);
        await s.WriteAsync(msg, ct);
        await s.FlushAsync(ct);
    }

    /// <summary>读取一条完整消息；流结束或非法长度返回 null。</summary>
    public static async Task<byte[]?> ReadAsync(Stream s, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        if (!await ReadExactAsync(s, lenBuf, ct)) return null;
        int len = BitConverter.ToInt32(lenBuf);
        if (len <= 0 || len > MaxMessage) return null;
        var buf = new byte[len];
        return await ReadExactAsync(s, buf, ct) ? buf : null;
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(off), ct);
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }
}
