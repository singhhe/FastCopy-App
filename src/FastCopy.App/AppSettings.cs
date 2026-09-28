using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FastCopy.Core;

namespace FastCopy.App;

/// <summary>
/// Small persisted state so the background/tray auto-copy path has somewhere to get a destination and
/// options from when there is no visible window for the user to type them into.
/// </summary>
internal sealed class AppSettings
{
    public string DefaultDestination { get; set; } = string.Empty;
    public FileConflictAction ConflictAction { get; set; } = FileConflictAction.Overwrite;
    public bool VerifyAfterCopy { get; set; }
    /// <summary>
    /// The old "start copying the moment you press Ctrl+C, into a fixed folder" behaviour. Off by
    /// default now that Ctrl+V in Explorer is the normal path - it fires on a keystroke people press
    /// constantly without meaning to start a transfer, which already caused one accidental multi-GB
    /// copy during testing. Still available from the tray for a deliberate one-key dump.
    /// </summary>
    public bool AutoCopyEnabled { get; set; }

    /// <summary>Intercept Ctrl+V in File Explorer and run the copy through FastCopy.</summary>
    public bool ExplorerPasteEnabled { get; set; } = true;

    public bool HasShownTrayHint { get; set; }

    /// <summary>
    /// Bumped when a default changes in a way that should reach people who already have a settings
    /// file, since an absent property deserializes to the new default but a present one does not.
    /// See <see cref="Migrate"/>.
    /// </summary>
    public int SettingsVersion { get; set; }

    /// <summary>
    /// Set once the user ticks "Don't show this again" on the donation prompt. Until then the prompt
    /// appears on every launch - see <see cref="MainWindow.ShowDonatePromptIfDue"/>.
    /// </summary>
    public bool SuppressDonatePrompt { get; set; }

    private static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastCopy", "settings.json");

    private const int CurrentVersion = 1;

    /// <summary>True when no settings file existed - i.e. this is a first run on this machine.</summary>
    [JsonIgnore]
    public bool IsFirstRun { get; private set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null)
                {
                    loaded.Migrate();
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Corrupt or unreadable settings shouldn't block launch - just start fresh.
        }
        return new AppSettings { IsFirstRun = true, SettingsVersion = CurrentVersion };
    }

    /// <summary>
    /// Brings a settings file written by an older build up to date. Version 0 files predate the
    /// Ctrl+V-in-Explorer handler and have AutoCopyEnabled persisted as true purely because that used
    /// to be the default, not because anyone chose it - turn it off so the two don't both fire.
    /// </summary>
    private void Migrate()
    {
        if (SettingsVersion >= CurrentVersion) return;

        if (SettingsVersion < 1) AutoCopyEnabled = false;

        SettingsVersion = CurrentVersion;
        Save();
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
