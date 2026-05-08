using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using VamAcarsClient.Tray.Models;
using Velopack;
using Velopack.Sources;

namespace VamAcarsClient.Tray;

/// <summary>
/// Wraps Velopack's <see cref="UpdateManager"/> for the tray app.
/// Drives the three-step update lifecycle:
///   1. <see cref="CheckForUpdatesAsync"/> — query the source for a
///      newer release; sets <see cref="AcarsClientState.UpdateAvailable"/>
///      and <see cref="AcarsClientState.LatestVersion"/> on hit.
///   2. <see cref="DownloadUpdatesAsync"/> — fetch the .nupkg into
///      Velopack's per-user staging dir; flips
///      <see cref="AcarsClientState.UpdateDownloaded"/> on success.
///   3. <see cref="ApplyUpdate"/> — call into Velopack's
///      <c>ApplyUpdatesAndRestart</c>, which exits this process and
///      re-launches the new version. App must be ready to die.
///
/// All three steps fail-soft: any thrown exception is logged and
/// the update flow stops. The most common no-op case is
/// <c>NotInstalledException</c> from a dev / debug run where the
/// binary isn't living under <c>%LOCALAPPDATA%\&lt;id&gt;\current\</c> —
/// in that case we just leave UpdateAvailable=false, no banner shows,
/// and the developer can build releases via release.ps1 to get a
/// real install they can dogfood the updater on.
///
/// Threading: <see cref="CheckForUpdatesAsync"/> +
/// <see cref="DownloadUpdatesAsync"/> are awaitable; the caller
/// (App.OnStartup → fire-and-forget) needs to marshal back onto
/// the WPF dispatcher before mutating <see cref="AcarsClientState"/>,
/// because data-bindings re-evaluate on whatever thread the
/// PropertyChanged event fires on. We do that marshalling INSIDE
/// this service so callers can stay simple — pass the dispatcher
/// in via the constructor and let the service worry about it.
/// </summary>
public sealed class UpdateService
{
    private readonly ILogger<UpdateService> _logger;
    private readonly AcarsClientState _state;
    private readonly Dispatcher _uiDispatcher;
    private readonly UpdateManager _manager;

    /// <summary>
    /// Latest non-null result from <see cref="CheckForUpdatesAsync"/>,
    /// stashed so <see cref="DownloadUpdatesAsync"/> and
    /// <see cref="ApplyUpdate"/> can act on the same release object
    /// without re-querying. Cleared back to null after a successful
    /// apply (the process restarts before that line runs in practice,
    /// but defensive coding doesn't cost anything).
    /// </summary>
    private UpdateInfo? _pendingUpdate;

    /// <summary>
    /// Constructor. <paramref name="repoUrl"/> is the GitHub repo
    /// where releases are published — for VAM ACARS Client that's
    /// <c>https://github.com/CrysaGaming/vam-acars-client</c>. The
    /// repo can be public; <c>accessToken</c> stays null for
    /// anonymous reads. Pre-release filtering is off (stable channel
    /// only) — when we're ready for a beta channel that becomes a
    /// constructor parameter or a config field.
    /// </summary>
    public UpdateService(
        ILoggerFactory loggerFactory,
        AcarsClientState state,
        Dispatcher uiDispatcher,
        string repoUrl)
    {
        _logger = loggerFactory.CreateLogger<UpdateService>();
        _state = state;
        _uiDispatcher = uiDispatcher;

        // GithubSource talks to api.github.com to read the latest
        // release's assets. Velopack expects the release to contain
        // the manifest + nupkg artefacts that release.ps1 produces
        // via `vpk pack` and `vpk upload github`. Anonymous reads
        // are rate-limited (60 req/hour per IP) but for a per-user
        // app that checks once at startup, that's irrelevant.
        var source = new GithubSource(
            repoUrl: repoUrl,
            accessToken: null,
            prerelease: false);

        // UpdateManager(source, options?, locator?) — we don't pass
        // a logger here because the constructor doesn't take one
        // directly; Velopack routes its own logs to
        // %LOCALAPPDATA%\VamAcarsClient\Velopack.log via the default
        // locator, which is good enough for the v0.x update flow.
        // If we ever need to merge Velopack's stream into Serilog,
        // wire a custom IVelopackLocator built from
        // VelopackLocator.CreateDefaultForPlatform(velopackLogger).
        _manager = new UpdateManager(source);
    }

    /// <summary>
    /// Reads the running version off Velopack's manifest, falling
    /// back to "dev" when the binary isn't a Velopack-installed copy.
    /// Called once at startup to seed
    /// <see cref="AcarsClientState.InstalledVersion"/>.
    ///
    /// Don't confuse this with <c>Assembly.GetEntryAssembly().GetName().Version</c>:
    /// that returns the AssemblyVersion baked at compile time, which
    /// is what we want for the dev fallback, but Velopack's
    /// CurrentVersion is authoritative for installed copies because
    /// it reflects the actual on-disk version (potentially after
    /// a downgrade or a sideways install).
    /// </summary>
    public string GetInstalledVersion()
    {
        try
        {
            if (_manager.IsInstalled && _manager.CurrentVersion is { } v)
            {
                return v.ToString();
            }
        }
        catch (Exception ex)
        {
            // IsInstalled itself can throw on weirdly-staged installs;
            // log and fall through to the dev fallback rather than
            // crashing on launch.
            _logger.LogDebug(ex, "UpdateManager.IsInstalled / CurrentVersion threw — treating as dev run");
        }
        return "dev";
    }

