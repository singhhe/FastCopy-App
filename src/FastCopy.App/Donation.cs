using System.Diagnostics;

namespace FastCopy.App;

/// <summary>
/// FastCopy is free. This is the one place the donation link lives, shared by the launch prompt
/// (<see cref="DonateWindow"/>) and the tray menu's own "Donate" item.
/// </summary>
internal static class Donation
{
    /// <summary>PayPal no-code checkout link - the donor chooses the amount.</summary>
    public const string PayPalUrl = "https://www.paypal.com/ncp/payment/3WW9T4GNQZ6KC";

    /// <summary>
    /// Opens the donation page in the user's default browser. UseShellExecute is required here:
    /// .NET Core onwards it defaults to false, and Process.Start cannot launch a URL without it.
    /// </summary>
    public static void OpenPayPal()
    {
        try
        {
            Process.Start(new ProcessStartInfo(PayPalUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser, or the shell refused the launch. Not worth interrupting the user
            // over - the app works fine without the donation page opening.
        }
    }
}
