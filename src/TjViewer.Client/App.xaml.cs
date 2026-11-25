using System.Windows;
using TjViewer.Client.Services;
using TjViewer.Client.Views;

namespace TjViewer.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.Initialize();
        var session = new AppSession();
        var login = new LoginWindow(session);
        var ok = login.ShowDialog();
        if (ok == true)
        {
            var main = new MainWindow(session);
            MainWindow = main;
            main.Show();
        }
        else
        {
            Shutdown();
        }
    }
}
