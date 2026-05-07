using System.Collections.Concurrent;
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
                _logger.LogWarning(ex,
                    "Heartbeat send failed (network). Queued={QueuedCount}. Will retry.",
                    _queue.Count);
                HeartbeatFailed?.Invoke($"Network-Fehler: {ex.Message}");
                return;
            }

            if (response.IsSuccessStatusCode)
            {
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
}