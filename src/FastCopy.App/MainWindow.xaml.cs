using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FastCopy.Core;
using Microsoft.Win32;

namespace FastCopy.App;

public partial class MainWindow : Window
{
    private const string PlayIconData = "M 0,0 L 9,5 L 0,10 Z";
    private const string PauseIconData = "M 0,0 L 3,0 L 3,10 L 0,10 Z M 6,0 L 9,0 L 9,10 L 6,10 Z";
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly ObservableCollection<string> _sources = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private CopyEngine? _engine;
    private CancellationTokenSource? _cts;
    private TrayIconController? _tray;
    private ExplorerPasteHook? _pasteHook;
    private HwndSource? _hwndSource;
    private bool _isRealExit;
    private string? _lastClipboardSignature;

    public MainWindow()
    {
        InitializeComponent();
        SourcesList.ItemsSource = _sources;
        _sources.CollectionChanged += (_, _) => UpdateSourcesSummary();
        UpdateSourcesSummary();
        LoadSettingsIntoUi();
        InitializeTrayIcon();
        InitializeExplorerPasteHook();

        // Being in the tray from login is the whole point of the Ctrl+V handler - a keyboard hook can
        // only fire while something is running to hold it, and Windows has no way to start a stopped
        // program on a clipboard change. Only forced on a genuinely fresh install, so anyone who has
        // deliberately turned it off since keeps their choice.
        if (_settings.IsFirstRun)
        {
            StartupRegistration.SetEnabled(true);
            _tray?.SetStartWithWindows(true);
        }

        // WPF has no theme-change event of its own; SystemEvents is the standard way any desktop app
        // (WinForms or WPF) learns the user flipped Windows between light and dark while it was running.
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyTitleBar(this, ThemeManager.DetectSystemTheme());

        // A clipboard-format listener is how a Win32/WPF app learns the clipboard changed without polling;
        // it's what makes "auto-copy the instant you Ctrl+C anywhere" possible even while the window is
        // hidden in the tray. The window's own hwnd survives Hide(), so this stays registered until real exit.
        _hwndSource = (HwndSource)PresentationSource.FromVisual(this)!;
        _hwndSource.AddHook(WndProc);
        AddClipboardFormatListener(_hwndSource.Handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
            _ = TryAutoCopyFromClipboardAsync();
        return IntPtr.Zero;
    }

    /// <summary>
    /// The window now represents "FastCopy is running" rather than "FastCopy is open" - closing it via the
    /// X button backgrounds the app to the tray (where the clipboard watcher keeps working) instead of
    /// exiting. Only the tray's own Exit command sets _isRealExit and lets the close proceed.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isRealExit)
        {
            e.Cancel = true;
            SaveSettingsFromUi();
            Hide();

            if (!_settings.HasShownTrayHint)
            {
                _tray?.ShowBalloon("FastCopy is still running", "It's in the tray, watching for copied files. Right-click the icon to exit.");
                _settings.HasShownTrayHint = true;
                _settings.Save();
            }
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hwndSource is not null)
        {
            RemoveClipboardFormatListener(_hwndSource.Handle);
            _hwndSource.RemoveHook(WndProc);
        }
        _pasteHook?.Dispose();
        _tray?.Dispose();
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        base.OnClosed(e);
    }

