using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace VamAcarsClient.Core;

/// <summary>
/// Client for POST /api/acars/create-booking — Welle B / B5 phase 2.
///
/// Creates a Booking against a Route from the paired user's airline.
/// Triggered by the tray's OFP-import handler when the user clicks
/// "Buchung erstellen" on a matched-route banner.
///
/// # Usage
///
/// One-shot per click. The OFP-import flow first uses
/// <see cref="AirlineRoutesService"/> to populate the route catalogue
/// cache; on a match, the tray shows a confirm-button; this service
/// fires the POST when the button is clicked.
///
/// # Response contract
///
/// Three branches on the C# side (mapped to the server's HTTP shape):
///
///   - 200 success → returns a populated <see cref="CreateBookingResult"/>
///     with .Booking set and .Error null. Caller surfaces "Buchung
///     NGN901 erstellt" to the pilot.
///   - 4xx policy failure (400/404/409) → returns a result with
///     .Booking null and .Error populated (error-code + human-readable
///     German message). Caller surfaces the message verbatim.
///   - 401 auth failure → returns null, mirroring the contract of
///     ActiveBookingService / StatusService. Caller clears token,
///     prompts re-pair.
///
/// Throws only on transport failures (network, 5xx, timeout). Those
/// genuinely shouldn't happen in normal operation; the caller should
/// log and offer the user a retry.
///
/// # Why deserialize 4xx into a result rather than throw
///
/// The server-side endpoint returns structured error envelopes
/// ({ error: "machine-code", message: "human-de" }) specifically so
/// the client can show actionable messages to the pilot. Throwing on
/// 4xx would force the caller to parse the message out of an exception
/// — uglier than just returning the envelope.
///
/// 5xx is different: those signal server bugs or infrastructure
/// problems, not user-actionable errors. EnsureSuccessStatusCode-style
/// throwing is the right shape there.
///
/// # Why a separate service class (vs extending AirlineRoutesService)
///
/// Same per-endpoint-class pattern as the rest of Core. AirlineRoutes
/// is a read; create-booking is a write with different error semantics.
/// Mixing them would obscure the asymmetry.
/// </summary>
public sealed class CreateBookingService
{
    private readonly HttpClient _http;

    public CreateBookingService(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Create a booking against the given route. See class-level
    /// docstring for the three response branches.
    /// </summary>
    public async Task<CreateBookingResult?> CreateAsync(
        string token,
        string routeId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("token must not be empty", nameof(token));
        if (string.IsNullOrWhiteSpace(routeId))
            throw new ArgumentException("routeId must not be empty", nameof(routeId));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/acars/create-booking")
        {
            // System.Net.Http.Json serializes the anonymous DTO with
            // camelCase property names by default — but we use an
            // explicit object initializer to keep the wire shape
            // pinned even if defaults change in future runtime versions.
            Content = JsonContent.Create(new CreateBookingRequestBody { RouteId = routeId }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Caller will see null and trigger re-pair.
            return null;
        }

        // Both success and policy-failure responses carry a JSON body
        // we want to surface. Server's success shape is { booking: ... };
        // failure shape is { error, message, existingBookingId? }. We
        // deserialize one wrapper that has both as nullable members and
        // let the caller branch on which is set.
        if (response.IsSuccessStatusCode ||
            response.StatusCode == HttpStatusCode.BadRequest ||
            response.StatusCode == HttpStatusCode.NotFound ||
            response.StatusCode == HttpStatusCode.Conflict)
        {
            var result = await response.Content.ReadFromJsonAsync<CreateBookingResult>(ct);
            // Defense in depth — should never happen, but a server bug
            // returning empty body shouldn't crash the tray.
            return result ?? new CreateBookingResult();
        }

        // 5xx and unexpected codes: throw and let the caller handle as
        // a transport-class failure.
        response.EnsureSuccessStatusCode();
        // Unreachable after EnsureSuccessStatusCode but the compiler
        // needs a return.
        return null;
    }
}

/// <summary>
/// Outgoing request body for POST /api/acars/create-booking. The
/// server's zod schema accepts only the routeId in V1.
/// </summary>
internal sealed class CreateBookingRequestBody
{
    [JsonPropertyName("routeId")]
    public required string RouteId { get; init; }
}

/// <summary>
/// Response envelope for POST /api/acars/create-booking. Exactly one of
/// (Booking, Error) is populated; callers branch on which.
/// Success → Booking set, Error null.
/// Policy-failure (400/404/409) → Booking null, Error set.
/// </summary>
public sealed class CreateBookingResult
{
    [JsonPropertyName("booking")]
    public CreatedBooking? Booking { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>
    /// Only populated when Error == "active-booking-exists". The id of
    /// the booking that's currently blocking the new one — useful for
    /// future "switch to existing booking" UX, ignored in v1.
    /// </summary>
    [JsonPropertyName("existingBookingId")]
    public string? ExistingBookingId { get; init; }
}

/// <summary>
/// Successfully-created booking. The minimal subset the tray needs to
/// surface a confirmation ("Buchung NGN901 EDDK→EDDS erstellt") and
/// to track the id for any subsequent action (currently none — the
/// next Verbinden cycle picks it up via the active-booking endpoint).
/// </summary>
public sealed class CreatedBooking
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("flightNumber")]
    public required string FlightNumber { get; init; }

    [JsonPropertyName("departureIcao")]
    public required string DepartureIcao { get; init; }

    [JsonPropertyName("arrivalIcao")]
    public required string ArrivalIcao { get; init; }
}
