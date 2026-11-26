using System.Threading;
using System.Windows;
using TjViewer.Client.Services;
using TjViewer.Client.Views;
using TjViewer.Core;
using TjViewer.Core.Video;
using TjViewer.Shared.Logging;

namespace TjViewer.Client;

public partial class App : Application
{
    private AppSession? _session;
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 日志最先初始化：后续任何启动失败都能在本地日志中定位
        FileLog.Initialize("client");
        FileLog.Info($"客户端启动：版本={typeof(App).Assembly.GetName().Version} " +
                     $"参数=[{string.Join(' ', e.Args)}] 日志目录={FileLog.ActiveDirectory}");
        HookGlobalExceptionLogging();
        FFmpegLocator.Diagnostics += line => FileLog.Debug("ffmpeg: " + line);

        base.OnStartup(e);
        // 单实例：避免后台服务与注册表自启重复拉起，确保无残留进程
        _singleInstanceMutex = new Mutex(true, @"Local\TjViewer.Client.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            FileLog.Warn("检测到已有客户端实例，本次启动退出");
            Shutdown();
            return;
        }
        RuntimeTuning.Apply(); // 1ms 定时器 + 高优先级，降低采集/编码/渲染排队时延
        ServiceIntegration.ClearUserExit(); // 手动启动时恢复后台服务托管
        ThemeManager.Initialize();
        _session = new AppSession();
        ShowLogin();
    }

    /// <summary>登录 → 主界面；退出登录后回到登录页（清除本地账密）。</summary>
    private void ShowLogin()
    {
        FileLog.Info("显示登录窗口");
        var login = new LoginWindow(_session!);
        if (login.ShowDialog() != true)
        {
            // 用户在登录页关闭窗口 = 主动退出：写退出标记，
            // 否则后台服务会在 3 秒后把客户端重新拉起，表现为“窗口关不掉”。
            FileLog.Info("用户在登录页关闭窗口，写入退出标记并退出");
            ServiceIntegration.MarkUserExit();
            Shutdown();
            return;
        }
        FileLog.Info($"登录成功：用户={_session!.Username} 设备={_session.DeviceId}");
        var main = new MainWindow(_session);
        MainWindow = main;
        main.LogoutRequested += () =>
        {
            FileLog.Info("退出登录，返回登录页");
            Dispatcher.BeginInvoke(new Action(ShowLogin));
        };
        main.Show();
    }

    private void HookGlobalExceptionLogging()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            FileLog.Error("界面线程未处理异常", args.Exception);
            // 不设置 Handled：保持既有崩溃行为，避免掩盖逻辑错误
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) FileLog.Error("AppDomain 未处理异常", ex);
            else FileLog.Error("AppDomain 未处理异常：" + args.ExceptionObject);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            FileLog.Error("未观察的后台任务异常", args.Exception);
    }
}