    private void InitializeTrayIcon()
    {
        using Stream iconStream = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/AppIcon.ico")).Stream;
        var icon = new System.Drawing.Icon(iconStream);

        _tray = new TrayIconController(icon, _settings.AutoCopyEnabled, StartupRegistration.IsEnabled(),
            _settings.ExplorerPasteEnabled);
        _tray.OpenRequested += () => Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
        _tray.ExitRequested += () => Dispatcher.Invoke(() =>
        {
            _isRealExit = true;
            Close();
            System.Windows.Application.Current.Shutdown();
        });
        _tray.AutoCopyToggled += enabled => Dispatcher.Invoke(() =>
        {
            _settings.AutoCopyEnabled = enabled;
            _settings.Save();
        });
        _tray.StartWithWindowsToggled += enabled => Dispatcher.Invoke(() => StartupRegistration.SetEnabled(enabled));
        _tray.BalloonClicked += () => Dispatcher.Invoke(() => _cts?.Cancel());

        // Keeps the donation page reachable after someone ticks "Don't show this again" - dismissing the
        // prompt shouldn't mean they can never find it again if they change their mind.
        _tray.DonateRequested += () => Dispatcher.Invoke(Donation.OpenPayPal);

        _tray.ExplorerPasteToggled += enabled => Dispatcher.Invoke(() =>
        {
            _settings.ExplorerPasteEnabled = enabled;
            _settings.Save();
            if (_pasteHook is not null) _pasteHook.Enabled = enabled;
        });
    }

    private void InitializeExplorerPasteHook()
    {
        _pasteHook = new ExplorerPasteHook { Enabled = _settings.ExplorerPasteEnabled };
        _pasteHook.PasteIntercepted += OnExplorerPasteIntercepted;

        if (!_pasteHook.IsInstalled)
        {
            // Worth saying out loud: the user turned this on and every Ctrl+V would silently behave
            // exactly as before, with nothing to distinguish "off" from "broken".
            LogBox.AppendText("Could not install the Ctrl+V handler - Explorer pastes will use Windows' own copy.\n");
            _tray?.SetExplorerPasteAvailable(false);
        }
    }

    /// <summary>
    /// Runs on the UI thread straight off the keyboard hook, which has already swallowed the keystroke -
    /// so every path out of here must either start a copy or tell the user why it didn't. Silently
    /// returning would look exactly like Ctrl+V being broken.
    /// </summary>
    private void OnExplorerPasteIntercepted(IntPtr explorerWindow)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (IsBusy)
            {
                NotifyPaste("FastCopy is already running a copy - wait for it to finish.");
                return;
            }

            string? destination = ExplorerFolder.Resolve(explorerWindow);
            if (destination is null)
            {
                NotifyPaste("That window has no folder to paste into (This PC, a library or a search result).");
                return;
            }

            List<string> valid;
            try
            {
                valid = Clipboard.GetFileDropList().Cast<string>()
                    .Where(p => File.Exists(p) || Directory.Exists(p))
                    .ToList();
            }
            catch (COMException)
            {
                NotifyPaste("Another app is holding the clipboard - try that paste again.");
                return;
            }

            if (valid.Count == 0)
            {
                NotifyPaste("Nothing on the clipboard still exists on disk.");
                return;
            }

            // Reflect the paste target in the UI so a later Start button press goes to the same place
            // the user just pasted into, rather than a stale destination from an earlier transfer.
            DestinationBox.Text = destination;
            _sources.Clear();
            AddSources(valid);

