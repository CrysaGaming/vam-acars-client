using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace VamAcarsClient.Core;

/// <summary>
/// Client for GET /api/acars/airline-routes — Welle B / B5 phase 2.
///
/// Returns the airline-routes catalogue for the paired user's airline.
/// The tray app fetches this once at app-startup (after a token is
/// present) and caches the result on <c>AcarsClientState.AirlineRoutes</c>.
/// The OFP-import handler then does an in-memory lookup against the
/// cache to decide whether the pasted OFP matches a route the airline
/// flies.
///
/// # Usage
///
/// One-shot fetch, no polling. App.xaml.cs:OnStartup triggers it via
/// AcarsClientService after pairing-state turns paired. Subsequent
/// OFP-imports hit the cache without further server calls. A restart
/// of the tray app re-fetches; admins adding new routes mid-session
/// won't see them until the user reopens the app. Acceptable v1 —
/// route-catalogue changes are infrequent and admin-driven.
///
/// # Response contract
///
/// Returns the envelope on 2xx. The Routes array may be empty:
///   - Solo pilots (User.airlineId null on the server) get
///     { routes: [] } — they paired the ACARS client for free-flight
///     use but have no airline-routes catalogue.
///   - Brand-new airline with no routes yet → also empty array.
/// Either way the tray treats empty-routes as "no suggestions
/// available" and skips the OFP-match step gracefully.
///
/// Returns null on 401 (same contract as ActiveBookingService /
/// StatusService) — caller should clear the token and prompt for
/// re-pair.
///
/// Throws on transport failures. Caller logs and proceeds without
/// the cache; OFP-import still works, just without suggestions.
///
/// # Why a separate service class
///
/// Same per-endpoint-class pattern as ActiveBookingService and
/// StatusService — one class owns one endpoint's request shape,
/// response DTOs, and error semantics. Testable in isolation,
/// reusable across hosts (Cli + Tray).
/// </summary>
public sealed class AirlineRoutesService
{
    private readonly HttpClient _http;

    public AirlineRoutesService(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Fetch the airline-routes catalogue. Returns the envelope (with
    /// possibly-empty Routes array), null if the token was rejected,
    /// or throws on transport failure.
    /// </summary>
    public async Task<AirlineRoutesResponse?> FetchAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("token must not be empty", nameof(token));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/acars/airline-routes");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AirlineRoutesResponse>(ct);
    }
}

/// <summary>
/// Envelope returned by /api/acars/airline-routes. Routes may be
/// empty — see file-level docstring for the conditions.
/// </summary>
public sealed class AirlineRoutesResponse
{
    [JsonPropertyName("routes")]
    public required IReadOnlyList<AirlineRoute> Routes { get; init; }
}

/// <summary>
/// Single route entry. Mirrors the server's response shape in
/// apps/web/app/api/acars/airline-routes/route.ts.
///
/// AircraftTypeIcao is nullable — generic / type-flexible routes have
/// no specific type requirement (charter, ferry routes etc.). Matters
/// to the OFP-suggestion UI: when the route has a type requirement
/// AND the tray knows the loaded aircraft type, we can pre-warn about
/// a mismatch BEFORE the pilot creates the booking (rather than
/// surfacing it at Verbinden via the B4 dialog).
/// </summary>
public sealed class AirlineRoute
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("flightNumber")]
    public required string FlightNumber { get; init; }

    [JsonPropertyName("departureIcao")]
    public required string DepartureIcao { get; init; }

    [JsonPropertyName("arrivalIcao")]
    public required string ArrivalIcao { get; init; }

    [JsonPropertyName("aircraftTypeIcao")]
    public string? AircraftTypeIcao { get; init; }
}
