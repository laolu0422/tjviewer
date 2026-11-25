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
public sealed record ConnectIncomingPayload(string SessionId, string ControllerDeviceId, string ControllerDeviceName, Capabilities Capabilities);
public sealed record ConnectAcceptPayload(string SessionId);
public sealed record ConnectRejectPayload(string SessionId, string? Reason);
public sealed record ConnectReadyPayload(string SessionId, string HostDeviceId, string HostDeviceName, Capabilities Capabilities);
public sealed record ConnectFailedPayload(string? SessionId, string Reason);
public sealed record PeerSignalPayload(string SessionId, string Data);
public sealed record SessionEndPayload(string SessionId, string? Reason);
public sealed record ErrorPayload(string Code, string Message);

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
