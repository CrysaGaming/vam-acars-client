using System.Text.RegularExpressions;

namespace VamAcarsClient.Core;

/// <summary>
/// Parsed flight plan from an OFP (Operational Flight Plan) text dump.
/// All fields nullable — a pasted OFP might have departure but not
/// arrival, or callsign but no cruise altitude. Populates whatever the
/// regex heuristics can confidently extract; the consumer (MainWindow)
/// fills only those form fields whose values came back non-null.
///
/// Properties mirror <see cref="FlightContext"/>'s shape so adapter code
/// can copy fields one-to-one without translation.
/// </summary>
public sealed record OfpFlightPlan
{
    public string? Callsign { get; init; }
    public string? FlightNumber { get; init; }
    public string? DepartureIcao { get; init; }
    public string? ArrivalIcao { get; init; }
    public string? AircraftType { get; init; }
    public int? CruiseAltitudeFt { get; init; }
    public string? FlightRules { get; init; }
}

/// <summary>
/// Heuristic parser for OFP text dumps from SimBrief, FlightAware,
/// PFPX, ATC briefings, etc. (option #12). Not a strict-format parser
/// — instead, runs a battery of regexes against the input and reports
/// whatever fields it can identify with high confidence.
///
/// # Why heuristic
///
/// Pilots paste OFPs from many different tools, each with its own
/// format. SimBrief alone has 6+ output styles ("ATC", "Crew Pages",
/// "Operational", "PDF text dump"). Building a parser per format
/// would be a maintenance nightmare; the union of all patterns is
/// short, distinctive enough that false-positives are rare, and
/// resilient to format changes — when SimBrief tweaks its layout,
/// our regexes typically still catch the key fields.
///
/// # What we extract
///
/// Hard-required for a useful import:
///   - Callsign (e.g. "DLH900", "NGN901", "BAW 211")
///   - Departure ICAO (4-char code preceded by "DEP", "DEPARTURE",
///     "FROM", or in an "EDDF/EDDM" pair-shape)
///   - Arrival ICAO (same patterns)
///
/// Nice-to-have (filled when present):
///   - Cruise altitude (FL360, "CRZ ALT 36000FT", etc.)
///   - Aircraft ICAO type (A320, B738, etc.)
///   - Flight rules (IFR / VFR)
///   - Flight number (numeric portion of callsign, when separable)
///
/// # What we DON'T extract
///
/// - Route waypoints. The form has no field for them, and SimBrief
///   route strings are 100+ chars of cryptic ATC notation that we
///   can't usefully render in the FLUG-KONTEXT TextBoxes.
/// - Alternates. Same — no UI surface, and the heartbeat schema
///   doesn't carry them either.
/// - Fuel, payload, ZFW. Pilot-side info, not flight-plan info.
/// </summary>
public static class OfpParser
{
    // ─── Regex registry ──────────────────────────────────────────────
    //
    // Compiled once at class-init via static-readonly. RegexOptions.
    // IgnoreCase because OFPs vary on capitalization ("DEPARTURE"
    // vs "Departure" vs "departure"). Multiline so ^/$ match line
    // edges, useful when patterns key off line-starts.
    //
    // Patterns are intentionally conservative: we'd rather miss a
    // field than fill it with garbage. If a field can't be confidently
    // identified, the consumer just keeps the form's existing value.

