namespace VamAcarsClient.Core;

/// <summary>
/// Runtime configuration for the ACARS client. Populated from
/// appsettings.json + appsettings.Development.json by the bootstrap,
/// or constructed with defaults for tests / fallback when no config
/// file is present.
///
/// Kept as a simple POCO with init-only setters so it's bindable
/// directly via Microsoft.Extensions.Configuration.Bind() and also
/// trivially constructible in unit-tests:
///
///   var cfg = new VamConfig();              // production defaults
///   var cfg = configuration.Get<VamConfig>(); // from JSON
///
/// We intentionally don't use the [Required] / IOptions&lt;T&gt; pattern
/// from ASP.NET Core — for a single-process desktop app that's overkill.
/// The config-load happens once at startup and the resulting object is
/// passed by value to the consumers that need it.
/// </summary>
public sealed class VamConfig
{
    public VamSection Vam { get; init; } = new();
    public StorageSection Storage { get; init; } = new();
    public HeartbeatSection Heartbeat { get; init; } = new();

    public sealed class VamSection
    {
        /// <summary>Production server. Cloudflared tunnel terminates here.</summary>
        public string ApiBaseUrl { get; init; } = "https://vam.kevindrack.de";

        /// <summary>User-Agent header sent on every API request. Lets the
        /// server log identify our client and version separately from
        /// browsers and the bot.</summary>
        public string UserAgent { get; init; } = "VamAcarsClient/0.1 (.NET 10; Windows)";

        /// <summary>HTTP client request timeout. 30s is generous for normal
        /// roundtrips and tolerates Cloudflare cold-starts.</summary>
        public int RequestTimeoutSeconds { get; init; } = 30;
    }

    public sealed class StorageSection
    {
        /// <summary>Path under %LOCALAPPDATA% where we store the encrypted
        /// pairing-token. Per-user encryption via DPAPI means this folder
        /// only contains opaque bytes — safe even if the file is copied
        /// somewhere else (the data won't decrypt off this machine for
        /// this user).</summary>
        public string LocalAppDataFolderName { get; init; } = "VamAcarsClient";

        /// <summary>Filename of the encrypted token blob inside the local
        /// app-data folder.</summary>
        public string TokenFileName { get; init; } = "token.bin";

        /// <summary>Filename of the persisted last-used FlightContext
        /// (callsign / network / departure / arrival) inside the local
        /// app-data folder. JSON, plaintext — these are user-visible
        /// flight-plan fields, not secrets, so no DPAPI wrapping. The
        /// tray-app reads it at startup to pre-populate the form, and
        /// rewrites it after every successful Connect, so the user
        /// doesn't have to retype NGN901 / EDDF / EDDM every launch.</summary>
        public string FlightContextFileName { get; init; } = "flight-context.json";

        /// <summary>Filename of the persisted user preferences (audio
        /// cues, future toggles) inside the local app-data folder.
        /// JSON, plaintext — these are user-visible toggles, not
        /// secrets. Loaded once at startup to populate
        /// <see cref="VamAcarsClient.Tray.Models.AcarsClientState"/>;
        /// rewritten whenever the user toggles a preference in the
        /// MainWindow's EINSTELLUNGEN card.</summary>
        public string PreferencesFileName { get; init; } = "preferences.json";
    }

    public sealed class HeartbeatSection
    {
        /// <summary>How often we POST to /api/acars/heartbeat. 2s is the
        /// sweet spot — fast enough for live-map smoothness, slow enough
        /// to not hammer the server.</summary>
        public int IntervalSeconds { get; init; } = 2;

        /// <summary>How many heartbeats to keep buffered when the server
        /// is unreachable. At 2s interval, 100 entries = ~3min of offline
        /// tolerance. Beyond that, oldest is dropped.</summary>
        public int MaxQueueDepth { get; init; } = 100;
    }
}
