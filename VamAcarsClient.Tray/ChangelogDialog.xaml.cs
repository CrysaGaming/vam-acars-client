using System.Windows;
using VamAcarsClient.Core;

namespace VamAcarsClient.Tray;

/// <summary>
/// Changelog viewer dialog (Welle A — option A4). Opens from the
/// "Changelog anzeigen" button in EINSTELLUNGEN, fetches the most
/// recent release notes from GitHub, renders them as a scrollable
/// list of version entries.
///
/// Lifecycle:
///   1. MainWindow.OnShowChangelogClick instantiates with Owner=main
///      and calls ShowDialog().
///   2. Constructor wires up the UI and fires-and-forgets the initial
///      LoadAsync call. The dialog is interactive immediately; the
///      list populates when the fetch completes.
///   3. While the fetch is in flight, LoadingText is visible and the
///      content scroll is collapsed.
///   4. On completion: if entries exist, ContentScroll becomes visible
///      and the LoadingText hides. If empty, EmptyText takes over.
///   5. The user can click "Neu laden" to refetch without closing —
///      same path as the initial load, just driven by a button click.
///   6. Close button (or Enter/Escape via IsDefault/IsCancel) closes.
///      The dialog has no notion of "DialogResult=true vs false" —
///      it's a read-only viewer, nothing to commit.
///
/// Threading:
///   - LoadAsync runs the HTTP fetch on the default thread via
///     ChangelogFetcher.FetchAsync; the await context picks up the
///     UI dispatcher on resume, so the post-fetch state mutations
///     (Visibility flips, ItemsSource assign) all run on the UI
///     thread without explicit MarshalToUi.
///   - The fetch CTS is tied to the dialog's lifetime so closing the
///     dialog mid-fetch doesn't leak a request that mutates closed
///     controls. (See _cts field + OnClosing override.)
/// </summary>
public partial class ChangelogDialog : Window
{
    /// <summary>
    /// Cancellation source for in-flight fetches. Cancelled in
    /// OnClosing so a fetch that hasn't returned by the time the user
    /// closes the dialog doesn't try to mutate disposed controls.
    /// Recreated on each LoadAsync call.
    /// </summary>
    private CancellationTokenSource? _cts;

    public ChangelogDialog()
    {
        InitializeComponent();

        // Fire-and-forget the initial load. The dialog is interactive
        // immediately (the close button works, the refresh button is
        // available — it just queues another fetch on top of this one
        // which the second LoadAsync cancels via _cts re-assignment).
        _ = LoadAsync();
    }

    /// <summary>
    /// Fetch + render. Owns the loading/empty/content state transitions
    /// so the Refresh button can call it identically to the initial
    /// load path.
    ///
    /// Re-entrancy: each call cancels any previous in-flight fetch via
    /// _cts.Cancel() before starting a new one. Means the user can hit
    /// "Neu laden" rapid-fire without piling up concurrent requests;
    /// only the most recent click's result lands in the UI.
    /// </summary>
    private async Task LoadAsync()
    {
        // Cancel any in-flight fetch from a previous click. The old
        // FetchAsync will return [] from its OperationCanceledException
        // catch and silently die without touching the UI.
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Reset the visibility tri-state to "loading" before kicking
        // off. If this is a re-load triggered by the Neu-laden button
        // and the previous fetch had populated the list, the user sees
        // the loading text briefly before the new entries arrive —
        // matches the initial-load experience.
        LoadingText.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        ContentScroll.Visibility = Visibility.Collapsed;
        ReleaseList.ItemsSource = null;

        // Pull the App's factory which wires in the shared HttpClient
        // and a logger. Going through CreateChangelogFetcher keeps the
        // App's _http / _loggerFactory private rather than exposing
        // them as public properties (which would collide with the
        // Microsoft.Extensions.Logging.LoggerFactory static class name).
        var app = (App)Application.Current;
        var fetcher = app.CreateChangelogFetcher();

        var entries = await fetcher.FetchAsync(ct);

        // If we got cancelled mid-fetch (dialog closing, or a refresh
        // click that fired a new LoadAsync), don't touch the UI — the
        // controls may be disposed and ItemsSource assignment would
        // throw. The new LoadAsync (if any) will handle the UI from here.
        if (ct.IsCancellationRequested) return;

        // Decide the empty-vs-populated branch and toggle visibility.
        // Both branches hide LoadingText since the load is done either
        // way.
        LoadingText.Visibility = Visibility.Collapsed;
        if (entries.Count == 0)
        {
            EmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            ReleaseList.ItemsSource = entries;
            ContentScroll.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Refresh button click. Same as initial load — LoadAsync handles
    /// the re-entrancy via CTS cancellation, so a rapid-fire double-
    /// click on the refresh button never queues two fetches.
    /// </summary>
    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _ = LoadAsync();
    }

    /// <summary>
    /// Close button click. Just calls Close() — the CTS-cancel in
    /// OnClosing handles any in-flight fetch cleanup. We don't set
    /// DialogResult because this is a read-only dialog; the caller
    /// doesn't care if the user closed via the button vs the X vs
    /// Escape.
    /// </summary>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Override to cancel any in-flight fetch when the user closes the
    /// dialog. Without this, a slow fetch that returns after Close()
    /// would try to assign ItemsSource to disposed controls and throw.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _cts?.Cancel();
        base.OnClosing(e);
    }
}
