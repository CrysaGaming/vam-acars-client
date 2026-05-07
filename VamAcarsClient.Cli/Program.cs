using System.Reflection;
using System.Runtime.Loader;
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

// ─── HttpClient setup ────────────────────────────────────────────────

using var http = new HttpClient
{
    BaseAddress = new Uri(VamConfig.ApiBaseUrl),
    Timeout = TimeSpan.FromSeconds(30),
};
http.DefaultRequestHeaders.UserAgent.ParseAdd(VamConfig.UserAgent);

// ─── Mode selection ──────────────────────────────────────────────────

Console.WriteLine("=== VAM ACARS Client — Dev Console ===");
Console.WriteLine();
Console.WriteLine("Was möchtest du testen?");
Console.WriteLine("  [1] Pairing + Status (Server-Roundtrip)");
Console.WriteLine("  [2] SimConnect Read-Loop (Telemetrie aus MSFS)");
Console.WriteLine("  [q] Beenden");
Console.WriteLine();
Console.Write("Auswahl: ");
var mode = Console.ReadLine()?.Trim().ToLowerInvariant();

return mode switch
{
    "1" => await RunPairingFlowAsync(http),
    "2" => RunSimConnectFlow(),
    _ => 0,
};

// ═════════════════════════════════════════════════════════════════════
// Mode 1: Pairing + Status
// ═════════════════════════════════════════════════════════════════════

static async Task<int> RunPairingFlowAsync(HttpClient httpClient)
{
    var existingToken = TokenStore.TryLoad();
    if (existingToken is not null)
    {
        Console.WriteLine($"[i] Bereits gepaart. Token vorhanden ({existingToken.Length} chars).");
        Console.WriteLine("    Drücke ENTER zum Re-pair, oder 'q' + ENTER zum Beenden.");
        var input = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (input == "q") return 0;
    }

    Console.WriteLine();
    Console.WriteLine("1. Öffne https://vam.kevindrack.de/settings");
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
        TokenStore.Save(result.Token!);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[X] Token konnte nicht gespeichert werden: {ex.Message}");
        Console.WriteLine($"    Token (manuell sichern): {result.Token}");
        return 3;
    }

    Console.WriteLine();
    Console.WriteLine($"[✓] Erfolgreich gepaart als: {result.DisplayName}");
    Console.WriteLine($"[✓] Token gespeichert in %LOCALAPPDATA%\\{VamConfig.LocalAppDataFolderName}\\{VamConfig.TokenFileName}");

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

static int RunSimConnectFlow()
{
    Console.WriteLine();
    Console.WriteLine("[…] Verbinde zu MSFS via SimConnect …");
    Console.WriteLine("    (MSFS muss bereits laufen. Hauptmenü oder im Flug — beides ok.)");
    Console.WriteLine();

    using var sim = new SimConnectClient();

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