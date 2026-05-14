using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VamAcarsClient.Tray.Models;

namespace VamAcarsClient.Tray;

/// <summary>
/// Welle E / E1 — local HTTP-server serving an OBS-browser-source-friendly
/// overlay of the live ACARS state. Binds to <c>127.0.0.1</c> only.
///
/// # Endpoints
///
/// - <c>GET /</c>            → static HTML page (inline below) that polls
///                             <c>/state.json</c> at 1 Hz and renders the
///                             callsign / phase / route / aircraft / network
///                             into a compact overlay-bar.
/// - <c>GET /state.json</c>  → JSON snapshot of relevant
///                             <see cref="AcarsClientState"/> fields.
///                             Cache-Control: no-store so OBS's CEF never
///                             serves a stale snapshot.
/// - any other path          → 404, plain text.
///
/// # Why HttpListener and not Kestrel
///
/// Kestrel pulls in the entire ASP.NET Core hosting stack (DI, options,
/// configuration providers, logging adapters) for what amounts to two GET
/// routes that don't even need request body parsing. HttpListener is
/// built into <c>System.Net</c>, runs on the same thread-pool, requires
/// no NuGet, and the two-route surface is naturally expressed as a
/// switch on <c>Request.Url.AbsolutePath</c>. The total binary-size hit
/// of adding Kestrel would have been ~3 MB for no functional gain.
///
/// # Why loopback-only
///
/// Binding to <c>127.0.0.1</c> rather than <c>0.0.0.0</c> means:
///   - Windows firewall does not prompt on first run (loopback is exempt
///     from the firewall's default-deny inbound rule).
///   - No host on the LAN can read the overlay — only processes on the
///     same machine. A laptop in a coffee shop never exposes the pilot's
///     live position to anyone on the wifi.
///   - OBS running on a SECOND box (NDI / Streamlabs remote source) cannot
///     reach this overlay. That's the trade-off; users with that setup
///     should fall back to the vam.kevindrack.de overlay-URL.
///
/// # Why HttpListener path is hostname-string "127.0.0.1" not the IPAddress.Loopback object
///
/// HttpListener's prefix syntax wants <c>http://host:port/</c>, where
/// <c>host</c> can be <c>+</c> (all interfaces, requires admin),
/// <c>*</c> (all unclaimed), or a literal hostname. We use the literal
/// IP-string <c>127.0.0.1</c> so the prefix doesn't trip the
/// "namespace reservation required" path that <c>+</c> forces (which
/// would need an elevated <c>netsh http add urlacl</c> call on first
/// run). Loopback hostnames are exempt from that reservation.
///
/// # Port-fallback contract
///
/// <see cref="TryStart"/> attempts <c>8765</c> first and walks up to
/// <c>8775</c> (eleven ports total). The first one that binds wins;
/// <see cref="BoundPort"/> and <see cref="BoundUrl"/> reflect the
/// chosen port. If all eleven are busy the method returns false and
/// the listener stays uninitialized — the caller logs + surfaces a
/// status message; this is rare enough (would need 11 conflicting
/// loopback-listeners) that we don't add backoff or retry.
///
/// # Threading
///
/// <see cref="HttpListener"/> is sync-but-thread-pool-friendly; we run
/// the accept-loop on a dedicated <see cref="Task"/> so the WPF UI
/// thread is never blocked. Each accepted request is handled inline
/// on the listener task — the response payload is tiny (a few hundred
/// bytes of JSON or ~3 KB of HTML), so we don't need per-request
/// task-pool dispatch. Total throughput cap is one request per ~1 ms
/// which is two orders of magnitude above what a 1 Hz browser-source
/// poll generates.
///
/// All reads of <see cref="AcarsClientState"/> happen from the listener
/// task. INPC properties' getters are bare field reads on reference
/// types and primitives; concurrent read with the UI-thread setter is
/// safe under C#'s memory model (atomic word writes, no torn reads).
/// The JSON snapshot is therefore consistent within itself, even if a
/// concurrent setter fires mid-snapshot — at worst one field reflects
/// the post-write value and another reflects pre-write, which is the
/// same eventual-consistency the overlay's 1 Hz polling would produce
/// across consecutive snapshots anyway.
/// </summary>
public sealed class OverlayServer : IDisposable
{
    private readonly AcarsClientState _state;
    private readonly ILogger<OverlayServer> _logger;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    /// <summary>
    /// The port the listener is currently bound to, or null when the
    /// server is stopped (or failed to find a free port). Surfaced via
    /// <see cref="BoundUrl"/> so the UI can show the pilot exactly
    /// which URL to paste into OBS.
    /// </summary>
    public int? BoundPort { get; private set; }

