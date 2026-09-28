using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace FastCopy.App;

internal enum AppTheme
{
    Light,
    Dark,
}

/// <summary>
/// Detects the Windows light/dark app theme and applies it: swaps the color resource dictionary (all
/// controls bind colors via DynamicResource, so this updates live with no restart) and toggles the
/// native dark title bar via DWM. There is no WPF-provided theme API, so this reads the same registry
/// value Explorer and every other theme-aware Win32/WPF app reads.
/// </summary>
internal static class ThemeManager
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string PersonalizeValueName = "AppsUseLightTheme";
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    public static AppTheme DetectSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            if (key?.GetValue(PersonalizeValueName) is int appsUseLightTheme)
                return appsUseLightTheme == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // No read access or the key is missing (older Windows) - fall back to dark rather than guess wrong.
        }
        return AppTheme.Dark;
    }

    /// <summary>Swaps the active color dictionary. Safe to call repeatedly - existing bindings update live.</summary>
    public static void ApplyColors(AppTheme theme)
    {
        var uri = new Uri(theme == AppTheme.Dark ? "Themes/Colors.Dark.xaml" : "Themes/Colors.Light.xaml", UriKind.Relative);
        var dictionaries = Application.Current.Resources.MergedDictionaries;

        var existing = dictionaries.FirstOrDefault(d => d.Source is { } s && s.OriginalString.Contains("/Themes/Colors."));
        if (existing is not null)
            dictionaries.Remove(existing);

        dictionaries.Add(new ResourceDictionary { Source = uri });
    }

    /// <summary>Matches the native title bar to the theme. Requires the window's handle to already exist.</summary>
    public static void ApplyTitleBar(Window window, AppTheme theme)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int useDarkMode = theme == AppTheme.Dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDarkMode, sizeof(int));
    }
}
