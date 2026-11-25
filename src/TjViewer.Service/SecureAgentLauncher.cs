using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TjViewer.Service;

/// <summary>
/// 以 SYSTEM 身份在指定会话的 winlogon 桌面拉起 SecureAgent。
/// 做法：复制服务自身 SYSTEM 令牌 → 设置 TokenSessionId 为目标会话 → CreateProcessAsUser，
/// STARTUPINFO.lpDesktop 指定 winsta0\winlogon，使代理运行在安全桌面上。
/// </summary>
public static class SecureAgentLauncher
{
    public static bool Launch(string pipeName, uint sessionId, out string error)
    {
        error = "";
        var exe = FindAgentExe();
        if (exe is null) { error = "未找到 TjViewer.SecureAgent.exe"; return false; }

        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TOKEN_ALL_ACCESS, out var sysToken))
        {
            error = "OpenProcessToken 失败：" + new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        IntPtr dup = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            if (!Native.DuplicateTokenEx(sysToken, Native.TOKEN_ALL_ACCESS, IntPtr.Zero,
                    Native.SECURITY_IMPERSONATION, Native.TOKEN_PRIMARY, out dup))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Diag("DuplicateTokenEx ok");

            uint sid = sessionId;
            if (!Native.SetTokenInformation(dup, Native.TokenSessionId, ref sid, sizeof(uint)))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Diag("SetTokenInformation ok, session=" + sid);

            Native.CreateEnvironmentBlock(out env, dup, false);

            var si = new Native.STARTUPINFO
            {
                cb = Marshal.SizeOf<Native.STARTUPINFO>(),
                lpDesktop = @"winsta0\winlogon",
                dwFlags = Native.STARTF_USESHOWWINDOW,
                wShowWindow = 0, // SW_HIDE
            };
            // uiAccess 应用不能经 CreateProcessAsUser 直接创建（返回 740）。
            // 先以 CreateProcessAsUser 拉起无 uiAccess 的 cmd.exe（SYSTEM/winlogon），
            // 再由 cmd 用 CreateProcess 派生代理，从而正确授予 uiAccess。
            var cmd = $"cmd.exe /c \"\"{exe}\" --pipe {pipeName}\"";
            if (!Native.CreateProcessAsUser(dup, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    Native.CREATE_UNICODE_ENVIRONMENT, env, null, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Diag("launch via cmd ok, cmd-pid=" + pi.dwProcessId);

            try { Native.CloseHandle(pi.hThread); } catch { }
            try { Native.CloseHandle(pi.hProcess); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Diag("FAIL: " + ex.Message);
            return false;
        }
        finally
        {
            if (env != IntPtr.Zero) Native.DestroyEnvironmentBlock(env);
            if (dup != IntPtr.Zero) Native.CloseHandle(dup);
            Native.CloseHandle(sysToken);
        }
    }

    private static string? FindAgentExe()
    {
        // 标准布局：<安装目录>\TjViewer.SecureAgent.exe 与 <安装目录>\service\TjViewer.Service.exe
        var sibling = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "TjViewer.SecureAgent.exe"));
        if (File.Exists(sibling)) return sibling;
        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TjViewer", "TjViewer.SecureAgent.exe");
        return File.Exists(installed) ? installed : null;
    }

    private static void Diag(string line)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TjViewer", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "secure-launch.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch { }
    }
}
