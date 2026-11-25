using System.Windows;

namespace TjViewer.Client.Views;

/// <summary>陌生设备连接：输入 9 位连接码与 8 位验证码，选择观看或远程控制。</summary>
public partial class CodeConnectWindow : Window
{
    public string Code => CodeBox.Text.Trim();
    public string Password => PasswordBox.Text.Trim();
    public string Mode { get; private set; } = "control";
    public bool RememberCode => RememberCheck.IsChecked == true;

    public CodeConnectWindow(string lastCode)
    {
        InitializeComponent();
        CodeBox.Text = lastCode;
        RememberCheck.IsChecked = !string.IsNullOrEmpty(lastCode);
        Loaded += (_, _) => CodeBox.Focus();
    }

    private void OnWatch(object sender, RoutedEventArgs e) => Submit("watch");
    private void OnControl(object sender, RoutedEventArgs e) => Submit("control");

    private void Submit(string mode)
    {
        var code = Code;
        var pwd = Password;
        if (code.Length != 9 || !code.All(char.IsDigit))
        {
            Hint.Text = "连接码必须是 9 位数字。";
            return;
        }
        if (pwd.Length != 8)
        {
            Hint.Text = "验证码必须是 8 位字母或数字。";
            return;
        }
        Mode = mode;
        DialogResult = true;
    }
}
