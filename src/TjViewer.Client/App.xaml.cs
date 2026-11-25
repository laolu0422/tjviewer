using System.Windows;
using TjViewer.Client.Services;
using TjViewer.Client.Views;
using TjViewer.Core;

namespace TjViewer.Client;

public partial class App : Application
{
    private AppSession? _session;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RuntimeTuning.Apply(); // 1ms 定时器 + 高优先级，降低采集/编码/渲染排队时延
        ServiceIntegration.ClearUserExit(); // 手动启动时恢复后台服务托管
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
