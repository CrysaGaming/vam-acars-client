using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using VamAcarsClient.Core;

namespace VamAcarsClient.Tray.Models;

/// <summary>
/// Owns the runtime lifecycle of the ACARS client: SimConnect connection,
/// heartbeat-service, and the SimConnect poll-loop. Exposes <see cref="StartAsync"/>
/// and <see cref="StopAsync"/> for the UI to drive, and pumps every state-changing
/// event into <see cref="AcarsClientState"/> via the WPF UI dispatcher.
///
/// Why this lives in the Tray project (not Core): the Cli has its own bespoke
/// integration in Program.cs's RunHeartbeatFlowAsync — interactive Console-prompt
/// for FlightContext, blocking ReadLine for the wait-loop, Console.WriteLine for
/// status. None of that translates to a tray-app. This service is the WPF-shaped
/// equivalent: declarative state object for the UI, dispatcher-marshalled events,
/// non-blocking lifecycle, idempotent start/stop.
///
/// The Core types it composes (<see cref="SimConnectClient"/>, <see cref="HeartbeatService"/>,
/// <see cref="TokenStore"/>, <see cref="FlightContext"/>) stay untouched — exactly
/// the same contracts the Cli uses, just driven from a different host.
///
/// Threading model:
///   - StartAsync / StopAsync run on the calling thread (typically UI).
///   - The SimConnect poll-loop runs on a Task.Run background task with a CTS.
///   - HeartbeatService runs its own background loop internally (Core's design).
///   - Events from BOTH (HeartbeatSent / HeartbeatFailed / ReAuthRequired,
///     plus SimConnectClient.ConnectionLost) marshal onto the UI dispatcher
///     via <see cref="MarshalToUi"/> before mutating <see cref="AcarsClientState"/>.
///     This is the only safe way to update INPC properties that WPF bindings
///     observe — direct cross-thread mutation triggers binding-engine asserts.
/// </summary>
public sealed class AcarsClientService : IDisposable
{
    private readonly HttpClient _http;
    private readonly TokenStore _tokenStore;
    private readonly VamConfig _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly AcarsClientState _state;
    private readonly Dispatcher _uiDispatcher;
    private readonly ILogger<AcarsClientService> _logger;

    // Live during a heartbeat session; null between Stop and Start.
    private SimConnectClient? _sim;
    private HeartbeatService? _heartbeat;

    // Drives the SimConnect poll-loop. Disposed in StopAsync.
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    /// <summary>
    /// True between <see cref="StartAsync"/> succeeding and <see cref="StopAsync"/>
    /// returning. Used to gate Start (idempotent — second call is a no-op) and
    /// to skip teardown logic when nothing was running.
    /// </summary>
    public bool IsRunning => _heartbeat is not null;

    public AcarsClientService(
        HttpClient http,
        TokenStore tokenStore,
        VamConfig config,
        ILoggerFactory loggerFactory,
        AcarsClientState state,
        Dispatcher uiDispatcher)
    {
        _http = http;
        _tokenStore = tokenStore;
        _config = config;
        _loggerFactory = loggerFactory;
        _state = state;
        _uiDispatcher = uiDispatcher;
        _logger = loggerFactory.CreateLogger<AcarsClientService>();
    }

