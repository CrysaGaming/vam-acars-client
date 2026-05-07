using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using VamAcarsClient.Core;

// ─── Assembly-Resolver für SimConnect ────────────────────────────────
//
// Microsoft.FlightSimulator.SimConnect.dll is a legacy mixed-mode
// assembly from the FSX era (~2007). Modern .NET 10's default
// AssemblyLoadContext refuses to resolve it from the application base
// folder unless we explicitly hook the resolution event.
//
// The exact symptom this fixes: "Could not load file or assembly
// 'Microsoft.FlightSimulator.SimConnect, Version=12.2.0.0, ...'"
// on first SimConnect call, despite the DLL being present in the
// output folder.
//
// Solution: hook AssemblyLoadContext.Resolving for the default context,
// and on the first miss for SimConnect, load it manually from the exe
// directory. After that the runtime caches the assembly and subsequent
// resolutions succeed without going through this path.

AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
{
    if (assemblyName.Name != "Microsoft.FlightSimulator.SimConnect")
        return null;

    var exeDir = Path.GetDirectoryName(AppContext.BaseDirectory)
        ?? AppContext.BaseDirectory;
    var dllPath = Path.Combine(exeDir, "Microsoft.FlightSimulator.SimConnect.dll");
    if (!File.Exists(dllPath))
        return null;

    return context.LoadFromAssemblyPath(dllPath);
};

// ─── Configuration loading ───────────────────────────────────────────
//
// Order of precedence (later wins):
//   1. appsettings.json (production defaults, checked into git)
//   2. appsettings.Development.json (local overrides, git-ignored)
//
// AppContext.BaseDirectory is the folder the .exe runs from — same
// directory the build copies appsettings.json into via the csproj
// CopyToOutputDirectory rules.
//
// reloadOnChange:false because we don't watch the file for changes
// mid-run. Settings only matter at startup; restart to apply changes.
// Keeps the loop simpler than dealing with IOptionsMonitor /
// change-notifications.
//
// Env-var support is intentionally not wired (would require an
// additional NuGet package for a feature we don't need yet). If we
// later want CI-overrides, add Microsoft.Extensions.Configuration
// .EnvironmentVariables and chain .AddEnvironmentVariables() here.

var configBuilder = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false);

var configRoot = configBuilder.Build();
var config = configRoot.Get<VamConfig>() ?? new VamConfig();

// ─── Logging setup (Serilog) ─────────────────────────────────────────
//
// Serilog is configured via the "Serilog" section of appsettings.json.
// File-sink path uses the {LogPath} placeholder which we substitute
// here with %LOCALAPPDATA%\<LocalAppDataFolderName>\logs — same folder
// family that already holds token.bin. Per-user, machine-local,
// survives reboots and uninstalls (intentionally — log retention is
// the user's call, not ours).
//
// Why we substitute manually: the appsettings.json bakes in a literal
// "{LogPath}" string. Serilog.Settings.Configuration won't expand it.
// We rewrite the config-value before building the logger.

var logsDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    config.Storage.LocalAppDataFolderName,
    "logs");
Directory.CreateDirectory(logsDir);

// Replace {LogPath} placeholder in the loaded configuration. The key
// path matches the JSON nesting: "Serilog:WriteTo:1:Args:path" — the
// "1" is the second element of the WriteTo array (the File sink).
foreach (var kv in configRoot.AsEnumerable())
{
    if (kv.Value is { } v && v.Contains("{LogPath}"))
    {
        configRoot[kv.Key] = v.Replace("{LogPath}", logsDir);
    }
}

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configRoot)
    .CreateLogger();

// Bridge Serilog to Microsoft.Extensions.Logging so the services that
// take ILogger<T> can use it. dispose: false because we manually
// flush/close Log.CloseAndFlush() at process-exit instead.
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSerilog(Log.Logger, dispose: false);
});

// Catch-all: ensure file-sink flushes even if the process dies mid-write.
AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.CloseAndFlush();

// Bootstrap-level logger for messages not specific to a service class.
var bootstrapLogger = loggerFactory.CreateLogger("Bootstrap");
bootstrapLogger.LogInformation(
    "VamAcarsClient starting. LogDir={LogsDir}", logsDir);

// ─── HttpClient setup ────────────────────────────────────────────────

using var http = new HttpClient
{
    BaseAddress = new Uri(config.Vam.ApiBaseUrl),
    Timeout = TimeSpan.FromSeconds(config.Vam.RequestTimeoutSeconds),
};
http.DefaultRequestHeaders.UserAgent.ParseAdd(config.Vam.UserAgent);

