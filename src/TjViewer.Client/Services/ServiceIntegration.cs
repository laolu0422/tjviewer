using System.Diagnostics;
using System.IO;

namespace TjViewer.Client.Services;

/// <summary>
/// 与后台服务（TjViewerService）的轻量协作：
/// 用户主动「退出」时写标记，服务在本登录会话内不再拉起；手动启动客户端则清除标记。
/// 目录由安装脚本创建并授予 Users 修改权限；无权限时静默忽略，不影响客户端运行。
/// </summary>
internal static class ServiceIntegration
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TjViewer");

    private static string FlagPath
    {
        get
        {
            int session;
            try { session = Process.GetCurrentProcess().SessionId; }
            catch { session = 0; }
            return Path.Combine(DataDir, $"user-exit-{session}.flag");
        }
    }

    /// <summary>正常启动：清除上一次的用户退出标记，恢复服务托管。</summary>
    public static void ClearUserExit()
    {
        try { File.Delete(FlagPath); } catch { }
    }

    /// <summary>用户主动退出：阻止服务立即重新拉起。</summary>
    public static void MarkUserExit()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FlagPath, DateTime.Now.ToString("O"));
        }
        catch { }
    }
}
