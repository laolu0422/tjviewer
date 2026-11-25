namespace TjViewer.Shared;

/// <summary>账户状态（见 PRD §3.3）。</summary>
public enum AccountStatus
{
    Active = 0,
    Disabled = 1,
}

/// <summary>设备在线状态（见 PRD §3.3）。</summary>
public enum DeviceStatus
{
    Offline = 0,
    Online = 1,
    Controlled = 2,
}

/// <summary>连接会话状态（见 PRD §3.3）。</summary>
public enum SessionState
{
    Initiating,
    PendingAuth,
    Connected,
    Reconnecting,
    Ended,
    Failed,
}

/// <summary>文件传输任务状态（见 PRD §3.3）。</summary>
public enum TransferState
{
    Queued,
    Transferring,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>连接路径（见 PRD FR-09）。</summary>
public enum ConnectionPath
{
    Unknown,
    P2P,
    Relay,
}

/// <summary>画质模式（见 PRD FR-05）。</summary>
public enum QualityMode
{
    Adaptive,
    Manual,
}

/// <summary>音频方向（见 PRD FR-07）。</summary>
public enum AudioDirection
{
    HostToController,
    ControllerToHost,
}

/// <summary>能力权限位，独立授予/收回（见 PRD §3.2 / §5.2）。</summary>
[Flags]
public enum Capabilities
{
    None = 0,
    View = 1,
    Control = 2,
    File = 4,
    AudioListen = 8,
    MicSend = 16,
    All = View | Control | File | AudioListen | MicSend,
}
