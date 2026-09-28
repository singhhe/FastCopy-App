using System.Configuration;
using System.Data;
using System.Windows;

namespace FastCopy.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The window now hides to the tray instead of closing (see MainWindow.OnClosing), so the app must
        // no longer shut down just because its main window closed - only an explicit tray Exit should quit.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Colors are applied here (before the window is constructed) rather than via StartupUri so the
        // very first frame already matches the system theme instead of flashing the wrong one.
        ThemeManager.ApplyColors(ThemeManager.DetectSystemTheme());

        var window = new MainWindow();

        // Windows-startup launches (see StartupRegistration) pass --tray so the app comes up quietly in
        // the tray rather than popping a window in the user's face at every boot.
        if (e.Args.Contains("--tray"))
        {
            window.Show();
            window.Hide();
        }
        else
        {
            window.Show();

            // Deferred to ApplicationIdle so the main window has actually painted first - shown inline
            // here the modal pops over an empty frame, which reads as "the app is the nag screen".
            // Deliberately not shown on the --tray path above: that launch is Windows booting, not the
            // user opening FastCopy, and a modal over whatever they're doing at login is a different
            // (much worse) thing than a prompt when they open the app themselves.
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(window.ShowDonatePromptIfDue));
        }
    }
}

