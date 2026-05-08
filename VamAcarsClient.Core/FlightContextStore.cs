using System.Text.Json;

namespace VamAcarsClient.Core;

/// <summary>
/// Persists the last-used <see cref="FlightContext"/> as JSON so the
/// tray-app's MainWindow can pre-populate Callsign / Network /
/// Departure / Arrival on next launch instead of forcing the user to
/// retype the same values every time.
///
/// File location:
///   %LOCALAPPDATA%\&lt;LocalAppDataFolderName&gt;\&lt;FlightContextFileName&gt;
///
/// Same parent folder as <see cref="TokenStore"/> — keeps everything
/// app-related in one easy-to-back-up directory. LocalApplicationData
/// (not roaming ApplicationData) because flight-plan preferences are
/// machine-and-user-specific; nobody benefits from syncing them via
/// OneDrive.
///
/// Why JSON, not DPAPI: callsign + ICAOs are not secrets. They're
/// the kind of data the user pastes from SimBrief or a flight-plan
/// site without thinking. Encrypting them buys nothing and forfeits
/// the diagnostic upside of "open the file, see what was saved" when
/// debugging persistence behaviour.
///
/// Why not the registry: HKCU is fine in principle, but a single JSON
/// file is easier to nuke ("delete this file to reset") and easier
/// for users / support to inspect. JSON also gives us free schema
/// evolution — adding a field to FlightContext means old saved files
/// silently get the default value for the new field on next load.
///
/// Threading: synchronous file IO. The save happens once per Connect
/// click (rare, on the UI thread, &lt; 1ms for a tiny JSON file) and
/// the load happens once at startup. Async would add complexity
/// without buying anything noticeable.
///
/// Mirror of <see cref="TokenStore"/>'s shape on purpose — same
/// constructor signature, same Try/Save/Clear API. If we ever
/// introduce more user-preferences stores, sticking to this pattern
/// keeps them all swappable.
/// </summary>
public sealed class FlightContextStore
{
    private readonly VamConfig _config;

    /// <summary>
    /// JSON options shared across Save and TryLoad so the round-trip
    /// is predictable. Indented output keeps the file human-readable;
    /// the file is &lt; 200 bytes regardless so the cost of writing
    /// whitespace is irrelevant. PropertyNameCaseInsensitive eases
    /// recovery from manual edits where someone capitalises a property
    /// the wrong way.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public FlightContextStore(VamConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Resolve the absolute path. Creates the parent folder if missing
    /// — first launch on a fresh machine needs the directory before
    /// either store can write.
    /// </summary>
    private string GetPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = Path.Combine(localAppData, _config.Storage.LocalAppDataFolderName);
        Directory.CreateDirectory(folder); // no-op if exists
        return Path.Combine(folder, _config.Storage.FlightContextFileName);
    }

    /// <summary>
    /// Serialize and persist the given context. Overwrites any prior
    /// file. Called after every successful Connect, so the most-recent
    /// flight plan is what comes back on next launch — matches the
    /// "last writer wins" mental model users naturally have.
    ///
    /// We don't bother with atomic write-and-rename: the file is
    /// trivially small, the only consequence of a partial write is
    /// that TryLoad returns null next time and the user retypes the
    /// fields once.
    /// </summary>
    public void Save(FlightContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        var json = JsonSerializer.Serialize(context, JsonOptions);
        File.WriteAllText(GetPath(), json);
    }

    /// <summary>
    /// Try to read and deserialize the persisted context. Returns null
    /// for any failure mode — file missing (first launch), corrupted
    /// JSON, or schema mismatch (e.g. a required FlightContext field
    /// got added after the file was written). Caller treats null as
    /// "no saved context, fall back to XAML defaults".
    ///
    /// Required-property mismatch is the schema-evolution gotcha:
    /// FlightContext has <c>required</c> Callsign + Network, so if a
    /// future version adds another required property, old saved files
    /// will fail to deserialize. JsonException → null is the right
    /// behaviour for that scenario; the user retypes once and the new
    /// save format takes over.
    /// </summary>
    public FlightContext? TryLoad()
    {
        var path = GetPath();
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<FlightContext>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // Malformed file or schema mismatch. Same recovery as
            // missing file — caller falls back to defaults.
            return null;
        }
        catch (IOException)
        {
            // File locked or other transient disk-level error. Don't
            // crash; the UI works fine without a pre-populated form.
            return null;
        }
    }

    /// <summary>
    /// Delete the persisted context. Currently unused (the UI doesn't
    /// expose a "forget my flight plan" affordance), but kept on the
    /// API for symmetry with <see cref="TokenStore.Clear"/> and to
    /// give support / diagnostics a way to reset state without
    /// rummaging in <c>%LOCALAPPDATA%</c>.
    /// </summary>
    public void Clear()
    {
        var path = GetPath();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
