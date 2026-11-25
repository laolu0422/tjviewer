using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TjViewer.Service;

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        // 以 Windows 服务方式运行；未由 SCM 启动时自动按控制台程序运行，便于排查
        builder.Services.AddWindowsService(o => o.ServiceName = "TjViewerService");
        builder.Services.AddHostedService<SessionLauncher>();
        builder.Services.AddHostedService<ControlPipeListener>();
        builder.Build().Run();
    }
}
