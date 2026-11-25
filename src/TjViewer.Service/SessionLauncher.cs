using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TjViewer.Service;

/// <summary>
/// 后台服务：系统启动即以 LocalSystem 运行，不依赖任何用户登录。
/// 检测到活动控制台会话后，把客户端拉起到该会话（<c>--minimized</c>），
/// 客户端退出/崩溃后自动重新拉起；用户主动「退出」时凭标记跳过本次登录会话。
///
/// 能力边界：Windows 的登录前/锁屏桌面（Winlogon、安全桌面）不允许普通进程采集，
/// 因此「无用户登录时持续采集屏幕」不在本服务能力内，需要另行部署带 uiAccess 的
/// SecureAgent（须安装到 Program Files 并签名）。本服务保证：开机自启、
/// 无需登录即可运行、用户登录后立即接管、异常退出自动恢复。
/// </summary>
public sealed class SessionLauncher : BackgroundService
{
    private const uint NoSession = 0xFFFFFFFF;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TjViewer");
    private static readonly string LogDir = Path.Combine(DataDir, "logs");
    private static readonly object FileLock = new();

    private readonly ILogger<SessionLauncher> _log;
    private uint _lastSession = NoSession;
    private bool _missingLogged;
    private string _lastError = "";
    private long _lastErrorAt;

    public SessionLauncher(ILogger<SessionLauncher> log)
    {
        _log = log;
        Log("后台服务已启动（LocalSystem，开机自启，无需用户登录）");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { LogError("巡检异常：" + ex.Message); }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        Log("后台服务已停止");
    }

    private void Tick()
    {
        uint session = Native.WTSGetActiveConsoleSessionId();
        if (session == NoSession)
        {
            _lastSession = NoSession;
            return;
        }

        if (session != _lastSession)
        {
            _lastSession = session;
            // 新登录会话：清除上一次「用户主动退出」的标记，恢复托管
            try { File.Delete(ExitFlag(session)); } catch { }
            Log($"发现活动控制台会话 {session}");
        }

        if (File.Exists(ExitFlag(session))) return;
        if (IsClientRunning(session)) return;

        var exe = FindClientExe();
        if (exe is null)
        {
            if (!_missingLogged)
            {
                _missingLogged = true;
                Log("未找到 TjViewer.exe，跳过会话拉起（请先运行安装脚本）");
            }
            return;
        }

        Launch(exe, session);
    }

    private static string ExitFlag(uint session) => Path.Combine(DataDir, $"user-exit-{session}.flag");

    private static string? FindClientExe()
    {
        // 标准布局：<安装目录>\TjViewer.exe 与 <安装目录>\service\TjViewer.Service.exe
        var sibling = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "TjViewer.exe"));
        if (File.Exists(sibling)) return sibling;

        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TjViewer", "TjViewer.exe");
        return File.Exists(installed) ? installed : null;
    }

    private static bool IsClientRunning(uint session)
    {
        foreach (var p in Process.GetProcessesByName("TjViewer"))
        {
            try
            {
                if (p.SessionId == (int)session) return true;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return false;
    }

    /// <summary>以会话用户身份创建进程：用户令牌 → 主令牌 → 用户环境块 → winsta0\default 桌面。</summary>
    private void Launch(string exe, uint session)
    {
        if (!Native.WTSQueryUserToken(session, out var userToken))
        {
            LogError("WTSQueryUserToken 失败：" + new Win32Exception(Marshal.GetLastWin32Error()).Message, exe);
            return;
        }

        IntPtr primary = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            if (!Native.DuplicateTokenEx(userToken, Native.TOKEN_ALL_ACCESS, IntPtr.Zero,
                    Native.SECURITY_IMPERSONATION, Native.TOKEN_PRIMARY, out primary))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            Native.CreateEnvironmentBlock(out env, primary, false);

            var si = new Native.STARTUPINFO
            {
                cb = Marshal.SizeOf<Native.STARTUPINFO>(),
                lpDesktop = @"winsta0\default",
                dwFlags = Native.STARTF_USESHOWWINDOW,
                wShowWindow = Native.SW_SHOWMINNOACTIVE,
            };
            var cmd = $"\"{exe}\" --minimized";
            if (!Native.CreateProcessAsUser(primary, exe, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    Native.CREATE_UNICODE_ENVIRONMENT, env, Path.GetDirectoryName(exe), ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            try { Native.CloseHandle(pi.hThread); } catch { }
            // 高优先级调度：与安装器写入的 IFEO 双保险（ffmpeg 子进程继承该优先级）
            try
            {
                using var p = Process.GetProcessById((int)pi.dwProcessId);
                p.PriorityClass = ProcessPriorityClass.High;
            }
            catch { }
            try { Native.CloseHandle(pi.hProcess); } catch { }

            Log($"已在会话 {session} 拉起客户端（PID {pi.dwProcessId}，High 优先级）");
        }
        catch (Exception ex)
        {
            LogError("拉起客户端失败：" + ex.Message, exe);
        }
        finally
        {
            if (env != IntPtr.Zero) Native.DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) Native.CloseHandle(primary);
            Native.CloseHandle(userToken);
        }
    }

    private void Log(string message)
    {
        _log.LogInformation(message);
        Write($"INFO  {message}");
    }

    /// <summary>同类错误 30 秒内只写一次，避免无登录会话下刷爆日志。</summary>
    private void LogError(string message, string? detail = null)
    {
        long now = Environment.TickCount64;
        if (message == _lastError && now - _lastErrorAt < 30_000) return;
        _lastError = message;
        _lastErrorAt = now;
        _log.LogWarning(message);
        Write("ERROR " + message);
        if (detail is not null) Write("      " + detail);
    }

    private static void Write(string line)
    {
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(Path.Combine(LogDir, $"service-{DateTime.Now:yyyyMMdd}.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
