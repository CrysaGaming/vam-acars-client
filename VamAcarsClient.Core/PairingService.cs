using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace VamAcarsClient.Core;

/// <summary>
/// Client for POST /api/acars/pairing/redeem.
///
/// One-shot exchange: takes the 9-char code the user typed in, returns
/// a long-lived bearer-token plus a snippet of user-info for greeting.
///
/// Lifecycle:
///   1. Construct with a shared HttpClient (we never new-up our own —
///      HttpClient is meant to be reused for connection-pooling).
///   2. Call RedeemAsync(code).
///   3. On success: persist Token via TokenStore.Save(), display name.
///   4. On failure: surface the error-code to the UI ("Invalid or
///      expired code — please generate a new one").
///
/// Wire-format mirrors apps/web/app/api/acars/pairing/redeem/route.ts.
/// Server expects "XXX-XXX-XXX" with exactly the two dashes; we
/// normalize uppercase but DON'T add/remove dashes — let the server
/// reject malformed input so we don't silently mask user typos.
/// </summary>
public sealed class PairingService
{
    private readonly HttpClient _http;

    public PairingService(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Attempt to redeem a pairing-code. Returns Success with token+user
    /// on 200, Failure with error-message on 400, throws on transport
    /// errors (DNS, network, 5xx) — those are infrastructure-issues
    /// the caller should surface differently than user-input errors.
    /// </summary>
    public async Task<PairingResult> RedeemAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return PairingResult.Failure("Bitte gib einen Pairing-Code ein.");

        var normalized = code.Trim().ToUpperInvariant();

        var request = new RedeemRequest { Code = normalized };
        HttpResponseMessage response;

        try
        {
            response = await _http.PostAsJsonAsync(
                "/api/acars/pairing/redeem",
                request,
                ct);
        }
        catch (HttpRequestException ex)
        {
            // Network unreachable, DNS failure, TLS handshake error.
            // Distinct from server-said-no — caller should show "check
            // your internet connection" rather than "invalid code".
            throw new PairingTransportException(
                "Server nicht erreichbar. Internetverbindung prüfen.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Timeout on _http.Timeout (default 100s; we configure 30s
            // in the bootstrap below). Distinct from caller-cancellation.
            throw new PairingTransportException(
                "Server antwortet nicht (Zeitüberschreitung).", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<RedeemSuccess>(ct)
                ?? throw new PairingTransportException(
                    "Antwort konnte nicht gelesen werden.", null);

            return PairingResult.Success(body.Token, body.User.Name ?? body.User.Email);
        }

        // 400 / 4xx — server rejected. Try to parse the error-code into
        // a user-friendly message; fall back to generic message if
        // body isn't shaped as expected.
        try
        {
            var error = await response.Content.ReadFromJsonAsync<RedeemError>(ct);
            return error?.Error switch
            {
                "invalid-format" => PairingResult.Failure(
                    "Code-Format ungültig. Erwartet: XXX-XXX-XXX."),
                "invalid-or-expired" => PairingResult.Failure(
                    "Code ungültig oder abgelaufen. Bitte neuen Code generieren."),
                _ => PairingResult.Failure(
                    $"Server-Fehler: {error?.Error ?? "unbekannt"} (HTTP {(int)response.StatusCode})."),
            };
        }
        catch
        {
            return PairingResult.Failure(
                $"Unerwartete Server-Antwort (HTTP {(int)response.StatusCode}).");
        }
    }

    // ─── Wire-format DTOs ────────────────────────────────────────────
    // System.Text.Json camelCases by convention via JsonPropertyName.
    // Could also use JsonNamingPolicy globally, but explicit attributes
    // keep the wire-format visible at the type-definition.

    private sealed class RedeemRequest
    {
        [JsonPropertyName("code")]
        public required string Code { get; init; }
    }

    private sealed class RedeemSuccess
    {
        [JsonPropertyName("token")]
        public required string Token { get; init; }

        [JsonPropertyName("user")]
        public required RedeemUser User { get; init; }
    }

    private sealed class RedeemUser
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("email")]
        public required string Email { get; init; }
    }

    private sealed class RedeemError
    {
        [JsonPropertyName("error")]
        public string? Error { get; init; }
    }
}

/// <summary>
/// Result of a pairing-attempt. Discriminated via IsSuccess: when true
/// Token+DisplayName are populated; when false, ErrorMessage is set.
/// Records-based on purpose — immutable, value-equality, easy pattern-
/// matching in callers.
/// </summary>
public sealed record PairingResult
{
    public bool IsSuccess { get; init; }
    public string? Token { get; init; }
    public string? DisplayName { get; init; }
    public string? ErrorMessage { get; init; }

    public static PairingResult Success(string token, string displayName) =>
        new() { IsSuccess = true, Token = token, DisplayName = displayName };

    public static PairingResult Failure(string errorMessage) =>
        new() { IsSuccess = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Thrown for transport-level failures (network, DNS, timeout, 5xx).
/// Distinct exception-type so the UI can show a different message than
/// for server-side validation rejections (which return PairingResult.Failure).
/// </summary>
public sealed class PairingTransportException : Exception
{
    public PairingTransportException(string message, Exception? inner)
        : base(message, inner) { }
}