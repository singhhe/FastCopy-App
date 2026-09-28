using System.Windows;

namespace FastCopy.App;

/// <summary>
/// The launch-time donation reminder. FastCopy is free, so this asks rather than gates - every button
/// closes the dialog and the app carries on regardless of which one was pressed.
/// </summary>
public partial class DonateWindow : Window
{
    public DonateWindow()
    {
        InitializeComponent();
    }

    /// <summary>True when the user ticked "Don't show this again". The caller owns persisting it.</summary>
    public bool SuppressFuturePrompts => DontShowAgainCheckBox.IsChecked == true;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Same reason MainWindow does this: WPF styles the client area but not the OS title bar, so a
        // dark-themed dialog otherwise gets a white caption strip bolted on top.
        ThemeManager.ApplyTitleBar(this, ThemeManager.DetectSystemTheme());
    }

    private void Donate_Click(object sender, RoutedEventArgs e)
    {
        Donation.OpenPayPal();
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();
}
