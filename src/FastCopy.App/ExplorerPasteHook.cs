using System.Runtime.InteropServices;
using System.Text;

namespace FastCopy.App;

/// <summary>
/// Makes Ctrl+V inside File Explorer run a FastCopy transfer instead of Windows' own copy.
///
/// This is a low-level keyboard hook rather than a shell extension on purpose. Replacing Explorer's
/// actual file-operation engine (what TeraCopy's "default handler" does) means injecting a DLL into
/// explorer.exe and hooking IFileOperation - unsupported, needs admin, and breaks on Windows updates.
/// A WH_KEYBOARD_LL hook needs no admin, no injection, and no COM registration, and it covers the case
/// that actually matters: pasting files into a folder you're looking at.
///
/// The hook deliberately does the least work it can. Windows silently uninstalls a low-level hook whose
/// callback overruns LowLevelHooksTimeout (300ms by default), which would kill the feature for the rest
/// of the session, so the callback only runs cheap Win32 checks and hands the real work to the UI thread.
/// Resolving the Explorer window's folder involves COM and happens there, not here.
/// </summary>
internal sealed class ExplorerPasteHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_V = 0x56;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt
    private const int CF_HDROP = 15;
    private const int DROPEFFECT_MOVE = 2;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);
    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    // Held in a field, not passed inline: the delegate is the only managed reference the OS callback
    // has, and letting it be collected turns the next keystroke into a process-killing callback into
    // freed memory.
    private readonly HookProc _proc;
    private readonly uint _preferredDropEffect = RegisterClipboardFormatW("Preferred DropEffect");
    private IntPtr _hook;

    /// <summary>
    /// Raised on the UI thread when a Ctrl+V aimed at Explorer has been swallowed. The argument is the
    /// Explorer window's handle - resolve it to a folder with <see cref="ExplorerFolder"/>.
    /// </summary>
    public event Action<IntPtr>? PasteIntercepted;

    /// <summary>
    /// When false the hook stays installed but passes every key straight through. Toggling this is
    /// cheaper and far less error-prone than tearing the hook down and rebuilding it.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Must be called from a thread with a message loop - the WPF UI thread.</summary>
    public ExplorerPasteHook()
    {
        _proc = OnKey;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
    }

    /// <summary>False when the hook could not be installed; the caller should say so rather than
    /// leaving the user to wonder why Ctrl+V does nothing special.</summary>
    public bool IsInstalled => _hook != IntPtr.Zero;

    private IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !Enabled) return CallNextHookEx(_hook, nCode, wParam, lParam);

        int message = (int)wParam;
        if (message != WM_KEYDOWN && message != WM_SYSKEYDOWN)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if (info.VkCode != VK_V) return CallNextHookEx(_hook, nCode, wParam, lParam);

        // Ctrl+Shift+V and Ctrl+Alt+V are other apps' shortcuts - only plain Ctrl+V is ours.
        if (!IsDown(VK_CONTROL) || IsDown(VK_MENU))
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        IntPtr foreground = GetForegroundWindow();
        if (!ExplorerFolder.IsExplorerWindow(foreground))
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        // No files on the clipboard means this is a text paste into the search or rename box.
        if (!IsClipboardFormatAvailable(CF_HDROP))
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        // A Cut is a move, and moving within a volume is a rename Explorer does instantly - there is
        // nothing for a copy engine to improve on, so leave Ctrl+X/Ctrl+V alone entirely.
        if (IsClipboardCut())
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        // Swallow it, then let the UI thread do the slow part (COM folder resolution, reading the file
        // list, starting the copy) on its own time.
        PasteIntercepted?.Invoke(foreground);
        return 1;
    }

    private static bool IsDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// Reads the "Preferred DropEffect" clipboard format the way Explorer itself does. Any failure
    /// returns true ("treat as a cut"), which makes the hook decline to intervene - the safe direction,
    /// since the fallback is Windows performing the paste normally.
    /// </summary>
    private bool IsClipboardCut()
    {
        if (_preferredDropEffect == 0) return true;
        if (!OpenClipboard(IntPtr.Zero)) return true;

        try
        {
            IntPtr handle = GetClipboardData(_preferredDropEffect);
            if (handle == IntPtr.Zero) return false; // no hint set at all: Explorer treats that as a copy

            IntPtr data = GlobalLock(handle);
            if (data == IntPtr.Zero) return true;

            try
            {
                return (Marshal.ReadInt32(data) & DROPEFFECT_MOVE) != 0;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
