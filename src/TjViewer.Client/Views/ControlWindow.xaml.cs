using System.Windows;
using TjViewer.Client.Services;
using TjViewer.Client.ViewModels;
using TjViewer.Shared;

namespace TjViewer.Client.Views;

/// <summary>远程控制窗口（M0 占位；画面/输入/音频在 M2–M5 接入）。</summary>
public partial class ControlWindow : Window
{
    private readonly ConnectReadyPayload _p;
    private readonly MainViewModel _vm;

    public ControlWindow(AppSession s, ConnectReadyPayload p, MainViewModel vm)
    {
        InitializeComponent();
        _p = p;
        _vm = vm;
        Title = $"TjViewer - {p.HostDeviceName}";
        InfoText.Text = $"已连接到「{p.HostDeviceName}」\n会话：{p.SessionId}\n能力：{p.Capabilities}\n\n桌面画面 / 键鼠 / 音频将在后续里程碑接入。";
    }

    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        await _vm.EndSession(_p.SessionId);
        Close();
    }
}
