namespace VamAcarsClient.Core;

/// <summary>
/// User preferences POCO. Persisted as JSON in
/// %LOCALAPPDATA%\VamAcarsClient\preferences.json by
/// <see cref="PreferencesStore"/>. Mirrors <see cref="FlightContext"/>'s
/// shape (init-only properties, JSON-friendly) so the same serializer
/// pattern works.
///
/// Why a single POCO rather than separate stores per preference:
/// preferences are a grab-bag of user-toggles that grow over time
/// (audio cues now, theme toggle later, notification opt-in after
/// that, etc.). Centralising in one file means one read at startup,
/// one write per change, and JSON's schema-evolution gives us free
/// migration — adding a property defaults to the C# default for old
/// files, no migration script needed.
///
/// Why NOT add to FlightContext: that's flight-plan data (callsign,
/// route) that gets rewritten on every Connect. Preferences are
/// orthogonal — they shouldn't churn when the user just changes
/// flight number. Separate file keeps the writes uncorrelated.
/// </summary>
public sealed class Preferences
{
    /// <summary>
    /// When true, <see cref="VamAcarsClient.Tray.Models.AcarsClientService"/>
    /// plays a system sound on phase transitions to Pushback / Takeoff /
    /// Landed. Defaults to false so a fresh install is silent until the
    /// pilot opts in — sudden audio from a tray-app is annoying.
    /// </summary>
    public bool AudioCueEnabled { get; init; } = false;

    /// <summary>
    /// Welle D / D5 — demo-mode toggle. When true, the connect flow
    /// proceeds WITHOUT a valid token and WITHOUT sending any heartbeats
    /// to the server. SimConnect still attaches normally so live telemetry
    /// flows into the UI, but the heartbeat-loop is never started.
    ///
    /// # Use cases
    ///
    /// - Streamers showing the ACARS UI on-camera without a real account
    /// - Evaluators kicking the tires before committing to a pairing
    /// - First-launch demo before the user generates a pairing-code
    ///
    /// # What demo-mode does NOT do (v1)
    ///
    /// - No local phase-detector. Server-side decides phase from heartbeats,
    ///   and demo-mode sends none. <see cref="VamAcarsClient.Tray.Models.AcarsClientState.CurrentPhase"/>
    ///   stays at its default until/unless a real connect happens. UI's
    ///   phase indicators show "—" or stale.
    /// - No PIREP creation. The auto-PIREP pipeline is server-driven; no
    ///   heartbeats means no BLOCK_ON detection server-side, means no PIREP.
    /// - No live-map visibility. The pilot doesn't show up on the public
    ///   /live map because nothing reaches the server.
    ///
    /// Defaults to false so a fresh install runs in normal-pairing mode.
    /// Opting in is explicit (EINSTELLUNGEN checkbox), opting out is
    /// equally explicit. There's no auto-disable on successful pairing
    /// because that would surprise streamers who deliberately want demo
    /// even after having a real token on file.
    /// </summary>
    public bool DemoModeEnabled { get; init; } = false;
}
