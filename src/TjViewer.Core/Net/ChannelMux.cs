namespace TjViewer.Core.Net;

/// <summary>通道消息复用：首字节为类型，其余为负载。</summary>
public static class ChannelMux
{
    public static byte[] Wrap(byte type, byte[] payload)
    {
        var b = new byte[payload.Length + 1];
        b[0] = type;
        Array.Copy(payload, 0, b, 1, payload.Length);
        return b;
    }

    public static (byte Type, byte[] Payload) Unwrap(byte[] msg)
        => (msg[0], msg[1..]);
}
