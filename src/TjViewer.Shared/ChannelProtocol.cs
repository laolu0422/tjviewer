namespace TjViewer.Shared;

/// <summary>KCP 通道上的消息类型（首字节复用，见 M1/M2）。</summary>
public static class ChannelMsgType
{
    public const byte Video = 1;
    public const byte Input = 2;
    public const byte Audio = 3;
    public const byte File = 4;
    public const byte Control = 5;
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
