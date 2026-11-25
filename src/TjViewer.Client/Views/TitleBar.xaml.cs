using System.Windows;
using System.Windows.Controls;

namespace TjViewer.Client.Views;

/// <summary>统一的自定义标题栏：拖动/双击由 WindowChrome 处理，按钮区单独响应。</summary>
public partial class TitleBar : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(TitleBar), new PropertyMetadata("TjViewer"));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    private Window? _window;

    public TitleBar()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private void Attach()
    {
        _window = Window.GetWindow(this);
        if (_window is null) return;
        _window.StateChanged += OnStateChanged;
        if (_window.ResizeMode == ResizeMode.NoResize) MaxButton.Visibility = Visibility.Collapsed;
        UpdateMaxGlyph();
    }

    private void Detach()
    {
        if (_window is not null) _window.StateChanged -= OnStateChanged;
        _window = null;
    }

    private void OnStateChanged(object? sender, EventArgs e) => UpdateMaxGlyph();

    private void UpdateMaxGlyph()
    {
        if (_window is null) return;
        var maximized = _window.WindowState == WindowState.Maximized;
        MaxButton.Content = maximized
            ? new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse("M3,3 H10 V10 H3 Z M5,1 H12 V8"),
                Stroke = TryFindResource("TextBrush") as System.Windows.Media.Brush,
                StrokeThickness = 1.2,
            }
            : new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse("M2.5,2.5 H11.5 V11.5 H2.5 Z"),
                Stroke = TryFindResource("TextBrush") as System.Windows.Media.Brush,
                StrokeThickness = 1.2,
            };
        // WindowChrome 最大化时内容会溢出屏幕边缘，向内收一圈
        if (_window.Content is FrameworkElement root)
            root.Margin = maximized ? new Thickness(8) : new Thickness(0);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => _window!.WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e)
        => _window!.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => _window!.Close();
}