    /// <summary>
    /// The full URL the listener is bound to, or null when stopped.
    /// Includes the trailing slash so OBS treats it as the root path.
    /// </summary>
    public string? BoundUrl => BoundPort is int p ? $"http://127.0.0.1:{p}/" : null;

    /// <summary>
    /// True while the listener is accepting requests. Use this to
    /// gate idempotent start/stop (calling <see cref="TryStart"/>
    /// while already running is a no-op returning true).
    /// </summary>
    public bool IsRunning => _listener?.IsListening == true;

    // Port-fallback window. 8765 is the canonical default — a memorable
    // number in the unregistered range above 8000 (where dev servers
    // typically live but 8080/8000/3000 are crowded) — and we walk up
    // 10 more for the rare case that another tool already owns 8765.
    // Eleven ports is overkill in practice; the most common "already
    // bound" scenario is a leftover instance of THIS app from a previous
    // run that didn't shut down cleanly, which generally clears within
    // a TIME_WAIT cycle (a few seconds).
    private const int DefaultPort = 8765;
    private const int MaxPort = 8775;

    public OverlayServer(AcarsClientState state, ILogger<OverlayServer> logger)
    {
        _state = state;
        _logger = logger;
    }

    /// <summary>
    /// Attempt to bind the listener to <c>http://127.0.0.1:{port}/</c>
    /// starting at <see cref="DefaultPort"/> and walking up to
    /// <see cref="MaxPort"/>. Returns true on success and starts the
    /// accept-loop on a background task; returns false if every port
    /// in the window was busy (or if a prior listener is still around
    /// and this call is a no-op).
    ///
    /// Idempotent: calling <c>TryStart</c> while already running just
    /// returns true. The bound port doesn't change.
    /// </summary>
    public bool TryStart()
    {
        if (IsRunning)
        {
            _logger.LogDebug("OverlayServer.TryStart called while already running on port {Port} — no-op",
                BoundPort);
            return true;
        }

        for (var port = DefaultPort; port <= MaxPort; port++)
        {
            var listener = new HttpListener();
            // Prefix MUST end with /. HttpListener throws ArgumentException
            // on prefixes without trailing slash — discovered via the
            // .NET source, not documented prominently.
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");

            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 32 || ex.ErrorCode == 183)
            {
                // 32 = ERROR_SHARING_VIOLATION, 183 = ERROR_ALREADY_EXISTS.
                // Both mean "port is busy". Dispose the failed listener
                // (Start() didn't allocate the underlying handle, but
                // good housekeeping) and try the next port.
                listener.Close();
                _logger.LogDebug("OverlayServer port {Port} busy (HRESULT={Code}), trying next", port, ex.ErrorCode);
                continue;
            }
            catch (HttpListenerException ex)
            {
                // Other HttpListener errors are unexpected — log and bail.
                // The most common would be ERROR_ACCESS_DENIED (5), which
                // means the user lacks permission to bind on this port
                // — shouldn't happen on loopback under a normal user
                // account, but if it does, retrying other ports won't
                // help.
                listener.Close();
                _logger.LogError(ex,
                    "OverlayServer.TryStart failed unexpectedly on port {Port} (HRESULT={Code})",
                    port, ex.ErrorCode);
                return false;
            }

            _listener = listener;
            BoundPort = port;
            _cts = new CancellationTokenSource();
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));

            _logger.LogInformation("OverlayServer started on {Url}", BoundUrl);
            return true;
        }

        _logger.LogWarning(
            "OverlayServer.TryStart: ports {Start}..{End} all busy — server not started",
            DefaultPort, MaxPort);
        return false;
    }

    /// <summary>
    /// Stop accepting new requests and tear down the listener. Awaits
    /// the accept-loop's natural exit so the caller knows we're fully
    /// quiet by the time this returns. Idempotent — no-op if not
    /// currently running.
    /// </summary>
    public async Task StopAsync()
    {
        if (_listener is null)
        {
            _logger.LogDebug("OverlayServer.StopAsync called while not running — no-op");
            return;
        }

        var boundUrl = BoundUrl;
        _logger.LogInformation("OverlayServer stopping ({Url})", boundUrl);

        // Order matters: cancel first so the accept-loop sees the token
        // before we close the listener. Closing the listener mid-accept
        // throws ObjectDisposedException inside GetContext — the loop
        // catches that and exits cleanly, but flipping the CTS first
        // is the polite signal.
        try { _cts?.Cancel(); } catch { /* CTS already disposed */ }

        try { _listener.Stop(); } catch { /* listener already disposed */ }
        try { _listener.Close(); } catch { /* same */ }

        if (_listenTask is not null)
        {
            try { await _listenTask; } catch { /* shutdown noise */ }
            _listenTask = null;
        }

        _listener = null;
        BoundPort = null;
        _cts?.Dispose();
        _cts = null;

        _logger.LogInformation("OverlayServer stopped ({Url})", boundUrl);
    }

    /// <summary>
    /// Accept loop. Runs on a background task. Exits when the CTS is
    /// cancelled OR when the listener throws ObjectDisposedException
    /// (which is what <see cref="HttpListener.Stop"/> does to wake up
    /// the blocked <see cref="HttpListener.GetContextAsync"/>).
    /// </summary>
    private async Task ListenLoopAsync(CancellationToken ct)
    {
        _logger.LogDebug("OverlayServer accept-loop started");

        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Listener was Closed() under us — normal shutdown path.
                break;
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 995)
            {
                // ERROR_OPERATION_ABORTED — Stop() interrupted the
                // pending GetContext call. Also normal shutdown path.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OverlayServer GetContextAsync threw — exiting accept-loop");
                break;
            }

            // Handle synchronously on the listener task. The response
            // payloads are tiny (max ~3 KB), so the latency hit from
            // not dispatching to the thread-pool is sub-millisecond
            // and the simpler control flow is worth it.
            try
            {
                HandleRequest(context);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OverlayServer request-handler threw");
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
            }
        }

        _logger.LogDebug("OverlayServer accept-loop exited");
    }

    /// <summary>
    /// Route requests by absolute path. Only GET is supported; any
    /// other method gets 405. Unknown paths get 404. Both endpoints
    /// set <c>Cache-Control: no-store</c> so OBS's CEF doesn't serve
    /// a stale snapshot mid-flight.
    /// </summary>
    private void HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        // The Cache-Control + Access-Control-Allow-Origin headers go on
        // every response. CORS is permissive (*) because OBS's CEF
        // doesn't typically send Origin headers but other browser-
        // sources or test clients might; allowing all origins is safe
        // because the listener is loopback-only — nothing on the LAN
        // can reach it regardless of CORS policy.
        res.Headers.Add("Cache-Control", "no-store");
        res.Headers.Add("Access-Control-Allow-Origin", "*");

        if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            res.StatusCode = 405;
            res.AddHeader("Allow", "GET");
            res.Close();
            return;
        }

        var path = req.Url?.AbsolutePath ?? "/";

        switch (path)
        {
            case "/":
            case "/index.html":
                WriteHtml(res, OverlayHtml);
                return;

            case "/state.json":
                WriteJson(res, BuildStateJson(_state));
                return;

            default:
                res.StatusCode = 404;
                WriteText(res, "Not Found");
                return;
        }
    }

    private static void WriteHtml(HttpListenerResponse res, string html)
    {
        res.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(html);
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.OutputStream.Close();
    }

    private static void WriteJson(HttpListenerResponse res, string json)
    {
        res.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(json);
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.OutputStream.Close();
    }

    private static void WriteText(HttpListenerResponse res, string text)
    {
        res.ContentType = "text/plain; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(text);
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.OutputStream.Close();
    }

    /// <summary>
    /// Snapshot the relevant <see cref="AcarsClientState"/> fields into
    /// a JSON object for the overlay's JS to consume. Kept deliberately
    /// flat — no nested objects — so the overlay's render code is just
    /// <c>data.callsign</c> lookups without deep-property chains.
    ///
    /// All string fields fall back to null when the State doesn't have
    /// them; the overlay's JS renders null as an em-dash so the layout
    /// stays stable even before the first heartbeat lands.
    ///
    /// The <c>timestamp</c> field is the snapshot wall-clock so the
    /// overlay can show a "last updated 3 s ago" hint if needed (not
    /// currently used in the inline HTML, but stable surface for
    /// future enhancement).
    /// </summary>
    private static string BuildStateJson(AcarsClientState state)
    {
        // Use anonymous object + System.Text.Json. Allocates briefly,
        // but at 1 Hz that's negligible (~200 bytes per snapshot, gen-0
        // GC easily handles it).
        var snapshot = new
        {
            connectionStatus = state.ConnectionStatus.ToString(),
            callsign = state.Callsign,
            phase = state.CurrentPhase,
            phaseDisplay = state.PhaseDisplay,
            phaseEnteredAt = state.PhaseEnteredAt?.ToString("O"),
            aircraftType = state.AircraftType,
            aircraftRegistration = state.AircraftRegistration,
            networkHealthState = state.NetworkHealthState,
            heartbeatsSent = state.HeartbeatsSent,
            heartbeatsFailed = state.HeartbeatsFailed,
            heartbeatsQueued = state.HeartbeatsQueued,
            lastLatencyMs = state.LastLatencyMs,
            demoMode = state.DemoModeEnabled,
            activeBookingFlightNumber = state.ActiveBookingFlightNumber,
            activeBookingDepartureIcao = state.ActiveBookingDepartureIcao,
            activeBookingArrivalIcao = state.ActiveBookingArrivalIcao,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        };

        return JsonSerializer.Serialize(snapshot);
    }

    public void Dispose()
    {
        // Best-effort sync teardown for App.OnExit. We can't await the
        // listen-task here (we're inside Dispose, which is sync), so
        // we kick off cancel + close and let the OS reap the task
        // when the process exits. The listener releases its socket
        // immediately on Close.
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _cts?.Dispose();
    }

    // ─── Embedded overlay HTML ───────────────────────────────────────────
    //
    // Inline rather than an EmbeddedResource because the overlay is a
    // single self-contained HTML doc with inline CSS + JS — splitting it
    // into a .html file in Resources/ would just need a Stream-read here
    // and not buy us much (no template-substitution, no per-user
    // customization). Verbatim-string + raw-string-literal keeps the
    // markup readable without backslash-escapes for the double-quotes
    // in attributes.
    //
    // Layout: compact horizontal bar designed to sit at the top of an
    // OBS scene. Black-translucent background (rgba(0,0,0,0.65)) so it
    // stays readable over any video without being opaque. Mono-font for
    // values so numbers don't wobble between digits. Subtle border-
    // bottom in the brand-blue color for visual cohesion with the
    // existing live-map overlay.
    //
    // The JS polls /state.json every 1000 ms. We use fetch() with
    // { cache: "no-store" } to defensively pair with the server-side
    // Cache-Control header. On fetch failure (server stopped while OBS
    // still has the source up), the overlay fades to a "OFFLINE" banner
    // rather than freezing — gives the streamer a visible signal that
    // their overlay is stale.
    private const string OverlayHtml = """
<!DOCTYPE html>
<html lang="de">
<head>
  <meta charset="utf-8" />
  <title>VAM ACARS Overlay</title>
  <style>
    :root {
      --bg: rgba(0, 0, 0, 0.65);
      --fg: #e8e8ee;
      --muted: #9090a0;
      --accent-blue: #78c8ff;
      --accent-green: #5dd594;
      --accent-yellow: #f5c451;
      --accent-red: #ff7a87;
      --border: #34343f;
    }

    * { box-sizing: border-box; margin: 0; padding: 0; }

    body {
      font-family: "Segoe UI", system-ui, sans-serif;
      color: var(--fg);
      background: transparent;
      padding: 12px;
    }

    .bar {
      display: inline-flex;
      align-items: center;
      gap: 16px;
      padding: 10px 16px;
      background: var(--bg);
      border-bottom: 2px solid var(--accent-blue);
      border-radius: 6px;
      backdrop-filter: blur(4px);
      -webkit-backdrop-filter: blur(4px);
    }

    .cell { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
    .cell .label {
      font-size: 9px;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: var(--muted);
      font-weight: 600;
    }
    .cell .value {
      font-family: "Consolas", "Cascadia Mono", monospace;
      font-size: 16px;
      font-weight: 600;
      color: var(--fg);
      white-space: nowrap;
    }
    .cell .value.muted { color: var(--muted); font-weight: 400; }

    .pill {
      display: inline-block;
      padding: 3px 8px;
      border-radius: 10px;
      font-size: 10px;
      font-weight: 700;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      color: #0a0a14;
    }
    .pill.connected  { background: var(--accent-green); }
    .pill.connecting { background: var(--accent-yellow); }
    .pill.disconnected, .pill.unknown { background: var(--muted); }
    .pill.error, .pill.offline_pill { background: var(--accent-red); color: var(--fg); }
    .pill.demo { background: var(--accent-blue); }

    .sep {
      width: 1px;
      height: 28px;
      background: var(--border);
    }

    .arrow { color: var(--muted); padding: 0 4px; }

    .offline-banner {
      display: none;
      padding: 10px 16px;
      background: rgba(255, 122, 135, 0.18);
      border: 1px solid var(--accent-red);
      border-radius: 6px;
      color: var(--accent-red);
      font-size: 12px;
      font-weight: 600;
      letter-spacing: 0.04em;
    }
    .offline-banner.show { display: inline-block; }
    .bar.dim { opacity: 0.45; transition: opacity 0.3s; }
  </style>
</head>
<body>
  <div id="bar" class="bar">
    <div class="cell">
      <span class="label">Status</span>
      <span id="status-pill" class="pill unknown">UNKNOWN</span>
    </div>
    <div class="sep"></div>

    <div class="cell">
      <span class="label">Callsign</span>
      <span id="callsign" class="value muted">—</span>
    </div>
    <div class="sep"></div>

    <div class="cell">
      <span class="label">Route</span>
      <span id="route" class="value muted">—</span>
    </div>
    <div class="sep"></div>

    <div class="cell">
      <span class="label">Phase</span>
      <span id="phase" class="value muted">—</span>
    </div>
    <div class="sep"></div>

    <div class="cell">
      <span class="label">Aircraft</span>
      <span id="aircraft" class="value muted">—</span>
    </div>
    <div class="sep"></div>

    <div class="cell">
      <span class="label">Network</span>
      <span id="network-pill" class="pill unknown">UNKNOWN</span>
    </div>
    <span id="demo-badge" class="pill demo" style="display: none;">DEMO</span>
  </div>

  <div id="offline" class="offline-banner">⚠ Overlay-Server nicht erreichbar — Tray-App läuft nicht.</div>

  <script>
    // ─────────────────────────────────────────────────────────────────
    // Welle E / E1 — OBS overlay client-script.
    //
    // Polls /state.json at 1 Hz (matches the heartbeat-cadence of the
    // tray-app's ACARS service, so the overlay updates approximately
    // in step with each heartbeat-response). On fetch failure (server
    // stopped, network glitch), the bar dims and an "OFFLINE" banner
    // surfaces so the streamer sees something is wrong rather than a
    // frozen-snapshot illusion.
    //
    // We use fetch with cache: "no-store" to defensively pair with the
    // server's Cache-Control: no-store header — CEF (OBS's browser
    // engine) has been observed to serve cached responses against
    // its own intent in some configurations.
    // ─────────────────────────────────────────────────────────────────
    const $ = id => document.getElementById(id);
    const setText = (id, text) => {
      const el = $(id);
      el.textContent = text || '—';
      el.classList.toggle('muted', !text);
    };
    const setPill = (id, klass, text) => {
      const el = $(id);
      el.className = 'pill ' + klass;
      el.textContent = text;
    };

    // Map ConnectionStatus enum strings → pill CSS class.
    const statusClass = (s) => {
      switch ((s || '').toLowerCase()) {
        case 'connected':    return 'connected';
        case 'connecting':   return 'connecting';
        case 'error':        return 'error';
        case 'disconnected':
        default:             return 'disconnected';
      }
    };

    // Map NetworkHealthState → pill class. Note: 'offline' collides
    // with the existing 'offline-banner', so we use 'offline_pill'
    // (underscore) for the pill class to avoid CSS selector ambiguity.
    const networkClass = (n) => {
      switch ((n || '').toLowerCase()) {
        case 'online':   return 'connected';
        case 'degraded': return 'connecting';
        case 'offline':  return 'offline_pill';
        default:         return 'unknown';
      }
    };

    let consecutiveFailures = 0;
    const FAILURE_THRESHOLD = 3; // 3 seconds = obvious outage

    async function poll() {
      try {
        const resp = await fetch('/state.json', { cache: 'no-store' });
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        const data = await resp.json();

        consecutiveFailures = 0;
        $('bar').classList.remove('dim');
        $('offline').classList.remove('show');

        setPill('status-pill', statusClass(data.connectionStatus),
                (data.connectionStatus || 'Unknown').toUpperCase());

        setText('callsign', data.callsign);

        // Compose "EDDF→EDDM" if both ends known, else the booking's
        // route if available, else em-dash. Live flight context isn't
        // currently in State; this is a future enhancement.
        const dep = data.activeBookingDepartureIcao;
        const arr = data.activeBookingArrivalIcao;
        if (dep && arr) {
          $('route').textContent = dep + ' → ' + arr;
          $('route').classList.remove('muted');
        } else {
          setText('route', null);
        }

        // Phase display includes elapsed-time when available
        // ("Cruise — 0:42"); fall back to bare phase name on a server
        // that hasn't committed PhaseEnteredAt yet.
        setText('phase', data.phaseDisplay || data.phase);

        // "A20N / D-ANNE" composite, em-dash on missing.
        const ac = [data.aircraftType, data.aircraftRegistration]
          .filter(Boolean).join(' / ');
        setText('aircraft', ac);

        setPill('network-pill', networkClass(data.networkHealthState),
                (data.networkHealthState || 'Unknown').toUpperCase());

        // Demo badge: visible only when demo-mode is on. Tells the
        // streamer's audience this isn't a real flight being tracked.
        $('demo-badge').style.display = data.demoMode ? 'inline-block' : 'none';

      } catch (err) {
        consecutiveFailures++;
        if (consecutiveFailures >= FAILURE_THRESHOLD) {
          $('bar').classList.add('dim');
          $('offline').classList.add('show');
        }
      }
    }

    // Kick off immediately so the first render doesn't wait 1s, then
    // continue at 1 Hz. setInterval rather than recursive setTimeout
    // because we don't care about exact spacing — the server's snapshot
    // is cheap, and a missed beat just means the next one catches up.
    poll();
    setInterval(poll, 1000);
  </script>
</body>
</html>
""";
}
