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
/// Pairing flow: in-app via <see cref="PairingDialog"/>, opened from
/// MainWindow's "Pairen…" button when HasToken=false. The CLI's Mode 1
/// pairing is still available for headless / dev-console workflows but
/// is no longer the only entry point.
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
    /// Persistence for user preferences (audio cues, future toggles).
    /// Loaded once at startup; <see cref="SetAudioCueEnabled"/> rewrites
    /// it whenever the user toggles a preference. Always non-null after
    /// OnStartup; the Load() returns defaults on missing-file rather
    /// than null, so the field is meaningful from the moment construction
    /// completes.
    /// </summary>
    private PreferencesStore? _preferencesStore;

    /// <summary>
    /// Standalone read-only handle on the crash-recovery marker file
    /// (option #13). Used at <see cref="OnStartup"/> to detect a stale
    /// marker from a previously-crashed session, and at
    /// <see cref="DiscardRecoverableSession"/> to delete the marker
    /// when the user dismisses the recovery banner without resuming.
    ///
    /// The MARKER's WRITE/CLEAR LIFECYCLE during a normal Connect/
    /// Disconnect cycle is owned by <see cref="AcarsClientService"/>'s
    /// own <c>SessionMarkerStore</c> instance — both stores point at
    /// the same on-disk file (no per-instance state), so the dual-
    /// ownership doesn't risk inconsistency.
    /// </summary>
    private SessionMarkerStore? _sessionMarkerStore;

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
    /// Auto-start-with-Windows toggle service. Owns the
    /// HKCU\…\Run registry value for <see cref="VamAcarsClientTray"/>.
    /// Mutated via <see cref="SetAutoStart"/> from the MainWindow
    /// checkbox click; probed once at startup so
    /// <see cref="AcarsClientState.AutoStartEnabled"/> reflects the
    /// real registry state on the bound UI.
    /// </summary>
    private AutoStartService? _autoStartService;

    /// <summary>
    /// Velopack update orchestrator. Probes GitHub Releases at startup
    /// (fire-and-forget background task), keeps
    /// <see cref="AcarsClientState.UpdateAvailable"/> /
    /// <see cref="AcarsClientState.LatestVersion"/> /
    /// <see cref="AcarsClientState.UpdateDownloaded"/> in sync. The
    /// MainWindow's "Installieren" button drives <see cref="ApplyUpdate"/>
    /// which exits the process and re-launches the new version. Null
    /// before OnStartup completes.
    /// </summary>
    private UpdateService? _updateService;

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
    /// Factory for the Welle A — A4 changelog viewer. Pulls in the
    /// App-owned <see cref="HttpClient"/> (shared connection pool with
    /// the heartbeat path) and a logger scoped to the
    /// <see cref="ChangelogFetcher"/> category.
    ///
    /// Wrapped as a method rather than exposing <c>_http</c> +
    /// <c>_loggerFactory</c> directly so the dialog has one entry-point
    /// to use and the App keeps the dependencies private. Cheap to call
    /// (no I/O); the actual GitHub fetch only happens when the dialog
    /// calls <see cref="ChangelogFetcher.FetchAsync"/>.
    ///
    /// Throws <see cref="InvalidOperationException"/> if called before
    /// OnStartup completes (which would mean an injected race during
    /// startup — should never happen via the normal user-driven
    /// "Changelog anzeigen" click path).
    /// </summary>
    public ChangelogFetcher CreateChangelogFetcher()
    {
        if (_http is null || _loggerFactory is null)
        {
            throw new InvalidOperationException(
                "App not yet initialized — CreateChangelogFetcher called before OnStartup completed.");
        }
        return new ChangelogFetcher(_http, _loggerFactory.CreateLogger<ChangelogFetcher>());
    }

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

        // Auto-start-with-Windows status: probe the registry once at
        // startup so the bound MainWindow checkbox shows the real
        // current state (and survives the user manually editing
        // HKCU\…\Run via regedit or third-party tools between
        // sessions). IsEnabled() is itself catch-all-and-return-false,
        // so this won't throw, but the outer try/catch belts-and-
        // braces against future implementation drift.
        _autoStartService = new AutoStartService();
        try
        {
            State.AutoStartEnabled = _autoStartService.IsEnabled();
            _logger.LogInformation(
                "Auto-start status at launch: {Enabled}",
                State.AutoStartEnabled ? "enabled" : "disabled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutoStartService probe failed during startup");
            State.AutoStartEnabled = false;
        }

        // ─── Preferences (option #5) ───────────────────────────────────
        // Load once at startup — defaults to AudioCueEnabled=false on
        // missing-file (first launch) or any IO/JSON error. Bound
        // MainWindow checkbox flips through SetAudioCueEnabled below.
        _preferencesStore = new PreferencesStore(Config);
        var prefs = _preferencesStore.Load();
        State.AudioCueEnabled = prefs.AudioCueEnabled;
        _logger.LogInformation(
            "Preferences loaded: AudioCueEnabled={Enabled}",
            State.AudioCueEnabled);

        // ─── Crash-recovery probe (option #13) ─────────────────────────
        // If a SessionMarker is on disk, the previous session ended
        // without a clean Stop. Surface it on the bound state so the
        // MainWindow's recovery banner becomes visible. This runs
        // BEFORE the AcarsClientService is constructed because
        // _sessionMarkerStore lives inside the service — but a stand-
        // alone read-only probe needs no service infrastructure, just
        // the same on-disk file. We instantiate a one-shot store here
        // for the read; the service's owned instance handles
        // write/clear during normal lifecycle.
        //
        // Stale markers (e.g. days old) surface as banners just like
        // fresh ones — the banner shows elapsed time so the user can
        // gauge whether to resume or discard. We don't auto-discard
        // by age threshold because what counts as "stale" is per-user
        // (some pilots take long lunch breaks mid-flight).
        _sessionMarkerStore = new SessionMarkerStore(Config);
        try
        {
            var marker = _sessionMarkerStore.TryLoad();
            if (marker is not null)
            {
                State.RecoverableSession = marker;
                _logger.LogInformation(
                    "Recoverable session detected: callsign={Callsign}, started={StartedAt}",
                    marker.Callsign, marker.StartedAtUtc);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SessionMarkerStore.TryLoad failed during startup");
        }

        // ─── Velopack updater (M5) ─────────────────────────────────────
        // Construct after AutoStart so the update-check fires while the
        // rest of OnStartup is still running. The check is async + non-
        // blocking (fire-and-forget) so we don't gate window-open on
        // the network round-trip to api.github.com — the user gets the
        // tray icon and a working app immediately, and the "Update
        // verfügbar" indicator pops in a few hundred ms later if there
        // is one.
        //
        // Repo URL is hardcoded for v0.x. If we ever move releases to
        // another repo (or split stable / beta channels onto separate
        // repos), this becomes a VamConfig field. For now it lives
        // alongside the same constant baked into release.ps1, so a
        // repo move is a two-place coordinated change.
        const string ReleaseRepoUrl = "https://github.com/CrysaGaming/vam-acars-client";
        _updateService = new UpdateService(_loggerFactory!, State, Dispatcher, ReleaseRepoUrl);
        State.InstalledVersion = _updateService.GetInstalledVersion();
        _logger.LogInformation("Installed version: {Version}", State.InstalledVersion);

        // Fire-and-forget. The service marshals UI updates back onto
        // our dispatcher; we just kick the work off and don't await.
        // Discarded with `_ =` so the compiler doesn't complain about
        // an unobserved task.
        _ = _updateService.CheckAndDownloadAsync();

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

        // ─── Welle B / B5: lazy-fetch airline-routes catalogue ────────
        //
        // Pulls the paired user's airline-routes list and caches it on
        // State.AirlineRoutes so OFP-import can do an instant in-memory
        // lookup. Gated on HasToken so unpaired pilots don't fire a
        // pointless request that would just 401.
        //
        // Fire-and-forget on a background task: blocking OnStartup
        // until the fetch returns would delay window-show and tray-
        // icon registration by however long the round-trip takes,
        // which is poor UX on slow networks. The fetch is silent —
        // success = routes are there; failure = no suggestions banner
        // on next OFP-import, same as solo-pilot empty state. Either
        // way the rest of the tray works.
        //
        // We don't await the task; the discard makes that explicit
        // and silences IDE warnings. Errors inside the task are
        // logged by FetchAirlineRoutesAsync's catch blocks.
        if (State.HasToken)
        {
            _ = _acarsService.FetchAirlineRoutesAsync();
        }

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

        // ─── Crash-report hook (Welle A — A5) ─────────────────────────
        // Catch unhandled UI-thread exceptions, write a structured
        // report to disk, and show the user a dialog with paths
        // forward (copy report, open folder, open GitHub issues).
        // The handler marks the exception as Handled=true so the
        // process keeps running — most WPF dispatcher exceptions
        // are recoverable (a single click handler threw, but the
        // rest of the app is fine). Truly catastrophic crashes
        // (StackOverflow, AccessViolation) bypass this path because
        // they're considered fatal by .NET; we don't try to handle
        // them.
        //
        // We also hook AppDomain.UnhandledException for background-
        // thread crashes that don't reach the dispatcher. Those CAN'T
        // be marked as handled (the process is going down regardless)
        // but we can still write the report so the user has the
        // forensics next launch.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        // ─── Logs-cleanup job (Welle A — A7) ───────────────────────────
        // Fire-and-forget background cleanup of log files older than 30
        // days. Runs once per app launch on a Task.Run worker so it
        // can't slow startup even if the directory has hundreds of
        // files. The active log file for today is held open by Serilog
        // and will throw IOException on delete — that's handled
        // gracefully inside the service (skipped + logged at Debug).
        //
        // We don't await: the result (number of files deleted) is
        // logged from inside the service, and the user has no reason
        // to know whether cleanup ran. Discarded with `_ =` so the
        // compiler doesn't complain about the unobserved task.
        _ = Task.Run(() =>
        {
            try
            {
                LogsCleanupService.CleanupOldLogs(
                    Config,
                    _loggerFactory?.CreateLogger(typeof(LogsCleanupService).FullName!));
            }
            catch (Exception ex)
            {
                // Defensive — the service itself swallows everything,
                // but a future refactor could change that. We never
                // want a logs-cleanup failure to surface anywhere
                // visible to the user.
                _logger?.LogWarning(ex, "LogsCleanupService.CleanupOldLogs threw unexpectedly");
            }
        });

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
        // Trigger tray-tooltip refresh on any of the inputs that
        // contribute to it (option #11): connection-status flips drive
        // the prefix; aircraft-type/registration changes drive the
        // suffix when Connected. We don't filter to ConnectionStatus
        // alone because the live aircraft display lags the connection
        // by a few seconds (first heartbeat-response), and we want the
        // tooltip to refresh once that lands.
        if (e.PropertyName == nameof(AcarsClientState.ConnectionStatus)
            || e.PropertyName == nameof(AcarsClientState.AircraftType)
            || e.PropertyName == nameof(AcarsClientState.AircraftRegistration))
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
    /// Toggle the "start with Windows" registration. Driven by the
    /// MainWindow's checkbox click handler, which passes the new
    /// desired state. On success we update <see cref="State"/>'s
    /// mirror so the bound checkbox stays in sync; on failure we
    /// re-read the registry and write back the actual truth, so the
    /// UI never drifts from the system state (e.g. if a write failed
    /// halfway and we don't actually know what's there).
    ///
    /// Errors are logged and swallowed so the user can keep using
    /// the app — the failure mode is "checkbox flips back to its
    /// previous position", which is the right visual feedback for
    /// "your toggle didn't take effect".
    /// </summary>
    public void SetAutoStart(bool enabled)
    {
        if (_autoStartService is null) return;

        try
        {
            if (enabled)
            {
                _autoStartService.Enable();
            }
            else
            {
                _autoStartService.Disable();
            }
            State.AutoStartEnabled = enabled;
            _logger?.LogInformation(
                "Auto-start {Status} by user",
                enabled ? "enabled" : "disabled");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Failed to toggle auto-start to {Desired} — re-reading registry to re-sync UI",
                enabled);
            // Belts-and-braces: re-read so the bound checkbox flips
            // back to whatever the registry actually says, even if
            // the write partially succeeded.
            State.AutoStartEnabled = _autoStartService.IsEnabled();
        }
    }

    /// <summary>
    /// Toggle the audio-cue-on-phase-transition preference. Same
    /// pattern as <see cref="SetAutoStart"/>: update the in-memory
    /// state mirror, persist to disk, swallow IO errors so the user
    /// can keep working. Called from MainWindow's checkbox click.
    ///
    /// Disk failure mode: the in-memory toggle still flips (bound
    /// checkbox stays in sync with what the user just clicked) but
    /// the on-disk file is stale, so a restart re-loads the old
    /// value. Acceptable trade — better than reverting the checkbox
    /// after a click landed.
    /// </summary>
    public void SetAudioCueEnabled(bool enabled)
    {
        State.AudioCueEnabled = enabled;
        if (_preferencesStore is null) return;

        try
        {
            _preferencesStore.Save(new Preferences { AudioCueEnabled = enabled });
            _logger?.LogInformation(
                "Audio cue {Status} by user",
                enabled ? "enabled" : "disabled");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "PreferencesStore save failed");
        }
    }

    /// <summary>
    /// Drives the "Installieren" button in the EINSTELLUNGEN card.
    /// Delegates to <see cref="UpdateService.ApplyUpdate"/> which
    /// exits this process and re-launches the new version via the
    /// Velopack helper. Stops the heartbeat service first so an
    /// in-flight HTTP request doesn't get torn mid-flight by the
    /// Process.Exit Velopack triggers — the server-side stale-session
    /// cleanup from M3.9 would handle a hard-cut, but a clean stop
    /// is friendlier to the cooperating side.
    ///
    /// Returns silently when no update is staged. The caller (the
    /// XAML button's IsEnabled binding) shouldn't call this without
    /// <see cref="AcarsClientState.UpdateDownloaded"/> being true,
    /// but defensive null-check is cheap.
    /// </summary>
    public void ApplyUpdate()
    {
        if (_updateService is null) return;

        _logger?.LogInformation("Applying update on user request — stopping heartbeats first");

        // Best-effort stop. Don't await — ApplyUpdate is invoked
        // from a click handler, we want the apply to fire promptly.
        // Velopack's ApplyUpdatesAndRestart will Process.Exit() this
        // process anyway, so any in-flight heartbeat will be reaped
        // by the OS within microseconds either way; the StopAsync
        // call is a courtesy.
        if (_acarsService?.IsRunning == true)
        {
            _ = _acarsService.StopAsync();
        }

        _updateService.ApplyUpdate();
    }

    /// <summary>
    /// Drives the "Auf Updates prüfen" button (M5 Phase 2). Fires
    /// a re-check against GitHub Releases on user demand, instead
    /// of waiting for the next app restart. Fire-and-forget; the
    /// service's internal <see cref="AcarsClientState.UpdateChecking"/>
    /// flip-flopping makes the button visually responsive without
    /// the caller having to await.
    ///
    /// Re-entrancy is guarded inside <see cref="UpdateService.CheckAndDownloadAsync"/>
    /// via the UpdateChecking state, so even if the click somehow
    /// fires while the startup-check is still in flight, the second
    /// invocation just queues another one — they won't race each
    /// other's dispatcher work because each marshalls every state
    /// mutation through the same UI thread.
    /// </summary>
    public void CheckForUpdatesNow()
    {
        if (_updateService is null) return;
        _logger?.LogInformation("Manual update check requested by user");
        _ = _updateService.CheckAndDownloadAsync();
    }

    /// <summary>
    /// Drives the "Sim erkennen" button in the PRE-FLIGHT card (option
    /// #11). Wraps <see cref="AcarsClientService.ProbeAircraftAsync"/>,
    /// flips <see cref="AcarsClientState.IsProbingSim"/> for the UI's
    /// in-flight feedback, and pushes the result into the bound
    /// DetectedAircraft* state fields.
    ///
    /// Idempotent and re-entrancy-safe via IsProbingSim — a second call
    /// while the first is in flight returns immediately. Failures (MSFS
    /// not running, timeout, COMException) populate StatusMessage with
    /// a friendly note and clear the Detected* fields so the UI shows
    /// em-dashes rather than stale data.
    ///
    /// Async-void would also be acceptable here (it's invoked from a
    /// click handler) but Task lets the caller await for tests / future
    /// composition without restructuring.
    /// </summary>
    public async Task ProbeSimAsync()
    {
        if (_acarsService is null) return;
        if (State.IsProbingSim)
        {
            _logger?.LogDebug("ProbeSimAsync: already in flight — ignoring concurrent click");
            return;
        }

        State.IsProbingSim = true;
        State.StatusMessage = "Erkenne Flugzeug in MSFS …";

        try
        {
            var result = await _acarsService.ProbeAircraftAsync();
            if (result is { } r)
            {
                State.DetectedAircraftType = r.Type;
                State.DetectedAircraftRegistration = r.Registration;
                State.DetectedAircraftTitle = r.Title;
                // Option #14: map the raw szApplicationName to a friendly
                // canonical name ("MSFS 2020", "MSFS 2024", "FSX", "P3D")
                // before displaying. MapSimulatorName falls through to
                // the raw input for unknown sims, so niche P3D forks /
                // FSX:SE still see their actual sim name. We don't
                // include the version major.minor in the visible label —
                // pilots care about which sim, not which build, and the
                // raw version is in the log anyway.
                State.DetectedSimulator = AcarsClientState.MapSimulatorName(r.SimulatorName);

                // "UNKN" is the SimTelemetry sentinel for empty values
                // (see SimTelemetry.AircraftType). Treat it as "no
                // aircraft loaded" in the status message rather than
                // pretending we got useful data.
                if (string.IsNullOrEmpty(r.Type) || r.Type == "UNKN")
                {
                    State.StatusMessage = string.IsNullOrWhiteSpace(State.DetectedSimulator)
                        ? "Sim erreichbar, aber kein Flugzeug geladen."
                        : $"{State.DetectedSimulator} erreichbar, aber kein Flugzeug geladen.";
                }
                else
                {
                    var simPrefix = string.IsNullOrWhiteSpace(State.DetectedSimulator)
                        ? "Flugzeug"
                        : State.DetectedSimulator;
                    State.StatusMessage = $"{simPrefix}: {r.Type} / {r.Registration ?? "—"}";
                }
                _logger?.LogInformation(
                    "Sim probe succeeded: type={Type}, reg={Reg}, sim={Sim} {SimVersion}",
                    r.Type, r.Registration, r.SimulatorName, r.SimulatorVersion);
            }
            else
            {
                // Probe returned null — clear stale data so the UI
                // doesn't keep showing the previous probe's result.
                State.DetectedAircraftType = null;
                State.DetectedAircraftRegistration = null;
                State.DetectedAircraftTitle = null;
                State.DetectedSimulator = null;
                State.StatusMessage = "MSFS nicht erreichbar. Sim gestartet?";
                _logger?.LogInformation("Sim probe returned null (MSFS not running or timeout)");
            }
        }
        catch (Exception ex)
        {
            // ProbeAircraftAsync swallows its own exceptions and returns
            // null in error paths, but defensive catch here in case a
            // future refactor changes that contract.
            _logger?.LogWarning(ex, "ProbeSimAsync threw unexpectedly");
            State.StatusMessage = $"Sim-Probe fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            State.IsProbingSim = false;
        }
    }

    /// <summary>
    /// In-app device pairing. Wraps <see cref="PairingService.RedeemAsync"/>
    /// + <see cref="TokenStore.Save"/> + <see cref="AcarsClientState.HasToken"/>
    /// flip into a single call so <see cref="PairingDialog"/> talks to
    /// one method on the App rather than reaching into <c>_http</c>,
    /// <c>_tokenStore</c>, and <c>State</c> separately.
    ///
    /// Returns the raw <see cref="PairingResult"/> so the dialog can
    /// distinguish success from validation-failure (server-rejected code
    /// → IsSuccess=false + ErrorMessage) and surface the appropriate
    /// message. Transport failures bubble out as
    /// <see cref="PairingTransportException"/> — the dialog handles those
    /// in a separate catch.
    ///
    /// On success we:
    ///   1. Persist the token via <see cref="TokenStore.Save"/> (DPAPI-
    ///      encrypted, %LOCALAPPDATA%\VamAcarsClient\token.dat).
    ///   2. Flip <see cref="AcarsClientState.HasToken"/>=true so the
    ///      MainWindow's VERBINDUNG card immediately re-renders the
    ///      "✓ Gepaart" badge + the "Pairen…" button hides itself.
    ///   3. Update <see cref="AcarsClientState.StatusMessage"/> with the
    ///      friendly "Gepaart als {displayName}" cue so the footer-strip
    ///      confirms the action took effect.
    ///
    /// Token-store write failure is logged but NOT surfaced as an
    /// IsSuccess=false — the server-side pair already happened, the
    /// token exists, just couldn't be persisted. Returning Success here
    /// would be misleading; we return a synthetic Failure with a hint
    /// so the user can retry, and we DON'T flip HasToken. The next
    /// pairing attempt re-redeems a fresh code (the old code is
    /// consumed-server-side regardless of our IO outcome — by design,
    /// codes are one-shot). User just generates another code.
    ///
    /// PairingService is constructed per-call (stateless wrapper around
    /// HttpClient), so we don't keep a long-lived field for it.
    /// </summary>
    public async Task<PairingResult> PairDeviceAsync(string code)
    {
        if (_http is null || _tokenStore is null)
        {
            // Defensive — should never trip post-OnStartup. Surface as
            // a user-visible failure rather than a NullReferenceException.
            _logger?.LogWarning("PairDeviceAsync called before OnStartup completed");
            return PairingResult.Failure("App noch nicht initialisiert. Bitte kurz warten und erneut versuchen.");
        }

        var pairing = new PairingService(_http);
        PairingResult result;
        try
        {
            result = await pairing.RedeemAsync(code);
        }
        catch (PairingTransportException ex)
        {
            _logger?.LogWarning(ex, "PairDeviceAsync transport failure");
            // Bubble up so the dialog's catch-block can render a
            // distinct "Verbindungsfehler" message instead of treating
            // it like a validation rejection.
            throw;
        }

        if (!result.IsSuccess)
        {
            _logger?.LogInformation(
                "PairDeviceAsync rejected by server: {Error}",
                result.ErrorMessage);
            return result;
        }

        // Server-side pair succeeded — persist + flip state.
        try
        {
            _tokenStore.Save(result.Token!);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "PairDeviceAsync: server accepted but TokenStore.Save failed — user must re-pair with a fresh code");
            // Note: don't flip HasToken=true here. We don't have a usable
            // persistent token. User regenerates a fresh code (old one is
            // already consumed) and tries again.
            return PairingResult.Failure(
                "Pairing erfolgreich, aber Token konnte nicht gespeichert werden. "
                + "Bitte neuen Code generieren und erneut versuchen.");
        }

        State.HasToken = true;
        State.StatusMessage = $"Gepaart als {result.DisplayName ?? "—"}.";
        _logger?.LogInformation(
            "PairDeviceAsync succeeded: displayName={DisplayName}",
            result.DisplayName);

        return result;
    }

    /// <summary>
    /// Drives the "Wiederverbinden" button on the recovery banner
    /// (option #13). Translates the in-memory marker back into a
    /// <see cref="FlightContext"/>, pre-fills the MainWindow form so
    /// the user can sanity-check what's about to be sent, and clears
    /// the marker state. Does NOT auto-trigger Connect — the user
    /// still has to tick the pre-flight checklist + click Verbinden.
    /// That extra step is intentional: a crashed session might have
    /// happened mid-flight at FL360, and we'd rather have the pilot
    /// confirm the situation than silently re-launch the connection
    /// before they've reoriented in MSFS.
    ///
    /// Form pre-fill writes through to MainWindow's bound TextBox
    /// fields directly. The user can then edit before clicking
    /// Verbinden — which is the existing flow's natural shape.
    ///
    /// We don't re-write the marker here either — the existing one
    /// stays on disk until either (a) a successful Connect overwrites
    /// it via AcarsClientService.StartAsync, or (b) the user clicks
    /// Verwerfen on the banner. State.RecoverableSession is cleared
    /// so the banner disappears; the user knows their action took
    /// effect.
    ///
    /// No-op (returns immediately) if the state has no marker — should
    /// only happen if this is called via a stale UI event after the
    /// banner already cleared, but defensive bail is cheap.
    /// </summary>
    public Task ResumeRecoverableSessionAsync()
    {
        var marker = State.RecoverableSession;
        if (marker is null)
        {
            _logger?.LogDebug("ResumeRecoverableSessionAsync called with no marker present — no-op");
            return Task.CompletedTask;
        }

        _logger?.LogInformation(
            "Resume requested for recoverable session: callsign={Callsign}",
            marker.Callsign);

        // Convert + persist as the new last-used context. SaveFlightContext
        // is also called inside OnConnectClick post-success, so the
        // double-save here is mildly redundant — but it ensures that
        // even if the user closes the window without clicking Verbinden,
        // the flight-context.json now reflects the recovered marker
        // rather than whatever was last saved.
        var context = marker.ToFlightContext();
        SaveFlightContext(context);
        LastFlightContext = context;

        // Pre-fill the form. The MainWindow may not be open yet
        // (recovery from a tray-only launch), but if it is, push the
        // values straight into the bound TextBoxes so the user sees
        // exactly what's about to be sent. We don't try to open the
        // window here — the click that just landed implies the window
        // is already visible.
        if (_mainWindow is not null)
        {
            _mainWindow.CallsignBox.Text = context.Callsign;
            _mainWindow.NetworkBox.Text = context.Network;
            _mainWindow.DeparturBox.Text = context.DepartureIcao ?? string.Empty;
            _mainWindow.ArrivalBox.Text = context.ArrivalIcao ?? string.Empty;
        }

        // Clear the in-memory marker so the banner hides. The on-disk
        // file is left alone — the next successful Connect overwrites
        // it via AcarsClientService.StartAsync. If the user closes the
        // app before reconnecting, the marker stays on disk and the
        // banner re-appears next launch (which is the right behaviour
        // — they haven't actually reconnected yet).
        State.RecoverableSession = null;
        State.StatusMessage = $"Sitzung wiederhergestellt: {context.Callsign}. Pre-flight prüfen, dann Verbinden.";

        return Task.CompletedTask;
    }

    /// <summary>
    /// Drives the "Verwerfen" button on the recovery banner (option
    /// #13). Deletes the marker from disk + clears the in-memory
    /// state, so the banner disappears and won't return on next
    /// launch. Idempotent and IO-failure-tolerant.
    ///
    /// We unconditionally clear the in-memory state (so the banner
    /// hides even if disk-clear fails) and log the IO error. The
    /// next launch would re-show the banner if the disk-clear
    /// failed silently here — minor UX nuisance, not worth blocking
    /// the dismiss gesture over.
    /// </summary>
    public void DiscardRecoverableSession()
    {
        _logger?.LogInformation("Discard requested for recoverable session");

        try
        {
            _sessionMarkerStore?.Clear();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SessionMarkerStore.Clear failed during discard — banner may re-appear");
        }

        State.RecoverableSession = null;
        State.StatusMessage = "Wiederherstellung verworfen.";
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

        // Option #11: Enrich the tooltip with the live aircraft when
        // we have one. Format: "VAM ACARS Client — Verbunden · A320 / D-ANNE".
        // Only when Connected, because Disconnected/Connecting/Error
        // states either don't have aircraft data yet (Connecting,
        // Disconnected) or the data is stale-and-misleading (Error).
        // Truncates to fit Windows' ~63-char tooltip ceiling — anything
        // longer gets shell-truncated with an ellipsis we can't control.
        if (State.ConnectionStatus == ConnectionStatus.Connected)
        {
            var type = State.AircraftType;
            var reg = State.AircraftRegistration;
            if (!string.IsNullOrWhiteSpace(type))
            {
                var aircraftSuffix = string.IsNullOrWhiteSpace(reg)
                    ? type
                    : $"{type} / {reg}";

                var enriched = $"{tooltip} · {aircraftSuffix}";
                // Cap at 63 chars (the Win32 NOTIFYICONDATA.szTip limit
                // is 64 incl. null-terminator). Trim with single-char
                // ellipsis rather than three dots so we don't lose
                // additional characters of the aircraft display.
                tooltip = enriched.Length > 63 ? enriched[..62] + "…" : enriched;
            }
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

        // Unsubscribe crash-handler hooks symmetrically. Both events
        // can outlive App in some shutdown paths (the framework
        // doesn't auto-clean managed-event subscriptions on
        // Application teardown), so unhooking explicitly here
        // prevents the rare case where a background-thread crash
        // during OnExit cleanup tries to touch a half-disposed App.
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;

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

    /// <summary>
    /// Welle A — A5 crash-handler for UI-thread exceptions. Captures
    /// the exception into a structured report, surfaces the
    /// <see cref="CrashReportDialog"/>, and marks the exception as
    /// Handled so the process keeps running.
    ///
    /// Re-entrancy guard: if the crash-handler itself throws while
    /// writing the report or showing the dialog, the outer
    /// try/catch logs the secondary failure and lets the original
    /// exception propagate (Handled stays false). Better to crash
    /// loudly than swallow + double-fault.
    ///
    /// We use a sentinel <see cref="_crashHandlerActive"/> flag to
    /// prevent infinite recursion: if the dialog ITSELF throws and
    /// the exception reaches the dispatcher again, we don't try to
    /// show another dialog — that would loop forever on a deep
    /// failure (e.g. graphics-stack collapse). Second-level crashes
    /// log + bubble.
    /// </summary>
    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        if (_crashHandlerActive)
        {
            // Already in the middle of handling a crash and a second
            // one fired. Don't recurse — let the framework do its
            // normal teardown.
            _logger?.LogError(e.Exception, "Secondary unhandled exception inside crash handler — letting it propagate");
            return;
        }

        try
        {
            _crashHandlerActive = true;
            _logger?.LogError(e.Exception, "Unhandled dispatcher exception — capturing crash report");

            var snapshot = BuildAppContextSnapshot();
            var writer = new CrashReportWriter(
                Config,
                _loggerFactory?.CreateLogger<CrashReportWriter>());
            var result = writer.Capture(e.Exception, snapshot);

            // Build a one-line summary for the dialog header. We trim
            // the message to keep the header readable even when the
            // exception's Message is multi-paragraph.
            var summary = $"{e.Exception.GetType().Name}: {Truncate(e.Exception.Message, 200)}";

            var dialog = new CrashReportDialog(result, summary);
            // Use Owner=MainWindow if the window is alive + visible,
            // otherwise standalone. The dialog gracefully handles
            // either case.
            if (_mainWindow is not null && _mainWindow.IsVisible)
            {
                dialog.Owner = _mainWindow;
            }
            dialog.ShowDialog();

            // Mark as Handled so WPF doesn't tear down. The user has
            // acknowledged the crash; we can keep running.
            e.Handled = true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Crash handler itself threw — letting original exception propagate");
            // Leave Handled=false so the framework's default behavior
            // (tear down the app) takes over.
        }
        finally
        {
            _crashHandlerActive = false;
        }
    }

    /// <summary>
    /// Backstop for crashes that don't reach the WPF dispatcher
    /// (background threads, finalizers). These ARE going to terminate
    /// the process — IsTerminating=true on the args reflects that —
    /// so we just write the report for post-mortem analysis. No
    /// dialog because the process is collapsing behind us; the user
    /// will see the report next launch.
    ///
    /// The handler runs on whatever thread threw, NOT the UI thread,
    /// so we can't reliably show WPF UI here. Just IO and logging.
    /// </summary>
    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception ex) return;

        try
        {
            _logger?.LogCritical(ex,
                "Unhandled background-thread exception (IsTerminating={IsTerminating}) — capturing crash report",
                e.IsTerminating);

            var snapshot = BuildAppContextSnapshot();
            var writer = new CrashReportWriter(
                Config,
                _loggerFactory?.CreateLogger<CrashReportWriter>());
            writer.Capture(ex, snapshot);
        }
        catch
        {
            // Last-ditch — log via Serilog directly. Even that may
            // fail if the process is already disintegrating; nothing
            // more we can do.
            try { Log.Fatal(ex, "AppDomain unhandled exception, crash handler itself failed"); }
            catch { /* give up */ }
        }
    }

    /// <summary>
    /// Re-entrancy guard for the crash handler. See
    /// <see cref="OnDispatcherUnhandledException"/> for rationale.
    /// </summary>
    private bool _crashHandlerActive;

    /// <summary>
    /// Build a minimal app-state snapshot for inclusion in crash
    /// reports. Wrapped in a try/catch so a property-getter throwing
    /// can't double-fault the crash handler — falls back to a null
    /// snapshot which the writer handles fine.
    /// </summary>
    private AppContextSnapshot? BuildAppContextSnapshot()
    {
        try
        {
            return new AppContextSnapshot(
                ConnectionStatus: State.ConnectionStatus.ToString(),
                HasToken: State.HasToken,
                DetectedSimulator: State.DetectedSimulator,
                AircraftType: State.AircraftType,
                HeartbeatsSent: State.HeartbeatsSent,
                HeartbeatsFailed: State.HeartbeatsFailed,
                StatusMessage: State.StatusMessage);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "BuildAppContextSnapshot threw — using null snapshot");
            return null;
        }
    }

    /// <summary>
    /// Truncate a string to max-length with an ellipsis when needed.
    /// Used for crash-summary in the dialog header — full message is
    /// in the report body, the summary just needs to fit on screen.
    /// </summary>
    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Length <= max) return value;
        return value[..(max - 1)] + "…";
    }
}
