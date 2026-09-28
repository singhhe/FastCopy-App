using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FastCopy.App;

/// <summary>
/// Works out which folder an Explorer window is currently showing, so an intercepted Ctrl+V knows where
/// the files were meant to go.
/// </summary>
internal static class ExplorerFolder
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    // CabinetWClass is a normal Explorer window, ExploreWClass the legacy two-pane one. Progman and
    // WorkerW are the desktop, which is a folder view too - pasting onto the desktop should work.
    private const string ExplorerClass = "CabinetWClass";
    private const string LegacyExplorerClass = "ExploreWClass";
    private const string DesktopClass = "Progman";
    private const string DesktopWorkerClass = "WorkerW";

    public static bool IsExplorerWindow(IntPtr hwnd)
    {
        string cls = GetClassName(hwnd);
        return cls is ExplorerClass or LegacyExplorerClass or DesktopClass or DesktopWorkerClass;
    }

    /// <summary>
    /// Returns the folder path the window is showing, or null when there isn't one - "This PC",
    /// Libraries, search results and Recycle Bin are all Explorer windows with no filesystem path
    /// behind them, and there is nowhere to copy to in that case.
    ///
    /// Must be called on an STA thread (the WPF UI thread): it goes through the Shell COM automation
    /// object, which is also why this is not done inside the keyboard hook.
    /// </summary>
    public static string? Resolve(IntPtr hwnd)
    {
        string cls = GetClassName(hwnd);
        if (cls is DesktopClass or DesktopWorkerClass)
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        object? shell = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return null;

            shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;

            // Shell.Windows() enumerates exactly the Explorer (and legacy IE) windows, which is the set
            // we care about; matching on HWND picks out the one the user is actually looking at.
            dynamic windows = ((dynamic)shell).Windows();
            foreach (dynamic window in windows)
            {
                if ((IntPtr)(long)window.HWND != hwnd) continue;

                string? url = window.LocationURL as string;
                if (string.IsNullOrEmpty(url)) return null; // a virtual folder - no path to paste into

                string path = new Uri(url).LocalPath;
                return Directory.Exists(path) ? path : null;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UriFormatException
                                      or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // Explorer restarting, or a shell window that doesn't expose LocationURL. Caller reports it.
        }
        finally
        {
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }

        return null;
    }

    private static string GetClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        var buffer = new StringBuilder(256);
        int length = GetClassNameW(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }
}
