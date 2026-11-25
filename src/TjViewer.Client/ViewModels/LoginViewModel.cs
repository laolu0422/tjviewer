using CommunityToolkit.Mvvm.ComponentModel;
using TjViewer.Client.Services;
using TjViewer.Shared;

namespace TjViewer.Client.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly AppSession _session;
    private ApiClient _api;

    [ObservableProperty] private string serverUrl;
    [ObservableProperty] private string account = "";
    [ObservableProperty] private string regUsername = "";
    [ObservableProperty] private string regEmail = "";
    [ObservableProperty] private string resetEmail = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool rememberCredentials;
    [ObservableProperty] private bool autoLogin;

    public event Action? LoggedIn;

    public LoginViewModel(AppSession session)
    {
        _session = session;
        serverUrl = session.ServerUrl;
        account = session.Settings.SavedAccount ?? "";
        rememberCredentials = session.Settings.RememberCredentials;
        autoLogin = session.Settings.AutoLogin;
        _api = new ApiClient(session);
    }

    partial void OnAutoLoginChanged(bool value)
    {
        if (value) RememberCredentials = true; // 自动登录必然要求记住账密
    }

    /// <summary>自动登录：仅在已保存密码时尝试。</summary>
    public async Task TryAutoLoginAsync()
    {
        if (!AutoLogin) return;
        var pwd = _session.SavedPassword;
        if (string.IsNullOrEmpty(pwd) || string.IsNullOrWhiteSpace(Account)) return;
        await LoginAsync(pwd);
    }

    private void ApplyServer()
    {
        if (!string.IsNullOrWhiteSpace(ServerUrl) && ServerUrl != _session.ServerUrl)
        {
            _session.ServerUrl = ServerUrl.Trim();
            _api = new ApiClient(_session);
        }
    }

    public async Task LoginAsync(string password)
    {
        ApplyServer();
        Busy = true; Status = "登录中…";
        try
        {
            var auth = await _api.Login(new LoginRequest(Account.Trim(), password));
            _session.ApplyAuth(auth);
            _session.SaveCredentials(Account.Trim(), password, RememberCredentials, AutoLogin);
            Status = "登录成功";
            LoggedIn?.Invoke();
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { Busy = false; }
    }

    public async Task RegisterAsync(string password)
    {
        ApplyServer();
        Busy = true; Status = "注册中…";
        try
        {
            var auth = await _api.Register(new RegisterRequest(RegUsername.Trim(), RegEmail.Trim(), password));
            _session.ApplyAuth(auth);
            Account = RegUsername.Trim();
            _session.SaveCredentials(Account, password, RememberCredentials, AutoLogin);
            Status = "注册成功";
            LoggedIn?.Invoke();
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { Busy = false; }
    }

    public async Task ResetRequestAsync()
    {
        ApplyServer();
        Busy = true; Status = "发送中…";
        try
        {
            var msg = await _api.ResetRequest(ResetEmail.Trim());
            Status = msg.Message ?? "若该邮箱已注册，将收到重置邮件";
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { Busy = false; }
    }
}
