using System.Windows.Forms;

namespace FastCopy.App;

/// <summary>
/// Wraps the WinForms NotifyIcon (WPF has no tray-icon type of its own) so the rest of the app never has
/// to `using System.Windows.Forms`, which would collide with WPF's own MessageBox/Application types.
/// </summary>
internal sealed class TrayIconController : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _autoCopyItem;
    private readonly ToolStripMenuItem _startWithWindowsItem;
    private readonly ToolStripMenuItem _explorerPasteItem;

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public event Action<bool>? AutoCopyToggled;
    public event Action<bool>? StartWithWindowsToggled;
    public event Action? BalloonClicked;
    public event Action? DonateRequested;
    public event Action<bool>? ExplorerPasteToggled;

    public TrayIconController(System.Drawing.Icon icon, bool autoCopyEnabled, bool startWithWindows,
        bool explorerPasteEnabled)
    {
        var menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("Open FastCopy");
        openItem.Click += (_, _) => OpenRequested?.Invoke();
        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());

        // The headline feature: Ctrl+V in Explorer runs through FastCopy instead of Windows' copy.
        _explorerPasteItem = new ToolStripMenuItem("Handle Ctrl+V in File Explorer")
            { CheckOnClick = true, Checked = explorerPasteEnabled };
        _explorerPasteItem.Click += (_, _) => ExplorerPasteToggled?.Invoke(_explorerPasteItem.Checked);
        menu.Items.Add(_explorerPasteItem);

        // The older, blunter behaviour, kept for a deliberate one-key dump to a fixed folder. Off by
        // default - it fires on a keystroke people press without meaning to start a transfer.
        _autoCopyItem = new ToolStripMenuItem("Also copy on Ctrl+C (to default folder)")
            { CheckOnClick = true, Checked = autoCopyEnabled };
        _autoCopyItem.Click += (_, _) => AutoCopyToggled?.Invoke(_autoCopyItem.Checked);
        menu.Items.Add(_autoCopyItem);

        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = startWithWindows };
        _startWithWindowsItem.Click += (_, _) => StartWithWindowsToggled?.Invoke(_startWithWindowsItem.Checked);
        menu.Items.Add(_startWithWindowsItem);

        menu.Items.Add(new ToolStripSeparator());

        // FastCopy is free; this is the only permanent way to find the donation page once the launch
        // prompt has been dismissed with "Don't show this again".
        var donateItem = new ToolStripMenuItem("Donate...");
        donateItem.Click += (_, _) => DonateRequested?.Invoke();
        menu.Items.Add(donateItem);

        menu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = "FastCopy",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => BalloonClicked?.Invoke();
    }

    /// <summary>Reflects a change made outside the menu (e.g. first-run auto-enable) back into it.</summary>
    public void SetStartWithWindows(bool enabled) => _startWithWindowsItem.Checked = enabled;

    /// <summary>
    /// Greys out the Ctrl+V item when the keyboard hook could not be installed, so the menu can't claim
    /// a feature that isn't running.
    /// </summary>
    public void SetExplorerPasteAvailable(bool available)
    {
        _explorerPasteItem.Enabled = available;
        if (!available)
        {
            _explorerPasteItem.Checked = false;
            _explorerPasteItem.Text = "Handle Ctrl+V in File Explorer (unavailable)";
        }
    }

    /// <summary>NotifyIcon.Text throws past 63 characters - truncate rather than let a long path crash it.</summary>
    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
