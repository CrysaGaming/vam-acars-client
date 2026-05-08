using System.Text.Json;

namespace VamAcarsClient.Core;

/// <summary>
/// Persists <see cref="Preferences"/> as JSON in
/// %LOCALAPPDATA%\&lt;LocalAppDataFolderName&gt;\&lt;PreferencesFileName&gt;.
/// Mirrors <see cref="FlightContextStore"/>'s shape exactly — same
/// constructor, same Try/Save/Clear API — so future preference-bound
/// code can swap stores or compose them without re-learning.
///
/// File location, format, and threading rationale all match
/// FlightContextStore (see its docstring for details). The only
/// substantive difference is the Try/Load failure mode: when no file
/// exists, we return a fresh <see cref="Preferences"/> instance with
/// defaults rather than null. Preferences must always be available
/// for the UI to bind against; defaults-on-miss is what the user
/// expects on first launch.
/// </summary>
public sealed class PreferencesStore
{
    private readonly VamConfig _config;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public PreferencesStore(VamConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    private string GetPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = Path.Combine(localAppData, _config.Storage.LocalAppDataFolderName);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, _config.Storage.PreferencesFileName);
    }

    /// <summary>
    /// Serialize and persist preferences. Overwrites any prior file.
    /// Called from <see cref="VamAcarsClient.Tray.App.SetAudioCueEnabled"/>
    /// (and any future preference setters) right after the in-memory
    /// state is updated, so the on-disk file always reflects the
    /// current state of the UI.
    /// </summary>
    public void Save(Preferences prefs)
    {
        if (prefs is null) throw new ArgumentNullException(nameof(prefs));
        var json = JsonSerializer.Serialize(prefs, JsonOptions);
        File.WriteAllText(GetPath(), json);
    }

    /// <summary>
    /// Read and deserialize preferences. Returns a fresh defaults-
    /// populated instance for any failure mode (file missing, malformed
    /// JSON, IO error). Callers don't have to null-check — the contract
    /// is "always returns a usable Preferences object".
    /// </summary>
    public Preferences Load()
    {
        var path = GetPath();
        if (!File.Exists(path)) return new Preferences();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Preferences>(json, JsonOptions)
                ?? new Preferences();
        }
        catch (JsonException)
        {
            return new Preferences();
        }
        catch (IOException)
        {
            return new Preferences();
        }
    }
}
