using System.Windows;
using TjViewer.Client.Services;
using TjViewer.Client.ViewModels;
using TjViewer.Shared;
using TjViewer.Shared.Logging;

namespace TjViewer.Client.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppSession _s;
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _reallyClose;
    private bool _loggingOut;

    public event Action? LogoutRequested;

    public MainWindow(AppSession s)
    {
        InitializeComponent();
        _s = s;
        _vm = new MainViewModel(s);
        DataContext = _vm;
        _vm.IncomingConnect += OnIncoming;
        _vm.OpenControl += OnOpenControl;
        _vm.Notify += msg => MessageBox.Show(this, msg, "TjViewer");
        Loaded += async (_, _) =>
        {
            try { await _vm.InitializeAsync(); }
            catch (Exception ex)
            {
                _vm.Status = "初始化失败：" + ex.Message;
                MessageBox.Show(this, ex.Message, "初始化失败");
            }
            if (Environment.GetCommandLineArgs().Contains("--minimized")) Hide();
        };
        InitTray();
        UpdateAutoStartButton();
        Closing += (_, e) =>
        {
            if (_reallyClose) return; // 已确认完全关闭 / 退出登录，直接放行

            // 已记住的关闭行为：直接生效，不再弹窗
            var behavior = _s.Settings.CloseBehavior;
            if (behavior == "minimize") { e.Cancel = true; Hide(); FileLog.Info("关闭主窗口：按已记住设置最小化到托盘"); return; }
            if (behavior == "exit") { FileLog.Info("关闭主窗口：按已记住设置完全退出"); BeginFullExit(); return; }

            // 弹窗让用户选择关闭行为
            var dlg = new CloseOptionsWindow { Owner = this };
            var ok = dlg.ShowDialog() == true;
            FileLog.Info($"关闭选项弹窗：确认={ok} 选择={(ok ? dlg.Choice.ToString() : "取消")} 记住={ok && dlg.Remember}");
            if (ok && dlg.Choice == CloseChoice.Exit)
            {
                if (dlg.Remember) SaveCloseBehavior("exit");
                BeginFullExit(); // 不取消，窗口继续关闭
            }
            else if (ok && dlg.Choice == CloseChoice.Minimize)
            {
                if (dlg.Remember) SaveCloseBehavior("minimize");
                e.Cancel = true;
                Hide();
            }
            else
            {
                e.Cancel = true; // 取消弹窗：保持窗口
            }
        };
        Closed += async (_, _) =>
        {
            _tray?.Dispose();
            await _vm.ShutdownAsync();
            TjViewer.Core.ChildProcessTracker.KillAll(); // 终止残留子进程（ffmpeg 等），确保无后台残留
            if (_loggingOut) LogoutRequested?.Invoke();
            else Application.Current.Shutdown();
        };
    }

    /// <summary>完全关闭：标记用户退出（后台服务本会话不再拉起），随后窗口关闭触发清理。</summary>
    private void BeginFullExit()
    {
        FileLog.Info("开始完全关闭：标记用户退出并终止关联后台进程");
        _reallyClose = true;
        ServiceIntegration.MarkUserExit();
    }

    private void SaveCloseBehavior(string behavior)
    {
        _s.Settings.CloseBehavior = behavior;
        SecretStore.Save(_s.Settings);
    }

    /// <summary>托盘「关闭行为设置」：重新选择关闭行为；取消勾选「记住」则恢复每次询问。</summary>
    private void ConfigureCloseBehavior()
    {
        var remembered = _s.Settings.CloseBehavior is "exit" or "minimize";
        // 从托盘打开时主窗口可能隐藏，不设 Owner、居中屏幕，确保对话框前置可见
        var dlg = new CloseOptionsWindow(remembered) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        if (dlg.ShowDialog() != true) return;
        var behavior = dlg.Choice switch
        {
            CloseChoice.Exit => "exit",
            CloseChoice.Minimize => "minimize",
            _ => "ask",
        };
        SaveCloseBehavior(dlg.Remember ? behavior : "ask");
        FileLog.Info($"托盘设置关闭行为：{behavior}（记住={dlg.Remember}）");
    }

    private void InitTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "TjViewer 远程控制",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => ShowFromTray());
        menu.Items.Add("关闭行为设置…", null, (_, _) => ConfigureCloseBehavior());
        menu.Items.Add("退出", null, (_, _) =>
        {
            BeginFullExit(); // 完全关闭：标记用户退出，后台服务本会话不再拉起
            Close();
        });
        _tray.ContextMenuStrip = menu;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void UpdateAutoStartButton()
        => AutoStartBtn.Content = AutoStart.IsEnabled() ? "开机自启：开" : "开机自启：关";

    private void OnAutoStart(object sender, RoutedEventArgs e)
    {
        // 自启动开关仅持久化注册表配置：不立即重启、不影响当前会话，下次手动启动/重新登录后生效
        if (AutoStart.IsEnabled())
        {
            AutoStart.Disable();
            FileLog.Info("开机自启已关闭（下次启动生效）");
        }
        else
        {
            var r = MessageBox.Show(this,
                "开启后，TjViewer 将在下次启动（重启软件或重新登录 Windows）时自动运行并上线。\n\n" +
                "该设置仅保存配置，不会立即重启软件，当前会话可继续正常使用。是否开启？",
                "开机自启", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            AutoStart.Enable();
            FileLog.Info("开机自启已开启（下次启动生效，当前会话不受影响）");
        }
        UpdateAutoStartButton();
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is DeviceDto d) await _vm.ConnectAsync(d);
        else MessageBox.Show(this, "请先选择一个设备", "TjViewer");
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        try { await _vm.RefreshDevices(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "刷新失败"); }
    }

    private async void OnIncoming(ConnectIncomingPayload p)
    {
        // 同账号设备直接建立连接；异账号（连接码）才弹确认
        if (p.SameAccount)
        {
            await _vm.AcceptIncoming(p.SessionId, p.Mode, p.Capabilities);
            return;
        }
        var action = p.Mode == "watch" ? "观看" : "远程控制";
        var r = MessageBox.Show(this,
            $"设备「{p.ControllerDeviceName}」请求{action}本机。\n请求能力：{p.Capabilities}\n\n是否接受？",
            "远程连接请求", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) await _vm.AcceptIncoming(p.SessionId, p.Mode, p.Capabilities);
        else await _vm.RejectIncoming(p.SessionId);
    }

    private async void OnFixedCode(object sender, RoutedEventArgs e)
        => await _vm.SetFixedCodeAsync(FixedCodeCheck.IsChecked == true);

    private async void OnCodeConnect(object sender, RoutedEventArgs e)
    {
        var dlg = new CodeConnectWindow(_s.Settings.LastCode ?? "") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        await _vm.ConnectByCodeAsync(dlg.Code, dlg.Password, dlg.Mode, dlg.RememberCode);
    }

    private async void OnLogout(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this, "退出登录将清除本地保存的账号与密码，是否继续？", "退出登录",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        _s.ClearCredentials();
        _s.Clear();
        _reallyClose = true;
        _loggingOut = true;
        Close();
    }

    private void OnOpenControl(RemoteSession session)
    {
        var win = new ControlWindow(session);
        win.Show();
    }
}
