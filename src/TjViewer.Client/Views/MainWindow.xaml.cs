using System.Windows;
using TjViewer.Client.Services;
using TjViewer.Client.ViewModels;
using TjViewer.Shared;

namespace TjViewer.Client.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppSession _s;

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
        };
        Closed += async (_, _) => await _vm.ShutdownAsync();
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

    private void OnOpenControl(ConnectReadyPayload p)
    {
        var win = new ControlWindow(_s, p, _vm);
        win.Show();
    }
}
