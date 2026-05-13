using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VamAcarsClient.Core;

/// <summary>
/// Welle A — A7. Background cleanup job that deletes old log files from
/// <c>%LOCALAPPDATA%\VamAcarsClient\logs\</c>. Serilog rolls daily, so
/// over a year of regular use accumulates ~365 files — small individually
/// (a few hundred KB to a few MB each) but the directory bloat makes
/// support-debugging slow ("which log was the day I had that issue?")
/// and on SSD-pressured systems it's polite to clean up after ourselves.
///
/// Design rationale:
///   - 30-day retention by default. Long enough for the user to
///     correlate a "this happened last month" complaint with logs;
///     short enough that the directory doesn't grow unbounded over
///     years.
///   - Crash reports (<c>reports\</c>) are explicitly NOT touched here.
///     Those are user-facing artifacts (the user might still need to
///     report an old crash) and have a different lifecycle. If a future
///     feature wants to clean those too, it goes through a dedicated
///     opt-in path.
///   - Run once at app startup (fire-and-forget Task.Run from
///     <see cref="VamAcarsClient.Tray.App.OnStartup"/>). The active
///     log file for today is excluded automatically because Serilog
///     holds an exclusive write lock on it — the delete just throws
///     IOException, we catch and skip.
///   - All exceptions swallowed. A failed cleanup never blocks app
///     startup; the next launch will retry with fresh state.
/// </summary>
public static class LogsCleanupService
{
    /// <summary>
    /// Default retention window in days. Files with
    /// <see cref="FileInfo.LastWriteTimeUtc"/> older than this many
    /// days are eligible for deletion.
    /// </summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>
    /// Scan <c>%LOCALAPPDATA%\&lt;LocalAppDataFolderName&gt;\logs\</c>
    /// and delete <c>*.log</c> files older than
    /// <paramref name="retentionDays"/>. Returns the number of files
    /// actually deleted (for logging / future telemetry — caller can
    /// ignore).
    ///
    /// Designed to be called via <c>Task.Run(() => CleanupOldLogs(...))</c>
    /// from <c>App.OnStartup</c>. Runs synchronously inside whatever
    /// thread Task.Run gave it; no async IO because the directory is
    /// small (typically &lt;100 files), so the simple synchronous
    /// enumeration is faster than the async-state-machine overhead.
    /// </summary>
    public static int CleanupOldLogs(
        VamConfig config,
        ILogger? logger = null,
        int retentionDays = DefaultRetentionDays,
        Func<DateTimeOffset>? now = null)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        logger ??= NullLogger.Instance;
        var nowFn = now ?? (() => DateTimeOffset.UtcNow);

        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            config.Storage.LocalAppDataFolderName,
            "logs");

        if (!Directory.Exists(logsDir))
        {
            // First launch — no logs to clean. Cheap exit before any
            // file-system iteration.
            logger.LogDebug("LogsCleanupService: logs directory missing, nothing to clean");
            return 0;
        }

        var cutoff = nowFn().UtcDateTime - TimeSpan.FromDays(retentionDays);
        var deleted = 0;
        var skipped = 0;
        var totalChecked = 0;

        try
        {
            // EnumerateFiles avoids materialising the full array up
            // front — for 100s of files this difference is irrelevant,
            // but it's the right idiom for a possibly-large directory.
            foreach (var path in Directory.EnumerateFiles(logsDir, "*.log"))
            {
                totalChecked++;
                try
                {
                    var info = new FileInfo(path);
                    if (info.LastWriteTimeUtc < cutoff)
                    {
                        File.Delete(path);
                        deleted++;
                    }
                }
                catch (IOException ex)
                {
                    // Most common cause: today's active log file held
                    // open by Serilog. Expected, log at Debug.
                    skipped++;
                    logger.LogDebug(ex,
                        "LogsCleanupService: skipped (likely in-use) {Path}", path);
                }
                catch (UnauthorizedAccessException ex)
                {
                    // Less common but possible — file marked read-only
                    // or permission tightened externally. Skip + log.
                    skipped++;
                    logger.LogDebug(ex,
                        "LogsCleanupService: skipped (unauthorized) {Path}", path);
                }
                catch (Exception ex)
                {
                    // Catch-all for surprises (network drive disappeared
                    // mid-enum, etc.) — log at Warning since these
                    // aren't expected.
                    skipped++;
                    logger.LogWarning(ex,
                        "LogsCleanupService: unexpected error deleting {Path}", path);
                }
            }
        }
        catch (Exception ex)
        {
            // Directory enumeration itself failed (rare — permission
            // change, removable media disconnected). Log Warning and
            // bail; the next launch retries.
            logger.LogWarning(ex,
                "LogsCleanupService: directory enumeration failed for {LogsDir}", logsDir);
            return deleted;
        }

        if (deleted > 0 || skipped > 0)
        {
            logger.LogInformation(
                "LogsCleanupService: checked {Total} log files, deleted {Deleted}, skipped {Skipped} (retention: {Days} days)",
                totalChecked, deleted, skipped, retentionDays);
        }
        else
        {
            logger.LogDebug(
                "LogsCleanupService: checked {Total} log files, none eligible for deletion (retention: {Days} days)",
                totalChecked, retentionDays);
        }

        return deleted;
    }
}
