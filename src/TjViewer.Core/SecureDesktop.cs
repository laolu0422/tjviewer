using System.Runtime.InteropServices;
using System.Text;

namespace TjViewer.Core;

/// <summary>
/// 安全桌面（Winlogon / UAC / 锁屏）检测。
/// 普通桌面名为 "Default"；切换到安全桌面时输入桌面变为 "Winlogon"。
/// 被控端据此判断是否需要交由 SecureAgent 采集与注入。
/// </summary>
public static class SecureDesktop
{
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const int UOI_NAME = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, uint nLength, out uint lpnLengthNeeded);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    /// <summary>当前输入桌面名；无法打开时返回空串。</summary>
    public static string GetInputDesktopName()
    {
        IntPtr h = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(256);
            GetUserObjectInformation(h, UOI_NAME, sb, (uint)sb.Capacity, out _);
            return sb.ToString();
        }
        finally { CloseDesktop(h); }
    }

    /// <summary>输入桌面不是 Default 即视为安全桌面（Winlogon 等）。</summary>
    public static bool IsSecureDesktopActive()
    {
        var name = GetInputDesktopName();
        return !string.IsNullOrEmpty(name) && !name.Equals("Default", StringComparison.OrdinalIgnoreCase);
    }
}