    /// <summary>
    /// Callsign patterns. Covers:
    ///   - Explicit label: "Callsign: NGN901", "ATC CALLSIGN: DLH900"
    ///   - SimBrief brackets: "[ DLH900 ]"
    ///   - "FLT NO" / "FLIGHT NO": "FLT NO   : DLH900"
    /// 3-7 alphanumeric chars total — covers typical airline callsigns
    /// (3-letter ICAO + 1-4 digits) and bizjet tail-number style.
    /// </summary>
    private static readonly Regex CallsignRegex = new(
        @"(?:^|\s)(?:CALLSIGN|FLT\s*NO|FLIGHT\s*NO|FLIGHT)\s*[:#]?\s*([A-Z]{2,3}\d{1,4}[A-Z]?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// SimBrief-style header bracket: "[ DLH900 ]" or "[DLH900]".
    /// Distinct regex from CallsignRegex because the surrounding context
    /// is different (no label keyword) — combining them into one pattern
    /// would force complex alternation.
    /// </summary>
    private static readonly Regex BracketCallsignRegex = new(
        @"\[\s*([A-Z]{2,3}\d{1,4}[A-Z]?)\s*\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Departure ICAO. Patterns:
    ///   - "DEPARTURE: EDDF" / "DEP: EDDF" / "FROM: EDDF"
    ///   - "DEPARTURE  : EDDF / FRA / FRANKFURT" (SimBrief format)
    /// 4-char alpha code immediately after the label.
    /// </summary>
    private static readonly Regex DepartureRegex = new(
        @"(?:^|\s)(?:DEPARTURE|DEP|FROM|ORIG|ORIGIN)\s*[:#]?\s*([A-Z]{4})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>Same shape, arrival side.</summary>
    private static readonly Regex ArrivalRegex = new(
        @"(?:^|\s)(?:ARRIVAL|ARR|TO|DEST|DESTINATION)\s*[:#]?\s*([A-Z]{4})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// "EDDF/EDDM" or "EDDF / EDDM" pair shorthand — common in route
    /// summary lines. Two 4-char ICAO codes separated by a slash with
    /// optional surrounding spaces. Used as a fallback when the
    /// labeled patterns don't match (some OFPs only list the pair).
    /// </summary>
    private static readonly Regex IcaoPairRegex = new(
        @"\b([A-Z]{4})\s*[/\-]\s*([A-Z]{4})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Cruise altitude. Captures both flight-level ("FL360") and
    /// foot-style ("36000FT", "36000 ft", "CRZ ALT 36000"). FL gets
    /// multiplied by 100; foot-style is taken as-is.
    /// </summary>
    private static readonly Regex FlightLevelRegex = new(
        @"(?:^|\s)(?:CRUISE|CRZ|FL|LEVEL|ALT)\s*[:#]?\s*(?:FL\s*)?(\d{2,3})(?:\s*FT|0?(?!\d))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Bare flight-level shorthand "FL360" — used when the cruise label
    /// isn't present but a level appears in route context.
    /// </summary>
    private static readonly Regex BareFlightLevelRegex = new(
        @"\bFL\s*(\d{2,3})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Aircraft ICAO type. Covers "AIRCRAFT: A320", "TYPE: B738".
    /// Restricted to the 4-char ICAO designator pattern ([A-Z]\d{2,3}
    /// or [A-Z]{2}\d{2}) — wider matches would catch garbage like
    /// version numbers.
    /// </summary>
    private static readonly Regex AircraftTypeRegex = new(
        @"(?:^|\s)(?:AIRCRAFT|TYPE|A\/C|EQUIPMENT)\s*[:#]?\s*([A-Z]\d[A-Z\d]{1,2}|[A-Z]{2}\d{2}|[BAC]\d{3})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Flight rules. SimBrief reports "RULES: IFR" or similar; ATC
    /// briefings often have just "IFR" or "VFR" on its own line.
    /// </summary>
    private static readonly Regex FlightRulesRegex = new(
        @"(?:^|\s)(?:RULES|FLIGHT\s*RULES|FR)\s*[:#]?\s*(IFR|VFR|YFR|ZFR)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Run the heuristic battery against the input and return whatever
    /// fields came back. Returns null if the input is empty or whitespace-
    /// only — there's nothing for the caller to do with that.
    ///
    /// The result may have all fields null if no patterns matched (e.g.
    /// the user pasted random text). That's still a valid result; the
    /// caller can show "no fields recognised" and skip the form-fill.
    /// </summary>
    public static OfpFlightPlan? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Pre-uppercase a copy for case-insensitive but origin-preserving
        // capture (we still return UpperInvariant ICAO codes for form
        // consistency, but uppercasing the source first means downstream
        // .ToUpper() calls become no-ops).
        var input = text;

        // ─── Callsign ────────────────────────────────────────────────
        var callsign = MatchFirst(CallsignRegex, input)
            ?? MatchFirst(BracketCallsignRegex, input);
        callsign = callsign?.ToUpperInvariant();

        // Flight number = trailing digits of callsign, if separable.
        // E.g. "DLH900" → "900", "NGN1234A" → "1234". Useful for the
        // server's flight-number field separately from the radio callsign.
        string? flightNumber = null;
        if (callsign is not null)
        {
            var digitsMatch = Regex.Match(callsign, @"\d+");
            if (digitsMatch.Success) flightNumber = digitsMatch.Value;
        }

        // ─── Departure / Arrival ICAO ────────────────────────────────
        var departure = MatchFirst(DepartureRegex, input)?.ToUpperInvariant();
        var arrival = MatchFirst(ArrivalRegex, input)?.ToUpperInvariant();

        // Pair-fallback: if either is still null, scan for an "EDDF/EDDM"
        // shaped match and assign in order. Don't override an already-
        // labeled match — labels are more reliable than pair shorthand.
        if (departure is null || arrival is null)
        {
            var pair = IcaoPairRegex.Match(input);
            if (pair.Success)
            {
                departure ??= pair.Groups[1].Value.ToUpperInvariant();
                arrival ??= pair.Groups[2].Value.ToUpperInvariant();
            }
        }

        // ─── Cruise altitude ─────────────────────────────────────────
        // Try the labeled regex first, then bare "FL\d+". The labeled
        // regex captures e.g. "CRUISE: FL360" or "ALT 36000FT" — the
        // captured number must be normalised to feet.
        int? cruiseFt = null;
        var labeledLevel = MatchFirst(FlightLevelRegex, input);
        if (labeledLevel is not null && int.TryParse(labeledLevel, out var n1))
        {
            // Heuristic: 2-3 digit values are flight-levels (× 100);
            // 4-5 digit values are already feet. Threshold at 1000.
            cruiseFt = n1 < 1000 ? n1 * 100 : n1;
        }
        if (cruiseFt is null)
        {
            var bareLevel = MatchFirst(BareFlightLevelRegex, input);
            if (bareLevel is not null && int.TryParse(bareLevel, out var n2))
            {
                cruiseFt = n2 * 100; // bare FL is always × 100
            }
        }

        // ─── Aircraft type / flight rules ────────────────────────────
        var aircraft = MatchFirst(AircraftTypeRegex, input)?.ToUpperInvariant();
        var rules = MatchFirst(FlightRulesRegex, input)?.ToUpperInvariant();

        return new OfpFlightPlan
        {
            Callsign = callsign,
            FlightNumber = flightNumber,
            DepartureIcao = departure,
            ArrivalIcao = arrival,
            AircraftType = aircraft,
            CruiseAltitudeFt = cruiseFt,
            FlightRules = rules,
        };
    }

    /// <summary>
    /// Run a single regex and return the first capture group's value,
    /// or null if no match. Helper that flattens the ceremony of
    /// Match().Success + Groups[1].Value across the parse path.
    /// </summary>
    private static string? MatchFirst(Regex regex, string input)
    {
        var match = regex.Match(input);
        if (!match.Success) return null;
        if (match.Groups.Count < 2) return null;
        var captured = match.Groups[1].Value;
        return string.IsNullOrWhiteSpace(captured) ? null : captured.Trim();
    }
}
