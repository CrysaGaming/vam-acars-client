using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VamAcarsClient.Tray.Models;

/// <summary>
/// High-level tri-state for the heartbeat connection. Drives the tray
/// icon's tooltip text, the status-window's coloured pill, and the
/// menu's read-only Status row. Kept deliberately coarse — fine-grained
/// substates (TokenInvalid, ServerDown, SimDisconnected, etc.) live in
/// <see cref="AcarsClientState.StatusMessage"/> so this enum doesn't
/// explode every time we add a new failure-mode.
/// </summary>
public enum ConnectionStatus
{
    /// <summary>Initial state and the post-Stop state. No heartbeats are flowing.</summary>
    Disconnected,

    /// <summary>Heartbeat-service is starting up, or recovering from a transient outage.</summary>
    Connecting,

    /// <summary>Heartbeats are flowing successfully (last one acknowledged within the recent window).</summary>
    Connected,

    /// <summary>
    /// A non-recoverable error blocks heartbeats. Examples: missing
    /// token, 401 from the server, SimConnect refused after retries.
    /// User typically needs to take action — re-pair, restart MSFS, etc.
    /// </summary>
    Error,
}

/// <summary>
/// View-model for the tray-app. Holds everything the UI binds against —
/// connection status, current flight context, server URL, last-seen
/// timestamps. Implemented with manual INotifyPropertyChanged rather
/// than CommunityToolkit.Mvvm's source-generator: small enough that the
/// boilerplate is fine, and one fewer NuGet to track during M4.
///
/// Threading note: WPF data-binding requires property-changed events
/// to fire on the UI thread. The heartbeat-service runs on a background
/// timer, so when we eventually wire it up (next M4 session), the
/// service-side adapter must marshal updates via Dispatcher.Invoke
/// before assigning to these properties. That marshalling lives in the
/// adapter, NOT here — this class stays UI-agnostic so it could be
/// hosted under a different UI framework later.
/// </summary>
public class AcarsClientState : INotifyPropertyChanged
{
    /// <summary>
    /// Constructor: seeds the pre-flight checklist with the default
    /// items (option #10) and wires per-item PropertyChanged forwarding
    /// so any IsChecked toggle bubbles up as a PreflightComplete change.
    ///
    /// Why hook PropertyChanged on each item rather than re-computing
    /// PreflightComplete inside a setter: the items are POCOs the UI
    /// mutates directly via two-way binding — there's no setter on the
    /// State for us to intercept. Subscribing to each item is the only
    /// way to know when one of them flipped without polling.
    ///
    /// We don't subscribe to the collection's CollectionChanged event
    /// because we don't currently add/remove items at runtime. If a
    /// future feature lets users customise the list, this constructor
    /// would need an extra hook to attach/detach the per-item handler
    /// when items come and go.
    /// </summary>
    public AcarsClientState()
    {
        PreflightChecklist = new ObservableCollection<PreflightChecklistItem>
        {
            // Default item-set. Order = realistic pre-departure flow.
            // Keys are stable identifiers; labels are user-facing German.
            new() { Key = "flight_plan", Label = "Flugplan eingegeben (FMC/Route)" },
            new() { Key = "fuel_loaded", Label = "Treibstoff geladen" },
            new() { Key = "doors_closed", Label = "Türen geschlossen" },
            new() { Key = "beacon_on",    Label = "Beacon an" },
            new() { Key = "pushback_ok",  Label = "Pushback-Freigabe" },
        };

        foreach (var item in PreflightChecklist)
        {
            item.PropertyChanged += OnPreflightItemChanged;
        }
    }

