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
