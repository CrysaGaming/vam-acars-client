using System.Runtime.InteropServices;

namespace VamAcarsClient.Core;

/// <summary>
/// Snapshot of aircraft state read from SimConnect.
///
/// FIELD ORDER IS BINDING — must match RegisterTelemetryDefinition()
/// in SimConnectClient exactly.
///
/// Mixed types: doubles for SimConnect FLOAT64 SimVars, fixed-size
/// char arrays for STRING32/STRING64 SimVars. Strings need explicit
/// MarshalAs(ByValTStr) so the runtime sizes them correctly during
/// inter-op marshalling — without it, the fixed-byte-budget after
/// the strings would be wrong and downstream fields would shift.
///
/// Strings are zero-terminated; .NET's default marshalling of
/// fixed-size char-arrays handles trim-to-null automatically when
/// you read the field through a .NET string property. We expose
/// helper-properties on top of the raw fields for cleaner consumer
/// code (AircraftType, AircraftRegistration).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
public struct SimTelemetry
{
    // ─── Position (FLOAT64) ────────────────────────────────────
    public double LatitudeDeg;
    public double LongitudeDeg;
    public double AltitudeFt;
    public double AltitudeAglFt;

    // ─── Speed (FLOAT64) ───────────────────────────────────────
    public double GroundSpeedKts;
    public double IndicatedAirspeedKts;
    public double TrueAirspeedKts;
    public double VerticalSpeedFpm;

    // ─── Attitude (FLOAT64) ────────────────────────────────────
    public double HeadingTrueDeg;
    public double PitchDeg;
    public double BankDeg;

    // ─── State (booleans-as-doubles) ───────────────────────────
    public double OnGround;
    public double ParkingBrake;
    public double GearDown;
    public double FlapsPercent;

    // ─── Engine + throttle ─────────────────────────────────────
    public double EngineN1Avg;
    public double ThrottlePercent;
    public double FuelTotalKg;       // FUEL TOTAL QUANTITY WEIGHT (kg)

    // ─── Autopilot ─────────────────────────────────────────────
    public double AutopilotMaster;
    public double AutopilotAltLock;

    // ─── Environment & Wind (Welle B — B1) ─────────────────────
    // Sim-side weather snapshot. Server pairs this with a real-METAR
    // fetch at PIREP-time to render the "Weather Comparison" section
    // on the analysis page (Sim 270/65 OAT -54°C vs Real 280/72 -56°C).
    //
    // Field order is binding — must match the AddToDataDefinition calls
    // in SimConnectClient.RegisterTelemetryDefinition exactly. New fields
    // appended here MUST be appended there in the same sequence (and
    // vice-versa), or the FLOAT64 slots silently shift and downstream
    // values become garbage.
    //
    // Units chosen to match the server's heartbeat zod schema directly,
    // so HeartbeatService.BuildHeartbeat can pass them through with at
    // most a Math.Round (server requires int for all three of these):
    //   - WindVelocity in knots (server: windSpeedKts, nonnegative int)
    //   - WindDirection in degrees true (server: windDirection, 0-360 int)
    //   - Temperature in Celsius (server: oatCelsius, signed int — can
    //     be deeply negative at cruise altitude)
    //   - Pressure in millibars (server-side field arrives in Welle B
    //     Phase 2 — DB column ambientPressureMb. Sending now is safe:
    //     zod strips unknown fields by default, so a pre-B2 server just
    //     ignores it.)
    //
    // Why BAROMETER PRESSURE (millibars) over AMBIENT PRESSURE (inHg):
    // AMBIENT PRESSURE in SimConnect is actually inches-of-mercury
    // despite the name suggesting absolute pressure; BAROMETER PRESSURE
    // is the standard sea-level QNH in mb that matches METAR's Q-group
    // directly. Comparing apples to apples in the UI later.
    public double AmbientWindVelocityKts;
    public double AmbientWindDirectionDeg;
    public double AmbientTemperatureCelsius;
    public double AmbientPressureMb;

