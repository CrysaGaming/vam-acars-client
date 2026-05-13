using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VamAcarsClient.Core;

/// <summary>
/// Captures structured crash reports for the Welle A — A5
/// error-reporting workflow.
///
/// When an unhandled exception reaches the App's
/// DispatcherUnhandledException hook, we want three things to happen
/// in this order:
///
///   1. A self-contained JSON report lands on disk under
///      <c>%LOCALAPPDATA%\VamAcarsClient\reports\crash-{utc-timestamp}.json</c>
///      so the user can attach it to a bug report without us trying
///      to upload anything on their behalf (no telemetry, no auto-
///      submit — user controls disclosure).
///
///   2. The report is small enough to paste into a GitHub issue body
///      (under ~5 KB typical) but rich enough to debug post-mortem:
///      exception chain, .NET runtime version, OS version, app
///      version, simulator state (if known), tail of the running log
///      file. We capture the log-tail by re-reading the active
///      Serilog file sink, rather than wiring an in-process buffer
///      that'd add lifetime complexity to the logger setup.
///
///   3. The writer NEVER throws into the crash-handler. Anything that
///      goes wrong here (disk full, log file locked, JSON-encoding
///      failure) collapses to a synthetic minimal report so the
///      CrashReportDialog at least surfaces SOMETHING actionable. We
///      don't want a crash-in-the-crash-handler to silently swallow
///      the original failure.
///
/// THREAD MODEL:
///   Called from the DispatcherUnhandledException callback which
///   already runs on the UI thread. We do a small amount of synchronous
///   IO (log-tail read + JSON write) before returning to the caller —
///   acceptable in a crash path where the user is going to see the
///   dialog anyway, and dispatching to a worker would risk the process
///   dying before the IO completes.
/// </summary>
public sealed class CrashReportWriter
{
    private readonly VamConfig _config;
    private readonly ILogger<CrashReportWriter> _logger;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    /// Max lines of the active log file to include in the report's
    /// "log_tail" field. 200 lines balances "enough context to trace
    /// what was happening just before the crash" against keeping the
    /// JSON small enough to copy-paste comfortably.
    /// </summary>
    private const int LogTailLines = 200;

    public CrashReportWriter(
        VamConfig config,
        ILogger<CrashReportWriter>? logger = null,
        Func<DateTimeOffset>? now = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? NullLogger<CrashReportWriter>.Instance;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Build + write a crash report for the given exception. Returns
    /// the rendered JSON string AND the file path it was written to
    /// (so the dialog can show both inline AND offer "open the file
    /// in explorer"). Both are returned even if the file-write failed;
    /// in that case the FilePath is null and the JSON is still
    /// available for clipboard-copy.
    ///
    /// All exceptions inside the writer are swallowed — the only thing
    /// callers should fear is "what if this returns inconsistent or
    /// trimmed data?". Answer: it returns the best-effort capture
    /// with placeholders for fields it couldn't fill, never throws.
    /// </summary>
    public CrashReportResult Capture(Exception exception, AppContextSnapshot? appContext = null)
    {
        if (exception is null) throw new ArgumentNullException(nameof(exception));

        // Build the report dict step by step so partial failures still
        // produce a usable JSON. Each section is wrapped in a try/catch
        // and falls back to a placeholder string ("(unavailable: …)").
        var report = new Dictionary<string, object?>(StringComparer.Ordinal);

        // ─── timestamp + envelope ──────────────────────────────────
        var now = _now();
        report["timestamp_utc"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        report["report_format_version"] = 1;

        // ─── exception chain ───────────────────────────────────────
        // Walks the InnerException chain so a wrapped exception
        // (TargetInvocationException → actual exception) doesn't hide
        // the root cause. Each entry is type + message + stack trace,
        // serialised separately so the JSON stays human-readable
        // rather than collapsing into a single multi-line string.
        try
        {
            var chain = new List<object>();
            var current = (Exception?)exception;
            var depth = 0;
            while (current is not null && depth < 10)
            {
                chain.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = current.GetType().FullName,
                    ["message"] = current.Message,
                    ["stack_trace"] = current.StackTrace,
                    ["source"] = current.Source,
                });
                current = current.InnerException;
                depth++;
            }
            report["exception_chain"] = chain;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CrashReportWriter: failed to capture exception chain");
            report["exception_chain"] = $"(unavailable: {ex.Message})";
        }

        // ─── runtime + OS ──────────────────────────────────────────
        try
        {
            report["dotnet_version"] = RuntimeInformation.FrameworkDescription;
            report["os_version"] = RuntimeInformation.OSDescription;
            report["os_architecture"] = RuntimeInformation.OSArchitecture.ToString();
            report["process_architecture"] = RuntimeInformation.ProcessArchitecture.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CrashReportWriter: failed to capture runtime info");
            report["runtime_info_error"] = ex.Message;
        }

        // ─── app version ───────────────────────────────────────────
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            report["app_version"] = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                                    ?? asm.GetName().Version?.ToString()
                                    ?? "(unknown)";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CrashReportWriter: failed to capture app version");
            report["app_version"] = $"(unavailable: {ex.Message})";
        }

