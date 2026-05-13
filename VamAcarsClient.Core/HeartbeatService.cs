using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VamAcarsClient.Core;

/// <summary>
/// Sends heartbeats to POST /api/acars/heartbeat at a configurable
/// interval, with offline-tolerant behavior.
///
/// LIFECYCLE:
///   1. Construct with HttpClient, token, FlightContext.
///   2. Start() — begins the loop on a background task.
///   3. Per-tick: fresh telemetry → enqueue → drain queue.
///   4. Stop() — cancels the loop, drains pending if possible.
///
/// REPLAY-QUEUE BEHAVIOR:
///   - Successful tick: queue stays empty.
///   - Network-error tick: heartbeat enqueued, retry next tick. If
///     queue grows past MaxQueueDepth, oldest is dropped (logged).
///   - Server-side 4xx (other than 401): heartbeat is broken (e.g.,
///     bad payload), drop without retry. Logged.
///   - Server-side 401: token revoked. Stop the loop and signal
///     ReAuthRequired event so caller can prompt re-pair.
///   - 5xx: transient, treat like network-error.
///
/// CLOCK-DRIFT:
///   Each heartbeat has a client-side `timestamp`. Server rejects
///   outside ±5min/-1min window. We use DateTimeOffset.UtcNow when
///   building the payload — if the queue grows old, those timestamps
///   become stale and the server rejects. We don't try to "fix" old
///   timestamps; that would be replay-attack-friendly.
///
/// THREAD MODEL:
///   The send-loop runs on its own task (Task.Run). Telemetry-snapshot
///   is read via SimConnectClient.LatestTelemetry (thread-safe getter).
///   The loop is the only consumer of the queue. Producers are the
///   loop itself (enqueue current tick). Caller never enqueues.
/// </summary>
public sealed class HeartbeatService : IDisposable
{
    private readonly HttpClient _http;
    private readonly SimConnectClient _sim;
    private readonly string _token;
    private readonly FlightContext _flightContext;
    private readonly TimeSpan _interval;
    private readonly ILogger<HeartbeatService> _logger;

    private readonly ConcurrentQueue<HeartbeatRequest> _queue = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    // ─── Local block-event tracking (option #5) ─────────────────────────
    //
    // The server's M3.9 phase detector emits BLOCK_OFF / BLOCK_ON events
    // server-side from heartbeat phase transitions, and M6 fires the
    // auto-PIREP helper from those server-side events. That works but
    // the server-side path uses null payload, so generate-pirep.ts's
    // server-derived block-to-block (M7 / option #13) is the best it
    // can do — wall-clock-based, sim-rate-blind.
    //
    // Option #5 closes that gap: when the heartbeat response tells us a
    // phase transition just landed at the server, we mirror the same
    // detection locally, snapshot fuel + block-off time at PreFlight →
    // moving transitions, and emit a richer BLOCK_ON event via
    // /api/acars/event when the server reports the BlockOn transition.
    //
    // Race vs the M6 server-bridge: the server-bridge fires from the
    // SAME heartbeat that returns the response we read here, so it
    // typically wins by 30–80ms (its tx is already in flight when our
    // POST hits). The auto-PIREP helper's session-already-closed guard
    // means our POST quietly no-ops on lost races. The wins come on
    // network blips / DB lag / heavy server load where the bridge runs
    // late — our POST fills the gap and provides the rich payload.
    //
    // Why track _currentSessionId rather than re-reading from each
    // response: the server can rotate sessions (stale-cleanup, manual
    // disconnect/reconnect) and we need to reset the BLOCK_OFF snapshot
    // when that happens. Without the reset, a fresh flight would inherit
    // the previous session's block-off time and emit nonsense deltas.
    private string? _previousPhase;
    private string? _currentSessionId;
    private DateTimeOffset? _blockOffAt;
    private int? _fuelAtBlockOff;

    // ─── Telemetry-tab tracking (Welle A — option A1) ────────────────────
    //
    // Rolling-window of outcomes for the last 5 minutes. Each entry is a
    // (timestamp, isSuccess, latencyMs) tuple. The window self-trims to
    // 5 min on every read of the aggregate properties — no background
    // cleanup task needed (the queue lives in the same process as the
    // heartbeat-loop and reads are infrequent).
    //
    // Why ConcurrentQueue<(...)>: the producer is the single-flight
    // drain-loop, the consumers are public getters called from the UI
    // thread. Lock-free enqueue/dequeue is the simplest correct primitive
    // for a single-producer-multi-reader queue with FIFO semantics.
    //
    // Why also store individual latency separately: the UI wants to show
    // "last latency" prominently (most-recent-tick figure). Walking the
    // history every render to find the newest success-tuple would be
    // wasteful — _lastLatencyMs is a hot path that gets updated in O(1)
    // on every successful send.
    //
    // Window-size choice (5 min): long enough that the average smooths
    // over network jitter and short network blips, short enough that
    // when conditions improve the figures recover within the same
    // session. 60 entries at 5-sec intervals would be too small (no
    // smoothing of a single 2-sec outlier); 30 min would be too sticky
    // for a session that just started.
    private readonly ConcurrentQueue<HeartbeatOutcome> _outcomeHistory = new();
    private long _lastLatencyMs;
    private DateTimeOffset _lastOutcomeAt;

