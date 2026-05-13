using System.Diagnostics;
using System.IO;
using System.Windows;
using VamAcarsClient.Core;

namespace VamAcarsClient.Tray;

/// <summary>
/// Crash-report dialog (Welle A — option A5). Shown by
/// <see cref="App.OnDispatcherUnhandledException"/> when an unhandled
/// exception reaches the UI thread.
///
/// The dialog is fully self-contained: it receives the CrashReportResult
/// from the writer and renders it. It has no awareness of the original
/// exception or the app state — by the time the dialog opens, the
/// writer has already serialised everything relevant into the report
/// JSON, and the dialog's job is just to surface it to the user.
///
/// Lifecycle:
///   1. App.OnDispatcherUnhandledException catches the exception.
///   2. Writes the report via CrashReportWriter.Capture.
///   3. Instantiates this dialog with the result.
///   4. ShowDialog() blocks until the user closes it.
///   5. App's handler marks the exception as Handled so the process
///      doesn't tear down. User keeps working.
///
/// Why modal (ShowDialog) rather than non-modal: the user is currently
/// blocked by a crash they need to acknowledge. A non-modal dialog
/// could end up behind MainWindow and feel like the app just froze.
/// </summary>
public partial class CrashReportDialog : Window
{
    private readonly CrashReportResult _report;
    private const string GitHubIssuesUrl =
        "https://github.com/CrysaGaming/vam-acars-client/issues/new";

    public CrashReportDialog(CrashReportResult report, string errorSummary)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
        InitializeComponent();

        // The summary is the truncated exception message — keeps the
        // header readable. Full type + stack lives in the report body
        // below.
        ErrorSummary.Text = errorSummary;

        // Show the path when the writer succeeded, hide otherwise.
        // We don't want a phantom "Bericht: (null)" line in the header
        // when the write failed — that's confusing.
        if (!string.IsNullOrEmpty(_report.FilePath))
        {
            ReportPathLine.Text = $"Bericht: {_report.FilePath}";
        }
        else
        {
            ReportPathLine.Text = "(Berichtsdatei konnte nicht gespeichert werden — Inhalt unten ist kopierbar.)";
        }

        ReportTextBox.Text = _report.Json;
    }

    /// <summary>
    /// Copy the full JSON report to the clipboard. Wrapped in try/catch
    /// because Clipboard.SetText can occasionally throw COMException
    /// when another process holds the clipboard lock (Remote Desktop,
    /// some screen-recorder utilities). We catch + ignore — the user
    /// can manually select + Ctrl+C from the read-only TextBox as a
    /// fallback.
    /// </summary>
    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_report.Json);
            CopyButton.Content = "✓ Kopiert";
        }
        catch
        {
            CopyButton.Content = "Kopieren fehlgeschlagen — manuell auswählen";
        }
    }

    /// <summary>
    /// Open the reports folder in Explorer. Falls back to opening the
    /// parent (LocalAppData) folder if the reports folder itself
    /// doesn't exist (which would only happen if the writer failed
    /// before creating it).
    /// </summary>
    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string? targetPath = null;
            if (!string.IsNullOrEmpty(_report.FilePath))
            {
                // Open Explorer with the report file pre-selected. /select,
                // is the documented Explorer flag for "open the parent
                // folder and highlight this child file".
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{_report.FilePath}\"",
                    UseShellExecute = true,
                });
                return;
            }

            // No file written — open the reports folder by path, or
            // its parent if reports doesn't exist either.
            targetPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VamAcarsClient",
                "reports");
            if (!Directory.Exists(targetPath))
            {
                targetPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VamAcarsClient");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = targetPath,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Don't surface the error to the user — the buttons are
            // best-effort affordances. Worst case they can navigate
            // to %LOCALAPPDATA% manually.
        }
    }

    /// <summary>
    /// Open the GitHub Issues page in the default browser. We don't
    /// prefill the body because URL-encoded JSON would balloon the
    /// URL past most browsers' ~8 KB limit. The Copy button covers
    /// the prefill use-case — user clicks Copy, then GitHub, then
    /// pastes into the issue.
    /// </summary>
    private void OnGitHubClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = GitHubIssuesUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // No-op — same reasoning as OnOpenFolderClick. User can
            // navigate to the repo manually.
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
