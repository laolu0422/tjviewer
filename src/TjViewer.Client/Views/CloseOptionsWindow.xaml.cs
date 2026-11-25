using System.Windows;

namespace TjViewer.Client.Views;

/// <summary>主窗口关闭行为选择。</summary>
public enum CloseChoice
{
    /// <summary>取消，保持窗口。</summary>
    Cancel,
    /// <summary>仅隐藏主窗口到托盘，后台继续运行。</summary>
    Minimize,
    /// <summary>完全关闭：退出并终止关联后台进程。</summary>
    Exit,
}

/// <summary>关闭主窗口时弹出：让用户选择「完全关闭」或「最小化到托盘」，可记住选择。</summary>
public partial class CloseOptionsWindow : Window
{
    public CloseChoice Choice { get; private set; } = CloseChoice.Cancel;
    public bool Remember => RememberCheck.IsChecked == true;

    public CloseOptionsWindow(bool rememberChecked = false)
    {
        InitializeComponent();
        RememberCheck.IsChecked = rememberChecked;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => Submit(CloseChoice.Minimize);
    private void OnExit(object sender, RoutedEventArgs e) => Submit(CloseChoice.Exit);

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Cancel;
        DialogResult = false;
    }

    private void Submit(CloseChoice choice)
    {
        Choice = choice;
        DialogResult = true;
    }
}
