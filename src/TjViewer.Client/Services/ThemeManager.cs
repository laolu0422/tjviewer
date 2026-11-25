using System.Windows;
using Microsoft.Win32;

namespace TjViewer.Client.Services;

/// <summary>
/// 主题管理：跟随 Windows 应用主题（浅色=紫白，深色=紫黑），监听系统设置变化实时切换。
/// 画刷全部以 DynamicResource 引用，切换时仅替换主题字典，无需重建窗口。
/// </summary>
public static class ThemeManager
{
    private static ResourceDictionary? _current;

    public static void Initialize()
    {
        Apply(ReadSystemLight());
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
            {
                var light = ReadSystemLight();
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(light));
            }
        };
    }

    private static bool ReadSystemLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int i ? i != 0 : true;
        }
        catch { return true; }
    }

    private static void Apply(bool light)
    {
        var app = Application.Current;
        if (app is null) return;
        var dict = new ResourceDictionary
        {
            Source = new Uri(light ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative),
        };
        var merged = app.Resources.MergedDictionaries;
        if (_current is not null) merged.Remove(_current);
        merged.Add(dict);
        _current = dict;
    }
}