            // No countdown: pressing Ctrl+V is an explicit instruction to paste right now, the same
            // reasoning that makes a direct Start click skip it. The countdown exists for the paths
            // that trigger themselves off a bare Ctrl+C.
            await StartCopyAsync(valid, destination, countdown: false, background: !IsVisible);
        }));
    }

    /// <summary>Reports a swallowed-paste outcome wherever the user is currently looking.</summary>
    private void NotifyPaste(string message)
    {
        LogBox.AppendText(message + "\n");
        if (!IsVisible)
            _tray?.ShowBalloon("FastCopy", message, System.Windows.Forms.ToolTipIcon.Warning);
        else
            SetStatus(message, "StatusWarningBrush");
    }

    /// <summary>
    /// The launch-time donation reminder. FastCopy is free, so this asks rather than gates: it comes up
    /// on every launch until the user ticks "Don't show this again", and nothing in the app is withheld
    /// either way.
    ///
    /// Reads and writes <see cref="_settings"/> rather than loading its own <see cref="AppSettings"/>:
    /// this window rewrites the whole settings file on close, so a flag saved through a second instance
    /// would be silently clobbered a moment later.
    /// </summary>
    public void ShowDonatePromptIfDue()
    {
        if (_settings.SuppressDonatePrompt) return;

        var dialog = new DonateWindow { Owner = this };
        dialog.ShowDialog();

        if (dialog.SuppressFuturePrompts)
        {
            _settings.SuppressDonatePrompt = true;
            _settings.Save();
        }
    }

    private void LoadSettingsIntoUi()
    {
        DestinationBox.Text = _settings.DefaultDestination;
        VerifyCheckBox.IsChecked = _settings.VerifyAfterCopy;
        ConflictActionCombo.SelectedIndex = _settings.ConflictAction switch
        {
            FileConflictAction.Skip => 1,
            FileConflictAction.OverwriteIfNewer => 2,
            FileConflictAction.KeepBoth => 3,
            _ => 0,
        };
    }

    private void SaveSettingsFromUi()
    {
        _settings.DefaultDestination = DestinationBox.Text.Trim();
        _settings.VerifyAfterCopy = VerifyCheckBox.IsChecked == true;
        _settings.ConflictAction = SelectedConflictAction;
        _settings.Save();
    }

    /// <summary>
    /// Fires on every clipboard change system-wide, not just ones aimed at this app - this is the "start
    /// as soon as you copy/paste anything" behavior, triggered by the copy (Ctrl+C) itself rather than
    /// requiring a Ctrl+V into this window.
    /// </summary>
    private async Task TryAutoCopyFromClipboardAsync()
    {
        if (!_settings.AutoCopyEnabled || IsBusy) return;
        if (!Clipboard.ContainsFileDropList()) return;

        var paths = Clipboard.GetFileDropList().Cast<string>().ToList();
        if (paths.Count == 0) return;

        // Windows fires WM_CLIPBOARDUPDATE more often than the content actually changes; skip repeats so
        // the same selection can't be queued twice (e.g. once from this watcher, once from a Ctrl+V).
        string signature = string.Join('|', paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        if (signature == _lastClipboardSignature) return;
        _lastClipboardSignature = signature;

        // A Cut (Ctrl+X) is almost always "move this somewhere in Explorer", not "copy it via FastCopy" -
        // only react to an actual Copy, the same distinction Explorer itself makes.
        if (IsClipboardCutOperation()) return;

        var valid = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (valid.Count == 0) return;

        // The live textbox, not the persisted setting, is the source of truth while the app is running -
        // it still holds its value correctly even while the window is hidden in the tray.
        string destination = DestinationBox.Text.Trim();
        if (destination.Length == 0 || !Directory.Exists(destination))
        {
            _tray?.ShowBalloon("FastCopy", "Open FastCopy and set a destination to turn on auto-copy.",
                System.Windows.Forms.ToolTipIcon.Warning);
            return;
        }

        _sources.Clear();
        AddSources(valid);
        await StartCopyAsync(valid, destination, countdown: true, background: true);
    }

    private static bool IsClipboardCutOperation()
    {
        try
        {
            if (Clipboard.GetData("Preferred DropEffect") is not MemoryStream stream) return false;
            byte[] bytes = stream.ToArray();
            if (bytes.Length < 4) return false;
            const int DropEffectMove = 2;
            return (BitConverter.ToInt32(bytes, 0) & DropEffectMove) != 0;
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            return false;
        }
    }

    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return; // this category covers theme changes

        var theme = ThemeManager.DetectSystemTheme();
        ThemeManager.ApplyColors(theme);
        ThemeManager.ApplyTitleBar(this, theme);
    }

    /// <summary>Sets the status line's text and its semantic color together so they can never drift apart.</summary>
    private void SetStatus(string text, string brushResourceKey)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, brushResourceKey);
    }

    private void AddSources(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (!_sources.Contains(path, StringComparer.OrdinalIgnoreCase))
                _sources.Add(path);
        }
    }

    private int _summaryGeneration;

    // Walking large source trees for a byte count is exactly the kind of scan that must not run on the
    // UI thread (see BuildPlan's own move off the calling thread) - this runs on the pool and drops its
    // result if the source list changed again before it finished.
    private async void UpdateSourcesSummary()
    {
        int generation = ++_summaryGeneration;
        EmptySourcesText.Visibility = _sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_sources.Count == 0)
        {
            SourcesSummaryText.Text = string.Empty;
            return;
        }

        var paths = _sources.ToList();
        SourcesSummaryText.Text = "Counting...";

        var (fileCount, totalBytes) = await Task.Run(() =>
        {
            long bytes = 0;
            int count = 0;
            foreach (string path in paths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        bytes += new FileInfo(path).Length;
                        count++;
                    }
                    else if (Directory.Exists(path))
                    {
                        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        {
                            bytes += new FileInfo(file).Length;
                            count++;
                        }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return (count, bytes);
        });

        if (generation != _summaryGeneration) return; // superseded by a newer sources change
        SourcesSummaryText.Text = $"{fileCount} file(s), {FormatBytes(totalBytes)}";
    }

    private void OpenDestination_Click(object sender, RoutedEventArgs e)
    {
        string destination = DestinationBox.Text.Trim();
        if (destination.Length == 0 || !Directory.Exists(destination))
        {
            MessageBox.Show(this, "Choose a destination folder that exists first.", "FastCopy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
    }

    private FileConflictAction SelectedConflictAction => ConflictActionCombo.SelectedIndex switch
    {
        1 => FileConflictAction.Skip,
        2 => FileConflictAction.OverwriteIfNewer,
        3 => FileConflictAction.KeepBoth,
        _ => FileConflictAction.Overwrite,
    };

    private void BrowseSourceFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose file(s) to copy", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            AddSources(dialog.FileNames);
    }

    private void BrowseSourceFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to copy" };
        if (dialog.ShowDialog(this) == true)
            AddSources(new[] { dialog.FolderName });
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        foreach (string selected in SourcesList.SelectedItems.Cast<string>().ToList())
            _sources.Remove(selected);
    }

    private void RemoveSingleSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
            _sources.Remove(path);
    }

    private void ClearSources_Click(object sender, RoutedEventArgs e) => _sources.Clear();

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a destination folder" };
        if (dialog.ShowDialog(this) == true)
            DestinationBox.Text = dialog.FolderName;
    }

    private bool IsBusy => _cts is not null;

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var sources = _sources.ToList();
        string destination = DestinationBox.Text.Trim();

        if (sources.Count == 0 || string.IsNullOrEmpty(destination))
        {
            MessageBox.Show(this, "Add at least one source and choose a destination.", "FastCopy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string? missing = sources.FirstOrDefault(s => !File.Exists(s) && !Directory.Exists(s));
        if (missing is not null)
        {
            MessageBox.Show(this, $"Source path does not exist: {missing}", "FastCopy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveSettingsFromUi();

        // Clicking Start is already an explicit, intentional confirmation, so no countdown is needed here.
        await StartCopyAsync(sources, destination, countdown: false);
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (!Clipboard.ContainsFileDropList()) return; // not file content: let normal textbox paste happen untouched

        e.Handled = true;
        await HandleIncomingPathsAsync(Clipboard.GetFileDropList().Cast<string>().ToList());
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = !IsBusy && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        e.Handled = true;
        var paths = ((string[])e.Data.GetData(DataFormats.FileDrop)!).ToList();
        await HandleIncomingPathsAsync(paths);
    }

    /// <summary>
    /// Shared by paste (Ctrl+V) and drag-and-drop: both should start a copy immediately with no Start
    /// click, per the "start as soon as I paste/drop anything" requirement.
    /// </summary>
    private async Task HandleIncomingPathsAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy)
        {
            SetStatus("A copy is already running — cancel or wait for it to finish before pasting/dropping.", "StatusWarningBrush");
            return;
        }

        var valid = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (valid.Count == 0) return; // nothing usable (e.g. plain text) - ignore quietly

        _sources.Clear();
        AddSources(valid);

        string destination = DestinationBox.Text.Trim();
        if (string.IsNullOrEmpty(destination))
        {
            var dialog = new OpenFolderDialog { Title = "Choose a destination folder" };
            if (dialog.ShowDialog(this) != true) return; // user backed out of the picker - abort quietly, no error
            destination = dialog.FolderName;
            DestinationBox.Text = destination;
        }

        SaveSettingsFromUi();
        await StartCopyAsync(valid, destination, countdown: true);
    }

    private async Task StartCopyAsync(IReadOnlyList<string> sources, string destination, bool countdown, bool background = false)
    {
        _cts = new CancellationTokenSource();
        LogBox.Clear();
        SetRunningState(true);
        SetStatus("Scanning...", "AccentTextBrush");
        FileText.Text = string.Empty;
        SpeedText.Text = string.Empty;
        SizeText.Text = string.Empty;
        ProgressBarControl.Value = 0;

        if (background)
        {
            _tray?.ShowBalloon("FastCopy",
                $"Copying {sources.Count} item(s) to {destination} in 3s — click here to cancel.");
        }

        try
        {
            if (countdown)
            {
                // Paste-to-start combined with the default Overwrite conflict policy means one stray
                // Ctrl+V could begin clobbering files with zero confirmation, so a paste-triggered copy
                // gets a short cancelable countdown before any file I/O begins. A Start-button click is
                // already an intentional confirmation, so it skips straight past this.
                for (int s = 3; s >= 1; s--)
                {
                    SetStatus($"Starting in {s}s — click Cancel to abort", "StatusWarningBrush");
                    await Task.Delay(1000, _cts.Token);
                }
            }

            SetStatus("Scanning...", "AccentTextBrush");
            _engine = new CopyEngine();
            var options = new CopyOptions
            {
                VerifyAfterCopy = VerifyCheckBox.IsChecked == true,
                ConflictAction = SelectedConflictAction,
            };
            var progress = new Progress<CopyProgressEventArgs>(OnProgress);

            var result = await _engine.CopyAsync(sources, destination, options, progress, _cts.Token);
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Canceled", "TextSecondaryBrush");
            if (!IsVisible) _tray?.ShowBalloon("FastCopy", "Canceled.");
        }
        catch (Exception ex)
        {
            SetStatus("Failed", "StatusErrorBrush");
            LogBox.AppendText($"Error: {ex.Message}\n");
            if (!IsVisible) _tray?.ShowBalloon("FastCopy", $"Failed: {ex.Message}", System.Windows.Forms.ToolTipIcon.Error);
        }
        finally
        {
            SetRunningState(false);
            _cts?.Dispose();
            _cts = null;
            _engine = null;
            _tray?.SetTooltip("FastCopy");
        }
    }

    private void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        if (_engine is null) return;

        if (_engine.IsPaused)
        {
            _engine.Resume();
            PauseResumeLabel.Text = "Pause";
            PauseIcon.Data = Geometry.Parse(PauseIconData);
            SetStatus("Copying...", "AccentTextBrush");
        }
        else
        {
            _engine.Pause();
            PauseResumeLabel.Text = "Resume";
            PauseIcon.Data = Geometry.Parse(PlayIconData);
            SetStatus("Paused", "StatusWarningBrush");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
    }

    private void OnProgress(CopyProgressEventArgs e)
    {
        ProgressBarControl.Value = e.PercentComplete;

        // Progress<T>.Report posts back to the UI thread asynchronously, so a report can land after
        // the copy's finally block has already nulled out _engine — guard against that race here.
        if (_engine is { IsPaused: false })
        {
            string text = e.Phase switch
            {
                CopyPhase.Scanning => "Scanning...",
                CopyPhase.Copying => $"Copying — {e.FilesCompleted}/{e.TotalFiles} files",
                CopyPhase.Verifying => $"Verifying — {e.FilesCompleted}/{e.TotalFiles} files",
                _ => e.Phase.ToString(),
            };
            SetStatus(text, "AccentTextBrush");
            _tray?.SetTooltip(e.TotalFiles > 0 ? $"FastCopy — {text} ({e.PercentComplete:0}%)" : $"FastCopy — {text}");
        }

        FileText.Text = string.IsNullOrEmpty(e.CurrentFile) ? string.Empty : Path.GetFileName(e.CurrentFile);
        SpeedText.Text = e.BytesPerSecond > 0 ? $"{FormatBytes(e.BytesPerSecond)}/s" : string.Empty;
        SizeText.Text = $"{FormatBytes(e.TotalBytesCopied)} / {FormatBytes(e.TotalBytes)}";
        EtaText.Text = e.EstimatedTimeRemaining is { } eta ? $"ETA {eta:mm\\:ss}" : string.Empty;
    }

    private void ShowResult(CopyResult result)
    {
        if (result.Canceled)
            SetStatus("Canceled", "TextSecondaryBrush");
        else if (result.FilesFailed > 0)
            SetStatus("Completed with errors", "StatusWarningBrush");
        else
            SetStatus("Completed", "StatusSuccessBrush");

        FileText.Text = string.Empty;
        SpeedText.Text = string.Empty;
        EtaText.Text = string.Empty;

        LogBox.AppendText(
            $"Copied {result.FilesCopied} file(s) in {result.Elapsed:mm\\:ss} " +
            $"(parallelism used: {result.ParallelismUsed}, avg {FormatBytes(result.AverageBytesPerSecond)}/s).\n");

        if (result.FilesSkipped > 0) LogBox.AppendText($"Skipped: {result.FilesSkipped}\n");
        if (result.FilesRetried > 0) LogBox.AppendText($"Retried: {result.FilesRetried}\n");
        if (result.FilesFailed > 0) LogBox.AppendText($"Failed: {result.FilesFailed}\n");
        if (result.FilesVerifiedOk > 0 || result.FilesVerifiedFailed > 0)
            LogBox.AppendText($"Verified OK: {result.FilesVerifiedOk}, verification failed: {result.FilesVerifiedFailed}\n");

        foreach (var error in result.Errors)
            LogBox.AppendText(error + "\n");

        if (!IsVisible && !result.Canceled)
        {
            var icon = result.FilesFailed > 0 ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info;
            _tray?.ShowBalloon("FastCopy",
                $"Copied {result.FilesCopied} file(s) ({FormatBytes(result.BytesCopied)}) in {result.Elapsed:mm\\:ss}." +
                (result.FilesFailed > 0 ? $" {result.FilesFailed} failed." : ""), icon);
        }
    }

    private void SetRunningState(bool running)
    {
        StartButton.IsEnabled = !running;
        PauseResumeButton.IsEnabled = running;
        PauseResumeLabel.Text = "Pause";
        PauseIcon.Data = Geometry.Parse(PauseIconData);
        CancelButton.IsEnabled = running;
        SourcesList.IsEnabled = !running;
        DestinationBox.IsEnabled = !running;
        BrowseSourceFileButton.IsEnabled = !running;
        BrowseSourceFolderButton.IsEnabled = !running;
        RemoveSourceButton.IsEnabled = !running;
        ClearSourcesButton.IsEnabled = !running;
        BrowseDestButton.IsEnabled = !running;
        VerifyCheckBox.IsEnabled = !running;
        ConflictActionCombo.IsEnabled = !running;
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return $"{value:0.##} {units[i]}";
    }
}