    /// <summary>
    /// Window for rolling stats (failure-rate, average-latency). Heartbeats
    /// older than this are dropped from the history on next aggregate read.
    /// </summary>
    private static readonly TimeSpan StatsWindow = TimeSpan.FromMinutes(5);

    private readonly record struct HeartbeatOutcome(
        DateTimeOffset At,
        bool Success,
        long LatencyMs);

    /// <summary>
    /// JSON-options for heartbeat serialization. Critical setting:
    /// DefaultIgnoreCondition.WhenWritingNull — Zod's `.optional()` on
    /// the server means "field absent OR undefined", NOT "field present
    /// with null". If we send `"flightRules": null`, Zod rejects with
    /// `invalid_enum_value`. By dropping null-properties from JSON
    /// entirely, optional fields disappear when they're not set —
    /// exactly what Zod expects.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>How many pending heartbeats to keep before dropping oldest.</summary>
    private const int MaxQueueDepth = 100;

    /// <summary>Fired when the server returns 401 — token revoked, app must re-pair.</summary>
    public event Action? ReAuthRequired;

    /// <summary>Fired on every successful send. Includes server's response.</summary>
    public event Action<HeartbeatResponse>? HeartbeatSent;

    /// <summary>Fired on every failed send. Param: human-readable reason.</summary>
    public event Action<string>? HeartbeatFailed;

    public HeartbeatService(
        HttpClient http,
        SimConnectClient sim,
        string token,
        FlightContext flightContext,
        TimeSpan? interval = null,
        ILogger<HeartbeatService>? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _sim = sim ?? throw new ArgumentNullException(nameof(sim));
        _token = !string.IsNullOrWhiteSpace(token)
            ? token
            : throw new ArgumentException("token required", nameof(token));
        _flightContext = flightContext ?? throw new ArgumentNullException(nameof(flightContext));
        _interval = interval ?? TimeSpan.FromSeconds(2);
        // NullLogger fallback: callers that don't pass a logger get a
        // no-op. Keeps the service usable from tests / minimal setups
        // without forcing a logger-factory just to instantiate.
        _logger = logger ?? NullLogger<HeartbeatService>.Instance;
    }

