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
}
