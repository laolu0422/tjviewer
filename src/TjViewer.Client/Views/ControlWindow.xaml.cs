using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TjViewer.Client.Services;
using TjViewer.Core.Input;
using TjViewer.Shared;

namespace TjViewer.Client.Views;

/// <summary>远程控制窗口：渲染被控端画面，回传键鼠输入；观看模式只读。</summary>
public partial class ControlWindow : Window
{
    private readonly RemoteSession _session;
    private readonly DispatcherTimer _qualityTimer;
    private KeyboardHook? _hook;
    private WriteableBitmap? _bmp;
    private bool _updatingUi;
    private Point _lastMove = new(-1, -1);
    private int _renderPending;
    private byte[]? _latestFrame;

    public ControlWindow(RemoteSession session)
    {
        InitializeComponent();
        _session = session;
        Title = session.CanControl ? "TjViewer - 远程控制" : "TjViewer - 远程观看";

        _session.FrameDecoded += OnFrame;
        _session.Status += s => Dispatcher.BeginInvoke(() => StatusText.Text = s);
        _session.PathChanged += p => Dispatcher.BeginInvoke(() => PathText.Text = p == ConnectionPath.P2P ? "P2P 直连" : "服务器中转");
        _session.Ended += () => Dispatcher.BeginInvoke(Close);
        _session.FileCompleted += (n, path) => Dispatcher.BeginInvoke(() => MessageBox.Show(this, $"已接收：{n}\n{path}", "文件传输"));
        _session.FileFailed += (n, r) => Dispatcher.BeginInvoke(() => MessageBox.Show(this, $"接收失败：{n}（{r}）", "文件传输"));
        _session.FileProgress += (n, p) => Dispatcher.BeginInvoke(() => StatusText.Text = $"接收 {n}：{p:P0}");

        _qualityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _qualityTimer.Tick += (_, _) => { _qualityTimer.Stop(); SendManualQuality(); };

        _updatingUi = true;
        FpsSlider.Value = 1;      // 30 FPS
        BitrateSlider.Value = 2;  // 6 Mbps
        _updatingUi = false;
        UpdateQualityLabels();

        if (!session.CanControl)
        {
            ModeBadge.Visibility = Visibility.Visible;
            MicBtn.IsEnabled = false;
            MicBtn.Visibility = Visibility.Collapsed;
            FileBtn.Visibility = Visibility.Collapsed;
            FooterText.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) => VideoImage.Focus();
        Closed += async (_, _) =>
        {
            _hook?.Dispose();
            _hook = null;
            _session.FrameDecoded -= OnFrame;
            await _session.DisposeAsync();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        _hook = new KeyboardHook(hwnd);
        _hook.Key += OnGlobalKey;
        Activated += (_, _) => _hook?.Install();
        Deactivated += (_, _) => _hook?.Uninstall();
        if (IsActive) _hook.Install();
    }

    /// <summary>远程窗口前台时接管键盘（含 Alt+Tab、Win 组合），转发到被控端。</summary>
    private void OnGlobalKey(ushort scan, bool down, bool extended)
    {
        if (!_session.CanControl) return;
        _session.SendInput(new InputEvent("key", Scan: scan, Down: down, Extended: extended));
    }

    private void UpdateQualityLabels()
    {
        int fi = (int)Math.Round(FpsSlider.Value);
        int bi = (int)Math.Round(BitrateSlider.Value);
        fi = Math.Clamp(fi, 0, QualityProfiles.FpsOptions.Length - 1);
        bi = Math.Clamp(bi, 0, QualityProfiles.BitrateOptions.Length - 1);
        FpsText.Text = $"{QualityProfiles.FpsOptions[fi]} FPS";
        BitrateText.Text = $"{QualityProfiles.BitrateOptions[bi] / 1024} Mbps";
    }

    private void OnQualitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi || _session is null) return;
        UpdateQualityLabels();
        if (AdaptiveBtn.IsChecked == true) return;
        _qualityTimer.Stop();
        _qualityTimer.Start();
    }

    private void SendManualQuality()
    {
        int fi = Math.Clamp((int)Math.Round(FpsSlider.Value), 0, QualityProfiles.FpsOptions.Length - 1);
        int bi = Math.Clamp((int)Math.Round(BitrateSlider.Value), 0, QualityProfiles.BitrateOptions.Length - 1);
        _session.SendQuality(new QualityRequest("manual", 0, QualityProfiles.FpsOptions[fi], QualityProfiles.BitrateOptions[bi]));
    }

    private void OnAdaptiveChecked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || _session is null) return;
        _updatingUi = true;
        LockBtn.IsChecked = false;
        FpsSlider.IsEnabled = false;
        BitrateSlider.IsEnabled = false;
        _updatingUi = false;
        _session.SendQuality(new QualityRequest("adaptive"));
    }

    private void OnAdaptiveUnchecked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || _session is null) return;
        _updatingUi = true;
        bool locked = LockBtn.IsChecked == true;
        FpsSlider.IsEnabled = !locked;
        BitrateSlider.IsEnabled = !locked;
        _updatingUi = false;
        if (!locked) SendManualQuality();
    }

    private void OnLockChecked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || _session is null) return;
        _updatingUi = true;
        AdaptiveBtn.IsChecked = false;
        FpsSlider.IsEnabled = false;
        BitrateSlider.IsEnabled = false;
        _updatingUi = false;
        SendManualQuality();
    }

    private void OnLockUnchecked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || _session is null) return;
        _updatingUi = true;
        bool adaptive = AdaptiveBtn.IsChecked == true;
        FpsSlider.IsEnabled = !adaptive;
        BitrateSlider.IsEnabled = !adaptive;
        _updatingUi = false;
    }

    private void OnMicOn(object sender, RoutedEventArgs e) => _session.StartMic();
    private void OnMicOff(object sender, RoutedEventArgs e) => _session.StopMic();

    private async void OnSendFile(object sender, RoutedEventArgs e)
    {
        if (!_session.CanControl) return;
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
        _latestFrame = bgra;
        // 渲染忙时丢帧，避免 120FPS 下 UI 线程积压
        if (Interlocked.Exchange(ref _renderPending, 1) == 1) return;
        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _renderPending, 0);
            var frame = _latestFrame;
            if (frame is not null) Render(frame);
        });
    }

    private void Render(byte[] bgra)
    {
        var info = _session.VideoInfo;
        if (info is null) return;
        int w = info.Width, h = info.Height;
        if (bgra.Length < w * h * 4) return;
        if (_bmp is null || _bmp.PixelWidth != w || _bmp.PixelHeight != h)
            _bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        _bmp.WritePixels(new Int32Rect(0, 0, w, h), bgra, w * 4, 0);
        VideoImage.Source = _bmp;
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
        if (!_session.CanControl) return;
        var p = MapToHost(e.GetPosition(VideoImage));
        var ip = new Point((int)p.X, (int)p.Y);
        if (ip == _lastMove) return; // 合并重复坐标，减少输入消息量
        _lastMove = ip;
        _session.SendInput(new InputEvent("move", (int)p.X, (int)p.Y));
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_session.CanControl) return;
        var p = MapToHost(e.GetPosition(VideoImage));
        _session.SendInput(new InputEvent("move", (int)p.X, (int)p.Y));
        _session.SendInput(new InputEvent("button", Button: Btn(e.ChangedButton), Down: true));
        VideoImage.CaptureMouse();
        VideoImage.Focus();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_session.CanControl) return;
        _session.SendInput(new InputEvent("button", Button: Btn(e.ChangedButton), Down: false));
        if (VideoImage.IsMouseCaptured) VideoImage.ReleaseMouseCapture();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_session.CanControl) return;
        _session.SendInput(new InputEvent("wheel", Delta: e.Delta));
    }

    private static int Btn(System.Windows.Input.MouseButton b) => b switch
    {
        System.Windows.Input.MouseButton.Left => 0,
        System.Windows.Input.MouseButton.Right => 1,
        System.Windows.Input.MouseButton.Middle => 2,
        _ => 0,
    };

    private void OnDisconnect(object sender, RoutedEventArgs e) => Close();
}
