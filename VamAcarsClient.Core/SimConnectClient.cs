using Microsoft.FlightSimulator.SimConnect;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VamAcarsClient.Core;

/// <summary>
/// Manages the SimConnect connection to MSFS. Read-only client — we
/// only consume telemetry, never write back to the sim (no autopilot
/// commands, no panel-state changes). Read-only design avoids whole
/// classes of liability questions ("did your client cause my crash?").
///
/// THREADING MODEL — important to internalize:
///
///   SimConnect is fundamentally a Win32 message-pump API. It needs a
///   Windows window-handle (HWND) to receive notifications, OR a manual
///   "ReceiveMessage()" pump on a thread dedicated to that. Console
///   apps don't have a window-handle. We use the manual-pump approach.
///
///   Layout:
///     - Pump-thread: sleeps on the SimConnect event-handle, calls
///       ReceiveMessage() when MSFS pings, dispatches to our handlers.
///       The handlers run on this thread.
///     - Main thread (or read-loop thread): periodically calls
///       RequestDataOnSimObject() to ask for fresh telemetry. The
///       reply arrives async on the pump-thread.
///
///   Latest telemetry is stored in a thread-safe field; consumers read
///   it from any thread. This is "eventually consistent" — when the
///   read-loop fires faster than SimConnect replies, we re-read the
///   same snapshot, which is fine (we're polling, not streaming).
///
/// LIFECYCLE:
///   1. Connect() → opens SimConnect, registers data-def, starts pump.
///   2. Pump-thread runs in background until Disconnect() or sim-exit.
///   3. RequestTelemetry() pulls a fresh snapshot (fire-and-forget).
///   4. LatestTelemetry returns the most recent reply (or null).
///   5. Disconnect() unregisters callbacks, stops pump, disposes SC.
///
/// MSFS-NOT-RUNNING: Connect() throws COMException if the sim isn't
/// running. Caller should catch it and retry on a timer.
/// </summary>
public sealed class SimConnectClient : IDisposable
{
    private SimConnect? _sc;
    private Thread? _pumpThread;
    private CancellationTokenSource? _pumpCts;
    private readonly object _telemetryLock = new();
    private SimTelemetry? _latestTelemetry;

    /// <summary>
    /// Most recent telemetry snapshot received from MSFS, or null if
    /// no reply has arrived yet. Thread-safe.
    /// </summary>
    public SimTelemetry? LatestTelemetry
    {
        get
        {
            lock (_telemetryLock) return _latestTelemetry;
        }
    }

    /// <summary>True if Connect() succeeded and Disconnect() hasn't been called.</summary>
    public bool IsConnected => _sc is not null;

    // SimConnect's "open" call requires a Win32 window-handle for the
    // event-notification mechanism — but it accepts IntPtr.Zero, which
    // tells it to create an internal worker-window. We use the latter
    // to avoid pulling in WinForms/WPF as a dependency.
    private const string AppName = "VamAcarsClient";
    private const uint WM_USER_SIMCONNECT = 0x0402; // arbitrary, > WM_USER

    public event Action<SimTelemetry>? TelemetryReceived;
    public event Action<string>? ConnectionLost;

    /// <summary>
    /// Open the SimConnect connection. Throws COMException with
    /// HResult 0x800401F0 if MSFS isn't running. Caller catches and
    /// retries on a timer if appropriate.
    /// </summary>
    public void Connect()
    {
        if (_sc is not null)
            throw new InvalidOperationException("Already connected.");

        // IntPtr.Zero = no window-handle, SimConnect creates an internal
        // worker window. WM_USER_SIMCONNECT is the message-id it'll post
        // for events but since we use the manual receive-loop pattern
        // (see RunPumpLoop), the value is largely cosmetic.
        _sc = new SimConnect(
            AppName,
            IntPtr.Zero,
            WM_USER_SIMCONNECT,
            null, // ManualResetEvent: we use our own loop instead
            0);

        // Wire up the events we care about.
        _sc.OnRecvOpen += OnRecvOpen;
        _sc.OnRecvQuit += OnRecvQuit;
        _sc.OnRecvException += OnRecvException;
        _sc.OnRecvSimobjectData += OnRecvSimobjectData;

        RegisterTelemetryDefinition(_sc);

        // Start the pump-thread. We use a dedicated foreground thread
        // (IsBackground=false would prevent process-exit; we want
        // background so process can clean up on user-quit).
        _pumpCts = new CancellationTokenSource();
        _pumpThread = new Thread(() => RunPumpLoop(_pumpCts.Token))
        {
            Name = "SimConnect-Pump",
            IsBackground = true,
        };
        _pumpThread.Start();
    }

