namespace TjViewer.Shared;

/// <summary>KCP 通道上的消息类型（首字节复用，见 M1/M2）。</summary>
public static class ChannelMsgType
{
    public const byte Video = 1;
    public const byte Input = 2;
    public const byte Audio = 3;
    public const byte File = 4;
    public const byte Control = 5;
    public const byte Quality = 6;
    public const byte Handshake = 7;
}

/// <summary>输入事件（控制端→被控端，见 PRD FR-06）。Kind: move/button/wheel/key/text。</summary>
public sealed record InputEvent(
    string Kind,
    int X = 0,
    int Y = 0,
    int Button = 0,
    bool Down = false,
    int Delta = 0,
    ushort Scan = 0,
    bool Extended = false,
    string? Text = null);

/// <summary>视频协商信息（被控端→控制端）。</summary>
public sealed record VideoInfo(int Width, int Height, int Fps);

/// <summary>画质请求（控制端→被控端，见 PRD FR-05/M3）。Mode: adaptive/manual；manual 时 Tier/Fps/BitrateKbps 独立生效，0 表示用默认值。</summary>
public sealed record QualityRequest(string Mode, int Tier = 1, int Fps = 0, int BitrateKbps = 0);

/// <summary>文件传输子类型（见 PRD FR-08/M6）。</summary>
public static class FileMsgType
{
    public const byte Offer = 0;
    public const byte Chunk = 1;
    public const byte Done = 2;
    public const byte Resume = 3;
}

/// <summary>文件传输要约。</summary>
public sealed record FileOffer(string TransferId, string Name, long Size, string Sha256);
