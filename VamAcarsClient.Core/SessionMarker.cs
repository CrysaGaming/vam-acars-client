using System.Text.Json;

namespace VamAcarsClient.Core;

/// <summary>
/// Crash-recovery marker (option #13). Written to disk by
/// <see cref="VamAcarsClient.Tray.Models.AcarsClientService"/> when a
/// session starts; deleted when it ends cleanly. If the file is still
/// on disk at next app launch, the previous session ended in a way
/// that didn't run our normal teardown — app crash, OS reboot,
/// force-kill via Task Manager — and the tray-app surfaces a "letzte
/// Sitzung wiederherstellen?" banner with the captured context.
///
/// Why JSON + plaintext: this is flight-plan data (callsign, ICAOs)
/// the user pastes from SimBrief without thinking. Encrypting it buys
/// nothing and forfeits the diagnostic upside of "open the file, see
/// what was saved" when debugging recovery behaviour. Mirrors the
/// rationale + shape of <see cref="FlightContextStore"/>.
///
/// Why a separate marker rather than re-using flight-context.json:
/// flight-context.json's lifecycle is "save after every successful
/// Connect, never delete" — its presence has no recovery semantics,
/// so we can't repurpose it. The marker has a strict
/// write-on-Start / delete-on-Stop lifecycle, making its mere
/// existence the recovery signal.
///
/// Schema versioning: when adding fields, default-value them in the
/// init so old marker files still deserialize. If we ever need a
/// breaking change, bumping the filename (session-marker-v2.json)
/// is preferable to in-place migration — old markers from before the
/// crash are days old anyway, discarding them isn't a real loss.
/// </summary>
public sealed class SessionMarker
{
    /// <summary>
    /// UTC instant when the session started. Used by the recovery
    /// banner to render "vor 5 Minuten unterbrochen" — gives the
    /// pilot a sense of how stale the session is. Stored as ISO-8601
    /// string (DateTimeOffset round-trip format) for portability;
    /// the tray-app parses this back into a DateTimeOffset on load.
    /// </summary>
    public required string StartedAtUtc { get; init; }

    /// <summary>The callsign that was active. Echoed in the recovery
    /// banner so the user can recognize which flight they were on.</summary>
    public required string Callsign { get; init; }

    /// <summary>The network value (Offline/VATSIM/IVAO).</summary>
    public required string Network { get; init; }

    public string? FlightNumber { get; init; }
    public string? DepartureIcao { get; init; }
    public string? ArrivalIcao { get; init; }
    public int? CruiseAltitudeFt { get; init; }
    public string? FlightRules { get; init; }

    /// <summary>
    /// Convert this marker back into a <see cref="FlightContext"/> so
    /// the recovery flow can hand it straight to
    /// <see cref="VamAcarsClient.Tray.Models.AcarsClientService.StartAsync"/>.
    /// All flight-context-relevant fields round-trip exactly; we only
    /// add StartedAtUtc on the marker side, which the FlightContext
    /// doesn't need.
    /// </summary>
    public FlightContext ToFlightContext() => new()
    {
        Callsign = Callsign,
        Network = Network,
        FlightNumber = FlightNumber,
        DepartureIcao = DepartureIcao,
        ArrivalIcao = ArrivalIcao,
        CruiseAltitudeFt = CruiseAltitudeFt,
        FlightRules = FlightRules,
    };

    /// <summary>
    /// Build a marker from a live <see cref="FlightContext"/> at the
    /// moment of Connect. Stamps the current UTC time as
    /// <see cref="StartedAtUtc"/>; everything else copies one-to-one.
    /// </summary>
    public static SessionMarker FromFlightContext(FlightContext context, DateTimeOffset now)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        return new SessionMarker
        {
            StartedAtUtc = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            Callsign = context.Callsign,
            Network = context.Network,
            FlightNumber = context.FlightNumber,
            DepartureIcao = context.DepartureIcao,
            ArrivalIcao = context.ArrivalIcao,
            CruiseAltitudeFt = context.CruiseAltitudeFt,
            FlightRules = context.FlightRules,
        };
    }
}

/// <summary>
/// Persists the <see cref="SessionMarker"/> for crash-recovery
/// (option #13). Mirrors <see cref="FlightContextStore"/>'s shape
/// exactly — same constructor, same Save / TryLoad / Clear API —
/// so future "store-some-state" code can copy the pattern without
/// re-learning it.
///
/// File location, threading, and failure semantics all match
/// FlightContextStore (see its docstring). The substantive
/// difference is the lifecycle: this file is written ONCE per
/// session (on Start) and deleted on clean Stop, whereas
/// flight-context.json is overwritten on every Connect and never
/// deleted. The presence of the file is itself the signal —
/// existence at app-launch == "previous session crashed".
/// </summary>
public sealed class SessionMarkerStore
{
    private readonly VamConfig _config;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public SessionMarkerStore(VamConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    private string GetPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = Path.Combine(localAppData, _config.Storage.LocalAppDataFolderName);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, _config.Storage.SessionMarkerFileName);
    }

    /// <summary>
    /// Serialize and persist the marker. Overwrites any prior file —
    /// shouldn't happen in normal flow (the marker is deleted on
    /// clean Stop), but if a user disables the app mid-session and
    /// reconnects without restart, this is the safe overwrite path.
    /// IO errors propagate to the caller; AcarsClientService wraps
    /// the call in try/catch so a write-failure can't kill the
    /// connect flow.
    /// </summary>
    public void Save(SessionMarker marker)
    {
        if (marker is null) throw new ArgumentNullException(nameof(marker));
        var json = JsonSerializer.Serialize(marker, JsonOptions);
        File.WriteAllText(GetPath(), json);
    }

    /// <summary>
    /// Read and deserialize the marker. Returns null for any failure
    /// mode — file missing (expected, no recovery needed), corrupt
    /// JSON, or schema mismatch. Caller treats null as "no recovery
    /// needed, normal startup".
    ///
    /// Stale-marker handling: we don't reject markers based on age
    /// here. A 12-hour-old marker is still a real signal that the
    /// previous session ended badly — the user might have just come
    /// back to their machine after lunch. The recovery banner in the
    /// UI shows the elapsed time; if the user decides "that's too
    /// stale", they hit Verwerfen.
    /// </summary>
    public SessionMarker? TryLoad()
    {
        var path = GetPath();
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SessionMarker>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Delete the marker. Called from
    /// <see cref="VamAcarsClient.Tray.Models.AcarsClientService.StopAsync"/>
    /// on clean disconnect, and from the recovery banner's "Verwerfen"
    /// handler when the user dismisses without resuming. Idempotent
    /// — missing-file is the success case.
    /// </summary>
    public void Clear()
    {
        var path = GetPath();
        if (File.Exists(path))
        {
            try { File.Delete(path); }
            catch (IOException) { /* swallow — non-critical */ }
        }
    }
}