    /// <summary>
    /// Register the SimVar list that fills SimTelemetry. The order MUST
    /// match the field-order in SimTelemetry — SimConnect writes
    /// positionally into the struct.
    /// </summary>
    private static void RegisterTelemetryDefinition(SimConnect sc)
    {
        // Helper: each AddToDataDefinition call appends one SimVar to
        // the named definition. Args:
        //   def-id, simvar-name, units, datatype, epsilon (0=always-send),
        //   datum-id (DEFAULT for sequential)
        void Add(string name, string units) =>
            sc.AddToDataDefinition(
                DataDefinitionId.Telemetry,
                name,
                units,
                SIMCONNECT_DATATYPE.FLOAT64,
                0.0f,
                SimConnect.SIMCONNECT_UNUSED);

        // Order matches SimTelemetry exactly. If you reorder one, reorder both.
        Add("PLANE LATITUDE", "degrees");
        Add("PLANE LONGITUDE", "degrees");
        Add("PLANE ALTITUDE", "feet");
        Add("PLANE ALT ABOVE GROUND", "feet");

        Add("GROUND VELOCITY", "knots");
        Add("AIRSPEED INDICATED", "knots");
        Add("AIRSPEED TRUE", "knots");
        Add("VERTICAL SPEED", "feet per minute");

        Add("PLANE HEADING DEGREES TRUE", "degrees");
        Add("PLANE PITCH DEGREES", "degrees");
        Add("PLANE BANK DEGREES", "degrees");

        Add("SIM ON GROUND", "bool");
        Add("BRAKE PARKING POSITION", "bool");
        Add("GEAR HANDLE POSITION", "bool");
        Add("FLAPS HANDLE PERCENT", "percent");

        Add("ENG N1 RPM:1", "percent");
        Add("GENERAL ENG THROTTLE LEVER POSITION:1", "percent");

        Add("AUTOPILOT MASTER", "bool");
        Add("AUTOPILOT ALTITUDE LOCK", "bool");

        // Tell SimConnect the C# struct it should marshal into. Generic
        // type binds the C-side definition to our managed struct layout.
        sc.RegisterDataDefineStruct<SimTelemetry>(DataDefinitionId.Telemetry);
    }

    /// <summary>
    /// Request a fresh telemetry snapshot. Fire-and-forget: the reply
    /// arrives async on the pump-thread and updates LatestTelemetry +
    /// fires TelemetryReceived. Safe to call from any thread.
    /// </summary>
    public void RequestTelemetry()
    {
        if (_sc is null) return; // Not connected; silent no-op.

        // SIMCONNECT_PERIOD.ONCE = one-shot reply, no auto-repeat.
        // SIMCONNECT_OBJECT_ID_USER = the user's aircraft.
        _sc.RequestDataOnSimObject(
            DataRequestId.Telemetry,
            DataDefinitionId.Telemetry,
            SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.ONCE,
            SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
            0, 0, 0);
    }

    /// <summary>
    /// Pump-thread loop. SimConnect's ReceiveMessage() blocks for a
    /// short period waiting for incoming data; our token-poll
    /// inbetween lets us shut down cleanly on Disconnect().
    /// </summary>
    private void RunPumpLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _sc?.ReceiveMessage();
            }
            catch (COMException ex)
            {
                // Connection died (sim crashed, user quit MSFS).
                // Surface to caller, exit pump.
                ConnectionLost?.Invoke($"SimConnect-Verbindung verloren: 0x{ex.HResult:X8}");
                return;
            }
            catch (Exception ex)
            {
                ConnectionLost?.Invoke($"Pump-Loop-Fehler: {ex.Message}");
                return;
            }

            // ReceiveMessage is non-blocking when no data is queued, so
            // we sleep a bit to avoid pegging a CPU core. 50ms gives
            // good responsiveness vs. CPU-load tradeoff. SimConnect
            // delivers data when MSFS has it ready, not on our schedule.
            try
            {
                Thread.Sleep(50);
            }
            catch (ThreadInterruptedException)
            {
                // Disconnect() interrupted us — exit cleanly.
                return;
            }
        }
    }

    // ─── SimConnect event handlers (called on pump-thread) ──────────

    private void OnRecvOpen(SimConnect _, SIMCONNECT_RECV_OPEN data)
    {
        // Connection-handshake completed. data.szApplicationName is
        // "Microsoft Flight Simulator" or similar — useful for logging.
    }

    private void OnRecvQuit(SimConnect _, SIMCONNECT_RECV data)
    {
        // User quit MSFS. The pump will get COMException on next
        // ReceiveMessage and surface ConnectionLost. We don't double-fire.
    }

    private void OnRecvException(SimConnect _, SIMCONNECT_RECV_EXCEPTION ex)
    {
        // SimConnect reports a usage-error (e.g., bad SimVar name).
        // These are programmer-errors, not user-errors. Surface but
        // don't crash — sim might still be usable.
        var exceptionType = (SIMCONNECT_EXCEPTION)ex.dwException;
        Console.Error.WriteLine($"[SimConnect-Exception] {exceptionType} (sendID={ex.dwSendID})");
    }

    private void OnRecvSimobjectData(SimConnect _, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        // The "Telemetry" reply we requested. dwData[0] is our struct.
        if (data.dwRequestID != (uint)DataRequestId.Telemetry) return;
        if (data.dwData is not { Length: > 0 }) return;
        if (data.dwData[0] is not SimTelemetry telemetry) return;

        lock (_telemetryLock) _latestTelemetry = telemetry;

        TelemetryReceived?.Invoke(telemetry);
    }

    // ─── Cleanup ────────────────────────────────────────────────────

    public void Disconnect()
    {
        if (_sc is null) return;

        _pumpCts?.Cancel();
        _pumpThread?.Interrupt();
        _pumpThread?.Join(TimeSpan.FromSeconds(2));

        try
        {
            _sc.Dispose();
        }
        catch
        {
            // SimConnect can throw on dispose if the sim disappeared
            // mid-shutdown. We don't care at this point.
        }
        _sc = null;
        _pumpThread = null;
        _pumpCts?.Dispose();
        _pumpCts = null;
    }

    public void Dispose() => Disconnect();
}