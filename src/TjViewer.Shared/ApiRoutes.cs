namespace TjViewer.Shared;

/// <summary>服务器与客户端共用的路由常量，避免硬编码漂移。</summary>
public static class ApiRoutes
{
    public const string Register = "/api/auth/register";
    public const string Login = "/api/auth/login";
    public const string Refresh = "/api/auth/refresh";
    public const string ResetRequest = "/api/auth/reset-request";
    public const string ResetConfirm = "/api/auth/reset-confirm";
    public const string ChangePassword = "/api/auth/change-password";
    public const string Devices = "/api/devices";
    public const string SignalHub = "/ws/signal";
}
