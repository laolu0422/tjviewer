using Microsoft.Win32;

namespace TjViewer.Client.Services;

/// <summary>开机自启（HKCU Run 键，用户会话启动，无需管理员；见 PRD FR-10）。须用户明确同意。</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TjViewer";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is not null;
    }

    public static void Enable()
    {
        var exe = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, $"\"{exe}\" --minimized");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
