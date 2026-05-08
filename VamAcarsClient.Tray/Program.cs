using System.IO;
using Velopack;

namespace VamAcarsClient.Tray;

/// <summary>
/// Explicit entry-point for the tray app. Replaces the auto-generated
/// Main that the WPF SDK would emit from <c>App.xaml</c>'s
/// <c>&lt;ApplicationDefinition&gt;</c> — we need this so we can run
/// <see cref="VelopackApp"/> hooks BEFORE any WPF type touches its
/// initializer, the SimConnect resolver, the heartbeat service, etc.
///
/// What VelopackApp.Build().Run() does:
///   - Detects the Velopack hook flags in argv (<c>--veloapp-install</c>,
///     <c>--veloapp-firstrun</c>, <c>--veloapp-obsolete</c>,
///     <c>--veloapp-updated</c>, <c>--veloapp-uninstall</c>).
///   - Runs the appropriate hook callback, then exits the process.
///   - For a normal launch (no hook flag), returns immediately and
///     lets us continue on to construct the WPF App.
///
/// Without VelopackApp.Run() running early, an update apply would
/// boot the full WPF stack on a process whose only job is to record
/// a single registry pin, taking ~0.5 s instead of ~50 ms — and
/// could fail outright if Dispatcher / SimConnect setup throws.
///
/// Threading: <c>[STAThread]</c> is mandatory for any Main that will
/// eventually call <c>Application.Run()</c>. WPF's COM-interop layer
/// (drag-and-drop, OLE, the clipboard) requires single-threaded
/// apartment, and the framework throws InvalidOperationException
/// on a MTA thread when you Show() a window. The auto-generated
/// Main has this attribute too; we preserve it here.
///
/// Logging note: we don't pass a custom logger to VelopackApp.
/// Velopack's logger interface is <c>Velopack.Logging.IVelopackLogger</c>
/// (not the Microsoft.Extensions.Logging.ILogger we use everywhere
/// else), so wiring would mean another tiny adapter class. The
/// hooks are silent on a normal launch and Velopack falls back to
/// <c>%LOCALAPPDATA%\VamAcarsClient\Velopack.log</c> for hook
/// noise — good enough for now. We can plumb a proper bridge in
/// a follow-up if hook debugging becomes a recurring need.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // ─── Velopack bootstrap ─────────────────────────────────────────
        // Build() registers no hooks beyond the framework defaults; we
        // don't need OnFirstRun (no welcome flow yet) or OnAfterUpdate
        // (no migration steps yet). If we ever need them they slot in
        // here as fluent builder calls before .Run().
        VelopackApp.Build().Run();

        // ─── Normal WPF startup ────────────────────────────────────────
        // From here we mirror what the auto-generated Main would have
        // done: instantiate App, call InitializeComponent (which the
        // XAML compiler emits into App.g.cs to wire up the
        // Application.Resources tree), then enter the dispatcher loop
        // via Application.Run(). All our existing OnStartup logic
        // (config, Serilog, AcarsClientService, tray icon, …) fires
        // from inside App.OnStartup as before.
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
