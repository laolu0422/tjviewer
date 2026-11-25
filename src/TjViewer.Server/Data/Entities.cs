using TjViewer.Shared;

namespace TjViewer.Server.Data;

public class User
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public AccountStatus Status { get; set; } = AccountStatus.Active;
    public bool IsAdmin { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Device> Devices { get; set; } = new();
}

public class Device
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceKey { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public User? Owner { get; set; }
    public string Name { get; set; } = "";
    public string? Note { get; set; }
    public string OsSummary { get; set; } = "";
    public DeviceStatus Status { get; set; } = DeviceStatus.Offline;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    public bool UnattendedEnabled { get; set; }
    public string? UnattendedHash { get; set; }
    public bool AutoStart { get; set; }
}

public class RefreshToken
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Revoked { get; set; }
}

public class PasswordResetToken
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public class SessionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ControllerDeviceId { get; set; } = "";
    public string HostDeviceId { get; set; } = "";
    public SessionState State { get; set; }
    public Capabilities Capabilities { get; set; }
    public ConnectionPath Path { get; set; } = ConnectionPath.Unknown;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
}

public class AuditEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? ActorId { get; set; }
    public string Action { get; set; } = "";
    public string? Target { get; set; }
    public string Result { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class SystemConfig
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
