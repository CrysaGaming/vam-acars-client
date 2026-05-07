using System.Runtime.InteropServices;

namespace VamAcarsClient.Core;

/// <summary>
/// Snapshot of aircraft state read from SimConnect, sampled at the
/// read-loop frequency (1 Hz initially, configurable).
///
/// FIELD ORDER IS BINDING. SimConnect maps SimVars to struct-fields
/// positionally — the order in this struct must EXACTLY match the order
/// in which RegisterDataDefinition() registers the SimVars in
/// SimConnectClient. Reordering one without the other = nonsense data
/// at runtime, no compile error.
///
/// LayoutKind.Sequential + Pack=1 ensures no surprise padding from the
/// runtime that would shift offsets. Pack=1 is the standard for
/// SimConnect-marshalled structs.
///
/// All numeric fields are double (float64) because that's what
/// SimConnect's SIMCONNECT_DATATYPE_FLOAT64 returns. Even fields that
/// would be naturally bool or int (onGround, gearDown) come in as
/// double — SimConnect doesn't have a bool type. Convert in
/// downstream code, not here.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SimTelemetry
{
    // ─── Position ──────────────────────────────────────────────
    public double LatitudeDeg;
    public double LongitudeDeg;
    public double AltitudeFt;        // PLANE ALTITUDE (true MSL)
    public double AltitudeAglFt;     // PLANE ALT ABOVE GROUND

    // ─── Speed ─────────────────────────────────────────────────
    public double GroundSpeedKts;    // GROUND VELOCITY (kts)
    public double IndicatedAirspeedKts;
    public double TrueAirspeedKts;
    public double VerticalSpeedFpm;  // VERTICAL SPEED in fpm

    // ─── Attitude ──────────────────────────────────────────────
    public double HeadingTrueDeg;    // PLANE HEADING DEGREES TRUE
    public double PitchDeg;
    public double BankDeg;

    // ─── State (booleans-as-doubles per SimConnect convention) ─
    public double OnGround;          // 1.0 = on ground, 0.0 = airborne
    public double ParkingBrake;      // 1.0 = set, 0.0 = released
    public double GearDown;          // 1.0 = down, 0.0 = up
    public double FlapsPercent;      // 0..100

    // ─── Engine + throttle (averaged) ──────────────────────────
    public double EngineN1Avg;       // 0..100
    public double ThrottlePercent;   // 0..100

    // ─── Autopilot state ───────────────────────────────────────
    public double AutopilotMaster;       // 1.0 = on
    public double AutopilotAltLock;      // 1.0 = altitude-hold engaged
}

/// <summary>
/// IDs for SimConnect data-definitions and request-handles.
/// SimConnect uses uint-typed enums to identify what we registered;
/// these constants make calls type-safe instead of magic-number-laden.
/// </summary>
internal enum DataDefinitionId
{
    Telemetry = 1,
}

internal enum DataRequestId
{
    Telemetry = 1,
}