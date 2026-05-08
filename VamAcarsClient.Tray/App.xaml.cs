using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using H.NotifyIcon;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using VamAcarsClient.Core;
using VamAcarsClient.Tray.Models;

namespace VamAcarsClient.Tray;

/// <summary>
/// WPF application entry-point. Mirrors the bootstrap responsibilities
/// of <c>VamAcarsClient.Cli/Program.cs</c> but adapted for a tray-app
/// life-cycle: no console output, no interactive mode-selection, no
/// blocking <c>ReadLine</c> loops. The tray icon is the user's primary
/// surface; <see cref="MainWindow"/> is opened on demand.
///
/// Lifecycle:
///   OnStartup → load config → init Serilog → create state +
///   AcarsClientService → instantiate tray icon (kept on a field to
///   prevent GC) → return. Window stays hidden. App keeps running
///   until <see cref="OnExitClick"/> triggers Application.Current.
///   Shutdown(), which fires OnExit.
///
/// What this file does NOT do (yet, deferred to a later session):
///   - Auto-start with Windows (registry HKCU\…\Run integration).
///   - Show pairing UI inside the window. For now the user pairs via
///     the Cli in Mode 1; the tray-app reads the same TokenStore.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Tray icon, kept alive for the lifetime of the App. Without this
    /// reference, the icon is liable to vanish after the first GC pass
    /// because <see cref="FindResource"/> returns the instance but
    /// XAML-defined resources aren't strong-rooted.
    /// </summary>
    private TaskbarIcon? _trayIcon;

    /// <summary>
    /// Single status-window instance. Created lazily on first
    /// <see cref="OnShowWindowClick"/>; subsequent shows reuse it. Closing
    /// the window via the X button hides instead of destroys it
    /// (see <see cref="MainWindow"/> code-behind), which keeps the
    /// DataContext binding alive across reopens.
    /// </summary>
    private MainWindow? _mainWindow;

    private ILoggerFactory? _loggerFactory;
    private ILogger<App>? _logger;

    /// <summary>
    /// Single HttpClient owned by the App for the lifetime of the
    /// process. Reused by AcarsClientService (which only borrows it,
    /// doesn't own it) so we don't churn DNS/TCP-pool state every
    /// time the user clicks Connect/Disconnect. Disposed in OnExit.
    /// </summary>
    private HttpClient? _http;

    /// <summary>
    /// Single TokenStore — same instance used for the startup probe
    /// AND handed off to AcarsClientService. Reusing the instance
    /// means both paths share any cached state (currently none, but
    /// future caching wouldn't accidentally diverge).
    /// </summary>
    private TokenStore? _tokenStore;

    /// <summary>
    /// Persistence for the last-used flight plan. Loaded once at
    /// startup into <see cref="LastFlightContext"/>, written via
    /// <see cref="SaveFlightContext"/> after every successful
    /// Connect. Single instance so we don't keep paying the JSON-
    /// options init cost on each save.
    /// </summary>
    private FlightContextStore? _flightContextStore;

    /// <summary>
    /// The flight context loaded from disk at startup, or null if no
    /// previous context exists (first launch) or the saved file
    /// couldn't be read. <see cref="MainWindow"/> reads this in its
    /// constructor to pre-populate the form fields, falling back to
    /// the XAML-baked defaults (NGN901 / Offline / EDDF / EDDM) when
    /// null.
    /// </summary>
    public FlightContext? LastFlightContext { get; private set; }

    /// <summary>
    /// Heartbeat-lifecycle owner. Created once in OnStartup, reused
    /// across Start/Stop cycles. Exposed via <see cref="Service"/> so
    /// MainWindow's Connect/Disconnect button can drive it.
    /// </summary>
    private AcarsClientService? _acarsService;

    /// <summary>
    /// Observable state that powers both the tray-menu Status row and
    /// the MainWindow's data-bindings. Single source of truth — when
    /// the heartbeat service updates fields, both surfaces re-render.
    /// </summary>
    public AcarsClientState State { get; } = new();

    public VamConfig Config { get; private set; } = new();

    /// <summary>
    /// Public handle for MainWindow code-behind to drive Start/Stop.
    /// Accessed via <c>((App)Application.Current).Service</c>. Null
    /// before OnStartup completes; non-null thereafter for the
    /// lifetime of the app.
    /// </summary>
    public AcarsClientService? Service => _acarsService;

    /// <summary>
    /// Status → tooltip-suffix map for the tray icon. The tray icon
    /// itself stays at the cyan brand colour for the lifetime of the
    /// app (loaded once via XAML's <c>IconSource</c> from
    /// <c>app-disconnected.ico</c>); state communication is handled
    /// entirely by the tooltip + the in-window status pill / button /
    /// live readouts.
    ///
    /// Why no per-state icon swap any more: Windows 11's tray
    /// rendering layer caches icons by their notification GUID. Once
    /// explorer.exe has rendered an icon for a given GUID, subsequent
    /// updates via <c>NIM_MODIFY</c> are silently dropped at the
    /// rendering layer (the Win32 call returns success, the tooltip
    /// half of the same call updates correctly, but the icon pixels
    /// don't refresh). We chased this through five workaround paths,
    /// including <c>NIM_DELETE + NIM_ADD</c> with the same GUID and
    /// per-transition GUID rotation. The GUID-rotation approach
    /// technically works (each new GUID gets rendered correctly),
    /// but Win11 puts every fresh GUID into the hidden-tray flyout by
    /// default — there is no documented API to promote a notify-icon
    /// to the always-visible tray, only the user can drag it out.
    /// Net effect for our use-case: dynamic colours land in a place
    /// most users never look, and rotation accumulates orphan
    /// registry rows in <c>HKCU\…\NotifyIconSettings</c>.
    ///
    /// The window already conveys state via three redundant signals
    /// (coloured pill, button label/colour, live status text), so
    /// shedding the tray-colour aspiration is a clean trade.
    ///
    /// Tooltip strings stay short of Windows' ~63-char limit so the
    /// shell doesn't truncate.
    /// </summary>
    private static readonly Dictionary<ConnectionStatus, string> StatusTooltipMap = new()
    {
        [ConnectionStatus.Disconnected] = "VAM ACARS Client — Getrennt",
        [ConnectionStatus.Connecting]   = "VAM ACARS Client — Verbinde…",
        [ConnectionStatus.Connected]    = "VAM ACARS Client — Verbunden",
        [ConnectionStatus.Error]        = "VAM ACARS Client — Fehler",
    };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ─── Assembly-Resolver für SimConnect ──────────────────────────
        // Same hook as Cli/Program.cs. Even though tonight's skeleton
        // doesn't load Core's SimConnect path, we put this in place
        // now so the next session's wire-up doesn't have to debug
        // assembly-resolution failures on top of integration work.
        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            if (assemblyName.Name != "Microsoft.FlightSimulator.SimConnect")
                return null;

            var exeDir = Path.GetDirectoryName(AppContext.BaseDirectory)
                ?? AppContext.BaseDirectory;
            var dllPath = Path.Combine(exeDir, "Microsoft.FlightSimulator.SimConnect.dll");
            return File.Exists(dllPath)
                ? context.LoadFromAssemblyPath(dllPath)
                : null;
        };

        // ─── Configuration ─────────────────────────────────────────────
        // Same precedence rules as Cli: appsettings.json → .Development.
        // Both files live next to the .exe (CopyToOutputDirectory in csproj).
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false);
        var configRoot = configBuilder.Build();
        Config = configRoot.Get<VamConfig>() ?? new VamConfig();

        // ─── Serilog ──────────────────────────────────────────────────
        // File-only sink: %LOCALAPPDATA%\<LocalAppDataFolderName>\logs\.
        // Same {LogPath} substitution dance as Cli — the appsettings
        // bakes in a literal placeholder which we expand here.
        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Config.Storage.LocalAppDataFolderName,
            "logs");
        Directory.CreateDirectory(logsDir);

        foreach (var kv in configRoot.AsEnumerable())
        {
            if (kv.Value is { } v && v.Contains("{LogPath}"))
            {
                configRoot[kv.Key] = v.Replace("{LogPath}", logsDir);
            }
        }

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configRoot)
            .CreateLogger();

        _loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(Log.Logger, dispose: false));
        _logger = _loggerFactory.CreateLogger<App>();
        _logger.LogInformation(
            "VamAcarsClient.Tray starting. ServerUrl={ServerUrl} LogDir={LogsDir}",
            Config.Vam.ApiBaseUrl, logsDir);

        // ─── State seeding ─────────────────────────────────────────────
        State.ServerUrl = Config.Vam.ApiBaseUrl;
        State.StatusMessage = "Bereit. Klicke 'Verbinden' im Status-Fenster.";
        State.ConnectionStatus = ConnectionStatus.Disconnected;

        // Token-presence check: poke the existing TokenStore that the
        // Cli writes to. We don't load/decrypt the token here (no API
        // call yet) — just check existence so the UI can show
        // "paired"/"not paired". The same _tokenStore instance is
        // reused later by AcarsClientService to load the actual token.
        _tokenStore = new TokenStore(Config);
        try
        {
            State.HasToken = _tokenStore.TryLoad() is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TokenStore probe failed during startup");
            State.HasToken = false;
        }

        // Last-used flight plan: read once at startup, exposed via
        // LastFlightContext so MainWindow's ctor can pre-populate
        // the form. Failures (corrupt JSON, schema mismatch, IO
        // errors) all surface as null — see FlightContextStore for
        // the complete fallback semantics.
        _flightContextStore = new FlightContextStore(Config);
        try
        {
            LastFlightContext = _flightContextStore.TryLoad();
            if (LastFlightContext is not null)
            {
                _logger.LogInformation(
                    "Restored flight context: callsign={Callsign}, network={Network}, dep={Dep}, arr={Arr}",
                    LastFlightContext.Callsign,
                    LastFlightContext.Network,
                    LastFlightContext.DepartureIcao,
                    LastFlightContext.ArrivalIcao);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FlightContextStore load failed during startup");
            LastFlightContext = null;
        }

        // ─── HttpClient + AcarsClientService ──────────────────────────
        // Single HttpClient for the process. AcarsClientService borrows
        // it (doesn't own it) so we can recycle Start/Stop without
        // tearing down the connection pool. Mirrors the Cli's pattern
        // where the same HttpClient is constructed once in Program.cs
        // and threaded through every mode.
        _http = new HttpClient
        {
            BaseAddress = new Uri(Config.Vam.ApiBaseUrl),
            Timeout = TimeSpan.FromSeconds(Config.Vam.RequestTimeoutSeconds),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(Config.Vam.UserAgent);

        // Service is built last because it needs the dispatcher, which
        // is only valid once the Application has fully constructed
        // itself. OnStartup runs after the dispatcher is ready, so
        // we're safe here. Dispatcher.CurrentDispatcher == this app's
        // UI thread dispatcher.
        _acarsService = new AcarsClientService(
            _http,
            _tokenStore,
            Config,
            _loggerFactory!,
            State,
            Dispatcher);

        // ─── Tray icon instantiation ──────────────────────────────────
        // Pull the XAML-declared icon out of Application.Resources and
        // pin it to a field. FindResource alone constructs the
        // TaskbarIcon WPF object but does NOT register the Win32
        // NOTIFYICONDATA — H.NotifyIcon's lazy-create path doesn't
        // reliably trigger under WPF's app-lifetime resource
        // pattern, leaving the icon invisible despite the WPF object
        // being live. ForceCreate() is the documented escape hatch
        // and forces the Shell_NotifyIconW registration immediately.
        // Without this call the icon never shows up in the tray.
        _trayIcon = (TaskbarIcon)FindResource("VamTrayIcon");
        try
        {
            _trayIcon.ForceCreate(enablesEfficiencyMode: false);
        }
        catch (Exception ex)
        {
            // Log loudly but don't abort startup — the rest of the app
            // (status window, heartbeat service) still functions; the
            // user just won't have a tray-click affordance to reach it.
            _logger.LogError(ex, "TaskbarIcon.ForceCreate() threw — Shell_NotifyIcon registration failed");
        }

        // ─── Status → tray-tooltip binding (M4 Phase 4) ────────────────
        // Subscribe to State.PropertyChanged so the tray tooltip
        // reflects the current ConnectionStatus. The XAML's
        // IconSource (app-disconnected.ico, cyan) is the permanent
        // visual — see StatusTooltipMap remarks for the rationale.
        // We call UpdateTrayIcon explicitly here so the initial
        // tooltip starts as "— Getrennt" instead of the bare brand
        // name baked into the XAML resource definition.
        State.PropertyChanged += OnStatePropertyChanged;
        UpdateTrayIcon();

        _logger.LogInformation(
            "Tray icon initialized. Token present: {HasToken}, Service ready",
            State.HasToken);
    }

    /// <summary>
    /// State-change handler. Filters for <see cref="AcarsClientState.ConnectionStatus"/>
    /// and pushes the new visual into the tray. Other property changes
    /// (status-message, counters, callsign, etc.) are picked up by the
    /// MainWindow's data-bindings directly — they don't need to ping
    /// the tray, which only encodes a coarse 4-state view.
    ///
    /// Runs on whichever thread fires PropertyChanged. The heartbeat-
    /// service marshalls onto the WPF dispatcher before mutating state
    /// (see AcarsClientService.MarshalToUi), so in practice this is
    /// always on the UI thread, which is required for the IconSource
    /// DependencyProperty assignment.
    /// </summary>
    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AcarsClientState.ConnectionStatus))
        {
            UpdateTrayIcon();
        }
    }

    /// <summary>
    /// Persist the given flight context to disk. Called by
    /// <see cref="MainWindow.OnConnectClick"/> after a successful
    /// <see cref="AcarsClientService.StartAsync"/>, so the next launch
    /// picks up exactly the values that just succeeded — not whatever
    /// half-edited state the user might have had if persistence were
    /// tied to the form's KeyDown.
    ///
    /// Failures are logged and swallowed: a write error here is a
    /// minor convenience hit (user retypes once next time), not a
    /// reason to break the connect flow that just finished.
    /// </summary>
    public void SaveFlightContext(FlightContext context)
    {
        if (_flightContextStore is null || context is null) return;
        try
        {
            _flightContextStore.Save(context);
            _logger?.LogDebug(
                "Persisted flight context: callsign={Callsign}, dep={Dep}, arr={Arr}",
                context.Callsign, context.DepartureIcao, context.ArrivalIcao);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "FlightContextStore save failed");
        }
    }

    /// <summary>
    /// Apply the current <see cref="AcarsClientState.ConnectionStatus"/>
    /// to the tray icon's tooltip. The icon image itself is fixed for
    /// the lifetime of the app (XAML's <c>IconSource</c> loads
    /// <c>app-disconnected.ico</c>) — see <see cref="StatusTooltipMap"/>'s
    /// remarks for why we don't swap colours per state.
    ///
    /// Idempotent; safe to call multiple times. No-op if the tray
    /// icon couldn't be instantiated (ForceCreate failed) — the rest
    /// of the app still functions, the user just won't have a
    /// tray-click affordance.
    /// </summary>
    private void UpdateTrayIcon()
    {
        if (_trayIcon is null) return;
        if (!StatusTooltipMap.TryGetValue(State.ConnectionStatus, out var tooltip))
        {
            // Defensive fallback for unmapped enum values — log,
            // don't crash. Would only happen if a future commit adds
            // a status and forgets to update StatusTooltipMap.
            _logger?.LogWarning(
                "No tooltip mapping for ConnectionStatus={Status} — leaving previous tooltip",
                State.ConnectionStatus);
            return;
        }

        // ToolTipText is a WPF DependencyProperty on TaskbarIcon; the
        // setter routes through H.NotifyIcon's internal NIM_MODIFY
        // with NIF_TIP, which Windows 11 honours correctly (unlike
        // NIF_ICON in the same call — see StatusTooltipMap remarks).
        _trayIcon.ToolTipText = tooltip;

        _logger?.LogDebug(
            "Tray tooltip updated for {Status}: {Tooltip}",
            State.ConnectionStatus, tooltip);
    }

    /// <summary>
    /// Tray icon left-click handler. Toggles the status-window: if
    /// hidden or never created, show + activate; if visible, hide.
    /// </summary>
    private void OnTrayLeftClick(object sender, RoutedEventArgs e)
    {
        if (_mainWindow is null || !_mainWindow.IsVisible)
        {
            ShowMainWindow();
        }
        else
        {
            _mainWindow.Hide();
        }
    }

    private void OnShowWindowClick(object sender, RoutedEventArgs e) => ShowMainWindow();

    private void ShowMainWindow()
    {
        // Lazy-create on first request. The DataContext is the
        // AcarsClientState singleton on this App instance — same one
        // referenced by the tray menu's status row, so updates are
        // reflected in both UIs without manual sync.
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow { DataContext = State };
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }
        // Activate forces the window to the foreground even if it was
        // already visible-but-behind-others. Without this, clicking the
        // tray on an already-open window does nothing visible.
        _mainWindow.Activate();
        _mainWindow.WindowState = WindowState.Normal;
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        _logger?.LogInformation("Exit requested via tray menu");
        // Application.Current.Shutdown propagates to OnExit below where
        // we do the actual cleanup. Forcing through the framework path
        // (instead of Environment.Exit) lets WPF tear down windows and
        // event-handlers in the right order.
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("VamAcarsClient.Tray shutting down");

        // Unsubscribe the tray-icon updater so we don't get a stray
        // PropertyChanged callback during teardown that touches an
        // already-disposed _trayIcon. State and App share a lifetime,
        // so this is more cleanliness than necessity, but it makes
        // the OnExit ordering explicit.
        State.PropertyChanged -= OnStatePropertyChanged;

        // Service first — if it's still running, kick the cancellation
        // tokens so the SimConnect poll-loop and HeartbeatService stop
        // scheduling new work. Dispose() is best-effort sync teardown
        // (we can't await StopAsync from a sync OnExit). Any in-flight
        // heartbeat task may outlive this by milliseconds; the OS reaps
        // it at process-exit.
        _acarsService?.Dispose();
        _acarsService = null;

        // HttpClient AFTER the service so any final in-flight request
        // the heartbeat-loop kicked off has a chance to complete or
        // fail cleanly against a still-valid client. Disposing first
        // would cause those tasks to ObjectDisposedException.
        _http?.Dispose();
        _http = null;

        // Dispose tray icon explicitly before the process dies — this
        // removes the icon from the system tray immediately. Without
        // this, the icon can linger as a "ghost" until the user mouses
        // over it (Windows tray-cleanup quirk dating back to XP).
        _trayIcon?.Dispose();
        _trayIcon = null;

        Log.CloseAndFlush();
        _loggerFactory?.Dispose();

        base.OnExit(e);
    }
}
