using System.Runtime.InteropServices;
using System.Text;

namespace TjViewer.Core;

/// <summary>
/// 安全桌面（Winlogon / UAC / 锁屏）检测与线程桌面绑定。
/// 普通桌面名为 "Default"；切换到安全桌面时输入桌面变为 "Winlogon"。
/// 被控端据此判断是否需要交由 SecureAgent 采集与注入。
/// </summary>
public static class SecureDesktop
{
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    private const int UOI_NAME = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, uint nLength, out uint lpnLengthNeeded);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>当前输入桌面名；无法打开时返回空串。</summary>
    public static string GetInputDesktopName()
    {
        IntPtr h = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        // 普通权限在 UAC/锁屏安全桌面下打开输入桌面会被拒绝；退化为 0 权限再试，仅用于取名
        if (h == IntPtr.Zero) h = OpenInputDesktop(0, false, 0);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(256);
            GetUserObjectInformation(h, UOI_NAME, sb, (uint)sb.Capacity, out _);
            return sb.ToString();
        }
        finally { CloseDesktop(h); }
    }

    /// <summary>
    /// 输入桌面不是 Default 即视为安全桌面。
    /// 关键补充：UAC/锁屏时普通权限进程通常无法打开输入桌面（OpenInputDesktop 返回 NULL），
    /// 这种“拒绝访问”本身就是安全桌面的可靠信号，因此打开失败也判定为安全桌面。
    /// 调用方应连续采样去抖，避免桌面切换瞬间误判。
    /// </summary>
    public static bool IsSecureDesktopActive()
    {
        IntPtr h = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (h == IntPtr.Zero) h = OpenInputDesktop(0, false, 0);
        if (h == IntPtr.Zero) return true;
        try
        {
            var sb = new StringBuilder(256);
            if (!GetUserObjectInformation(h, UOI_NAME, sb, (uint)sb.Capacity, out _)) return false;
            var name = sb.ToString();
            return !name.Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(h); }
    }

    /// <summary>
    /// 把当前线程绑定到输入桌面（安全桌面采集前调用）。
    /// GDI 的 CopyFromScreen 只能读取线程所在桌面；SecureAgent 运行在 winlogon 桌面时
    /// 线程默认已绑定，此方法用于桌面切换后或线程池线程的兜底绑定。
    /// 失败不抛异常（进程本身已在安全桌面时仍可继续采集）。
    /// </summary>
    public static bool TryAttachThreadToInputDesktop(out string desktopName)
    {
        desktopName = "";
        IntPtr h = OpenInputDesktop(0, false, DESKTOP_READOBJECTS | DESKTOP_SWITCHDESKTOP);
        if (h == IntPtr.Zero) h = OpenInputDesktop(0, false, 0);
        if (h == IntPtr.Zero) return false;
        try
        {
            var sb = new StringBuilder(256);
            if (!GetUserObjectInformation(h, UOI_NAME, sb, (uint)sb.Capacity, out _)) return false;
            desktopName = sb.ToString();

            var current = GetThreadDesktop(GetCurrentThreadId());
            if (current != IntPtr.Zero && current == h) return true;

            // 已与输入桌面同名（进程本身以 winsta0\winlogon 启动）时无需切换
            var currentName = new StringBuilder(256);
            if (current != IntPtr.Zero && GetUserObjectInformation(current, UOI_NAME, currentName,
                    (uint)currentName.Capacity, out _) &&
                currentName.ToString().Equals(desktopName, StringComparison.OrdinalIgnoreCase))
                return true;

            return SetThreadDesktop(h);
        }
        finally { CloseDesktop(h); }
    }
}
