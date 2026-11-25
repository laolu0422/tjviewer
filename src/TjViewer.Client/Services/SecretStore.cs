using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TjViewer.Client.Services;

public class ClientSettings
{
    public string ServerUrl { get; set; } = "http://localhost:5000";
    public string DeviceKey { get; set; } = "";
    public string? ProtectedRefreshToken { get; set; }
    public string? Username { get; set; }
}

/// <summary>本地设置与凭据存储：刷新令牌用 DPAPI 加密，禁止明文落盘（见 PRD §7.8）。</summary>
public static class SecretStore
{
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TjViewer");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static ClientSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(FilePath)) ?? new ClientSettings();
        }
        catch { }
        return new ClientSettings();
    }

    public static void Save(ClientSettings s)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string Protect(string plain)
        => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }
}
