using System.Diagnostics;
using System.Media;
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

    /// <summary>
    /// Crash-recovery marker store (option #13). Owned by this service
    /// because the marker's write/clear lifecycle is tightly coupled
    /// to StartAsync/StopAsync — having a separate component manage it
    /// would just shuffle responsibility for the exact same calls.
    /// Constructed once in the ctor so each Start doesn't re-allocate.
    /// </summary>
    private readonly SessionMarkerStore _sessionMarkerStore;

    // Live during a heartbeat session; null between Stop and Start.
    private SimConnectClient? _sim;
    private HeartbeatService? _heartbeat;

    // Drives the SimConnect poll-loop. Disposed in StopAsync.
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    // ─── Telemetry-tab UI poll (Welle A — option A1) ────────────────────
    //
    // DispatcherTimer (UI-thread, 2s cadence) reads telemetry-stats from
    // HeartbeatService (LastLatencyMs, AverageLatencyMs5Min, FailureRate-
    // Percent5Min, NetworkHealthState) and writes them onto AcarsClientState.
    //
    // Why a separate timer rather than updating on every HeartbeatSent
    // event: NetworkHealthState transitions need to fire even when NO
    // heartbeats are flowing (network died → "Offline" after 2 min). An
    // event-driven approach would leave the UI stuck on the last-seen
    // good state. The 2s tick is cheap enough that polling is fine, and
    // it matches the heartbeat-cadence so the user sees ~1 update per
    // heartbeat in steady state.
    //
    // Started after heartbeat.Start() succeeds, stopped/disposed at the
    // top of StopAsync's teardown sequence (before the heartbeat itself
    // is torn down — the timer reads from _heartbeat so it must stop
    // first to avoid a null-deref).
    private DispatcherTimer? _telemetryTimer;

    // ─── Network-recovered flash (Welle A — option A2) ──────────────────
    //
    // Tracks the prior NetworkHealthState string so OnTelemetryTick can
    // detect an Offline → Online transition and trigger a transient
    // "Verbindung wiederhergestellt" banner. The _recoveryFlashTimer is
    // a one-shot 5-second timer that clears the flag after the banner
    // has been visible long enough to register.
    //
    // We use a string-comparison rather than the previous-value being a
    // typed enum because HeartbeatService.NetworkHealthState returns a
    // plain string ("Online" / "Degraded" / "Offline" / "Unknown") —
    // duplicating that as an enum here would just add a mapping
    // boundary without buying type-safety beyond the four legal values.
    //
    // Why detect on Offline → Online (not Degraded → Online): the flash
    // is supposed to be a celebration of recovery from a real outage,
    // not a debounce of routine state-jitter. A flap from Degraded to
    // Online and back happens during normal network jitter; flashing
    // each transition would be noise. Offline is a clear 2-min silence
    // or >50% failure-rate floor, so the flash is rare and meaningful.
    private string? _previousNetworkHealthState;
    private DispatcherTimer? _recoveryFlashTimer;

    // ─── Sim-quit fast detection (Welle A — option A6) ──────────────────
    //
    // SimConnect's built-in ConnectionLost event fires when the SDK-level
    // pipe is torn down, but in practice that detection can lag the
    // actual MSFS process death by 10-30 seconds depending on how the
    // sim exits (clean File→Exit vs hard crash vs task-kill). The poll-
    // loop's RequestTelemetry calls also keep silently succeeding into
    // a dead pipe for several seconds.
    //
    // A6 adds a proactive process-watch: every 5 seconds, the poll-loop
    // checks whether any known simulator process is alive via
    // Process.GetProcessesByName. When _simProcessWasRunning flips from
    // true → false, we proactively transition to Error state with a
    // clear "MSFS beendet" message — typically within 5 seconds of the
    // process actually disappearing, well before SimConnect notices.
    //
    // Tracking semantics:
    //   - null = haven't checked yet (first tick) OR sim was never seen
    //   - true = we observed the sim process at least once recently
    //   - false = we observed the sim was running before, now it's gone
    //     (only reached via the true → false transition)
    //
    // Why nullable instead of a plain bool: distinguishes "first tick,
    // sim was already missing" (we shouldn't flip to Error — the user
    // may have started Verbinden before MSFS, see SimConnect's connect-
    // path which already handles that case) from "we saw the sim, now
    // it's gone" (THE case A6 cares about).
    //
    // Reset to null in StopAsync so the next Verbinden starts clean
    // without inheriting state from the previous session.
    private bool? _simProcessWasRunning;

    /// <summary>
    /// Known simulator process names (without .exe extension — matches
    /// what <see cref="Process.GetProcessesByName"/> expects). Covers the
    /// SimConnect-speaking sims we currently support; X-Plane is excluded
    /// because it doesn't use SimConnect.
    ///
    /// We match ANY of these as "sim is running" — a pilot switching
    /// between MSFS 2020 and MSFS 2024 mid-session shouldn't trigger a
    /// false-positive Error flip just because the specific exe changed.
    /// (The underlying SimConnect ConnectionLost would catch that
    /// transition separately if it actually broke the pipe.)
    /// </summary>
    private static readonly string[] SimProcessNames =
    {
        "FlightSimulator",       // MSFS 2020 (and MSFS 2024 in some configurations)
        "FlightSimulator2024",   // MSFS 2024 explicit
        "fsx",                   // FSX / FSX:SE
        "Prepar3D",              // P3D v4/v5
    };

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
        _sessionMarkerStore = new SessionMarkerStore(config);
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
        heartbeat.AircraftSubstitutionDelivered += OnAircraftSubstitutionDelivered;

        // ─── B4 P2C: stage pending aircraft-substitution disposition ──
        //
        // If the pre-connect dialog flow set State.Pending* (because the
        // pilot dispositioned a booked-vs-flown aircraft mismatch),
        // forward the snapshot to the freshly-constructed HeartbeatService
        // BEFORE heartbeat.Start() fires the first BuildHeartbeat call.
        // Doing it after Start is racy: a heartbeat could be built and
        // sent without the block.
        //
        // The state is mutated from the UI thread (MainWindow's connect
        // handler), and we read it here on the calling thread; both
        // accesses are serialized by C#'s memory model for the field
        // reads (each property is a simple field load). No SetStateAsync
        // round-trip is needed for a read.
        //
        // Null-check on intent gates the staging; the other three fields
        // get safe defaults so a malformed state (intent set but booked/
        // flown not) still produces a well-formed payload rather than a
        // NullReferenceException — server-side validation will reject
        // empty type strings if it ever happens.
        if (!string.IsNullOrWhiteSpace(_state.PendingSubstitutionIntent))
        {
            var sub = new HeartbeatAircraftSubstitution
            {
                Intent = _state.PendingSubstitutionIntent!,
                BookedAircraftType = _state.PendingSubstitutionBookedType ?? "",
                FlownAircraftType = _state.PendingSubstitutionFlownType ?? "",
                Reason = _state.PendingSubstitutionReason,
            };
            heartbeat.SetPendingAircraftSubstitution(sub);
            _logger.LogInformation(
                "B4 P2C: staged aircraft-substitution disposition on heartbeat (intent={Intent}, booked={Booked}, flown={Flown}, reason={Reason})",
                sub.Intent, sub.BookedAircraftType, sub.FlownAircraftType,
                sub.Reason ?? "(none)");
        }

        heartbeat.Start();
        _heartbeat = heartbeat;

        // ─── Telemetry-tab UI poll start (Welle A — option A1) ─────────
        // Start the 2s DispatcherTimer that reads HeartbeatService's
        // telemetry getters and writes them onto AcarsClientState. The
        // first tick fires after the interval (2s), so the UI shows
        // "0 ms / Unknown" briefly until the first poll completes —
        // acceptable since no heartbeats will have landed in that window
        // either.
        _telemetryTimer = new DispatcherTimer(DispatcherPriority.Background, _uiDispatcher)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _telemetryTimer.Tick += OnTelemetryTick;
        _telemetryTimer.Start();

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

        // ─── 5. Crash-recovery marker (option #13) ─────────────────────
        // Write the session marker AFTER everything else has succeeded
        // so a partially-failed Start (e.g., heartbeat-service threw
        // post-construction) doesn't leave a recovery breadcrumb for a
        // session that never actually started. Any IO failure here is
        // logged but doesn't abort the connect — losing crash-recovery
        // is annoying, but not worth aborting a flight that's otherwise
        // ready to go.
        try
        {
            var marker = SessionMarker.FromFlightContext(flightContext, DateTimeOffset.UtcNow);
            _sessionMarkerStore.Save(marker);
            _logger.LogDebug(
                "Session marker written for crash-recovery (callsign={Callsign})",
                flightContext.Callsign);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SessionMarkerStore.Save failed — crash recovery will be unavailable");
        }

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
            // Stop telemetry-timer FIRST. Its Tick handler reads from
            // _heartbeat (telemetry getters); leaving it running while
            // we null out _heartbeat below would risk a tick firing
            // mid-disposal. Stop() blocks no further ticks, the
            // detach + null-out then dispose without race.
            if (_telemetryTimer is not null)
            {
                _telemetryTimer.Stop();
                _telemetryTimer.Tick -= OnTelemetryTick;
                _telemetryTimer = null;
            }

            // A2 recovery-flash cleanup. If a flash is in progress when
            // the user disconnects, we want to clear it cleanly rather
            // than leaving a green banner showing for a session that's
            // already gone. Reset _previousNetworkHealthState too so
            // the next Verbinden starts clean and doesn't accidentally
            // detect a phantom "Offline → Online" transition based on
            // a stale value carried over from the previous session.
            if (_recoveryFlashTimer is not null)
            {
                _recoveryFlashTimer.Stop();
                _recoveryFlashTimer = null;
            }
            _previousNetworkHealthState = null;

            // A6 reset: clear sim-process-watch state so a fresh Start
            // doesn't inherit "we saw the sim was running" from the
            // previous session — even though Disconnect → Verbinden
            // typically happens with the sim still alive, the next
            // session re-establishes the observation from scratch and
            // never compares against pre-Stop state.
            _simProcessWasRunning = null;

            _heartbeat.HeartbeatSent -= OnHeartbeatSent;
            _heartbeat.HeartbeatFailed -= OnHeartbeatFailed;
            _heartbeat.ReAuthRequired -= OnReAuthRequired;
            _heartbeat.AircraftSubstitutionDelivered -= OnAircraftSubstitutionDelivered;
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

            // Telemetry-tab reset (Welle A — option A1). The four metrics
            // would otherwise stay frozen at the last-seen values from
            // before disconnect, misleading the user. Reset to defaults
            // so a fresh Verbinden starts clean.
            s.LastLatencyMs = 0;
            s.AverageLatencyMs5Min = 0;
            s.FailureRatePercent5Min = 0;
            s.NetworkHealthState = "Unknown";

            // A2 recovery-UX reset. TimeSinceLastOutcomeSec would stay
            // pinned at whatever-value-when-stopped (often a large
            // number if disconnect happened during an outage). Recovery
            // flash needs to be cleared in case Stop landed inside the
            // 5-second visibility window — otherwise the banner would
            // outlive the session.
            s.TimeSinceLastOutcomeSec = 0;
            s.NetworkRecoveredFlash = false;

            // Pre-flight checklist reset (option #10). Each Trennen →
            // Verbinden cycle should require a fresh tick-through —
            // that's the whole point of the gate. We reset on Stop
            // (rather than at the start of the next Start) so the UI
            // visibly clears the moment the session ends, not on the
            // next user click.
            s.ResetPreflightChecklist();

            // Welle B / B4 phase 2A — booking + substitution reset.
            // ActiveBooking* is the snapshot from the previous session's
            // pre-connect fetch; carrying it into a fresh Verbinden cycle
            // would briefly show a stale booking before the new fetch
            // refreshes it. Pending* would be even worse — a heartbeat
            // sent right after Verbinden could resurface a stale
            // substitution disposition that no longer applies (e.g., the
            // user fixed the loaded aircraft between sessions). Both
            // clears happen on the UI thread inside the same SetStateAsync
            // block as the other resets, so the UI sees one atomic
            // "back to clean" transition.
            s.ClearActiveBooking();
            s.ClearPendingSubstitution();
        });

        // ─── Crash-recovery marker cleanup (option #13) ────────────────
        // Delete the session marker AFTER the full teardown ran cleanly.
        // The marker's purpose is to flag "the previous session ended
        // without StopAsync running" — so as long as we got here, by
        // definition the session ended cleanly and the marker should go.
        // Idempotent: missing-file is the success case for Clear().
        // IO failure here is logged but not surfaced — the recovery
        // banner on next launch is a minor UX nuisance, not worth
        // failing the disconnect over.
        try
        {
            _sessionMarkerStore.Clear();
            _logger.LogDebug("Session marker cleared on clean Stop");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SessionMarkerStore.Clear failed — recovery banner may show on next launch");
        }

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

            // Audio cue on phase change (option #5). Server flips
            // PhaseChanged only on the heartbeat that detected the
            // transition, so this fires exactly once per phase
            // entrance — never on steady-state heartbeats. Gated on
            // AudioCueEnabled so the default-off install is silent
            // until the pilot opts in via EINSTELLUNGEN.
            if (response.PhaseChanged && _state.AudioCueEnabled)
            {
                PlayPhaseSound(response.CurrentPhase);
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

    /// <summary>
    /// Welle B / B4 phase 2C — handler for HeartbeatService's
    /// <see cref="HeartbeatService.AircraftSubstitutionDelivered"/> event.
    ///
    /// Fires once after the first heartbeat carrying the
    /// `aircraftSubstitution` block has been successfully ACKed by the
    /// server. The HeartbeatService has already atomically cleared its
    /// internal `_pendingAircraftSubstitution` field via Interlocked.
    /// CompareExchange; we mirror that on the UI side by clearing the
    /// four State.Pending* fields so:
    ///   - the UI no longer shows "Substitution wird gesendet" hints
    ///   - a subsequent Trennen → Verbinden cycle starts clean
    ///   - a re-staging from the dialog has well-defined initial state
    ///
    /// MarshalToUi because the event fires on the heartbeat-service's
    /// background task; direct State mutation would breach the WPF
    /// thread-affinity contract for INPC properties.
    ///
    /// Fire-and-forget marshal: we don't need to await the dispatcher
    /// invocation. The event is one-shot per dialog interaction; even
    /// if a tear-down race nulls _heartbeat between the event firing
    /// and the dispatcher running, ClearPendingSubstitution is safe
    /// (it's a pure state-mutation with no service-side dependencies).
    /// </summary>
    private void OnAircraftSubstitutionDelivered()
    {
        _logger.LogInformation(
            "B4 P2C: aircraft-substitution delivered — clearing State.Pending*");
        _ = MarshalToUi(() => _state.ClearPendingSubstitution());
    }

    /// <summary>
    /// DispatcherTimer tick handler for the telemetry-tab (Welle A — option A1).
    /// Reads four cheap getters from <see cref="HeartbeatService"/> and writes
    /// the snapshot onto <see cref="AcarsClientState"/>. Runs on the UI thread
    /// (DispatcherTimer guarantees that), so direct property assignment is safe
    /// without MarshalToUi.
    ///
    /// Defensive null-guard on _heartbeat: even though we stop the timer before
    /// nulling _heartbeat in StopAsync, there's a tiny race where a tick can be
    /// queued just as Stop() runs. The null-check makes that race a no-op
    /// rather than a NullReferenceException in the dispatcher.
    /// </summary>
    private void OnTelemetryTick(object? sender, EventArgs e)
    {
        var hb = _heartbeat;
        if (hb is null) return;

        _state.LastLatencyMs = hb.LastLatencyMs;
        _state.AverageLatencyMs5Min = hb.AverageLatencyMs5Min;
        _state.FailureRatePercent5Min = hb.FailureRatePercent5Min;

        // A2: write seconds-since-last-outcome (clamped). Huge values
        // (long.MaxValue = pre-first-heartbeat) get clamped to 0 so the
        // UI doesn't display nonsense like "9223372036854775807 s".
        var msSinceLast = hb.TimeSinceLastOutcomeMs;
        _state.TimeSinceLastOutcomeSec = msSinceLast >= long.MaxValue / 1000
            ? 0
            : msSinceLast / 1000;

        // A2 recovery-flash: detect Offline → Online transition. The
        // current state is read from hb directly (single read) and
        // compared against the previous tick's value. Note we update
        // _previousNetworkHealthState at the end of this method so the
        // comparison uses the value-from-last-tick.
        var currentHealth = hb.NetworkHealthState;
        _state.NetworkHealthState = currentHealth;

        if (_previousNetworkHealthState == "Offline" && currentHealth == "Online")
        {
            _logger.LogInformation("Network recovered (Offline → Online), flashing banner for 5s");
            _state.NetworkRecoveredFlash = true;

            // Replace any in-flight recovery-timer rather than stacking
            // them. Multiple Offline → Online flips in quick succession
            // (which shouldn't happen normally given the 2-min Offline
            // threshold, but defensive) just reset the 5-second window.
            _recoveryFlashTimer?.Stop();
            _recoveryFlashTimer = new DispatcherTimer(DispatcherPriority.Background, _uiDispatcher)
            {
                Interval = TimeSpan.FromSeconds(5),
            };
            _recoveryFlashTimer.Tick += (_, _) =>
            {
                _state.NetworkRecoveredFlash = false;
                _recoveryFlashTimer?.Stop();
                _recoveryFlashTimer = null;
            };
            _recoveryFlashTimer.Start();
        }

        _previousNetworkHealthState = currentHealth;
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

    /// <summary>
    /// One-shot SimConnect probe to detect the currently loaded aircraft
    /// (option #11). Lets the pre-flight UI show "this is the aircraft
    /// MSFS has loaded right now" without requiring the user to click
    /// Verbinden first — closes a real gap where the pilot only finds
    /// out they're in the wrong airframe after heartbeats start flowing.
    ///
    /// # Behaviour
    ///
    /// - <see cref="IsRunning"/>=true → returns the live telemetry
    ///   immediately (no second SimConnect connection; the running
    ///   session already has fresh data).
    /// - <see cref="IsRunning"/>=false → opens a temporary
    ///   <see cref="SimConnectClient"/>, requests telemetry, waits up
    ///   to <paramref name="timeout"/> for the first reply, then
    ///   disposes. Returns null on COMException (MSFS not running),
    ///   timeout, or any other error.
    ///
    /// The probe runs on a Task.Run worker — Connect() can block for
    /// a few hundred ms during the SimConnect handshake, and we don't
    /// want that on the UI thread. State mutations from the result are
    /// the caller's responsibility (App.ProbeSimAsync wraps the call
    /// and pushes results into <see cref="AcarsClientState"/>).
    ///
    /// # Why not run continuously
    ///
    /// We considered a permanent low-frequency probe-loop that pre-
    /// populates DetectedAircraft* whenever MSFS is open, regardless of
    /// the pilot's intent. Decided against it: SimConnect connections
    /// have observable cost (one entry in MSFS's diagnostics, a small
    /// memory footprint), and a tray-app shouldn't squat on the SDK
    /// when the user hasn't expressed intent to fly. On-demand keeps
    /// the contract clean: tray is idle until the user clicks something.
    /// </summary>
    public async Task<(string? Type, string? Registration, string? Title, string? SimulatorName, string? SimulatorVersion)?> ProbeAircraftAsync(TimeSpan? timeout = null)
    {
        var deadline = timeout ?? TimeSpan.FromSeconds(5);

        // Live-session shortcut. _sim is non-null while the heartbeat
        // service is active; LatestTelemetry may still be null in the
        // first second after Start (poll-loop hasn't fired yet) — in
        // that case fall through to a fresh probe rather than returning
        // null, since the user explicitly asked for current state.
        if (IsRunning && _sim?.LatestTelemetry is { } liveTele)
        {
            _logger.LogDebug("ProbeAircraftAsync: returning live telemetry from running session");
            return (liveTele.AircraftType, liveTele.AircraftRegistration, liveTele.AircraftTitle,
                _sim.SimulatorName, _sim.SimulatorVersion);
        }

        // Standalone probe. Run the connect + wait + dispose on a
        // background task so the UI thread isn't blocked.
        return await Task.Run(() =>
        {
            SimConnectClient? probe = null;
            try
            {
                probe = new SimConnectClient(_loggerFactory.CreateLogger<SimConnectClient>());
                probe.Connect();

                // SimConnect's RequestTelemetry is fire-and-forget; the
                // reply lands on the pump-thread which writes to
                // LatestTelemetry. We poll here with a short interval
                // rather than wiring up the TelemetryReceived event —
                // simpler control flow, and the worst-case latency is
                // a single polling-interval (50ms).
                var stopAt = DateTimeOffset.UtcNow + deadline;
                probe.RequestTelemetry();

                while (DateTimeOffset.UtcNow < stopAt)
                {
                    if (probe.LatestTelemetry is { } tele)
                    {
                        _logger.LogInformation(
                            "ProbeAircraftAsync detected aircraft: type={Type}, reg={Reg}, title={Title}, sim={Sim}",
                            tele.AircraftType, tele.AircraftRegistration, tele.AircraftTitle, probe.SimulatorName);
                        return ((string? Type, string? Registration, string? Title, string? SimulatorName, string? SimulatorVersion)?)
                            (tele.AircraftType, tele.AircraftRegistration, tele.AircraftTitle,
                             probe.SimulatorName, probe.SimulatorVersion);
                    }
                    Thread.Sleep(50);
                }

                _logger.LogWarning("ProbeAircraftAsync timed out waiting for telemetry after {Ms}ms",
                    (int)deadline.TotalMilliseconds);
                return null;
            }
            catch (COMException ex)
            {
                _logger.LogInformation(
                    "ProbeAircraftAsync: SimConnect refused (HRESULT=0x{HResult:X8}) — MSFS likely not running",
                    ex.HResult);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ProbeAircraftAsync failed unexpectedly");
                return null;
            }
            finally
            {
                try { probe?.Disconnect(); } catch { /* swallow shutdown noise */ }
                probe?.Dispose();
            }
        });
    }

    /// <summary>
    /// Welle B / B4 phase 2 — fetch the user's currently-active booking
    /// via <see cref="ActiveBookingService"/> and project the result onto
    /// <see cref="AcarsClientState"/>'s ActiveBooking* properties. Returns
    /// true on success (including the "no active booking" case), false on
    /// any failure path (no token, 401, transport-error).
    ///
    /// Companion to <see cref="ProbeAircraftAsync"/> for the pre-connect
    /// flow: the tray surfaces a booking-aware mismatch dialog when the
    /// booked aircraft type and the sim-loaded aircraft differ. This
    /// method is the data-fetch half; the comparison + dialog live in
    /// MainWindow (B4 P2B).
    ///
    /// # Error semantics
    ///
    /// - No token in store → returns false, logs a warning, leaves state
    ///   untouched. Caller should not have invoked us in this state
    ///   (the pairing-pill gates the UI button), but we defend
    ///   gracefully anyway.
    /// - 401 from server → returns false, clears ActiveBooking* state.
    ///   We deliberately do NOT trigger the re-pair flow here — the
    ///   heartbeat path owns that, and a 401 on this pre-connect probe
    ///   is rare enough that we'd rather surface "couldn't load booking,
    ///   try Connect anyway" than launch a re-pair flow off a side query.
    /// - Transport failure (network, 5xx, timeout) → returns false, logs
    ///   the exception, leaves state untouched. Caller is free to try
    ///   Connect anyway — the heartbeat path has its own retry/queue
    ///   plumbing that handles network sickness.
    /// - 2xx with booking=null → returns true, clears ActiveBooking*
    ///   state. This is a NORMAL state (free flight, no booking).
    /// - 2xx with booking populated → returns true, projects the
    ///   response onto ActiveBooking* properties.
    ///
    /// # Threading
    ///
    /// HTTP call runs on the calling thread (HttpClient is async-ready);
    /// state mutations marshal via <see cref="SetStateAsync"/> so WPF
    /// bindings see the updates on the UI dispatcher. The
    /// IsFetchingActiveBooking flag is set true before the await and
    /// false in finally, so the UI's "lädt Buchung…" hint correctly
    /// brackets the actual network IO.
    ///
    /// # Why this method lives on AcarsClientService
    ///
    /// The Core ActiveBookingService is the bare HTTP/JSON binding —
    /// re-usable from Cli, future bot integrations, tests. This wrapper
    /// adds WPF concerns (state projection, UI thread, INPC plumbing).
    /// Same pattern as ProbeAircraftAsync wrapping SimConnectClient.
    /// </summary>
    public async Task<bool> FetchActiveBookingAsync(CancellationToken ct = default)
    {
        var token = _tokenStore.TryLoad();
        if (token is null)
        {
            _logger.LogWarning("FetchActiveBookingAsync: no token in store — skipping");
            return false;
        }

        await SetStateAsync(s => s.IsFetchingActiveBooking = true);

        try
        {
            var svc = new ActiveBookingService(_http);
            var response = await svc.FetchAsync(token, ct);

            // 401 → svc returns null. Clear any stale ActiveBooking
            // state so the UI doesn't keep showing a booking the
            // server no longer agrees we own. We don't trigger re-pair
            // here — see method docstring for rationale.
            if (response is null)
            {
                _logger.LogWarning(
                    "FetchActiveBookingAsync: token rejected (401) — clearing booking state");
                await SetStateAsync(s => s.ClearActiveBooking());
                return false;
            }

            // Project onto state inside the dispatcher so all six setters
            // fire on the UI thread in one go. null-booking is a valid
            // outcome (free flight) — we clear ActiveBooking* fields so
            // the FLUG-KONTEXT card shows no booking-row.
            var booking = response.Booking;
            await SetStateAsync(s =>
            {
                if (booking is null)
                {
                    s.ClearActiveBooking();
                    return;
                }
                s.ActiveBookingId = booking.Id;
                s.ActiveBookingFlightNumber = booking.FlightNumber;
                s.ActiveBookingDepartureIcao = booking.DepartureIcao;
                s.ActiveBookingArrivalIcao = booking.ArrivalIcao;
                s.ActiveBookingAircraftType = booking.Aircraft?.Type;
                s.ActiveBookingAircraftRegistration = booking.Aircraft?.Registration;
            });

            if (booking is null)
            {
                _logger.LogInformation("FetchActiveBookingAsync: no active booking (free flight)");
            }
            else
            {
                _logger.LogInformation(
                    "FetchActiveBookingAsync: loaded booking {Id} ({Flight} {Dep}→{Arr}, aircraft={Type}/{Reg})",
                    booking.Id, booking.FlightNumber, booking.DepartureIcao, booking.ArrivalIcao,
                    booking.Aircraft?.Type ?? "(none)", booking.Aircraft?.Registration ?? "(none)");
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            // Caller-initiated cancellation (e.g., user clicked Verbinden
            // without waiting for the booking-probe). Not an error.
            _logger.LogDebug("FetchActiveBookingAsync: cancelled by caller");
            return false;
        }
        catch (Exception ex)
        {
            // Transport / parse failure. Leave state untouched — a stale
            // ActiveBooking is more useful than a cleared one if the
            // server just briefly hiccupped; the next pre-connect probe
            // will refresh it. Caller can still proceed with Connect.
            _logger.LogWarning(ex, "FetchActiveBookingAsync failed");
            return false;
        }
        finally
        {
            await SetStateAsync(s => s.IsFetchingActiveBooking = false);
        }
    }

    /// <summary>
    /// User-initiated re-pair. Tears down any active session, deletes the
    /// stored DPAPI-encrypted token, and resets HasToken so the UI shows
    /// "Nicht gepaart". The user then runs the CLI's pair command (or a
    /// future in-app pairing dialog) to redeem a fresh code.
    ///
    /// Symmetric with <see cref="OnReAuthRequired"/> — same three steps
    /// (stop → clear token → update state) — but exposed as a public
    /// awaitable for explicit user gestures, with a friendlier status
    /// message that doesn't imply server-side rejection.
    ///
    /// Idempotent: if not currently connected, StopAsync is a no-op and
    /// we just clear + update. Safe to call from a Click handler without
    /// pre-checks. Errors during TokenStore.Clear are logged but don't
    /// throw — the UI state still flips so the user sees something
    /// happened, and re-attempting a Start would surface the underlying
    /// problem (e.g. file locked) again with a clearer error path.
    /// </summary>
    public async Task UnpairAsync()
    {
        _logger.LogInformation("UnpairAsync requested by user");

        // Stop first so we're not heartbeating into the void after the
        // token is deleted. Order matters: a heartbeat in flight after
        // clear would 401, triggering OnReAuthRequired concurrently with
        // our manual update — the callback would override our friendlier
        // status with "Token abgelehnt (401)…". StopAsync first avoids
        // the gap entirely.
        await StopAsync();

        try { _tokenStore.Clear(); } catch (Exception ex) { _logger.LogWarning(ex, "TokenStore.Clear failed during unpair"); }

        await SetStateAsync(s =>
        {
            s.HasToken = false;
            s.ConnectionStatus = ConnectionStatus.Disconnected;
            s.StatusMessage = "Gerät entkoppelt. Erneut über die CLI pairen, dann verbinden.";
        });
    }

    // ─── SimConnect poll-loop ────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        _logger.LogDebug("SimConnect poll-loop started");

        // Tick counter for the A6 sim-process watch. Every 5th tick (5s
        // at the 1Hz cadence) we additionally probe Process.GetProcessesByName
        // — cheap enough that we could run it every tick, but 5s is
        // sufficient resolution for "MSFS quit" notification and keeps
        // the per-second hot-path doing only the SimConnect work it was
        // already doing.
        var tickCounter = 0;

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

            // ─── A6: sim-process watch ────────────────────────────────
            // Runs every 5th tick (5s cadence). The check itself is
            // ~5-10ms on a typical desktop — small enough to run on
            // the poll-loop thread without spinning up an extra worker.
            // All exceptions swallowed: if Process.GetProcessesByName
            // throws (rare; usually permission issues on locked-down
            // systems), we just skip this tick and try again in 5s.
            if (++tickCounter >= 5)
            {
                tickCounter = 0;
                try
                {
                    var isRunning = IsAnySimProcessRunning();
                    if (_simProcessWasRunning == true && !isRunning)
                    {
                        // The interesting transition: we previously saw
                        // the sim running, and now it's gone. Flip to
                        // Error state proactively rather than waiting
                        // for SimConnect to notice.
                        _logger.LogWarning(
                            "Sim process disappeared (proactive detection) — flipping to Error before SimConnect ConnectionLost fires");
                        _ = MarshalToUi(() =>
                        {
                            // Don't overwrite an Error state that's
                            // already in play (e.g., SimConnect's own
                            // ConnectionLost might have fired a hair
                            // earlier with a more specific message).
                            // Either signal is correct — the first one
                            // to set the message wins, which is fine
                            // for the user's mental model.
                            if (_state.ConnectionStatus != ConnectionStatus.Error)
                            {
                                _state.ConnectionStatus = ConnectionStatus.Error;
                                _state.StatusMessage =
                                    "MSFS beendet — Sim-Prozess nicht mehr erreichbar.";
                            }
                        });
                    }
                    _simProcessWasRunning = isRunning;
                }
                catch (Exception ex)
                {
                    // Process enumeration failed — just log and move on.
                    // Don't reset _simProcessWasRunning to null on
                    // failure because that would create a false-positive
                    // sim-quit detection on the next successful tick
                    // (true → null → true would skip the transition).
                    _logger.LogDebug(ex, "Sim-process check threw, skipping this tick");
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { break; }
        }
        _logger.LogDebug("SimConnect poll-loop stopped");
    }

    /// <summary>
    /// Probe whether any known simulator process is currently alive.
    /// Returns true on the first match in <see cref="SimProcessNames"/>;
    /// false if none match. Used by the A6 sim-quit-detection path in
    /// <see cref="PollLoopAsync"/>.
    ///
    /// Process.GetProcessesByName returns Process[] which we must
    /// dispose to release the underlying Win32 handle. The using-block
    /// + the inner loop dispose pattern keeps that clean even on the
    /// early-return path.
    ///
    /// We do NOT match on partial names or wildcards — exact-name only.
    /// Reduces the risk of a false positive from, say, a "FlightSimulator
    /// addon manager.exe" that's open but doesn't actually mean the sim
    /// is running.
    /// </summary>
    private static bool IsAnySimProcessRunning()
    {
        foreach (var name in SimProcessNames)
        {
            var matches = Process.GetProcessesByName(name);
            try
            {
                if (matches.Length > 0) return true;
            }
            finally
            {
                // Always dispose the Process handles, even if we found
                // a match and are returning early. Each Process instance
                // holds a Win32 handle that won't be released until the
                // GC runs without explicit Dispose.
                foreach (var p in matches) p.Dispose();
            }
        }
        return false;
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

    /// <summary>
    /// Map a phase name to its SystemSound and play it (option #5).
    /// Three phases are wired up — the rest of the state machine
    /// (Taxi, Climb, Cruise, Descent, Approach, BlockOff, BlockOn)
    /// stays silent because hitting a chime on every transition would
    /// be irritating during a normal flight. We picked the three that
    /// most pilots want sonic confirmation of: pushback (engines
    /// running, brakes off, flight has started), takeoff (wheels up),
    /// and Landed (touchdown — the "you made it" moment).
    ///
    /// SystemSounds.Asterisk / Beep / Exclamation are guaranteed to
    /// be present on every Windows install (mapped to the user's
    /// system sound scheme via the standard system-sound entries),
    /// so we don't need to ship WAV files. Trade-off: pilots can't
    /// pick custom sounds without editing the OS sound scheme. That's
    /// acceptable for v1; a future enhancement can ship per-phase WAVs
    /// in the install package.
    ///
    /// SystemSounds.X.Play() is non-blocking — the call returns
    /// immediately and the sound starts asynchronously. Safe to invoke
    /// from the UI thread inside MarshalToUi.
    /// </summary>
    private static void PlayPhaseSound(string? phase)
    {
        if (string.IsNullOrEmpty(phase)) return;
        switch (phase)
        {
            case "Pushback":
                SystemSounds.Asterisk.Play();
                break;
            case "Takeoff":
                SystemSounds.Beep.Play();
                break;
            case "Landed":
            case "Touchdown":
                SystemSounds.Exclamation.Play();
                break;
        }
    }
}