    /// <summary>Start the heartbeat loop. Idempotent.</summary>
    public void Start()
    {
        if (_loopTask is not null) return;
        _logger.LogInformation(
            "Heartbeat service starting (callsign={Callsign}, network={Network}, interval={Interval}s)",
            _flightContext.Callsign, _flightContext.Network, _interval.TotalSeconds);
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    /// <summary>Stop the loop. Awaits the current task to finish.</summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask; } catch { /* swallow shutdown errors */ }
        }
        _loopTask = null;
        _cts?.Dispose();
        _cts = null;
        _logger.LogInformation("Heartbeat service stopped.");
    }

    public int QueuedCount => _queue.Count;

    // ─── Telemetry-tab public getters (Welle A — option A1) ────────────
    //
    // These are read from the UI dispatcher on a DispatcherTimer tick, so
    // they need to be cheap. The trim-on-read pattern keeps the history
    // bounded without a background task: typical window has 150 entries
    // (5 min × 30 heartbeats/min at 2-sec cadence), Walk is O(n).
    //
    // All four getters share the same trim — calling all four in a row
    // (which the UI does once per tick) trims the queue four times in
    // sequence, which is fine because each trim is O(k) where k is the
    // number of expired entries (usually 0-2).

    /// <summary>
    /// Latency of the most recent successful heartbeat send, in ms.
    /// Returns 0 if no successful send has happened yet (fresh start
    /// or session that never reached the server).
    /// </summary>
    public long LastLatencyMs => Interlocked.Read(ref _lastLatencyMs);

    /// <summary>
    /// Average latency over successful sends in the last 5 minutes, in
    /// ms. Returns 0 if no successful sends have happened in the window.
    /// </summary>
    public long AverageLatencyMs5Min
    {
        get
        {
            TrimOutcomeHistory();
            long total = 0;
            int count = 0;
            // Snapshot via ToArray to avoid enumeration-during-modification.
            // ConcurrentQueue.ToArray is documented as a moment-in-time copy.
            foreach (var o in _outcomeHistory.ToArray())
            {
                if (o.Success)
                {
                    total += o.LatencyMs;
                    count++;
                }
            }
            return count > 0 ? total / count : 0;
        }
    }

    /// <summary>
    /// Failure-rate over the last 5 minutes, as integer percent (0-100).
    /// A failure is any outcome with Success=false (network error, 4xx
    /// payload-reject, 5xx server-error). Returns 0 if no outcomes in
    /// the window (uninitialized state, not "everything's fine").
    /// </summary>
    public int FailureRatePercent5Min
    {
        get
        {
            TrimOutcomeHistory();
            int total = 0;
            int failed = 0;
            foreach (var o in _outcomeHistory.ToArray())
            {
                total++;
                if (!o.Success) failed++;
            }
            return total > 0 ? (failed * 100) / total : 0;
        }
    }

    /// <summary>
    /// Coarse network-health classification derived from the same
    /// rolling window. Three buckets, chosen for UI legibility (one
    /// color per state rather than a percentage that pilots would have
    /// to interpret mid-flight):
    ///
    /// - "Online":   ≤10% failure-rate AND last outcome within 30s
    /// - "Degraded": 10-50% failure-rate OR last outcome 30s-2min ago
    /// - "Offline":  >50% failure-rate OR last outcome > 2min ago
    ///
    /// "Unknown" is returned if no outcomes have been recorded yet.
    /// </summary>
    public string NetworkHealthState
    {
        get
        {
            TrimOutcomeHistory();
            if (_outcomeHistory.IsEmpty) return "Unknown";

            var failureRate = FailureRatePercent5Min;
            var sinceLast = DateTimeOffset.UtcNow - _lastOutcomeAt;

            if (sinceLast > TimeSpan.FromMinutes(2)) return "Offline";
            if (sinceLast > TimeSpan.FromSeconds(30)) return "Degraded";
            if (failureRate > 50) return "Offline";
            if (failureRate > 10) return "Degraded";
            return "Online";
        }
    }

    /// <summary>
    /// Record an outcome for the rolling window. Called from
    /// DrainQueueAsync on every send-attempt (success or failure).
    /// Thread-safe via ConcurrentQueue.Enqueue.
    /// </summary>
    private void RecordOutcome(bool success, long latencyMs)
    {
        var now = DateTimeOffset.UtcNow;
        _outcomeHistory.Enqueue(new HeartbeatOutcome(now, success, latencyMs));
        _lastOutcomeAt = now;
        if (success)
        {
            // Interlocked-write so the LastLatencyMs getter (which uses
            // Interlocked.Read) sees a consistent value rather than a
            // torn read on 32-bit platforms. .NET doesn't guarantee
            // atomicity for plain long-writes outside 64-bit.
            Interlocked.Exchange(ref _lastLatencyMs, latencyMs);
        }
    }

    /// <summary>
    /// Drop outcomes older than StatsWindow. Called from each public
    /// getter so the window stays self-cleaning. Single-flight on the
    /// dequeue side because all readers happen on the UI dispatcher,
    /// but the underlying ConcurrentQueue is safe even if not.
    /// </summary>
    private void TrimOutcomeHistory()
    {
        var cutoff = DateTimeOffset.UtcNow - StatsWindow;
        while (_outcomeHistory.TryPeek(out var oldest) && oldest.At < cutoff)
        {
            _outcomeHistory.TryDequeue(out _);
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Build heartbeat from current telemetry. If sim isn't
            // delivering yet, skip this tick — no point sending zeros.
            var telemetry = _sim.LatestTelemetry;
            if (telemetry is { } t)
            {
                EnqueueHeartbeat(t);
            }

            // Drain queue. We send oldest-first so live-map progression
            // makes temporal sense if a flush happens after a re-connect.
            await DrainQueueAsync(ct);

            try
            {
                await Task.Delay(_interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void EnqueueHeartbeat(SimTelemetry t)
    {
        var hb = BuildHeartbeat(t, DateTimeOffset.UtcNow);
        _queue.Enqueue(hb);

        // Bound the queue. Drop oldest when over cap. This is a fast
        // path — TryDequeue is O(1) on ConcurrentQueue.
        while (_queue.Count > MaxQueueDepth && _queue.TryDequeue(out _))
        {
            _logger.LogWarning(
                "Heartbeat queue at cap ({Cap}) — dropping oldest entry.",
                MaxQueueDepth);
            HeartbeatFailed?.Invoke($"Queue voll — ältester Heartbeat verworfen.");
        }
    }

    private async Task DrainQueueAsync(CancellationToken ct)
    {
        // Single-flight: send one at a time so 401 handling stays clean.
        while (!ct.IsCancellationRequested && _queue.TryPeek(out var hb))
        {
            HttpResponseMessage response;
            // Stopwatch wraps the entire SendAsync — DNS, TLS-handshake,
            // request-build, network-roundtrip, server-processing, response.
            // What we record as "latency" is the user-observable end-to-end
            // time, which is what matters for telemetry-tab UX. If only
            // the network-roundtrip was interesting, we'd hook into
            // HttpClientHandler events, but this hot-path metric is fine.
            var sw = Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "/api/acars/heartbeat")
                {
                    Content = JsonContent.Create(hb, options: JsonOpts),
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

                response = await _http.SendAsync(req, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Network / DNS / timeout. Stop draining; retry next tick.
                // Logged at Warning — recoverable, not a programmer error.
                sw.Stop();
                RecordOutcome(success: false, latencyMs: sw.ElapsedMilliseconds);
                _logger.LogWarning(ex,
                    "Heartbeat send failed (network). Queued={QueuedCount}. Will retry.",
                    _queue.Count);
                HeartbeatFailed?.Invoke($"Network-Fehler: {ex.Message}");
                return;
            }
            sw.Stop();

            if (response.IsSuccessStatusCode)
            {
                RecordOutcome(success: true, latencyMs: sw.ElapsedMilliseconds);
                _queue.TryDequeue(out _);
                try
                {
                    var body = await response.Content.ReadFromJsonAsync<HeartbeatResponse>(ct);
                    if (body is not null)
                    {
                        _logger.LogDebug(
                            "Heartbeat sent OK. SessionId={SessionId}",
                            body.SessionId);

                        // Log phase transitions explicitly. Useful in the
                        // file-log to pinpoint exactly when the server's
                        // state-machine ticked from one phase to the next
                        // — particularly handy for tuning Welle 9's
                        // detection thresholds.
                        if (body.PhaseChanged && body.CurrentPhase is not null)
                        {
                            _logger.LogInformation(
                                "Phase change: {Phase} (source={Source}, sessionId={SessionId})",
                                body.CurrentPhase, body.PhaseSource, body.SessionId);
                        }

                        // Option #5: track block-events locally for richer
                        // BLOCK_ON event emission. Mirrors what the server's
                        // M3.9 phase detector does, but lets us snapshot the
                        // sim-truth fuel-at-block-off and emit a rich payload
                        // when BlockOn fires. See _previousPhase docstring
                        // above for why this lives here rather than server-
                        // side. Runs unconditionally — TrackBlockEvents is
                        // a no-op when nothing relevant happened.
                        TrackBlockEvents(body, hb);

                        HeartbeatSent?.Invoke(body);
                    }
                }
                catch
                {
                    // Body-parse failure is non-fatal — server accepted.
                }
                response.Dispose();
                continue;
            }

            // Non-2xx: classify. Read the body BEFORE disposing the
            // response so we can surface the server's error-detail.
            var status = (int)response.StatusCode;
            string errorBody;
            try
            {
                errorBody = await response.Content.ReadAsStringAsync(ct);
            }
            catch
            {
                errorBody = "<could not read response body>";
            }
            response.Dispose();

            // Record telemetry-tab outcome ONCE for any non-2xx path. All
            // three sub-paths (401 / 5xx / 4xx-drop) count as "failure"
            // for failure-rate purposes — they're all heartbeats that
            // didn't land. The latency captured is server-roundtrip-time
            // (the server DID respond, just rejected), which is still
            // a meaningful timing signal worth recording.
            RecordOutcome(success: false, latencyMs: sw.ElapsedMilliseconds);

            if (status == 401)
            {
                _logger.LogWarning(
                    "Token rejected by server (401). Stopping heartbeat, signaling re-pair.");
                HeartbeatFailed?.Invoke("Token abgelehnt (401). Re-pair erforderlich.");
                ReAuthRequired?.Invoke();
                _cts?.Cancel(); // stop the loop
                return;
            }

            if (status >= 500)
            {
                // Server transient. Retry next tick.
                _logger.LogWarning(
                    "Server error {Status} on heartbeat. Body={ErrorBody}. Will retry.",
                    status, errorBody);
                HeartbeatFailed?.Invoke($"Server-Fehler {status}: {errorBody}. Retry.");
                return;
            }

            // 4xx other than 401: payload is broken or rate-limited. Drop
            // this entry to avoid spinning on it. Body usually contains
            // the zod-validation issues — surfacing them makes debugging
            // schema-mismatches a one-shot. Logged at Error because a
            // schema-mismatch is a client bug, not a transient condition.
            _queue.TryDequeue(out _);
            _logger.LogError(
                "Heartbeat rejected by server ({Status}). Body={ErrorBody}. Dropping entry.",
                status, errorBody);
            HeartbeatFailed?.Invoke($"Heartbeat abgelehnt ({status}): {errorBody}");
        }
    }

    /// <summary>
    /// Local mirror of the server's M3.9 phase-detector for block-event
    /// purposes (option #5). Updates the per-session tracking fields
    /// based on the heartbeat response and, on a BlockOn transition,
    /// fires off a rich BLOCK_ON event to /api/acars/event.
    ///
    /// Why pass `lastSentHeartbeat` rather than re-reading telemetry from
    /// the sim: at the moment the heartbeat-response arrives, the sim's
    /// LatestTelemetry has likely advanced a tick or two beyond what the
    /// server processed. The fuelTotalKg the server saw — which it used
    /// to make the phase-decision being reported back — is in the
    /// just-sent heartbeat. Using that keeps client and server views of
    /// "fuel at this transition" consistent.
    ///
    /// Idempotent against same-phase repeats: PhaseChanged guards the
    /// transition logic, so heartbeat #2 reporting `phaseChanged=false,
    /// currentPhase=Cruise` triggers no work even though we update
    /// _previousPhase to keep it accurate.
    /// </summary>
    private void TrackBlockEvents(HeartbeatResponse body, HeartbeatRequest lastSentHeartbeat)
    {
        // Reset on session rotation. The server can rotate sessionIds
        // when stale-cleanup expires the previous one, or when a
        // disconnect/reconnect creates a fresh session row. The phase
        // history doesn't carry across — a new session is a new flight.
        if (body.SessionId is not null && body.SessionId != _currentSessionId)
        {
            _logger.LogDebug(
                "Session rotated: {OldSessionId} -> {NewSessionId}. Resetting block-event state.",
                _currentSessionId, body.SessionId);
            _currentSessionId = body.SessionId;
            _previousPhase = null;
            _blockOffAt = null;
            _fuelAtBlockOff = null;
        }

        if (!body.PhaseChanged || body.CurrentPhase is null)
        {
            // No transition — keep _previousPhase in sync with whatever
            // the server says so the next transition compares correctly.
            // (Edge case: the very first heartbeat of a session sets
            // PhaseChanged=true; defensive update otherwise.)
            if (body.CurrentPhase is not null)
            {
                _previousPhase = body.CurrentPhase;
            }
            return;
        }

        // BLOCK_OFF detection: PreFlight → {Pushback,Taxi,Takeoff}.
        // The server's M3.9 detector emits BLOCK_OFF on this same
        // transition; we mirror it locally to capture the fuel snapshot.
        // Fuel from the just-sent heartbeat is what the server saw; it's
        // the canonical "fuel at chocks-off". Once captured we don't
        // overwrite — a later mid-flight push-pull-push would otherwise
        // reset the snapshot to a worse-than-original value.
        var movingPhases = new[] { "Pushback", "Taxi", "Takeoff" };
        if (_previousPhase == "PreFlight"
            && Array.IndexOf(movingPhases, body.CurrentPhase) >= 0
            && _blockOffAt is null)
        {
            _blockOffAt = DateTimeOffset.UtcNow;
            _fuelAtBlockOff = lastSentHeartbeat.Engine?.FuelTotalKg;
            _logger.LogInformation(
                "Local BLOCK_OFF tracked at {At} (fuel={Fuel}kg).",
                _blockOffAt, _fuelAtBlockOff);
        }

        // BLOCK_ON detection: any phase → BlockOn. Fire the event POST.
        // Done fire-and-forget on a background task so we don't block
        // the heartbeat-loop; the loop is single-flight on responses
        // and a network roundtrip here would jam its cadence.
        if (body.CurrentPhase == "BlockOn" && _currentSessionId is not null)
        {
            var sessionId = _currentSessionId;
            var blockOffAt = _blockOffAt;
            var fuelAtBlockOff = _fuelAtBlockOff;
            var fuelAtBlockOn = lastSentHeartbeat.Engine?.FuelTotalKg;

            _ = Task.Run(() => EmitBlockOnEventAsync(
                sessionId, blockOffAt, fuelAtBlockOff, fuelAtBlockOn));
        }

        _previousPhase = body.CurrentPhase;
    }

    /// <summary>
    /// POST /api/acars/event with a BLOCK_ON event carrying the locally-
    /// computed totals. Fire-and-forget — caller doesn't await. Failures
    /// are logged at Warning (non-fatal: the M6 server-bridge has
    /// already filed the PIREP from the same heartbeat, so a failed
    /// client POST just means we lost the rich-payload optimization,
    /// not the PIREP itself).
    ///
    /// Uses the same _http + _token as heartbeat sends, so authn and
    /// base-URL are identical. No retry loop: a single attempt is
    /// enough for the rich-payload optimization. If it fails, the
    /// next BLOCK_ON event in this session is unlikely (BlockOn is
    /// terminal), and the server-bridge already covered the PIREP.
    /// </summary>
    private async Task EmitBlockOnEventAsync(
        string sessionId,
        DateTimeOffset? blockOffAt,
        int? fuelAtBlockOff,
        int? fuelAtBlockOn)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;

            // totalFlightTimeMin: client-computed block-to-block. Honours
            // exactly the wall-clock between the local BLOCK_OFF snapshot
            // and now (BLOCK_ON moment). Same accuracy class as the M7
            // server-derived block-to-block (#13), but emitted as the
            // primary client-reported value, so generate-pirep.ts uses
            // it at tier-1 of the fallback chain rather than tier-2.
            int? totalFlightTimeMin = null;
            if (blockOffAt is not null)
            {
                var minutes = (int)Math.Round((now - blockOffAt.Value).TotalMinutes);
                if (minutes > 0)
                {
                    totalFlightTimeMin = minutes;
                }
            }

            // totalFuelUsedKg: BLOCK_OFF snapshot − current. Negative
            // values shouldn't happen (refuel mid-flight is rare and not
            // really a thing in MSFS) but defensive — clamp to 0 rather
            // than emit nonsense.
            int? totalFuelUsedKg = null;
            if (fuelAtBlockOff is not null && fuelAtBlockOn is not null)
            {
                var used = fuelAtBlockOff.Value - fuelAtBlockOn.Value;
                totalFuelUsedKg = used > 0 ? used : 0;
            }

            // blockTimeMin mirrors totalFlightTimeMin in this v1 — both
            // represent the chocks-off → chocks-on duration. Future
            // refinement could split (e.g., totalFlightTimeMin = takeoff
            // → touchdown excluding taxi), but for now the schema-
            // documented BlockOnPayload puts both names in scope and
            // the helper consumes the first that's present.
            var payload = new Dictionary<string, object?>();
            if (totalFlightTimeMin is not null) payload["totalFlightTimeMin"] = totalFlightTimeMin;
            if (totalFuelUsedKg is not null) payload["totalFuelUsedKg"] = totalFuelUsedKg;
            if (totalFlightTimeMin is not null) payload["blockTimeMin"] = totalFlightTimeMin;

            var eventBody = new
            {
                timestamp = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
                sessionId,
                type = "BLOCK_ON",
                payload = payload.Count > 0 ? payload : null,
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/acars/event")
            {
                Content = JsonContent.Create(eventBody, options: JsonOpts),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            // Modest timeout: the request shouldn't take long; if it
            // does, server-bridge has already won the race anyway.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await _http.SendAsync(req, cts.Token);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "BLOCK_ON event posted (sessionId={SessionId}, flightMin={FlightMin}, fuelKg={FuelKg}).",
                    sessionId, totalFlightTimeMin, totalFuelUsedKg);
            }
            else
            {
                // 409 from the helper means session-already-closed —
                // the M6 server-bridge won the race. Logged at Debug
                // because that's the expected case (~90% of events).
                // Other status codes warrant attention.
                var status = (int)response.StatusCode;
                if (status == 409)
                {
                    _logger.LogDebug(
                        "BLOCK_ON event rejected as session-already-closed (server-bridge won race). sessionId={SessionId}",
                        sessionId);
                }
                else
                {
                    string errorBody;
                    try { errorBody = await response.Content.ReadAsStringAsync(cts.Token); }
                    catch { errorBody = "<unread>"; }
                    _logger.LogWarning(
                        "BLOCK_ON event POST returned {Status}. Body={Body}",
                        status, errorBody);
                }
            }
        }
        catch (Exception ex)
        {
            // Network error / timeout / unexpected. Non-fatal: server-
            // bridge has the PIREP covered from the heartbeat side.
            _logger.LogWarning(ex,
                "BLOCK_ON event POST failed for sessionId={SessionId}. Server-bridge fallback applies.",
                sessionId);
        }
    }

    private HeartbeatRequest BuildHeartbeat(SimTelemetry t, DateTimeOffset now)
    {
        // Convert SimConnect's "doubles-as-booleans" (0.0 / 1.0) to bools.
        // SimConnect uses 0.5 as the typical threshold to be tolerant of
        // float noise — values are usually exactly 0 or 1, but defense
        // in depth is cheap.
        static bool ToBool(double d) => d > 0.5;

        return new HeartbeatRequest
        {
            // Zod's `.datetime()` requires strict ISO-8601 with Z-suffix
            // for UTC. C#'s "o" format produces "+00:00" instead, which
            // Zod rejects despite being mathematically identical. Strip
            // the offset and append "Z" manually for compatibility.
            Timestamp = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            ClientVersion = "VamAcarsClient/0.1",
            Simulator = "MSFS",
            Network = _flightContext.Network,
            Phase = null, // server detects from telemetry (Welle 9 Phase 5)

            Flight = new HeartbeatFlight
            {
                Callsign = _flightContext.Callsign,
                FlightNumber = _flightContext.FlightNumber,
                Departure = _flightContext.DepartureIcao,
                Arrival = _flightContext.ArrivalIcao,
                CruiseAltitude = _flightContext.CruiseAltitudeFt,
                FlightRules = _flightContext.FlightRules,
            },

            Aircraft = new HeartbeatAircraft
            {
                // Server schema: max 16 chars. SimConnect's ATC MODEL can
                // return long strings like "CESSNA SKYHAWK 172S". We
                // truncate to fit; ideally users set their aircraft's
                // ATC MODEL to the ICAO designator (C172, A20N, B738) in
                // the .cfg, but that's a community-quality issue we
                // can't fix client-side.
                Type = TruncateAt(t.AircraftType, 16),
                Registration = TruncateAt(t.AircraftRegistration, 16),
                // M3.8: TITLE simvar (e.g. "Asobo A320neo Lufthansa") —
                // sent so the server's aircraft-resolver can pattern-
                // match against a rich string when ATC MODEL is junk.
                // Server schema accepts max 120; AircraftTitle returns
                // null for empty/whitespace which Zod treats as "absent"
                // (matches our WhenWritingNull JsonOpts).
                Title = t.AircraftTitle is null
                    ? null
                    : TruncateAt(t.AircraftTitle, 120),
            },

            Position = new HeartbeatPosition
            {
                Latitude = t.LatitudeDeg,
                Longitude = t.LongitudeDeg,
                AltitudeFt = (int)Math.Round(t.AltitudeFt),
                AltitudeAglFt = (int)Math.Round(t.AltitudeAglFt),
                HeadingTrue = NormalizeHeading(t.HeadingTrueDeg),
                Pitch = t.PitchDeg,
                Bank = t.BankDeg,
            },

            Speed = new HeartbeatSpeed
            {
                IndicatedKts = (int)Math.Round(Math.Max(0, t.IndicatedAirspeedKts)),
                TrueKts = (int)Math.Round(Math.Max(0, t.TrueAirspeedKts)),
                GroundKts = (int)Math.Round(Math.Max(0, t.GroundSpeedKts)),
                VerticalFpm = (int)Math.Round(t.VerticalSpeedFpm),
            },

            State = new HeartbeatState
            {
                OnGround = ToBool(t.OnGround),
                ParkingBrake = ToBool(t.ParkingBrake),
                FlapsPercent = (int)Math.Round(Math.Clamp(t.FlapsPercent, 0, 100)),
                GearDown = ToBool(t.GearDown),
                AutopilotMaster = ToBool(t.AutopilotMaster),
            },

            Engine = new HeartbeatEngine
            {
                N1Avg = t.EngineN1Avg,
                FuelTotalKg = (int)Math.Round(Math.Max(0, t.FuelTotalKg)),
            },
        };
    }

    /// <summary>
    /// SimConnect can return heading 359.9999 or sometimes -0.0001 due to
    /// float math. Server schema requires [0, 360]. Wrap into range.
    /// </summary>
    private static double NormalizeHeading(double hdg)
    {
        var wrapped = hdg % 360.0;
        if (wrapped < 0) wrapped += 360.0;
        if (wrapped >= 360.0) wrapped = 0.0;
        return wrapped;
    }

    /// <summary>
    /// Truncate to a maximum length. Used for aircraft.type and
    /// aircraft.registration which the server limits to 16 chars but
    /// SimConnect's ATC MODEL/ATC ID can exceed.
    /// </summary>
    private static string TruncateAt(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "UNKN";
        return value.Length <= max ? value : value[..max];
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}

// ─── Flight-context: user-supplied flight metadata ────────────────────
// SimConnect doesn't know callsign/flight-number/network. The user
// types these once at the start of a flight; service uses them on
// every heartbeat.

public sealed class FlightContext
{
    public required string Callsign { get; init; }
    public required string Network { get; init; } // "Offline" | "VATSIM" | "IVAO"
    public string? FlightNumber { get; init; }
    public string? DepartureIcao { get; init; }
    public string? ArrivalIcao { get; init; }
    public int? CruiseAltitudeFt { get; init; }
    public string? FlightRules { get; init; } // "IFR" | "VFR" | etc.
}

// ─── Wire-format DTOs ────────────────────────────────────────────────
// Property-names match HeartbeatSchema in apps/web/.../route.ts.
// System.Text.Json default policy keeps PascalCase by default, so we
// add JsonPropertyName attributes to force camelCase to match server.

public sealed class HeartbeatRequest
{
    [JsonPropertyName("timestamp")] public required string Timestamp { get; init; }
    [JsonPropertyName("clientVersion")] public required string ClientVersion { get; init; }
    [JsonPropertyName("simulator")] public required string Simulator { get; init; }
    [JsonPropertyName("network")] public required string Network { get; init; }
    [JsonPropertyName("phase")] public string? Phase { get; init; }

    [JsonPropertyName("flight")] public required HeartbeatFlight Flight { get; init; }
    [JsonPropertyName("aircraft")] public required HeartbeatAircraft Aircraft { get; init; }
    [JsonPropertyName("position")] public required HeartbeatPosition Position { get; init; }
    [JsonPropertyName("speed")] public required HeartbeatSpeed Speed { get; init; }
    [JsonPropertyName("state")] public required HeartbeatState State { get; init; }
    [JsonPropertyName("engine")] public HeartbeatEngine? Engine { get; init; }
}

public sealed class HeartbeatFlight
{
    [JsonPropertyName("callsign")] public required string Callsign { get; init; }
    [JsonPropertyName("flightNumber")] public string? FlightNumber { get; init; }
    [JsonPropertyName("departure")] public string? Departure { get; init; }
    [JsonPropertyName("arrival")] public string? Arrival { get; init; }
    [JsonPropertyName("cruiseAltitude")] public int? CruiseAltitude { get; init; }
    [JsonPropertyName("flightRules")] public string? FlightRules { get; init; }
}

public sealed class HeartbeatAircraft
{
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("registration")] public required string Registration { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
}

public sealed class HeartbeatPosition
{
    [JsonPropertyName("latitude")] public required double Latitude { get; init; }
    [JsonPropertyName("longitude")] public required double Longitude { get; init; }
    [JsonPropertyName("altitudeFt")] public required int AltitudeFt { get; init; }
    [JsonPropertyName("altitudeAglFt")] public int? AltitudeAglFt { get; init; }
    [JsonPropertyName("headingTrue")] public required double HeadingTrue { get; init; }
    [JsonPropertyName("pitch")] public double? Pitch { get; init; }
    [JsonPropertyName("bank")] public double? Bank { get; init; }
}

public sealed class HeartbeatSpeed
{
    [JsonPropertyName("indicatedKts")] public int? IndicatedKts { get; init; }
    [JsonPropertyName("trueKts")] public int? TrueKts { get; init; }
    [JsonPropertyName("groundKts")] public required int GroundKts { get; init; }
    [JsonPropertyName("verticalFpm")] public required int VerticalFpm { get; init; }
}

public sealed class HeartbeatState
{
    [JsonPropertyName("onGround")] public required bool OnGround { get; init; }
    [JsonPropertyName("parkingBrake")] public bool? ParkingBrake { get; init; }
    [JsonPropertyName("flapsPercent")] public int? FlapsPercent { get; init; }
    [JsonPropertyName("gearDown")] public bool? GearDown { get; init; }
    [JsonPropertyName("autopilotMaster")] public bool? AutopilotMaster { get; init; }
}

public sealed class HeartbeatEngine
{
    [JsonPropertyName("n1Avg")] public double? N1Avg { get; init; }
    [JsonPropertyName("fuelTotalKg")] public int? FuelTotalKg { get; init; }
}

public sealed class HeartbeatResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }

    // ─── Phase-echo (M3.7 / Welle 9 Phase 5) ─────────────────────────
    // Server reports back which phase it has resolved for this session
    // after processing this heartbeat. Lets the client surface phase
    // info in its UI without running its own detector. See server
    // docstring at apps/web/.../heartbeat/route.ts for semantics.

    /// <summary>
    /// Server's resolved phase after this heartbeat — one of the
    /// FlightPhase enum values: PreFlight, Pushback, Taxi, Takeoff,
    /// Climb, Cruise, Descent, Approach, Landing, TaxiIn, BlockOn.
    /// Modeled as string (not C# enum) so unknown values from a
    /// future server-version don't break parsing.
    /// </summary>
    [JsonPropertyName("currentPhase")] public string? CurrentPhase { get; init; }

    /// <summary>
    /// ISO-8601 UTC timestamp of when the current phase began. Lets
    /// the client show "in Climb for 2:34". May be null if the
    /// session has never seen a phase transition (defensive — should
    /// be set in practice from the very first heartbeat onward).
    /// </summary>
    [JsonPropertyName("currentPhaseEnteredAt")] public string? CurrentPhaseEnteredAt { get; init; }

    /// <summary>
    /// True iff *this* heartbeat caused the phase to transition.
    /// Useful for clients that want to highlight transitions (e.g.,
    /// log a "Phase: PreFlight → Taxi" line) without tracking the
    /// previous value themselves.
    /// </summary>
    [JsonPropertyName("phaseChanged")] public bool PhaseChanged { get; init; }

    /// <summary>
    /// Which side decided the phase. "client" = client sent `phase`
    /// and server respected it; "server" = server's state-machine
    /// determined it from telemetry. Helps debug "why did the server
    /// pick X?" — at the moment we always send phase=null so this
    /// will always be "server", but the field will matter once we
    /// optionally let users override mid-flight.
    /// </summary>
    [JsonPropertyName("phaseSource")] public string? PhaseSource { get; init; }

    // ─── Aircraft-echo (M3.8.1) ──────────────────────────────────────
    // Server reports back the M3.8-resolved aircraft identity. Mirrors
    // the phase-echo pattern above: client doesn't run the resolver,
    // server is the single source of truth, client surfaces what the
    // live-map already shows. Without these fields, a client UI would
    // either have to display the raw SimConnect ATC MODEL value (often
    // an ugly localization token) or duplicate the resolver code.

    /// <summary>
    /// Server's resolved ICAO aircraft type after running M3.8's
    /// 3-tier resolver: fleet-registration match → regex pattern →
    /// raw fallback. Examples: "A320", "B738", "DH8D". Always present
    /// on responses from M3.8.1+ servers; null for older servers.
    /// </summary>
    [JsonPropertyName("aircraftType")] public string? AircraftType { get; init; }

    /// <summary>
    /// Tail number echoed verbatim from the heartbeat payload. Same
    /// value the client sent — included for symmetry so a UI can
    /// display "A320 / D-ANNE" from one source. May be null if the
    /// pilot didn't set a registration in their flight context.
    /// </summary>
    [JsonPropertyName("aircraftRegistration")] public string? AircraftRegistration { get; init; }
}