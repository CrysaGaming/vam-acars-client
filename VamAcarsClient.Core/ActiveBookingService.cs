using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace VamAcarsClient.Core;

/// <summary>
/// Client for GET /api/acars/active-booking — Welle B / B4 phase 2.
///
/// Returns the user's currently-active booking (states Created /
/// SimBriefDispatched / InProgress) with the booked aircraft details
/// the pre-flight checklist compares against the sim-loaded aircraft.
///
/// # Usage
///
/// Called once at session-startup by the tray-app's pre-connect flow:
/// after ProbeSimAsync identifies the loaded aircraft, FetchAsync pulls
/// the active booking. If both are populated AND the aircraft-types
/// differ, the tray surfaces the aircraft-substitution dialog (B4 P2B).
///
/// # Response contract
///
/// Returns the deserialized envelope on 2xx. The `Booking` property
/// inside is nullable — `{ "booking": null }` from the server signals
/// "no active booking, free flight" and is a NORMAL state (not an
/// error). Callers should treat null-booking as "skip mismatch check"
/// rather than as a failure.
///
/// Returns null on 401 — same contract as StatusService.FetchAsync —
/// caller should clear the token and prompt for re-pair.
///
/// Throws on transport failures (network, 5xx, timeout). Caller should
/// log and proceed without the booking-check rather than aborting
/// connect — losing the mismatch dialog isn't worth blocking a flight.
///
/// # Why a separate service class
///
/// Mirrors the per-endpoint-class pattern already established by
/// StatusService / PairingService / ChangelogFetcher. Each class owns
/// one endpoint's request shape, response DTOs, and error semantics.
/// Tested in isolation, reusable across hosts (Cli + Tray).
///
/// # No retry logic
///
/// One-shot pre-connect call. If the server is unreachable here, the
/// network is likely too sick for a flight session to succeed anyway,
/// and the user is better served by seeing the connect-flow proceed
/// (where retries/queuing are first-class) than by silently waiting
/// on a booking-info call to retry.
/// </summary>
public sealed class ActiveBookingService
{
    private readonly HttpClient _http;

    public ActiveBookingService(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Fetch the active booking. Returns the envelope (with possibly-null
    /// Booking inside), null if the token was rejected, or throws on
    /// transport failure.
    /// </summary>
    public async Task<ActiveBookingResponse?> FetchAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("token must not be empty", nameof(token));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/acars/active-booking");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ActiveBookingResponse>(ct);
    }
}

/// <summary>
/// Envelope returned by /api/acars/active-booking. Booking is null when
/// the user has no active flight booked — see file-level docstring for
/// why null-booking is a normal state, not an error.
/// </summary>
public sealed class ActiveBookingResponse
{
    [JsonPropertyName("booking")]
    public ActiveBooking? Booking { get; init; }
}

/// <summary>
/// The booking itself, when present. Mirrors the server's response shape
/// in apps/web/app/api/acars/active-booking/route.ts.
///
/// Aircraft may be null even when the booking exists — routes without a
/// specific aircraft assignment (charter, type-flexible routes) return
/// `aircraft: null`, signalling to the tray that there's nothing to
/// compare the sim-loaded aircraft against. In that case, the mismatch
/// dialog is skipped entirely (free-aircraft choice on the route is
/// already the pilot's prerogative).
/// </summary>
public sealed class ActiveBooking
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>One of Created | SimBriefDispatched | InProgress.</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("flightNumber")]
    public required string FlightNumber { get; init; }

    [JsonPropertyName("departureIcao")]
    public required string DepartureIcao { get; init; }

    [JsonPropertyName("arrivalIcao")]
    public required string ArrivalIcao { get; init; }

    [JsonPropertyName("aircraft")]
    public ActiveBookingAircraft? Aircraft { get; init; }
}

/// <summary>
/// Aircraft tuple from the booking's route. The `Type` is the ICAO
/// designator (e.g. "A20N", "B738") that the tray compares against the
/// sim-loaded aircraft type. `Registration` is the assigned tail number;
/// shown in the dialog UI so the pilot can confirm which physical
/// airframe was booked.
/// </summary>
public sealed class ActiveBookingAircraft
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("registration")]
    public required string Registration { get; init; }
}
