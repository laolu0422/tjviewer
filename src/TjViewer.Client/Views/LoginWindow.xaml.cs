using System.Windows;
using TjViewer.Client.Services;
using TjViewer.Client.ViewModels;

namespace TjViewer.Client.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm;

    public LoginWindow(AppSession session)
    {
        InitializeComponent();
        _vm = new LoginViewModel(session);
        DataContext = _vm;
        _vm.LoggedIn += () => { DialogResult = true; Close(); };
    }

    private async void OnLogin(object sender, RoutedEventArgs e) => await _vm.LoginAsync(LoginPassword.Password);
    private async void OnRegister(object sender, RoutedEventArgs e) => await _vm.RegisterAsync(RegPassword.Password);
    private async void OnReset(object sender, RoutedEventArgs e) => await _vm.ResetRequestAsync();
}
