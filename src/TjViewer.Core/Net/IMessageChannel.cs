namespace TjViewer.Core.Net;

/// <summary>
/// 会话消息通道抽象：P2P KCP/UDP 或服务器中转 WebSocket。
/// 视频、音频、输入、文件、控制消息统一经此通道收发（首字节复用，见 ChannelMsgType）。
/// </summary>
public interface IMessageChannel : IAsyncDisposable
{
    event Action<byte[]>? MessageReceived;
    void Send(byte[] data);
    /// <summary>发送队列深度（拥塞信号，用于自适应码率与文件节流）。</summary>
    uint WaitSnd { get; }
}
