using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TjViewer.Client.Services;
using TjViewer.Core.Input;
using TjViewer.Shared;

namespace TjViewer.Client.Views;

/// <summary>远程控制窗口（见 PRD FR-05/06）：渲染被控端画面，回传键鼠输入。</summary>
public partial class ControlWindow : Window
{
    private readonly RemoteSession _session;
    private WriteableBitmap? _bmp;
    private bool _updatingUi;
    private Point _lastMove = new(-1, -1);

    public ControlWindow(RemoteSession session)
    {
        InitializeComponent();
        _session = session;
        Title = "TjViewer - 远程控制";
        _session.FrameDecoded += OnFrame;
        _session.Status += s => Dispatcher.BeginInvoke(() => StatusText.Text = s);
        _session.PathChanged += p => Dispatcher.BeginInvoke(() => PathText.Text = p == ConnectionPath.P2P ? "P2P 直连" : "服务器中转");
        _session.FileCompleted += (n, path) => Dispatcher.BeginInvoke(() => MessageBox.Show(this, $"已接收：{n}\n{path}", "文件传输"));
        _session.FileFailed += (n, r) => Dispatcher.BeginInvoke(() => MessageBox.Show(this, $"接收失败：{n}（{r}）", "文件传输"));
        _session.FileProgress += (n, p) => Dispatcher.BeginInvoke(() => StatusText.Text = $"接收 {n}：{p:P0}");
        Closed += async (_, _) =>
        {
            _session.FrameDecoded -= OnFrame;
            await _session.DisposeAsync();
        };
        VideoImage.Focusable = true;
        _updatingUi = true;
        QualityBox.SelectedIndex = 1;
        FpsBox.SelectedIndex = 1;
        QualityBox.IsEnabled = false; // 初始为自适应模式
        FpsBox.IsEnabled = false;
        _updatingUi = false;
        Loaded += (_, _) => VideoImage.Focus();
    }

