using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace VamAcarsClient.Core;

/// <summary>
/// Client for GET /api/acars/status.
///
/// Two purposes:
///   1. Smoke-test on launch: verify the stored token is still valid.
///      A 401 means the user revoked the pairing or rotated the token
///      on another device — caller should clear the local token and
///      re-prompt for pairing.
///   2. Server-time provider: every heartbeat in Milestone 3 will
///      include a client-side timestamp; the server rejects ones too
///      far from its clock. Calling status on launch lets the client
///      compare its clock to server-time and warn the user if drift
///      is severe (a stuck Windows-clock is a real failure mode).
///
/// Bearer-token is attached per-request via the AuthenticationHeaderValue
/// pattern. We don't bake the token into HttpClient.DefaultRequestHeaders
/// because tokens can rotate at runtime (re-pair) and the singleton
/// http-client lifetime outlives any single token.
/// </summary>
public sealed class StatusService
{
    private readonly HttpClient _http;

    public StatusService(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Returns the parsed status, null if the token was rejected (401),
    /// or throws on transport-failure. Caller distinguishes:
    ///   - Status object → connected, log it
    ///   - null         → token bad, clear+re-pair
    ///   - exception    → network issue, retry later
    /// </summary>
    public async Task<StatusResponse?> FetchAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("token must not be empty", nameof(token));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/acars/status");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StatusResponse>(ct);
    }
}

public sealed class StatusResponse
{
    [JsonPropertyName("user")]
    public required StatusUser User { get; init; }

    [JsonPropertyName("airline")]
    public StatusAirline? Airline { get; init; }

    [JsonPropertyName("preferredNetwork")]
    public required string PreferredNetwork { get; init; }

    /// <summary>ISO-8601. Used in Milestone 3 to detect clock-drift before
    /// heartbeating starts.</summary>
    [JsonPropertyName("serverTime")]
    public required DateTimeOffset ServerTime { get; init; }
}

public sealed class StatusUser
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("email")]
    public required string Email { get; init; }
}

public sealed class StatusAirline
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("icao")]
    public required string Icao { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}