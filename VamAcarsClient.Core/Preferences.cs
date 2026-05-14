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

    /// <summary>
    /// Welle E / E1 — OBS-overlay HTTP-server toggle. When true, the
    /// tray-app binds a small <see cref="System.Net.HttpListener"/> on
    /// loopback (127.0.0.1) that serves a self-contained HTML overlay
    /// and a JSON snapshot of the live ACARS state.
    ///
    /// # Use case
    ///
    /// Streamers point an OBS browser-source at <c>http://127.0.0.1:{port}/</c>
    /// and get callsign, phase, route, aircraft, and network on-stream
    /// without round-tripping through vam.kevindrack.de's overlay-URL
    /// (which currently lags 3-5 s). Loopback latency is ~1 ms, so the
    /// overlay refreshes essentially in lockstep with each heartbeat.
    ///
    /// # Security
    ///
    /// The listener is bound to <c>127.0.0.1</c> ONLY — not the LAN
    /// address, not 0.0.0.0. A Windows firewall prompt should never
    /// appear on first run for a loopback-only listener, and no host
    /// on the network can reach this port. We accept this trade-off
    /// (no remote OBS box can read the overlay) for the safety of not
    /// exposing the pilot's live position to any LAN guest.
    ///
    /// # Defaults
    ///
    /// Off on a fresh install so no listener is spun up unless the
    /// pilot opts in via EINSTELLUNGEN. The port defaults to 8765, a
    /// memorable value in the unregistered range; if 8765 is busy
    /// (another VAM-client instance, an unrelated tool), the server
    /// tries 8766..8775 before giving up. The bound port is surfaced
    /// to the UI via <c>AcarsClientState.OverlayServerUrl</c> so the
    /// pilot can copy the exact URL into OBS without guessing.
    /// </summary>
    public bool OverlayServerEnabled { get; init; } = false;

    /// <summary>
    /// Welle E / E2 — voice-commands toggle. When true, the tray-app
    /// constructs a <c>VoiceCommandService</c> that listens on the
    /// default microphone for a small grammar of "VAM …" phrases and
    /// drives the UI in response.
    ///
    /// # Supported phrases (v1 MVP)
    ///
    /// - "VAM Status"      → speaks current ConnectionStatus + heartbeats
    /// - "VAM Verbinden"   → triggers Connect (gated on PreflightComplete)
    /// - "VAM Trennen"     → triggers Disconnect
    /// - "VAM Flugzeug"    → speaks detected aircraft type + registration
    ///
    /// # Why German wake-word
    ///
    /// The grammar is loaded with the de-DE recognizer so pilot accents
    /// are correctly transcribed; "VAM" as wake-word survives the
    /// transition (it's a single short syllable that scores reliably).
    /// If a pilot has only en-US installed, the recognizer falls through
    /// to en-US and the same wake-word is used — accuracy drops on the
    /// German command-words but the most-used "Status" / "Connect" still
    /// land. We don't gate on language-pack availability; if no
    /// recognizer can be built, the service logs a warning and stays
    /// idle (the checkbox stays ticked but produces nothing).
    ///
    /// # Privacy
    ///
    /// Audio never leaves the machine. <c>System.Speech</c> uses the
    /// in-box SAPI 5.4 recognizer, no cloud round-trip. Default off
    /// because microphone-listening surprises users; opt-in is explicit.
    /// </summary>
    public bool VoiceCommandsEnabled { get; init; } = false;
}