var tokenStore = new TokenStore(config);

// ─── Mode selection ──────────────────────────────────────────────────

Console.WriteLine("=== VAM ACARS Client — Dev Console ===");
Console.WriteLine();

// Show effective config so dev-mode mistakes (e.g., pointing at the
// wrong server) are visible upfront. Don't dump the full object —
// only the values that affect runtime behavior visibly.
Console.WriteLine($"Server:    {config.Vam.ApiBaseUrl}");
Console.WriteLine($"Heartbeat: alle {config.Heartbeat.IntervalSeconds}s, queue cap {config.Heartbeat.MaxQueueDepth}");
Console.WriteLine();

Console.WriteLine("Was möchtest du testen?");
Console.WriteLine("  [1] Pairing + Status (Server-Roundtrip)");
Console.WriteLine("  [2] SimConnect Read-Loop (Telemetrie aus MSFS)");
Console.WriteLine("  [3] Live-Heartbeat (SimConnect → Server)");
Console.WriteLine("  [q] Beenden");
Console.WriteLine();
Console.Write("Auswahl: ");
var mode = Console.ReadLine()?.Trim().ToLowerInvariant();

return mode switch
{
    "1" => await RunPairingFlowAsync(http, tokenStore, config),
    "2" => RunSimConnectFlow(loggerFactory),
    "3" => await RunHeartbeatFlowAsync(http, tokenStore, config, loggerFactory),
    _ => 0,
};

// ═════════════════════════════════════════════════════════════════════
// Mode 1: Pairing + Status
// ═════════════════════════════════════════════════════════════════════

static async Task<int> RunPairingFlowAsync(HttpClient httpClient, TokenStore tokenStore, VamConfig config)
{
    var existingToken = tokenStore.TryLoad();
    if (existingToken is not null)
    {
        Console.WriteLine($"[i] Bereits gepaart. Token vorhanden ({existingToken.Length} chars).");
        Console.WriteLine("    Drücke ENTER zum Re-pair, oder 'q' + ENTER zum Beenden.");
        var input = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (input == "q") return 0;
    }

    Console.WriteLine();
    Console.WriteLine($"1. Öffne {config.Vam.ApiBaseUrl}/settings");
    Console.WriteLine("2. Scroll zur 'ACARS-Client'-Sektion");
    Console.WriteLine("3. Klicke 'Pairing-Code generieren'");
    Console.WriteLine("4. Tippe den Code unten ein (Format: XXX-XXX-XXX)");
    Console.WriteLine();
    Console.Write("Code: ");
    var code = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(code))
    {
        Console.WriteLine("[!] Kein Code eingegeben. Abbruch.");
        return 1;
    }

    var pairing = new PairingService(httpClient);
    PairingResult result;
    try
    {
        Console.WriteLine();
        Console.WriteLine("[…] Sende an Server …");
        result = await pairing.RedeemAsync(code);
    }
    catch (PairingTransportException ex)
    {
        Console.WriteLine($"[X] Transport-Fehler: {ex.Message}");
        return 2;
    }

    if (!result.IsSuccess)
    {
        Console.WriteLine($"[X] {result.ErrorMessage}");
        return 1;
    }

    try
    {
        tokenStore.Save(result.Token!);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[X] Token konnte nicht gespeichert werden: {ex.Message}");
        Console.WriteLine($"    Token (manuell sichern): {result.Token}");
        return 3;
    }

    Console.WriteLine();
    Console.WriteLine($"[✓] Erfolgreich gepaart als: {result.DisplayName}");
    Console.WriteLine($"[✓] Token gespeichert in %LOCALAPPDATA%\\{config.Storage.LocalAppDataFolderName}\\{config.Storage.TokenFileName}");

    Console.WriteLine();
    Console.WriteLine("[…] Status-Check …");
    var status = new StatusService(httpClient);
    StatusResponse? statusInfo;
    try
    {
        statusInfo = await status.FetchAsync(result.Token!);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[!] Status-Check fehlgeschlagen: {ex.Message}");
        return 0;
    }

    if (statusInfo is null)
    {
        Console.WriteLine("[!] Token wurde abgelehnt (401).");
        return 4;
    }

    var clockDriftSec = (DateTimeOffset.UtcNow - statusInfo.ServerTime).TotalSeconds;
    Console.WriteLine($"[✓] Status-Check OK.");
    Console.WriteLine($"    User:     {statusInfo.User.Name ?? statusInfo.User.Email}");
    Console.WriteLine($"    Airline:  {statusInfo.Airline?.Name ?? "<keine>"} ({statusInfo.Airline?.Icao ?? "—"})");
    Console.WriteLine($"    Network:  {statusInfo.PreferredNetwork}");
    Console.WriteLine($"    Drift:    {clockDriftSec:+0.00;-0.00;0.00}s (Server vs lokale Uhr)");
    return 0;
}

