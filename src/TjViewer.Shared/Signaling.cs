using System.Text.Json;

namespace TjViewer.Shared;

/// <summary>WebSocket 信令消息类型（见 PRD FR-04 / FR-09）。</summary>
public static class SignalTypes
{
    public const string Hello = "hello";
    public const string HelloAck = "hello_ack";
    public const string Presence = "presence";
    public const string ConnectRequest = "connect_request";
    public const string ConnectIncoming = "connect_incoming";
    public const string ConnectAccept = "connect_accept";
    public const string ConnectReject = "connect_reject";
    public const string ConnectReady = "connect_ready";
    public const string ConnectFailed = "connect_failed";
    public const string PeerSignal = "peer_signal";
    public const string RelaySwitch = "relay_switch";
    public const string CodeConnect = "code_connect";
    public const string CodesUpdated = "codes_updated";
    public const string SessionEnd = "session_end";
    public const string Heartbeat = "heartbeat";
    public const string Error = "error";
}

/// <summary>信令信封：type + 原始 JSON 负载，按 type 解析为具体记录。</summary>
public sealed record SignalMessage(string Type, JsonElement Payload);

public sealed record HelloPayload(string Token, string DeviceId);
public sealed record HelloAckPayload(string DeviceId, DateTimeOffset ServerTime);
public sealed record PresencePayload(string DeviceId, DeviceStatus Status);
public sealed record ConnectRequestPayload(string TargetDeviceId, Capabilities Capabilities);
public sealed record ConnectIncomingPayload(string SessionId, string ControllerDeviceId, string ControllerDeviceName, Capabilities Capabilities,
    bool SameAccount = false, string Mode = "control");
public sealed record ConnectAcceptPayload(string SessionId);
public sealed record ConnectRejectPayload(string SessionId, string? Reason);
public sealed record ConnectReadyPayload(string SessionId, string HostDeviceId, string HostDeviceName, Capabilities Capabilities, string Mode = "control");
public sealed record ConnectFailedPayload(string? SessionId, string Reason);
public sealed record PeerSignalPayload(string SessionId, string Data);
public sealed record RelaySwitchPayload(string SessionId);
/// <summary>中转 WebSocket 握手（首条文本消息）。</summary>
public sealed record RelayHelloPayload(string Token, string DeviceId, string SessionId);
/// <summary>中转连接上的文本通知。</summary>
public static class RelayText
{
    public const string Ready = "relay_ready";
    public const string PeerLeft = "peer_left";
}
public sealed record SessionEndPayload(string SessionId, string? Reason);
public sealed record ErrorPayload(string Code, string Message);

/// <summary>陌生设备连接：连接码 + 验证码（mode: watch / control）。</summary>
public sealed record CodeConnectPayload(string Code, string Password, string Mode);

/// <summary>被控端连接码（服务端分配；观看与控制两套互不干涉）。</summary>
public sealed record DeviceCodes(string WatchCode, string WatchPassword, string ControlCode, string ControlPassword, bool FixedCode);

/// <summary>验证码使用后轮换，服务端推送新码给被控端。</summary>
public sealed record CodesUpdatedPayload(DeviceCodes Codes);

/// <summary>申请/刷新本机连接码。</summary>
public sealed record CodesRequest(string DeviceId, bool FixedCode);

public static class SignalJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static SignalMessage Create<T>(string type, T payload)
        => new(type, JsonSerializer.SerializeToElement(payload, Options));

    public static T? Parse<T>(SignalMessage msg)
        => msg.Payload.Deserialize<T>(Options);

    public static string Serialize(SignalMessage msg)
        => JsonSerializer.Serialize(msg, Options);

    public static SignalMessage? Deserialize(string json)
        => JsonSerializer.Deserialize<SignalMessage>(json, Options);
}
