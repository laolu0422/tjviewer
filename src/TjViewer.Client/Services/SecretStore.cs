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
    /// <summary>记住的登录账号（用户名/邮箱）。</summary>
    public string? SavedAccount { get; set; }
    /// <summary>DPAPI 加密后的登录密码，仅“记住账密”时保存。</summary>
    public string? ProtectedPassword { get; set; }
    public bool RememberCredentials { get; set; }
    public bool AutoLogin { get; set; }
    /// <summary>上次使用的陌生设备连接码（不保存验证码）。</summary>
    public string? LastCode { get; set; }
    /// <summary>主窗口关闭行为：ask 每次弹窗询问 / exit 完全关闭 / minimize 最小化到托盘。</summary>
    public string CloseBehavior { get; set; } = "ask";
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