    /// <summary>
    /// Bring the client online: load token, connect SimConnect, spin up the
    /// heartbeat-service, start the SimConnect poll-loop. Returns true on
    /// success; false (with <see cref="AcarsClientState.StatusMessage"/>
    /// populated for the UI) if any step failed. Idempotent — a second call
    /// while already running is a no-op returning true.
    /// </summary>
    public async Task<bool> StartAsync(FlightContext flightContext)
    {
        if (IsRunning)
        {
            _logger.LogDebug("StartAsync called while already running — no-op");
            return true;
        }

        _logger.LogInformation(
            "Starting ACARS client (callsign={Callsign}, network={Network})",
            flightContext.Callsign, flightContext.Network);

        // ─── 1. Token ──────────────────────────────────────────────────
        // The TokenStore-probe in App.OnStartup already populated
        // State.HasToken, but that was at app-startup. The user could
        // have re-paired or revoked since then, so we always re-load
        // fresh here. Failing to load = clear state, surface to UI.
        var token = _tokenStore.TryLoad();
        if (token is null)
        {
            await SetStateAsync(s =>
            {
                s.ConnectionStatus = ConnectionStatus.Error;
                s.StatusMessage = "Kein Token gespeichert. Erst über die CLI pairen (Mode 1).";
                s.HasToken = false;
            });
            _logger.LogWarning("StartAsync aborted: no token in store");
            return false;
        }
        await SetStateAsync(s => { s.HasToken = true; s.ConnectionStatus = ConnectionStatus.Connecting; s.StatusMessage = "Verbinde zu MSFS …"; });

        // ─── 2. SimConnect ─────────────────────────────────────────────
        // Connect can throw COMException when MSFS isn't running
        // (typical: HRESULT 0x80040108). Surface that to the UI as a
        // friendly message rather than letting the exception bubble.
        var sim = new SimConnectClient(_loggerFactory.CreateLogger<SimConnectClient>());
        try
        {
            sim.Connect();
        }
        catch (COMException ex)
        {
            sim.Dispose();
            await SetStateAsync(s =>
            {
                s.ConnectionStatus = ConnectionStatus.Error;
                s.StatusMessage = $"SimConnect-Verbindung fehlgeschlagen (0x{ex.HResult:X8}). Läuft MSFS?";
            });
            _logger.LogError(ex, "SimConnect connect failed (HRESULT=0x{HResult:X8})", ex.HResult);
            return false;
        }
        catch (Exception ex)
        {
            sim.Dispose();
            await SetStateAsync(s =>
            {
                s.ConnectionStatus = ConnectionStatus.Error;
                s.StatusMessage = $"Unerwarteter Fehler beim SimConnect-Connect: {ex.Message}";
            });
            _logger.LogError(ex, "Unexpected error during SimConnect connect");
            return false;
        }

        // ConnectionLost fires when the SimConnect side detects a drop
        // (typically: MSFS quits while we're connected). We can't
        // recover here — we transition to Error and let the user
        // restart manually. Auto-reconnect is M5+ territory.
        sim.ConnectionLost += msg =>
        {
            _logger.LogWarning("SimConnect ConnectionLost: {Msg}", msg);
            _ = MarshalToUi(() =>
            {
                _state.ConnectionStatus = ConnectionStatus.Error;
                _state.StatusMessage = $"SimConnect-Verbindung verloren: {msg}";
            });
        };

        _sim = sim;

        // ─── 3. HeartbeatService ───────────────────────────────────────
        var heartbeat = new HeartbeatService(
            _http,
            _sim,
            token,
            flightContext,
            interval: TimeSpan.FromSeconds(_config.Heartbeat.IntervalSeconds),
            logger: _loggerFactory.CreateLogger<HeartbeatService>());

        heartbeat.HeartbeatSent += OnHeartbeatSent;
        heartbeat.HeartbeatFailed += OnHeartbeatFailed;
        heartbeat.ReAuthRequired += OnReAuthRequired;

        heartbeat.Start();
        _heartbeat = heartbeat;

        // ─── 4. SimConnect poll-loop ───────────────────────────────────
        // SimConnectClient is request/response — we have to poll
        // RequestTelemetry() on a cadence to get fresh data into
        // LatestTelemetry. The Cli does this synchronously on the main
        // thread; we run it on a background task so the UI thread
        // stays responsive. 1Hz matches the Cli's cadence.
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token));

        // Optimistic seed of the State callsign + flight-plan fields so
        // the UI has *something* before the first heartbeat-response
        // arrives (~2 s). Counters stay at 0 until then.
        await SetStateAsync(s =>
        {
            s.Callsign = flightContext.Callsign;
            s.StatusMessage = "Verbunden. Warte auf erste Heartbeat-Antwort…";
        });

        _logger.LogInformation("ACARS client started successfully");
        return true;
    }

    /// <summary>
    /// Tear down everything in reverse order: cancel poll-loop → stop heartbeat
    /// → disconnect SimConnect. Idempotent (no-op if not running). Awaits the
    /// poll-task and the heartbeat-stop so the caller knows we're fully quiet.
    /// </summary>
    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            _logger.LogDebug("StopAsync called while not running — no-op");
            return;
        }

        _logger.LogInformation("Stopping ACARS client");
        await SetStateAsync(s =>
        {
            s.ConnectionStatus = ConnectionStatus.Disconnected;
            s.StatusMessage = "Trenne Verbindung…";
        });

        // Cancel the SimConnect poll-loop first — stops new RequestTelemetry
        // calls so the heartbeat-service's outgoing buffer can drain
        // against a stable telemetry snapshot.
        _pollCts?.Cancel();
        if (_pollTask is not null)
        {
            try { await _pollTask; } catch { /* shutdown noise */ }
            _pollTask = null;
        }
        _pollCts?.Dispose();
        _pollCts = null;

        // Heartbeat next — flushes its in-flight task and stops scheduling.
        if (_heartbeat is not null)
        {
            _heartbeat.HeartbeatSent -= OnHeartbeatSent;
            _heartbeat.HeartbeatFailed -= OnHeartbeatFailed;
            _heartbeat.ReAuthRequired -= OnReAuthRequired;
            await _heartbeat.StopAsync();
            _heartbeat = null;
        }

        // SimConnect last — Disconnect releases the COM handle. After this,
        // a fresh Start needs to create a new SimConnectClient (which is
        // what we do — _sim is re-assigned in StartAsync).
        if (_sim is not null)
        {
            _sim.Disconnect();
            _sim.Dispose();
            _sim = null;
        }

        await SetStateAsync(s =>
        {
            // Don't clobber Error → Disconnected when stopping due to error
            // (e.g., ReAuthRequired). The ConnectionStatus already says
            // Error in those paths; leaving it lets the UI show the
            // detailed reason for the user.
            if (s.ConnectionStatus != ConnectionStatus.Error)
            {
                s.ConnectionStatus = ConnectionStatus.Disconnected;
                s.StatusMessage = "Getrennt.";
            }
            s.HeartbeatsQueued = 0;
        });

        _logger.LogInformation("ACARS client stopped");
    }

    // ─── Heartbeat-event handlers ────────────────────────────────────────

    private void OnHeartbeatSent(HeartbeatResponse response)
    {
        _ = MarshalToUi(() =>
        {
            _state.ConnectionStatus = ConnectionStatus.Connected;
            _state.HeartbeatsSent++;
            _state.HeartbeatsQueued = _heartbeat?.QueuedCount ?? 0;

            // Phase echo (Welle 9 / M3.7): only update when the server
            // actually returns a phase. Null-coalesce keeps the previous
            // displayed value during transient null responses (which
            // shouldn't happen post-M3.7 but defensive).
            if (response.CurrentPhase is not null)
            {
                _state.CurrentPhase = response.CurrentPhase;
            }
            if (response.CurrentPhaseEnteredAt is not null
                && DateTimeOffset.TryParse(response.CurrentPhaseEnteredAt, out var entered))
            {
                _state.PhaseEnteredAt = entered;
            }

            // Display the server-resolved aircraft identity (M3.8.1).
            // Server runs the M3.8 3-tier resolver and echoes the
            // result in the heartbeat response, mirroring the phase
            // pattern from M3.7. We prefer that resolved value so the
            // tray-app shows exactly what the live-map shows (e.g.
            // "A320 / D-ANNE") instead of the raw SimConnect ATC MODEL
            // which is often a localization token like
            // ATCCOM.AC_MODEL_A320.0.text.
            //
            // Fallback to LatestTelemetry?.AtcModel for resilience: if
            // we ever talk to a pre-M3.8.1 server (older deploy, dev
            // branch), the response won't include aircraftType — better
            // to show the raw ATC MODEL than nothing at all.
            var resolvedType = response.AircraftType
                ?? _sim?.LatestTelemetry?.AtcModel;
            if (!string.IsNullOrWhiteSpace(resolvedType))
            {
                _state.AircraftType = resolvedType.Length > 24
                    ? resolvedType[..24] + "…"
                    : resolvedType;
            }
            // Registration: echoed verbatim by the server. May be null
            // for free-flight pilots without a tail number set; that
            // null propagates to the UI which renders an em-dash.
            _state.AircraftRegistration = response.AircraftRegistration;

            _state.StatusMessage =
                $"Heartbeat OK · sent {_state.HeartbeatsSent} / failed {_state.HeartbeatsFailed} / queued {_state.HeartbeatsQueued}";
        });
    }

    private void OnHeartbeatFailed(string reason)
    {
        _ = MarshalToUi(() =>
        {
            _state.HeartbeatsFailed++;
            _state.HeartbeatsQueued = _heartbeat?.QueuedCount ?? 0;
            // Don't flip to Error on every transient failure (e.g., a
            // 1-second outage gets retried automatically by Core's queue).
            // Status stays "Connecting" so the UI can show the yellow
            // pill while we wait for the queue to drain.
            if (_state.ConnectionStatus == ConnectionStatus.Connected)
            {
                _state.ConnectionStatus = ConnectionStatus.Connecting;
            }
            _state.StatusMessage = $"Heartbeat fehlgeschlagen: {reason}";
        });
    }

    private void OnReAuthRequired()
    {
        _logger.LogWarning("ReAuthRequired event received — token rejected by server");
        // Clear stored token so the next Start sees no token and surfaces
        // the "pair again" message. We deliberately don't auto-trigger
        // a re-pair flow from here — pairing requires an interactive code
        // entry which the tray-app doesn't have a UI for yet.
        try { _tokenStore.Clear(); } catch (Exception ex) { _logger.LogWarning(ex, "TokenStore.Clear failed"); }

        _ = MarshalToUi(() =>
        {
            _state.HasToken = false;
            _state.ConnectionStatus = ConnectionStatus.Error;
            _state.StatusMessage = "Token abgelehnt (401). Bitte erneut über die CLI pairen.";
        });

        // Fire-and-forget the stop. Caller doesn't await this — we're in
        // a heartbeat-callback already, on the heartbeat's own task.
        _ = Task.Run(StopAsync);
    }

    // ─── SimConnect poll-loop ────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        _logger.LogDebug("SimConnect poll-loop started");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _sim?.RequestTelemetry();
            }
            catch (Exception ex)
            {
                // RequestTelemetry shouldn't normally throw, but defensively
                // log and continue — losing one tick is better than killing
                // the whole loop. ConnectionLost fires its own event if the
                // underlying COM state is bad.
                _logger.LogWarning(ex, "RequestTelemetry threw");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { break; }
        }
        _logger.LogDebug("SimConnect poll-loop stopped");
    }

    // ─── Dispatcher helpers ──────────────────────────────────────────────

    /// <summary>
    /// Fire-and-forget UI mutation. Returns the operation Task so callers
    /// can optionally await — most paths here don't, since we're inside
    /// background callbacks where we just want the UI to update eventually.
    /// </summary>
    private DispatcherOperation MarshalToUi(Action mutate) =>
        _uiDispatcher.InvokeAsync(mutate);

    /// <summary>
    /// Awaitable version for sequential setup paths (StartAsync / StopAsync)
    /// where we DO want to wait until the state-change has actually been
    /// applied before continuing.
    /// </summary>
    private Task SetStateAsync(Action<AcarsClientState> mutate) =>
        _uiDispatcher.InvokeAsync(() => mutate(_state)).Task;

    public void Dispose()
    {
        // Best-effort synchronous-ish teardown for App.OnExit. We can't
        // await StopAsync from a Dispose, so we kick off the cancel and
        // dispose immediately. The HeartbeatService's internal task may
        // outlive us briefly — that's acceptable at process-exit because
        // the OS reaps it within milliseconds.
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _sim?.Dispose();
    }
}