    private void OnQualityChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingUi || _session is null) return;
        if (AdaptiveBtn.IsChecked != true) SendManualQuality();
    }

    private void OnFpsChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingUi || _session is null) return;
        if (AdaptiveBtn.IsChecked != true) SendManualQuality();
    }

    /// <summary>手动模式：画质档位与帧率独立下发。</summary>
    private void SendManualQuality()
    {
        int tier = Math.Max(0, QualityBox.SelectedIndex);
        int fps = 0;
        if (FpsBox.SelectedItem is System.Windows.Controls.ComboBoxItem ci && int.TryParse(ci.Content?.ToString(), out var f)) fps = f;
        _session.SendQuality(new QualityRequest("manual", tier, fps));
    }

    private void OnAdaptiveChecked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || _session is null) return;
        _updatingUi = true;
        LockBtn.IsChecked = false;
        QualityBox.IsEnabled = false;
        FpsBox.IsEnabled = false;
        _updatingUi = false;
        _session.SendQuality(new QualityRequest("adaptive"));
    }

    private void OnAdaptiveUnchecked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || _session is null) return;
        _updatingUi = true;
        bool locked = LockBtn.IsChecked == true;
        QualityBox.IsEnabled = !locked;
        FpsBox.IsEnabled = !locked;
        _updatingUi = false;
        if (!locked) SendManualQuality();
    }

    /// <summary>强制锁定：关闭自适应并固定当前画质与帧率。</summary>
    private void OnLockChecked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || _session is null) return;
        _updatingUi = true;
        AdaptiveBtn.IsChecked = false;
        QualityBox.IsEnabled = false;
        FpsBox.IsEnabled = false;
        _updatingUi = false;
        SendManualQuality();
    }

    private void OnLockUnchecked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || _session is null) return;
        _updatingUi = true;
        QualityBox.IsEnabled = true;
        FpsBox.IsEnabled = true;
        _updatingUi = false;
    }

    private void OnMicOn(object sender, RoutedEventArgs e) => _session.StartMic();
    private void OnMicOff(object sender, RoutedEventArgs e) => _session.StopMic();

    private async void OnSendFile(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择要发送的文件" };
        if (dlg.ShowDialog(this) == true)
        {
            StatusText.Text = $"发送 {System.IO.Path.GetFileName(dlg.FileName)}…";
            try { await _session.SendFileAsync(dlg.FileName); StatusText.Text = "发送完成"; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "发送失败"); }
        }
    }

    private void OnFrame(byte[] bgra)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var info = _session.VideoInfo;
            if (info is null) return;
            int w = info.Width, h = info.Height;
            if (bgra.Length < w * h * 4) return;
            if (_bmp is null || _bmp.PixelWidth != w || _bmp.PixelHeight != h)
                _bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            _bmp.WritePixels(new Int32Rect(0, 0, w, h), bgra, w * 4, 0);
            VideoImage.Source = _bmp;
        });
    }

    private Point MapToHost(Point p)
    {
        var info = _session.VideoInfo;
        if (info is null) return new Point(0, 0);
        double iw = VideoImage.ActualWidth, ih = VideoImage.ActualHeight;
        if (iw <= 0 || ih <= 0) return new Point(0, 0);
        double scale = Math.Min(iw / info.Width, ih / info.Height);
        double rw = info.Width * scale, rh = info.Height * scale;
        double ox = (iw - rw) / 2, oy = (ih - rh) / 2;
        double x = (p.X - ox) / scale, y = (p.Y - oy) / scale;
        return new Point(Math.Clamp(x, 0, info.Width - 1), Math.Clamp(y, 0, info.Height - 1));
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = MapToHost(e.GetPosition(VideoImage));
        var ip = new Point((int)p.X, (int)p.Y);
        if (ip == _lastMove) return; // 合并重复坐标，减少输入消息量
        _lastMove = ip;
        _session.SendInput(new InputEvent("move", (int)p.X, (int)p.Y));
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = MapToHost(e.GetPosition(VideoImage));
        _session.SendInput(new InputEvent("move", (int)p.X, (int)p.Y));
        _session.SendInput(new InputEvent("button", Button: Btn(e.ChangedButton), Down: true));
        VideoImage.CaptureMouse();
        VideoImage.Focus();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _session.SendInput(new InputEvent("button", Button: Btn(e.ChangedButton), Down: false));
        if (VideoImage.IsMouseCaptured) VideoImage.ReleaseMouseCapture();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        => _session.SendInput(new InputEvent("wheel", Delta: e.Delta));

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        ushort vk = (ushort)KeyInterop.VirtualKeyFromKey(e.Key);
        ushort scan = InputInjector.VirtualKeyToScanCode(vk);
        if (scan == 0) return;
        _session.SendInput(new InputEvent("key", Scan: scan, Down: true, Extended: IsExtended(e.Key)));
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        ushort vk = (ushort)KeyInterop.VirtualKeyFromKey(e.Key);
        ushort scan = InputInjector.VirtualKeyToScanCode(vk);
        if (scan == 0) return;
        _session.SendInput(new InputEvent("key", Scan: scan, Down: false, Extended: IsExtended(e.Key)));
        e.Handled = true;
    }

    private static bool IsExtended(Key k) => k is Key.Up or Key.Down or Key.Left or Key.Right
        or Key.Insert or Key.Delete or Key.Home or Key.End or Key.PageUp or Key.PageDown
        or Key.RightCtrl or Key.RightAlt or Key.PrintScreen or Key.NumLock or Key.Divide;

    private static int Btn(System.Windows.Input.MouseButton b) => b switch
    {
        System.Windows.Input.MouseButton.Left => 0,
        System.Windows.Input.MouseButton.Right => 1,
        System.Windows.Input.MouseButton.Middle => 2,
        _ => 0,
    };

    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        await _session.DisposeAsync();
        Close();
    }
}