    /// <summary>
    /// Forward per-item IsChecked changes as a PreflightComplete change
    /// on the parent state. The handler doesn't filter by property name
    /// because IsChecked is the only mutable property on
    /// <see cref="PreflightChecklistItem"/> — any change is a tick-flip.
    /// </summary>
    private void OnPreflightItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        RaisePreflightCompleteChanged();
    }

    private ConnectionStatus _connectionStatus = ConnectionStatus.Disconnected;
    public ConnectionStatus ConnectionStatus
    {
        get => _connectionStatus;
        set => SetField(ref _connectionStatus, value);
    }

    private string? _statusMessage;
    /// <summary>
    /// Human-readable detail for the current state. Examples:
    /// "Heartbeats flowing — 42 sent / 0 failed", "Token rejected (401)",
    /// "MSFS not running". The tray-tooltip and the status-pill both
    /// pull from this field.
    /// </summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    private bool _hasToken;
    /// <summary>
    /// True iff a token is currently stored in the per-user TokenStore
    /// (encrypted via DPAPI). Used by the UI to decide whether to show
    /// "Paired" or a "Pair via Cli" instruction. Doesn't validate the
    /// token — a stored-but-revoked token still reads true here, the
    /// invalidation surfaces later via ConnectionStatus=Error.
    /// </summary>
    public bool HasToken
    {
        get => _hasToken;
        set => SetField(ref _hasToken, value);
    }

    private string _serverUrl = "";
    public string ServerUrl
    {
        get => _serverUrl;
        set => SetField(ref _serverUrl, value);
    }

    // ─── Flight-context fields (populated when heartbeats are flowing) ─

    private string? _callsign;
    public string? Callsign
    {
        get => _callsign;
        set => SetField(ref _callsign, value);
    }

    private string? _aircraftType;
    public string? AircraftType
    {
        get => _aircraftType;
        set => SetField(ref _aircraftType, value);
    }

    private string? _aircraftRegistration;
    /// <summary>
    /// Server-echoed tail number (M3.8.1). Same value the client sent
    /// in the heartbeat — included on the response for symmetry so the
    /// UI reads everything from one source. Display layer typically
    /// joins with <see cref="AircraftType"/> as "A320 / D-ANNE".
    /// </summary>
    public string? AircraftRegistration
    {
        get => _aircraftRegistration;
        set => SetField(ref _aircraftRegistration, value);
    }

    private string? _currentPhase;
    /// <summary>
    /// Server-resolved phase (echoed in the heartbeat response since M3.7).
    /// Null until the server has committed a phase for the current
    /// session. Display layer typically renders alongside
    /// <see cref="PhaseEnteredAt"/> as "Cruise — 0:42".
    /// </summary>
    public string? CurrentPhase
    {
        get => _currentPhase;
        set => SetField(ref _currentPhase, value);
    }

    private DateTimeOffset? _phaseEnteredAt;
    public DateTimeOffset? PhaseEnteredAt
    {
        get => _phaseEnteredAt;
        set => SetField(ref _phaseEnteredAt, value);
    }

    /// <summary>
    /// Computed phase + elapsed-time string for the FLUG-KONTEXT Phase row.
    /// Returns formats like "Cruise — 0:42" (under an hour) or
    /// "Cruise — 1:42:03" (over an hour); returns null when there's
    /// nothing meaningful to show, which lets the WPF binding's
    /// TargetNullValue kick in to render an em-dash.
    ///
    /// Why a computed property rather than a regular setter that we
    /// update each tick: the source-of-truth values (<see cref="CurrentPhase"/>
    /// + <see cref="PhaseEnteredAt"/>) only change on phase transitions,
    /// not every second. Computing the elapsed-time on read keeps both
    /// of those clean. The downside is WPF won't auto-refresh the binding
    /// — we need to raise PropertyChanged explicitly on a 1Hz tick from
    /// the UI side. <see cref="RaisePhaseDisplayChanged"/> is the hook
    /// for that; <see cref="MainWindow"/> drives it via DispatcherTimer.
    ///
    /// Defensive returns: empty/null phase, null PhaseEnteredAt, or
    /// negative elapsed (clock-skew) all degrade gracefully — we either
    /// return the bare phase name without elapsed, or null. Never throw,
    /// never display garbage like "Cruise — -1:32".
    /// </summary>
    public string? PhaseDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_currentPhase)) return null;
            if (_phaseEnteredAt is null) return _currentPhase;

            var elapsed = DateTimeOffset.UtcNow - _phaseEnteredAt.Value;
            if (elapsed.TotalSeconds < 0) return _currentPhase;

            var totalMin = (int)elapsed.TotalMinutes;
            var sec = elapsed.Seconds;
            if (totalMin < 60)
            {
                return $"{_currentPhase} — {totalMin}:{sec:D2}";
            }
            var hr = totalMin / 60;
            var min = totalMin % 60;
            return $"{_currentPhase} — {hr}:{min:D2}:{sec:D2}";
        }
    }

    /// <summary>
    /// Pokes WPF to re-evaluate any bindings on <see cref="PhaseDisplay"/>.
    /// Called from the UI's per-second DispatcherTimer in
    /// <see cref="MainWindow"/>. Cheap: a single PropertyChanged event
    /// raise — no allocation beyond the event-args itself which the
    /// runtime caches via the property-name-string interning.
    ///
    /// We expose this as a method rather than driving it from a setter
    /// because there's no underlying field to set. CurrentPhase and
    /// PhaseEnteredAt have their own setters that already fire
    /// PropertyChanged; this is purely the per-tick refresh path.
    /// </summary>
    public void RaisePhaseDisplayChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PhaseDisplay)));
    }

    // ─── Throughput counters ─────────────────────────────────────────

    private int _heartbeatsSent;
    public int HeartbeatsSent
    {
        get => _heartbeatsSent;
        set => SetField(ref _heartbeatsSent, value);
    }

    private int _heartbeatsFailed;
    public int HeartbeatsFailed
    {
        get => _heartbeatsFailed;
        set => SetField(ref _heartbeatsFailed, value);
    }

    private int _heartbeatsQueued;
    public int HeartbeatsQueued
    {
        get => _heartbeatsQueued;
        set => SetField(ref _heartbeatsQueued, value);
    }

    // ─── Telemetry-tab (Welle A — option A1) ──────────────────────────
    //
    // Latency / failure-rate / network-health snapshots refreshed by
    // AcarsClientService.OnTelemetryTick (DispatcherTimer, 2s). All
    // four feed the expanded HEARTBEATS card in MainWindow.xaml — see
    // the "TELEMETRIE" sub-section there.
    //
    // Why mirror state rather than bind directly to HeartbeatService:
    // the service lives in Core and uses DateTimeOffset / Interlocked.Read
    // primitives that don't speak to WPF's PropertyChanged plumbing. The
    // service exposes plain getters; this state-class wraps them with
    // INotifyPropertyChanged so the XAML bindings refresh cleanly.
    //
    // All four reset to defaults on disconnect (see AcarsClientService.
    // ResetHeartbeatCounters which now also clears these).

    private long _lastLatencyMs;
    /// <summary>
    /// Latest successful heartbeat round-trip time, in ms. 0 when not
    /// connected or no successful send has happened yet.
    /// </summary>
    public long LastLatencyMs
    {
        get => _lastLatencyMs;
        set => SetField(ref _lastLatencyMs, value);
    }

    private long _averageLatencyMs5Min;
    /// <summary>
    /// Rolling 5-minute average of successful-send latencies, in ms.
    /// Smooths over jitter so the displayed number is stable enough to
    /// glance at mid-flight.
    /// </summary>
    public long AverageLatencyMs5Min
    {
        get => _averageLatencyMs5Min;
        set => SetField(ref _averageLatencyMs5Min, value);
    }

    private int _failureRatePercent5Min;
    /// <summary>
    /// Percentage (0-100) of heartbeats in the last 5 minutes that
    /// failed for any reason (network, 401, 5xx, 4xx-drop). 0 means
    /// either "all good" OR "no data yet" — distinguish via
    /// NetworkHealthState == "Unknown".
    /// </summary>
    public int FailureRatePercent5Min
    {
        get => _failureRatePercent5Min;
        set => SetField(ref _failureRatePercent5Min, value);
    }

    private string _networkHealthState = "Unknown";
    /// <summary>
    /// Coarse health classification — "Online", "Degraded", "Offline",
    /// or "Unknown". UI color-codes based on this string. See
    /// HeartbeatService.NetworkHealthState for the bucketing rules.
    /// </summary>
    public string NetworkHealthState
    {
        get => _networkHealthState;
        set => SetField(ref _networkHealthState, value);
    }

    // ─── User preferences ────────────────────────────────────────────

    private bool _autoStartEnabled;
    /// <summary>
    /// Mirrors the HKCU\…\Run registration for "start with Windows".
    /// Set once at <see cref="App.OnStartup"/> from the live registry
    /// state, and rewritten by <see cref="App.SetAutoStart"/> after
    /// every successful toggle so the bound checkbox stays honest.
    ///
    /// Bound OneWay from the MainWindow's Auto-Start checkbox — the
    /// user's click triggers <see cref="App.SetAutoStart"/> directly
    /// via the click handler, NOT through the binding setter. That
    /// keeps registry IO out of the property setter and makes the
    /// success / failure path explicit.
    /// </summary>
    public bool AutoStartEnabled
    {
        get => _autoStartEnabled;
        set => SetField(ref _autoStartEnabled, value);
    }

    private bool _audioCueEnabled;
    /// <summary>
    /// User preference: play a SystemSound on phase transitions to
    /// Pushback / Takeoff / Landed. Persisted by
    /// <see cref="VamAcarsClient.Core.PreferencesStore"/>; loaded once
    /// at <see cref="VamAcarsClient.Tray.App.OnStartup"/> and rewritten
    /// via <see cref="VamAcarsClient.Tray.App.SetAudioCueEnabled"/>
    /// whenever the user toggles the EINSTELLUNGEN checkbox.
    ///
    /// Bound OneWay from the MainWindow's checkbox just like
    /// <see cref="AutoStartEnabled"/> — the binding-setter never
    /// fires from the user click (Click-handler routes through
    /// SetAudioCueEnabled), so registry/disk IO stays out of the
    /// hot setter path.
    /// </summary>
    public bool AudioCueEnabled
    {
        get => _audioCueEnabled;
        set => SetField(ref _audioCueEnabled, value);
    }

    // ─── Pre-flight checklist (option #10) ───────────────────────────
    //
    // Discipline-tool + connect-gate. The user ticks each item off
    // before clicking Verbinden; the button's IsEnabled is bound to
    // PreflightComplete (see MainWindow.xaml MultiDataTrigger), so
    // an incomplete checklist physically blocks the heartbeat-start.
    //
    // Per-session, NOT persisted across launches: every fresh app
    // start (and every Trennen → Verbinden cycle, see ResetPreflight
    // in AcarsClientService.StopAsync) resets all items to unchecked.
    // This is the whole point — running through the checklist is the
    // ritual, not the bookkeeping. Persisting "I always tick these
    // four before flight" would defeat the gate.
    //
    // Items are seeded once in the constructor below. Order matches
    // a realistic pre-departure flow (plan → fuel → doors → beacon →
    // pushback-clearance) so reading top-to-bottom mirrors what a
    // real-world cockpit checklist would say.
    //
    // Why ObservableCollection rather than List<>: WPF's ItemsControl
    // re-templates on collection-change events. We don't currently
    // mutate the collection at runtime (only the items' IsChecked),
    // but ObservableCollection is the natural choice and gives a
    // free upgrade path if a future config-file lets users customize
    // the list.

    /// <summary>
    /// The list of pre-flight items the user must check before the
    /// Verbinden button enables. See the section docstring above for
    /// design rationale.
    /// </summary>
    public ObservableCollection<PreflightChecklistItem> PreflightChecklist { get; }

    /// <summary>
    /// True iff every item in <see cref="PreflightChecklist"/> is
    /// currently checked, OR if the list is somehow empty (defensive —
    /// an empty list shouldn't happen but treating it as "no gate"
    /// is a saner failure mode than locking the user out of Verbinden
    /// forever).
    ///
    /// Computed property, not a stored field — its source-of-truth is
    /// the IsChecked bits of the individual items. Re-evaluated lazily
    /// on read; PropertyChanged is fired explicitly via
    /// <see cref="RaisePreflightCompleteChanged"/> from the constructor's
    /// per-item PropertyChanged subscription so WPF re-binds the
    /// MultiDataTrigger when any item flips.
    /// </summary>
    public bool PreflightComplete
    {
        get
        {
            if (PreflightChecklist.Count == 0) return true;
            foreach (var item in PreflightChecklist)
            {
                if (!item.IsChecked) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Reset every item to unchecked. Called from
    /// <see cref="VamAcarsClient.Tray.Models.AcarsClientService.StopAsync"/>
    /// so the next Verbinden requires a fresh tick-through. Also safe
    /// to call from the UI thread directly if a future "Reset" button
    /// is added — mutating IsChecked fires PropertyChanged on the item,
    /// which our subscription forwards as a PreflightComplete change.
    /// </summary>
    public void ResetPreflightChecklist()
    {
        foreach (var item in PreflightChecklist)
        {
            item.IsChecked = false;
        }
    }

    /// <summary>
    /// Pokes WPF to re-evaluate any bindings on
    /// <see cref="PreflightComplete"/>. Hooked up internally to each
    /// item's PropertyChanged in the constructor — callers don't need
    /// to invoke this manually unless they swap the entire collection
    /// out (which we don't currently do).
    /// </summary>
    private void RaisePreflightCompleteChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreflightComplete)));
    }

    // ─── Aircraft auto-detection (option #11) ────────────────────────
    //
    // Pre-connect peek at what MSFS has loaded right now. Populated by
    // <see cref="VamAcarsClient.Tray.App.ProbeSimAsync"/> via the service's
    // ProbeAircraftAsync method. Distinct from <see cref="AircraftType"/>
    // / <see cref="AircraftRegistration"/> — those are the SERVER-RESOLVED
    // values from the heartbeat-response, only available after Verbinden.
    // Detected* are the RAW SimConnect values, available before Verbinden.
    //
    // Why two separate field-pairs rather than reusing AircraftType /
    // Registration: the displayed flow is different. Pre-connect: "this is
    // what's in your sim right now, click verbinden to file it." Post-
    // connect: "this is what the server resolved + is committing to your
    // PIREP." Conflating them would make the UI lie about which value
    // it's showing — and would force the post-connect view to overwrite
    // the pre-connect snapshot, throwing away useful diagnostic info if
    // server resolution differs from what SimConnect reported.

    private string? _detectedAircraftType;
    /// <summary>
    /// Raw ATC MODEL from the most recent SimConnect probe — the type
    /// designator MSFS reports for the currently loaded aircraft. May
    /// be "UNKN" if the user hasn't loaded an aircraft, null if the
    /// probe has never run successfully.
    /// </summary>
    public string? DetectedAircraftType
    {
        get => _detectedAircraftType;
        set => SetField(ref _detectedAircraftType, value);
    }

    private string? _detectedAircraftRegistration;
    /// <summary>
    /// Raw ATC ID from the most recent SimConnect probe — the tail
    /// number set on the loaded aircraft. May be "UNKN" for free-flight
    /// users without a registration set, null pre-probe.
    /// </summary>
    public string? DetectedAircraftRegistration
    {
        get => _detectedAircraftRegistration;
        set => SetField(ref _detectedAircraftRegistration, value);
    }

    private string? _detectedAircraftTitle;
    /// <summary>
    /// Raw TITLE from the most recent SimConnect probe — the full
    /// aircraft name from aircraft.cfg (e.g. "Asobo A320neo Lufthansa").
    /// Currently retained for diagnostics + future tooltip enrichment;
    /// not displayed in the main UI yet.
    /// </summary>
    public string? DetectedAircraftTitle
    {
        get => _detectedAircraftTitle;
        set => SetField(ref _detectedAircraftTitle, value);
    }

    private bool _isProbingSim;
    /// <summary>
    /// True while a <see cref="VamAcarsClient.Tray.App.ProbeSimAsync"/>
    /// call is in flight. Drives the "Sim erkennen" button's disabled
    /// state + label-swap so the user gets visible feedback that the
    /// click registered. Flipped true at probe-start, false in finally.
    /// </summary>
    public bool IsProbingSim
    {
        get => _isProbingSim;
        set => SetField(ref _isProbingSim, value);
    }

    private string? _detectedSimulator;
    /// <summary>
    /// Canonical sim-name string for the PRE-FLIGHT card display
    /// (option #14). Populated by <see cref="VamAcarsClient.Tray.App.ProbeSimAsync"/>
    /// from <see cref="VamAcarsClient.Core.SimConnectClient.SimulatorName"/>
    /// after the SimConnect handshake. Examples: "MSFS 2020", "MSFS 2024",
    /// "FSX", "Prepar3D", or the raw szApplicationName for unknown sims.
    ///
    /// Distinct from the raw <c>SimulatorName</c> on the SimConnectClient
    /// — we apply a friendly-name mapper (see <see cref="MapSimulatorName"/>)
    /// before storing here so the UI shows familiar labels rather than
    /// MSFS internal strings like "KittyHawk".
    /// </summary>
    public string? DetectedSimulator
    {
        get => _detectedSimulator;
        set => SetField(ref _detectedSimulator, value);
    }

    /// <summary>
    /// Map the raw szApplicationName from the SimConnect handshake to a
    /// friendly canonical name shown in the UI (option #14). Falls
    /// through to the raw input for sims we don't recognise — pilots
    /// running niche P3D forks or FSX:SE deserve to see their actual
    /// sim name rather than null. Returns null only when the input is
    /// null/empty.
    ///
    /// MSFS 2020 reports "KittyHawk" (codename) in some builds, "Microsoft
    /// Flight Simulator" in others. The version-major distinguishes them
    /// from MSFS 2024's "FlightSimulator2024" which is also occasionally
    /// reported as "Aircraft 2024". We keep the mapper conservative —
    /// only mapping strings that are unambiguous, and passing through
    /// the rest verbatim.
    /// </summary>
    public static string? MapSimulatorName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Case-insensitive contains-match because the wrapped strings
        // can have surrounding noise ("Microsoft Flight Simulator (Steam)"
        // / "Microsoft Flight Simulator - SU13"). We check long forms
        // before short forms so "FSX" doesn't accidentally match
        // "MSFS X-Plane bridge" or similar future weirdness.
        if (raw.Contains("2024", StringComparison.OrdinalIgnoreCase))
            return "MSFS 2024";
        if (raw.Contains("KittyHawk", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("Microsoft Flight Simulator", StringComparison.OrdinalIgnoreCase))
            return "MSFS 2020";
        if (raw.Contains("Prepar3D", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("P3D", StringComparison.OrdinalIgnoreCase))
            return "Prepar3D";
        if (raw.Contains("Flight Simulator X", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("FSX", StringComparison.OrdinalIgnoreCase))
            return "FSX";

        return raw;
    }

    // ─── Crash-recovery (option #13) ─────────────────────────────────
    //
    // Non-null when App.OnStartup found a SessionMarker on disk —
    // means the previous session ended without a clean Stop (app
    // crash, OS reboot, force-kill). The MainWindow shows a banner
    // bound to HasRecoverableSession that offers Wiederverbinden /
    // Verwerfen.
    //
    // Lifecycle:
    //   - Set in App.OnStartup when SessionMarkerStore.TryLoad() returns
    //     a non-null marker.
    //   - Cleared (set to null) by App.DiscardRecoverableSession when
    //     the user clicks Verwerfen or after a successful resume-Connect.
    //   - Never written from inside this class — the marker file is
    //     owned by AcarsClientService, this property is just the
    //     UI-bound mirror.
    //
    // Why expose the full SessionMarker rather than just a "has-it" bool:
    // the banner needs to render the captured callsign and start-time
    // so the user can recognize which flight is being offered for
    // recovery. A bool would force a second binding for the details.

    private VamAcarsClient.Core.SessionMarker? _recoverableSession;
    /// <summary>
    /// The marker found at startup, or null if no recovery is needed.
    /// MainWindow's banner binds to <see cref="HasRecoverableSession"/>
    /// for visibility and to this property's fields (Callsign,
    /// DepartureIcao, ArrivalIcao, StartedAtUtc) for the body text.
    /// </summary>
    public VamAcarsClient.Core.SessionMarker? RecoverableSession
    {
        get => _recoverableSession;
        set
        {
            if (SetField(ref _recoverableSession, value))
            {
                // Both computed properties depend on this field — fire
                // their PropertyChanged so any binding refreshes.
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRecoverableSession)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecoverableSessionSummary)));
            }
        }
    }

    /// <summary>
    /// True iff <see cref="RecoverableSession"/> is non-null. Drives
    /// the recovery-banner's Visibility via BoolToVis converter.
    /// Computed because tying Visibility directly to a nullable
    /// reference would require a custom IValueConverter — a bool is
    /// simpler and re-uses the existing converter from EINSTELLUNGEN.
    /// </summary>
    public bool HasRecoverableSession => _recoverableSession is not null;

    /// <summary>
    /// Human-readable one-liner for the recovery banner, e.g.
    /// "NGN901 EDDF→EDDM (vor 12 Min unterbrochen)". Returns null when
    /// no marker is present so the binding's TargetNullValue can
    /// render a fallback. Time-elapsed is computed on read; the
    /// MainWindow's per-second DispatcherTimer also pokes
    /// PropertyChanged on this so the "vor X Min" ticks live.
    /// </summary>
    public string? RecoverableSessionSummary
    {
        get
        {
            var m = _recoverableSession;
            if (m is null) return null;

            // Compose the route portion. EDDF→EDDM if both ends known,
            // bare callsign otherwise. We deliberately render the
            // unicode arrow rather than a hyphen — visually distinguishes
            // a route from a date-range or other dash-shaped data.
            string route;
            if (!string.IsNullOrWhiteSpace(m.DepartureIcao) && !string.IsNullOrWhiteSpace(m.ArrivalIcao))
            {
                route = $"{m.Callsign} {m.DepartureIcao}→{m.ArrivalIcao}";
            }
            else
            {
                route = m.Callsign;
            }

            // Elapsed-time portion. Parse failure (corrupted timestamp)
            // degrades to bare route — never throw, never display garbage.
            if (DateTimeOffset.TryParse(m.StartedAtUtc, out var startedAt))
            {
                var elapsed = DateTimeOffset.UtcNow - startedAt;
                if (elapsed.TotalSeconds < 0)
                {
                    return route; // clock-skew; skip elapsed
                }

                string ago;
                if (elapsed.TotalMinutes < 1) ago = "vor wenigen Sekunden";
                else if (elapsed.TotalMinutes < 60) ago = $"vor {(int)elapsed.TotalMinutes} Min";
                else if (elapsed.TotalHours < 24) ago = $"vor {(int)elapsed.TotalHours} Std";
                else ago = $"vor {(int)elapsed.TotalDays} Tagen";

                return $"{route} ({ago} unterbrochen)";
            }

            return route;
        }
    }

    /// <summary>
    /// Pokes the bound recovery-banner to re-evaluate
    /// <see cref="RecoverableSessionSummary"/>'s elapsed-time portion.
    /// Same pattern as <see cref="RaisePhaseDisplayChanged"/>; called
    /// from the same per-second DispatcherTimer in MainWindow.
    /// </summary>
    public void RaiseRecoverableSessionSummaryChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecoverableSessionSummary)));
    }

    // ─── Updates (Velopack) ──────────────────────────────────────────

    private string _installedVersion = "dev";
    /// <summary>
    /// The currently running version, populated at
    /// <see cref="App.OnStartup"/>. For a Velopack-installed copy this
    /// is the SemVer Velopack tracks via the manifest in
    /// <c>%LOCALAPPDATA%\VamAcarsClient\current\</c>; for a dev /
    /// debug run from <c>bin\Debug</c> Velopack reports
    /// <see cref="UpdateException.NotInstalled"/> and we fall back to
    /// the literal <c>"dev"</c> so the bound EINSTELLUNGEN row says
    /// "Version: dev" — which is the truth, and a clearer signal than
    /// pretending we know a version we don't.
    /// </summary>
    public string InstalledVersion
    {
        get => _installedVersion;
        set => SetField(ref _installedVersion, value);
    }

    private bool _updateAvailable;
    /// <summary>
    /// True when <see cref="UpdateService.CheckForUpdatesAsync"/> has
    /// found a newer release on the configured update source than
    /// <see cref="InstalledVersion"/>. Drives the "Update verfügbar"
    /// indicator + button visibility in the EINSTELLUNGEN card.
    ///
    /// Stays false when we're running from a non-Velopack-installed
    /// build (dev run, CI), even if a newer release exists — we
    /// can't actually apply an update to an un-installed copy, so
    /// no point teasing the UI.
    /// </summary>
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        set => SetField(ref _updateAvailable, value);
    }

    private string? _latestVersion;
    /// <summary>
    /// Version string of the pending update (null when no update
    /// available). Surfaced as "Update verfügbar: 0.2.0" in the
    /// EINSTELLUNGEN card. Set alongside <see cref="UpdateAvailable"/>
    /// so the UI gets a single coherent transition.
    /// </summary>
    public string? LatestVersion
    {
        get => _latestVersion;
        set => SetField(ref _latestVersion, value);
    }

    private bool _updateDownloaded;
    /// <summary>
    /// True after <see cref="UpdateService.DownloadUpdatesAsync"/>
    /// completes successfully. Gates the "Installieren" button —
    /// while we're still downloading the user shouldn't be able to
    /// click apply, because <c>ApplyUpdatesAndRestart</c> needs the
    /// nupkg on disk first.
    /// </summary>
    public bool UpdateDownloaded
    {
        get => _updateDownloaded;
        set => SetField(ref _updateDownloaded, value);
    }

    private bool _updateChecking;
    /// <summary>
    /// True while <see cref="UpdateService.CheckAndDownloadAsync"/>
    /// has an in-flight call to GitHub Releases. Gates the
    /// "Auf Updates prüfen" button so the user can't fire a
    /// second concurrent check while the first is still running
    /// (the second would race the dispatcher state mutations and
    /// could leave UpdateAvailable / UpdateDownloaded in a stale
    /// configuration).
    ///
    /// Flipped true at the top of CheckAndDownloadAsync (after the
    /// first dispatcher InvokeAsync) and false in the finally block.
    /// The button's Content also swaps to "Prüfe..." while this is
    /// true so the user sees something is happening.
    /// </summary>
    public bool UpdateChecking
    {
        get => _updateChecking;
        set => SetField(ref _updateChecking, value);
    }

    // ─── INotifyPropertyChanged plumbing ─────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Helper for the property setters. Returns true and fires the
    /// event when the value actually changed (using EqualityComparer
    /// so reference-types compare by Equals). Same pattern the
    /// CommunityToolkit.Mvvm source-gen emits — kept inline here so the
    /// concrete properties stay readable.
    /// </summary>
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