        // ─── app context snapshot (optional, caller-supplied) ──────
        // The caller can pass in a snapshot of the bound app state at
        // crash time (connection status, paired flag, sim probe). We
        // don't take a direct reference to AcarsClientState here so
        // Core stays UI-agnostic.
        if (appContext is not null)
        {
            report["app_context"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["connection_status"] = appContext.ConnectionStatus,
                ["has_token"] = appContext.HasToken,
                ["sim_detected"] = appContext.DetectedSimulator,
                ["aircraft_type"] = appContext.AircraftType,
                ["heartbeats_sent"] = appContext.HeartbeatsSent,
                ["heartbeats_failed"] = appContext.HeartbeatsFailed,
                ["status_message"] = appContext.StatusMessage,
            };
        }

        // ─── log tail ──────────────────────────────────────────────
        // Find the most recent Serilog file in the configured logs dir
        // (Serilog rolls daily by default → newest mtime is "today").
        // Read the last N lines using a streaming read so a 100 MB log
        // file doesn't blow up our memory.
        try
        {
            var logsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                _config.Storage.LocalAppDataFolderName,
                "logs");

            if (Directory.Exists(logsDir))
            {
                var newest = new DirectoryInfo(logsDir)
                    .GetFiles("*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (newest is not null)
                {
                    report["log_file"] = newest.Name;
                    report["log_tail"] = ReadLastLines(newest.FullName, LogTailLines);
                }
                else
                {
                    report["log_tail"] = "(no log files in directory)";
                }
            }
            else
            {
                report["log_tail"] = "(logs directory missing)";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CrashReportWriter: failed to capture log tail");
            report["log_tail"] = $"(unavailable: {ex.Message})";
        }

        // ─── serialise + write ─────────────────────────────────────
        string json;
        try
        {
            json = JsonSerializer.Serialize(report, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CrashReportWriter: JSON serialization failed; using minimal fallback");
            // Last-ditch fallback: a plain string. Caller still gets
            // something usable even if the structured report is broken.
            json = $"Crash at {now:O}\n{exception}";
        }

        // Write to disk. If this fails we still return the JSON for
        // clipboard-copy, so the user has a path forward even when
        // the filesystem is hostile.
        string? filePath = null;
        try
        {
            var reportsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                _config.Storage.LocalAppDataFolderName,
                "reports");
            Directory.CreateDirectory(reportsDir);

            // Timestamp filename — sortable, no collisions even on
            // back-to-back crashes within the same second (we append
            // ticks-fractional). UTC so logs are unambiguous across
            // timezones.
            var filename = $"crash-{now:yyyyMMdd-HHmmss}-{now.Ticks % 10_000:D4}.json";
            filePath = Path.Combine(reportsDir, filename);
            File.WriteAllText(filePath, json);
            _logger.LogInformation("CrashReportWriter: wrote {File}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CrashReportWriter: failed to write report to disk");
            filePath = null;
        }

        return new CrashReportResult(json, filePath);
    }

    /// <summary>
    /// Read the last N lines of a text file without loading it fully
    /// into memory. Opens the file with FileShare.ReadWrite so we
    /// don't fight Serilog for the lock — if the file is being
    /// actively written we can still read what's already on disk.
    /// </summary>
    private static string ReadLastLines(string path, int n)
    {
        // Buffered read from the end is cleaner but more code; for
        // typical log-file sizes (under 10 MB) the streaming-read of
        // the whole file is fast enough and far simpler. We cap the
        // read at 5 MB to guard against pathological multi-GB logs.
        const int MaxReadBytes = 5 * 1024 * 1024;

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        if (stream.Length > MaxReadBytes)
        {
            stream.Seek(-MaxReadBytes, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream);
        // Ring-buffer of the last N lines. We discard older ones as
        // we go, so peak memory is bounded by N * avg-line-length
        // (typically under 100 KB).
        var ring = new Queue<string>(capacity: n);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (ring.Count >= n) ring.Dequeue();
            ring.Enqueue(line);
        }

        return string.Join("\n", ring);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Don't escape unicode chars — keeps "Übersetzungs" readable
        // in the output JSON rather than \u00DCbersetzungs.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// Result of <see cref="CrashReportWriter.Capture"/>. Contains the
/// rendered JSON (always present, even if the file-write failed) and
/// the file path (null when the write failed).
/// </summary>
public sealed record CrashReportResult(string Json, string? FilePath);

/// <summary>
/// Optional app-state snapshot the caller can pass to enrich the crash
/// report. Decouples Core from Tray's AcarsClientState type so this
/// stays UI-agnostic. Caller maps their state fields into this record
/// at crash-time.
/// </summary>
public sealed record AppContextSnapshot(
    string? ConnectionStatus,
    bool HasToken,
    string? DetectedSimulator,
    string? AircraftType,
    long HeartbeatsSent,
    long HeartbeatsFailed,
    string? StatusMessage);
