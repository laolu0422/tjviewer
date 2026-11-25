using System.Windows;
using TjViewer.Client.Services;
using TjViewer.Client.ViewModels;
using TjViewer.Shared;

namespace TjViewer.Client.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppSession _s;
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _reallyClose;

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
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "初始化失败"); }
            if (Environment.GetCommandLineArgs().Contains("--minimized")) Hide();
        };
        InitTray();
        UpdateAutoStartButton();
        Closing += (_, e) =>
        {
            if (!_reallyClose) { e.Cancel = true; Hide(); } // 最小化到托盘而非退出
        };
        Closed += async (_, _) => { _tray?.Dispose(); await _vm.ShutdownAsync(); };
    }

    private void InitTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "TjViewer 远程控制",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => ShowFromTray());
        menu.Items.Add("退出", null, (_, _) => { _reallyClose = true; Close(); });
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
        if (AutoStart.IsEnabled())
        {
            AutoStart.Disable();
        }
        else
        {
            var r = MessageBox.Show(this, "开启开机自启后，TjViewer 将在你登录 Windows 时自动运行并上线。是否开启？",
                "开机自启", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            AutoStart.Enable();
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
        var r = MessageBox.Show(this,
            $"设备「{p.ControllerDeviceName}」请求连接。\n请求能力：{p.Capabilities}\n\n是否接受？",
            "远程连接请求", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) await _vm.AcceptIncoming(p.SessionId);
        else await _vm.RejectIncoming(p.SessionId);
    }

    private void OnOpenControl(RemoteSession session)
    {
        var win = new ControlWindow(session);
        win.Show();
    }
}
