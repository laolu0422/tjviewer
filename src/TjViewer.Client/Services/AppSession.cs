namespace TjViewer.Client.Services;

/// <summary>当前登录会话状态（内存）+ 本地设置。</summary>
public class AppSession
{
    public ClientSettings Settings { get; } = SecretStore.Load();

    public string ServerUrl
    {
        get => Settings.ServerUrl.TrimEnd('/');
        set { Settings.ServerUrl = value; SecretStore.Save(Settings); }
    }

    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? DeviceId { get; set; }

    public string DeviceKey => DeviceIdentity.GetOrCreateKey(Settings);

    public string WsUrl => ServerUrl.Replace("https://", "wss://").Replace("http://", "ws://") + Shared.ApiRoutes.SignalHub;

    public string RelayUrl => ServerUrl.Replace("https://", "wss://").Replace("http://", "ws://") + Shared.ApiRoutes.RelayHub;

    public void ApplyAuth(Shared.AuthResponse auth)
    {
        AccessToken = auth.Token;
        RefreshToken = auth.RefreshToken;
        Username = auth.Username;
        Email = auth.Email;
        Settings.Username = auth.Username;
        Settings.ProtectedRefreshToken = SecretStore.Protect(auth.RefreshToken);
        SecretStore.Save(Settings);
    }

    public void LoadSavedRefresh()
    {
        RefreshToken = SecretStore.Unprotect(Settings.ProtectedRefreshToken);
        Username = Settings.Username;
    }

    public void Clear()
    {
        AccessToken = null;
        RefreshToken = null;
        Settings.ProtectedRefreshToken = null;
        SecretStore.Save(Settings);
    }
}
