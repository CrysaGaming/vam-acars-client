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

    // ─── Convenience properties (not marshalled) ──────────────
    // Trim trailing nulls/whitespace and provide fallback for empty
    // values. Default-aircraft (no model loaded) returns "" from
    // SimConnect; we substitute "UNKN" so downstream serialization
    // doesn't violate the schema's min(1) length constraint.
    public readonly string AircraftType =>
        string.IsNullOrWhiteSpace(AtcModel) ? "UNKN" : AtcModel.Trim();

    public readonly string AircraftRegistration =>
        string.IsNullOrWhiteSpace(AtcId) ? "UNKN" : AtcId.Trim();
}

internal enum DataDefinitionId
{
    Telemetry = 1,
}

internal enum DataRequestId
{
    Telemetry = 1,
}