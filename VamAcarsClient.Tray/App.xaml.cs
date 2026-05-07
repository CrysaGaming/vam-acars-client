using System.IO;
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
///   OnStartup → load config → init Serilog → create state →
///   instantiate tray icon (kept on a field to prevent GC) → return.
///   Window stays hidden. App keeps running until <see cref="OnExitClick"/>
///   triggers Application.Current.Shutdown(), which fires OnExit.
///
/// What this file does NOT do (yet, deferred to next M4 session):
///   - Wire up <see cref="SimConnectClient"/> + <see cref="HeartbeatService"/>
///     to <see cref="AcarsClientState"/>. The state currently shows a
///     static "skeleton — not connected" placeholder.
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
    /// Observable state that powers both the tray-menu Status row and
    /// the MainWindow's data-bindings. Single source of truth — when
    /// the heartbeat service updates fields, both surfaces re-render.
    /// </summary>
    public AcarsClientState State { get; } = new();

    public VamConfig Config { get; private set; } = new();

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
        State.StatusMessage = "Skeleton — Heartbeat-Service noch nicht verdrahtet";
        State.ConnectionStatus = ConnectionStatus.Disconnected;

        // Token-presence check: poke the existing TokenStore that the
        // Cli writes to. We don't load/decrypt the token here (no need —
        // we're not making API calls in skeleton mode), just check
        // existence so the UI can show "paired"/"not paired".
        try
        {
            var tokenStore = new TokenStore(Config);
            State.HasToken = tokenStore.TryLoad() is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TokenStore probe failed during startup");
            State.HasToken = false;
        }

        // ─── Tray icon instantiation ──────────────────────────────────
        // Pull the XAML-declared icon out of Application.Resources and
        // pin it to a field. The TaskbarIcon's constructor side-effect
        // is what actually creates the Win32 NOTIFYICONDATA — it
        // happens during XAML parsing inside FindResource.
        _trayIcon = (TaskbarIcon)FindResource("VamTrayIcon");

        _logger.LogInformation("Tray icon initialized. Token present: {HasToken}", State.HasToken);
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
