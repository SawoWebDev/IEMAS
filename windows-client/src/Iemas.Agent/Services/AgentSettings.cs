using System.IO;
using System.Text.Json;

namespace Iemas.Agent.Services;

/// <summary>§10 Settings (server URL configuration), §8.1 "Report technical state." Non-sensitive — plain JSON, unlike CredentialStore which is DPAPI-protected.</summary>
public class AgentSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IemasAgent");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public string ServerUrl { get; set; } = "https://localhost:8443";
    /// <summary>On by default: the Agent must be running to deliver pop-ups, so it starts with Windows unless the user turns it off.</summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>Set once the on-by-default choice has been applied, so older installs (saved before it was the default) get it once, and a later opt-out is respected.</summary>
    public bool StartupDefaultApplied { get; set; }
    public bool AllowInsecureTls { get; set; } = false;

    /// <summary>
    /// Left by the CMS installer package (Install-IEMAS-Agent.bat): registering with it approves this PC straight
    /// away. Deleted as soon as the PC is registered, so it never stays on disk longer than needed.
    /// </summary>
    public string? InstallKey { get; set; }

    /// <summary>The mailbox the installer package was made for, if any; registration then needs no typing.</summary>
    public string? InstallEmail { get; set; }

    /// <summary>Set once the first-run welcome tour has been shown on this PC.</summary>
    public bool WelcomeTourShown { get; set; }

    public static AgentSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AgentSettings>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch (IOException) { }
        catch (JsonException) { }

        return new AgentSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
    }
}
