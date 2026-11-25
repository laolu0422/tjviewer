using System.Windows;
using TjViewer.Client.Services;
using TjViewer.Client.Views;

namespace TjViewer.Client;

public partial class App : Application
{
    private AppSession? _session;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.Initialize();
        _session = new AppSession();
        ShowLogin();
    }

    /// <summary>登录 → 主界面；退出登录后回到登录页（清除本地账密）。</summary>
    private void ShowLogin()
    {
        var login = new LoginWindow(_session!);
        if (login.ShowDialog() != true)
        {
            Shutdown();
            return;
        }
        var main = new MainWindow(_session!);
        MainWindow = main;
        main.LogoutRequested += () => Dispatcher.BeginInvoke(new Action(ShowLogin));
        main.Show();
    }
}