// ═════════════════════════════════════════════════════════════════════
// Mode 2: SimConnect Read-Loop
// ═════════════════════════════════════════════════════════════════════

static int RunSimConnectFlow(ILoggerFactory loggerFactory)
{
    Console.WriteLine();
    Console.WriteLine("[…] Verbinde zu MSFS via SimConnect …");
    Console.WriteLine("    (MSFS muss bereits laufen. Hauptmenü oder im Flug — beides ok.)");
    Console.WriteLine();

    using var sim = new SimConnectClient(loggerFactory.CreateLogger<SimConnectClient>());

    sim.ConnectionLost += msg =>
    {
        Console.WriteLine();
        Console.WriteLine($"[!] {msg}");
    };

    var samplesReceived = 0;
    sim.TelemetryReceived += telemetry =>
    {
        samplesReceived++;
        var t = telemetry;
        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss}] " +
            $"{t.LatitudeDeg:F4}°,{t.LongitudeDeg:F4}° " +
            $"Alt:{t.AltitudeFt,5:F0}ft " +
            $"AGL:{t.AltitudeAglFt,5:F0}ft " +
            $"GS:{t.GroundSpeedKts,3:F0}kts " +
            $"VS:{t.VerticalSpeedFpm,+5:F0}fpm " +
            $"Hdg:{t.HeadingTrueDeg,3:F0}° " +
            $"OnGnd:{(t.OnGround > 0.5 ? "Y" : "N")} " +
            $"N1:{t.EngineN1Avg,3:F0}% " +
            $"Thr:{t.ThrottlePercent,3:F0}% " +
            $"AP:{(t.AutopilotMaster > 0.5 ? "ON" : "OFF")}");
    };

    try
    {
        sim.Connect();
    }
    catch (System.Runtime.InteropServices.COMException ex)
    {
        Console.WriteLine($"[X] SimConnect-Connect fehlgeschlagen: 0x{ex.HResult:X8}");
        Console.WriteLine($"    {ex.Message}");
        Console.WriteLine();
        Console.WriteLine("    Häufigste Ursache: MSFS läuft nicht. Bitte MSFS starten und nochmal probieren.");
        return 5;
    }

    Console.WriteLine("[✓] Verbunden. Polle alle 1s. Strg+C zum Beenden.");
    Console.WriteLine();

    var stopCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stopCts.Cancel();
    };

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    while (!stopCts.IsCancellationRequested)
    {
        sim.RequestTelemetry();
        try
        {
            Task.Delay(1000, stopCts.Token).Wait(stopCts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (AggregateException)
        {
            break;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"[✓] Stopp. {samplesReceived} Samples in {stopwatch.Elapsed.TotalSeconds:F1}s erhalten.");
    return 0;
}

// ═════════════════════════════════════════════════════════════════════
// Mode 3: Live-Heartbeat (full integration test)
// ═════════════════════════════════════════════════════════════════════
// Loads stored token, connects SimConnect, sends heartbeats to the
// server every config.Heartbeat.IntervalSeconds. Pilot should appear
// on the live-map at config.Vam.ApiBaseUrl/live with
// dataSource=ACARS_CLIENT.

static async Task<int> RunHeartbeatFlowAsync(HttpClient httpClient, TokenStore tokenStore, VamConfig config, ILoggerFactory loggerFactory)
{
    Console.WriteLine();

    // ─── Token check ───
    var token = tokenStore.TryLoad();
    if (token is null)
    {
        Console.WriteLine("[X] Kein Token gespeichert. Erst Mode 1 (Pairing) durchlaufen.");
        return 1;
    }
    Console.WriteLine($"[i] Token geladen ({token.Length} chars).");

    // ─── User input: Flight context ───
    // SimConnect doesn't know callsign/flight-number/network. User
    // types these once at the start of a flight.
    Console.WriteLine();
    Console.WriteLine("=== Flight-Kontext ===");
    Console.Write("Callsign (z.B. NGN901): ");
    var callsign = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(callsign))
    {
        Console.WriteLine("[X] Callsign erforderlich. Abbruch.");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine("Network:");
    Console.WriteLine("  [1] Offline (Standard für Test)");
    Console.WriteLine("  [2] VATSIM");
    Console.WriteLine("  [3] IVAO");
    Console.Write("Auswahl [1]: ");
    var networkChoice = Console.ReadLine()?.Trim();
    var network = networkChoice switch
    {
        "2" => "VATSIM",
        "3" => "IVAO",
        _ => "Offline",
    };

    Console.Write("Departure ICAO (optional, z.B. EDDF): ");
    var departure = Console.ReadLine()?.Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(departure)) departure = null;

    Console.Write("Arrival ICAO (optional, z.B. EDDM): ");
    var arrival = Console.ReadLine()?.Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(arrival)) arrival = null;

    var flightContext = new FlightContext
    {
        Callsign = callsign,
        Network = network,
        DepartureIcao = departure,
        ArrivalIcao = arrival,
    };

    // ─── SimConnect ───
    Console.WriteLine();
    Console.WriteLine("[…] Verbinde zu MSFS …");
    using var sim = new SimConnectClient(loggerFactory.CreateLogger<SimConnectClient>());
    try
    {
        sim.Connect();
    }
    catch (System.Runtime.InteropServices.COMException ex)
    {
        Console.WriteLine($"[X] SimConnect-Connect fehlgeschlagen: 0x{ex.HResult:X8}");
        Console.WriteLine("    MSFS muss laufen.");
        return 5;
    }
    Console.WriteLine("[✓] SimConnect verbunden.");

    // ─── Heartbeat-Service ───
    using var heartbeat = new HeartbeatService(
        httpClient,
        sim,
        token,
        flightContext,
        interval: TimeSpan.FromSeconds(config.Heartbeat.IntervalSeconds),
        logger: loggerFactory.CreateLogger<HeartbeatService>());

    var sentCount = 0;
    var failedCount = 0;
    string? lastSessionId = null;

    heartbeat.HeartbeatSent += response =>
    {
        sentCount++;
        lastSessionId = response.SessionId;
    };
    heartbeat.HeartbeatFailed += msg =>
    {
        failedCount++;
        Console.WriteLine($"[!] {msg}");
    };

    var stopCts = new CancellationTokenSource();
    var reAuthRequired = false;
    heartbeat.ReAuthRequired += () =>
    {
        reAuthRequired = true;
        tokenStore.Clear();
        stopCts.Cancel();
    };

    Console.WriteLine();
    Console.WriteLine($"[✓] Heartbeat startet (Callsign: {callsign}, Network: {network}).");
    Console.WriteLine($"    Polle MSFS @ 1Hz, sende an Server @ {config.Heartbeat.IntervalSeconds}s. Strg+C zum Beenden.");
    Console.WriteLine($"    Live-Map: {config.Vam.ApiBaseUrl}/live");
    Console.WriteLine();

    heartbeat.Start();

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stopCts.Cancel();
    };

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var lastStatusLine = DateTime.MinValue;

    while (!stopCts.IsCancellationRequested)
    {
        sim.RequestTelemetry();

        // Status-line every 5s so the user sees progress without spam.
        var now = DateTime.Now;
        if ((now - lastStatusLine).TotalSeconds >= 5)
        {
            var t = sim.LatestTelemetry;
            if (t is { } tel)
            {
                Console.WriteLine(
                    $"[{now:HH:mm:ss}] " +
                    $"sent={sentCount} failed={failedCount} queued={heartbeat.QueuedCount} | " +
                    $"{tel.LatitudeDeg:F4}°,{tel.LongitudeDeg:F4}° " +
                    $"Alt:{tel.AltitudeFt:F0}ft GS:{tel.GroundSpeedKts:F0}kts " +
                    $"OnGnd:{(tel.OnGround > 0.5 ? "Y" : "N")}");
            }
            lastStatusLine = now;
        }

        try
        {
            await Task.Delay(1000, stopCts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }

    Console.WriteLine();
    await heartbeat.StopAsync();
    sim.Disconnect();

    Console.WriteLine($"[✓] Stopp. {sentCount} sent, {failedCount} failed, {heartbeat.QueuedCount} ungesendet.");
    if (lastSessionId is not null)
    {
        Console.WriteLine($"    LiveSession-ID: {lastSessionId}");
    }
    if (reAuthRequired)
    {
        Console.WriteLine();
        Console.WriteLine("[!] Token wurde abgelehnt — bitte erneut pairen (Mode 1).");
        return 4;
    }
    return 0;
}
