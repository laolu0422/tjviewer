namespace TjViewer.Client.Services;

/// <summary>稳定设备标识与系统概要（见 PRD FR-03）。</summary>
public static class DeviceIdentity
{
    public static string GetOrCreateKey(ClientSettings s)
    {
        if (string.IsNullOrEmpty(s.DeviceKey))
        {
            s.DeviceKey = Guid.NewGuid().ToString("N");
            SecretStore.Save(s);
        }
        return s.DeviceKey;
    }

    public static string OsSummary => $"{Environment.OSVersion.VersionString} ({Environment.MachineName})";
}
