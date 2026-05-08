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