    // ─── Radios (Welle B — B2 phase 1) ─────────────────────────
    // COM1 + NAV1 frequencies for ATC-session tracking. The server
    // matches these against the VATSIM ATC datafeed (controller online
    // + frequency + position) to attribute the pilot to specific ATC
    // stations during the flight — "12:42 EDDF_GND 121.6 (5 min)" lines
    // on the PIREP-page.
    //
    // SimConnect's COM/NAV ACTIVE FREQUENCY simvars are pulled with the
    // "Hz" unit so we get full-precision integer Hertz as a double
    // (e.g. 121.605 MHz → 121_605_000.0). Server-side we convert to MHz
    // with 3-decimal precision to match aviation radio convention. Why
    // Hz over BCD16: BCD-encoding can't represent 8.33-kHz channel
    // spacing (121.605, 121.610, …) which is mandatory in European
    // airspace since 2018. Hz-as-double handles it natively.
    //
    // COM1 is the primary radio. COM1 Standby is included because pilots
    // pre-tune the next controller's frequency before contacting them,
    // and capturing standby lets the matcher reason about hand-offs
    // ("the pilot was on EDDF_TWR but had EDDF_DEP standby → expected
    // hand-off"). NAV1 active captures ILS frequencies during approach,
    // which the future v2 matcher could use to confirm which runway the
    // pilot intended (ILS-frequency-to-runway mapping is well-known per
    // ICAO databases).
    //
    // Sending now is safe pre-server: zod strips unknown fields by
    // default, so a pre-B2 server just ignores the new payload block.
    public double Com1ActiveFreqHz;
    public double Com1StandbyFreqHz;
    public double Nav1ActiveFreqHz;

    // ─── Aircraft identity (STRING32) ──────────────────────────
    // ATC MODEL = ICAO type designator (e.g. "C172", "A20N", "B738").
    // ATC ID    = aircraft registration (e.g. "D-EXXX", "N12345").
    // Fixed 32-byte buffers, zero-terminated. SimConnect's STRING32
    // MUST match this layout-size or marshalling silently corrupts
    // adjacent fields.
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string AtcModel;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string AtcId;

    // ─── Aircraft TITLE (STRING256) — M3.8 ─────────────────────
    // Full aircraft title from aircraft.cfg, e.g.
    //   "Asobo A320neo Lufthansa D-AINY"
    //   "FlyByWire A32NX"
    //   "Cessna 172 Skyhawk Asobo Original"
    // Often the most-reliable signal for type identification, especially
    // when ATC MODEL leaks a localization-token like "ATCCOM.AC_MODEL".
    // The server's aircraft-type resolver (M3.8) pattern-matches this
    // first against its regex table.
    //
    // STRING256 chosen over STRING128: real titles routinely run 60-100
    // chars; a developer-mode aircraft-folder name appended makes it
    // longer still. 256 is comfortable headroom; the field is sent
    // once per heartbeat so the wire-cost is irrelevant.
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string AtcTitle;

    // ─── Convenience properties (not marshalled) ──────────────
    // Trim trailing nulls/whitespace and provide fallback for empty
    // values. Default-aircraft (no model loaded) returns "" from
    // SimConnect; we substitute "UNKN" so downstream serialization
    // doesn't violate the schema's min(1) length constraint.
    public readonly string AircraftType =>
        string.IsNullOrWhiteSpace(AtcModel) ? "UNKN" : AtcModel.Trim();

    public readonly string AircraftRegistration =>
        string.IsNullOrWhiteSpace(AtcId) ? "UNKN" : AtcId.Trim();

    /// <summary>
    /// Trimmed aircraft title or null. Unlike AircraftType/Registration
    /// above this returns null rather than "UNKN" because the server
    /// schema treats `aircraft.title` as `.optional()` — sending null
    /// just omits the field entirely (Zod accepts), whereas "UNKN"
    /// would feed pattern-matching a known-bad sentinel.
    /// </summary>
    public readonly string? AircraftTitle =>
        string.IsNullOrWhiteSpace(AtcTitle) ? null : AtcTitle.Trim();
}

internal enum DataDefinitionId
{
    Telemetry = 1,
}

internal enum DataRequestId
{
    Telemetry = 1,
}