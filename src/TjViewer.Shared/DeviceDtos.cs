namespace TjViewer.Shared;

/// <summary>设备注册（见 PRD FR-03）。DeviceKey 为稳定设备标识。</summary>
public sealed record RegisterDeviceRequest(string DeviceKey, string DeviceName, string? Note, string OsSummary);

public sealed record DeviceDto(
    string Id,
    string DeviceKey,
    string Name,
    string? Note,
    DeviceStatus Status,
    DateTimeOffset LastSeen,
    string OsSummary,
    bool UnattendedEnabled,
    bool AutoStart);

public sealed record UpdateDeviceRequest(string? Name, string? Note, bool? UnattendedEnabled, bool? AutoStart);

public sealed record HeartbeatRequest(string DeviceId);
