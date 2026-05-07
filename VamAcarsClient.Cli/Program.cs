using VamAcarsClient.Core;

// ─── HttpClient setup ────────────────────────────────────────────────
// One HttpClient shared across the lifetime of the process. .NET's
// SocketsHttpHandler-default reuses connections, so this is the
// recommended pattern. BaseAddress + UserAgent set once here so the
// services don't have to know URL-shape or branding.

using var http = new HttpClient
{
    BaseAddress = new Uri(VamConfig.ApiBaseUrl),
    Timeout = TimeSpan.FromSeconds(30),
};
http.DefaultRequestHeaders.UserAgent.ParseAdd(VamConfig.UserAgent);

// ─── Token check ─────────────────────────────────────────────────────

var existingToken = TokenStore.TryLoad();
if (existingToken is not null)
{
    Console.WriteLine($"[i] Bereits gepaart. Token vorhanden ({existingToken.Length} chars).");
    Console.WriteLine("    Drücke ENTER zum Re-pair, oder 'q' + ENTER zum Beenden.");
    var input = Console.ReadLine()?.Trim().ToLowerInvariant();
    if (input == "q") return 0;
}

// ─── Prompt for pairing-code ─────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("=== VAM ACARS Client — Pairing ===");
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

// ─── Redeem ──────────────────────────────────────────────────────────

var pairing = new PairingService(http);
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

// ─── Persist token ───────────────────────────────────────────────────

try
{
    TokenStore.Save(result.Token!);
}
catch (Exception ex)
{
    // DPAPI-failure or disk-issue. Don't lose the token — show it to
    // the user once so they can manually re-pair if they need to.
    Console.WriteLine($"[X] Token konnte nicht gespeichert werden: {ex.Message}");
    Console.WriteLine($"    Token (manuell sichern): {result.Token}");
    return 3;
}

Console.WriteLine();
Console.WriteLine($"[✓] Erfolgreich gepaart als: {result.DisplayName}");
Console.WriteLine($"[✓] Token gespeichert in %LOCALAPPDATA%\\{VamConfig.LocalAppDataFolderName}\\{VamConfig.TokenFileName}");

// ─── Sanity-check: authenticated GET /api/acars/status ───────────────
// Validates the full request-flow (auth header, server reachable,
// token actually accepted on a non-public endpoint).

Console.WriteLine();
Console.WriteLine("[…] Status-Check …");
var status = new StatusService(http);
StatusResponse? statusInfo;
try
{
    statusInfo = await status.FetchAsync(result.Token!);
}
catch (Exception ex)
{
    Console.WriteLine($"[!] Status-Check fehlgeschlagen: {ex.Message}");
    Console.WriteLine("    Pairing war erfolgreich, aber Server unerreichbar für Status.");
    return 0; // Pairing was the actual goal; status is bonus.
}

if (statusInfo is null)
{
    Console.WriteLine("[!] Token wurde abgelehnt (401). Ungewöhnlich nach erfolgreichem Pair.");
    return 4;
}

var clockDriftSec = (DateTimeOffset.UtcNow - statusInfo.ServerTime).TotalSeconds;
Console.WriteLine($"[✓] Status-Check OK.");
Console.WriteLine($"    User:     {statusInfo.User.Name ?? statusInfo.User.Email}");
Console.WriteLine($"    Airline:  {statusInfo.Airline?.Name ?? "<keine>"} ({statusInfo.Airline?.Icao ?? "—"})");
Console.WriteLine($"    Network:  {statusInfo.PreferredNetwork}");
Console.WriteLine($"    Drift:    {clockDriftSec:+0.00;-0.00;0.00}s (Server vs lokale Uhr)");

return 0;