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