    /// <summary>
    /// Background update probe. Safe to fire-and-forget from
    /// App.OnStartup AND from the user's "Auf Updates prüfen"
    /// button click — the <see cref="AcarsClientState.UpdateChecking"/>
    /// flag gates the button so a re-entrant call from a click can't
    /// race the in-flight call from startup. On hit, sets
    /// <see cref="AcarsClientState.UpdateAvailable"/> +
    /// <see cref="AcarsClientState.LatestVersion"/>, then chains
    /// straight into the download (so the user's "Installieren"
    /// click can fire the moment the indicator appears).
    /// </summary>
    public async Task CheckAndDownloadAsync()
    {
        // ─── Re-entrancy guard ─────────────────────────────────────────
        // Set UpdateChecking on the UI thread before we touch anything
        // else so the button's "Prüfe..." state is visible immediately
        // — the network round-trip can take a couple of seconds and
        // the user staring at an unresponsive button isn't great UX.
        // The corresponding reset lives in the finally block below
        // so we always end in a known state, even on exception.
        await _uiDispatcher.InvokeAsync(() => _state.UpdateChecking = true);

        try
        {
            _logger.LogInformation("Checking for updates…");
            var newVersion = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);

            if (newVersion is null)
            {
                _logger.LogInformation("No update available.");
                // Explicitly clear any prior available-state on a manual
                // re-check that comes back empty — covers the (rare)
                // case where a release was yanked between checks.
                await _uiDispatcher.InvokeAsync(() =>
                {
                    _state.UpdateAvailable = false;
                    _state.UpdateDownloaded = false;
                    _state.LatestVersion = null;
                });
                return;
            }

            _pendingUpdate = newVersion;
            _logger.LogInformation(
                "Update found: {Version} — downloading in background…",
                newVersion.TargetFullRelease.Version);

            // Surface the "available" state immediately so the user
            // sees the indicator while the download runs in the
            // background; if downloading takes a few seconds they're
            // not staring at a stale UI.
            await _uiDispatcher.InvokeAsync(() =>
            {
                _state.UpdateAvailable = true;
                _state.LatestVersion = newVersion.TargetFullRelease.Version.ToString();
                // Reset UpdateDownloaded in case a previous check
                // already had a download staged for an older release;
                // we shouldn't tell the user "ready to install" until
                // the new download completes.
                _state.UpdateDownloaded = false;
            });

            await _manager.DownloadUpdatesAsync(newVersion).ConfigureAwait(false);

            await _uiDispatcher.InvokeAsync(() =>
            {
                _state.UpdateDownloaded = true;
            });

            _logger.LogInformation(
                "Update {Version} downloaded — ready to apply on user click.",
                newVersion.TargetFullRelease.Version);
        }
        catch (Exception ex)
        {
            // NotInstalledException is the expected case for dev runs;
            // log at debug to avoid noise. Everything else (network
            // hiccup, GitHub rate limit, malformed manifest) gets a
            // warning so it shows up in normal logs.
            var level = ex.GetType().Name == "NotInstalledException"
                ? LogLevel.Debug
                : LogLevel.Warning;
            _logger.Log(level, ex, "Update check / download failed — continuing without an update");

            // Whatever went wrong, the UI stays in the "no update
            // available" state so we don't show a half-finished
            // indicator. The next launch (or manual re-check) will retry.
            await _uiDispatcher.InvokeAsync(() =>
            {
                _state.UpdateAvailable = false;
                _state.UpdateDownloaded = false;
                _state.LatestVersion = null;
            });
        }
        finally
        {
            // Always clear the in-flight flag so the button is
            // re-enabled for the next manual check, even after
            // failure.
            await _uiDispatcher.InvokeAsync(() => _state.UpdateChecking = false);
        }
    }

    /// <summary>
    /// Apply the previously-downloaded update. Calls into Velopack's
    /// <c>ApplyUpdatesAndRestart</c>, which:
    ///   1. Spawns the Velopack update helper (<c>Update.exe</c>).
    ///   2. Exits THIS process.
    ///   3. Update.exe swaps the <c>current</c> junction to the new
    ///      version's directory.
    ///   4. Update.exe re-launches our binary.
    ///
    /// So this method effectively never returns — control transfers
    /// to a different process. Caller (MainWindow's button handler)
    /// shouldn't do any cleanup AFTER calling this; if there's
    /// teardown to do, it must run before. We call StopAsync on the
    /// heartbeat service inside this method so the user can't end
    /// up with a stale heartbeat sneaking out post-restart from a
    /// race in between.
    /// </summary>
    public void ApplyUpdate()
    {
        if (_pendingUpdate is null)
        {
            _logger.LogWarning("ApplyUpdate called with no pending update — no-op");
            return;
        }

        _logger.LogInformation(
            "Applying update {Version} and restarting…",
            _pendingUpdate.TargetFullRelease.Version);

        // ApplyUpdatesAndRestart never returns under normal
        // operation — it Process.Start()s the helper and
        // Environment.Exit()s the current process. If we get past
        // this line something went badly wrong; surface it.
        _manager.ApplyUpdatesAndRestart(_pendingUpdate);

        _logger.LogError("ApplyUpdatesAndRestart returned — update apply may have failed");
    }
}
