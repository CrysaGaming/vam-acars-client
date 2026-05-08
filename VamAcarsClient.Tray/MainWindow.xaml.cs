using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using VamAcarsClient.Core;

namespace VamAcarsClient.Tray;

/// <summary>
/// Status-window code-behind. Most of the UI logic lives in the XAML
/// data-bindings against <see cref="Models.AcarsClientState"/>. This
/// class adds two behaviours that bindings can't express cleanly:
///
///   1. Closing via the X button hides the window instead of disposing
///      it (so the tray-click can re-open it with state intact).
///
///   2. The Connect/Disconnect button click-handler reads the form
///      fields, builds a <see cref="FlightContext"/>, and drives
///      <see cref="App.Service"/> Start/Stop.
///
/// Why hide-instead-of-close: re-creating the window every time would
/// lose user-side state (scroll, selections) and force the bindings
/// to re-evaluate from scratch. Tray-apps universally do hide-on-close
/// — users expect this. The actual app-shutdown happens via the tray
/// Exit menu, not the window's X button.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Drives 1Hz refreshes of <see cref="Models.AcarsClientState.PhaseDisplay"/>
    /// so the elapsed-time portion ("Cruise — 0:42") ticks live without
    /// the source-of-truth fields (CurrentPhase + PhaseEnteredAt) having
    /// to fire PropertyChanged on every second. DispatcherTimer fires
    /// on the UI thread, so the binding-engine work happens where it
    /// needs to anyway. 1s interval is the resolution of the displayed
    /// "m:ss" — finer would just waste cycles, coarser would visibly stutter.
    ///
    /// The timer runs unconditionally for the lifetime of the window
    /// (which is the lifetime of the app, since the window only ever
    /// hides + shows, never disposes). When CurrentPhase is null the
    /// computed property returns null and the binding shows "—" via
    /// TargetNullValue — the tick is harmless. CPU cost is negligible:
    /// one PropertyChanged raise + one binding-engine re-eval of a
    /// small string per second.
    /// </summary>
    private readonly DispatcherTimer _phaseTickTimer;

    public MainWindow()
    {
        InitializeComponent();
        PopulateFromSavedContext();

        // 1Hz tick to refresh the elapsed-time portion of the Phase row.
        // Hooked up after InitializeComponent so the binding-engine has
        // already wired up; no risk of firing before the binding exists.
        _phaseTickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _phaseTickTimer.Tick += OnPhaseTick;
        _phaseTickTimer.Start();
    }

    /// <summary>
    /// Per-second handler that nudges the state to re-evaluate
    /// <see cref="Models.AcarsClientState.PhaseDisplay"/>. The state
    /// raises PropertyChanged for the computed property and WPF's
    /// binding-engine fetches the new value — same path a regular
    /// setter would take, just driven by the clock instead of by an
    /// assignment.
    ///
    /// Defensive null-check on app.State because at very-early-startup
    /// (before App.OnStartup completes) Application.Current cast might
    /// race; cheap to guard, no observed crash but symmetry with other
    /// handlers that do the same.
    /// </summary>
    private void OnPhaseTick(object? sender, EventArgs e)
    {
        var app = (App)Application.Current;
        app.State?.RaisePhaseDisplayChanged();
    }

    /// <summary>
    /// Pre-populates the four flight-plan TextBoxes from the
    /// FlightContext that <see cref="App.OnStartup"/> loaded from disk.
    /// Falls through silently when no saved context exists (first
    /// launch or load failure) — the XAML's baked-in defaults
    /// (NGN901 / Offline / EDDF / EDDM) stay in place.
    ///
    /// Runs in the constructor (after InitializeComponent so the
    /// x:Name'd TextBox fields exist) rather than on Loaded so the
    /// UI never flickers from default → restored values: by the time
    /// the window is shown the boxes already hold the right text.
    ///
    /// We translate null/empty <c>DepartureIcao</c> + <c>ArrivalIcao</c>
    /// to empty strings rather than the XAML defaults — if the user
    /// last connected without a flight plan, that's the state they'd
    /// expect to come back to, not the boilerplate EDDF/EDDM.
    /// </summary>
    private void PopulateFromSavedContext()
    {
        var saved = ((App)Application.Current).LastFlightContext;
        if (saved is null) return;

        CallsignBox.Text = saved.Callsign;
        NetworkBox.Text = saved.Network;
        DeparturBox.Text = saved.DepartureIcao ?? string.Empty;
        ArrivalBox.Text = saved.ArrivalIcao ?? string.Empty;
    }

    /// <summary>
    /// Intercepts the window's Close intent. Sets <c>e.Cancel = true</c>
    /// to abort the close and instead hides the window. The hidden
    /// instance stays bound to the same <c>DataContext</c>, so when
    /// the user re-opens it (via tray-click or "Status-Fenster öffnen…"
    /// menu), the latest state is already populated.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        // App shutdown path bypasses this — when Application.Current.Shutdown
        // is called from the tray Exit menu, WPF tears down windows
        // without firing OnClosing on each (it goes through ExitEventArgs
        // instead). So we don't need a special "is the app exiting" guard
        // here — the only path through OnClosing is a user-initiated close.
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    /// <summary>
    /// Connect/Disconnect button click. Toggles based on the service's
    /// current IsRunning state (NOT on the button's visual label, which
    /// can briefly lag the actual state during transition animations).
    ///
    /// Disable the button for the duration of the transition so a
    /// rapid-fire double-click can't kick off two Start attempts. The
    /// PrimaryButton style already disables itself on
    /// ConnectionStatus=Connecting via DataTrigger, but the trigger
    /// fires AFTER StartAsync's first SetState arrives, leaving a
    /// few-ms window where a fast click could race in. Hard-disabling
    /// in code closes that race.
    /// </summary>
    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        var service = app.Service;
        if (service is null) return; // app not fully booted; shouldn't happen post-OnStartup

        ConnectButton.IsEnabled = false;
        try
        {
            if (service.IsRunning)
            {
                await service.StopAsync();
            }
            else
            {
                // Read form fields. TextBox.Text is never null for an
                // initialised TextBox, but trim defensively. Empty
                // ICAO fields are valid (free-flight without a flight
                // plan); we map "" → null for the FlightContext POCO.
                var flightContext = new FlightContext
                {
                    Callsign = string.IsNullOrWhiteSpace(CallsignBox.Text)
                        ? "NGN001"  // last-resort default; UI shouldn't allow empty in practice
                        : CallsignBox.Text.Trim().ToUpperInvariant(),
                    Network = string.IsNullOrWhiteSpace(NetworkBox.Text)
                        ? "Offline"
                        : NetworkBox.Text.Trim(),
                    DepartureIcao = string.IsNullOrWhiteSpace(DeparturBox.Text)
                        ? null
                        : DeparturBox.Text.Trim().ToUpperInvariant(),
                    ArrivalIcao = string.IsNullOrWhiteSpace(ArrivalBox.Text)
                        ? null
                        : ArrivalBox.Text.Trim().ToUpperInvariant(),
                };
                await service.StartAsync(flightContext);

                // Service started without throwing → the values the
                // user just connected with are worth preserving for
                // next launch. Done after StartAsync (not before) so
                // we never persist a context the user immediately
                // saw fail. App.SaveFlightContext swallows its own
                // exceptions so a write error here can't kill the
                // connect flow.
                app.SaveFlightContext(flightContext);
            }
        }
        finally
        {
            // Re-enable unless the style says we should stay disabled
            // (Connecting state). PrimaryButton's DataTrigger handles
            // the visual; we just need to clear our hard-disable so
            // future state transitions can re-style.
            ConnectButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Auto-start checkbox handler (M4 Phase 3). Reads the post-toggle
    /// IsChecked state and delegates to <see cref="App.SetAutoStart"/>,
    /// which owns the actual registry mutation + state-syncing.
    ///
    /// Click (not Checked / Unchecked) is intentional: those events
    /// also fire when the OneWay binding propagates a state change
    /// from outside the UI (registry hand-edited, App.OnStartup probe),
    /// which would loop an unintended Enable/Disable through the
    /// service. Click only fires on actual user gestures, breaking
    /// the loop cleanly.
    /// </summary>
    private void OnAutoStartClick(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        // CheckBox.IsChecked is bool? — null is the indeterminate
        // tri-state, which we never use here. The == true comparison
        // collapses null to false, matching our ON/OFF semantics.
        var enabled = AutoStartCheck.IsChecked == true;
        app.SetAutoStart(enabled);
    }

    /// <summary>
    /// Audio-cue checkbox click handler (option #5). Mirrors
    /// <see cref="OnAutoStartClick"/>'s shape exactly: read the
    /// post-click IsChecked, delegate to App.SetAudioCueEnabled
    /// which owns the in-memory state update + JSON-file persistence.
    /// Click (not Checked / Unchecked) for the same loop-breaking
    /// reason — propagated OneWay binding changes shouldn't echo
    /// back through the persistence path.
    /// </summary>
    private void OnAudioCueClick(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        var enabled = AudioCueCheck.IsChecked == true;
        app.SetAudioCueEnabled(enabled);
    }

    /// <summary>
    /// Update-installieren button handler (M5). Delegates to
    /// <see cref="App.ApplyUpdate"/>, which stops the heartbeat
    /// service and then hands off to Velopack's
    /// <c>ApplyUpdatesAndRestart</c>. The current process exits
    /// inside that call and the new version re-launches a moment
    /// later — there's no UI work to do after returning from
    /// ApplyUpdate, because in practice it doesn't return.
    ///
    /// The XAML binds <c>IsEnabled</c> to
    /// <see cref="Models.AcarsClientState.UpdateDownloaded"/>, so
    /// this handler will never fire before the nupkg is actually
    /// staged. No additional gating needed here for the
    /// download-readiness side.
    ///
    /// M5 Phase 3 connect-gating: if heartbeats are currently
    /// flowing, prompt the user for confirmation before tearing
    /// the session down. App.ApplyUpdate stops the service either
    /// way (necessary for clean Velopack apply), but the user
    /// might have just lined up an approach into EDDS at FL280
    /// and not realised an update was staged — a one-tap "are
    /// you sure" gate prevents accidentally bricking a 90-min
    /// flight. Default-No so a stray Enter-press doesn't dismiss
    /// the dialog into proceeding.
    /// </summary>
    private void OnApplyUpdateClick(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;

        // Guard rail: warn if heartbeats are flowing. Connecting
        // is treated the same as Connected — even a partial session
        // might already have committed flight-state on the server
        // that the user wouldn't want to abandon mid-handshake.
        var status = app.State.ConnectionStatus;
        if (status == Models.ConnectionStatus.Connected ||
            status == Models.ConnectionStatus.Connecting)
        {
            var result = MessageBox.Show(
                this,
                "Die Verbindung zum VAM-Server ist aktuell aktiv. " +
                "Beim Installieren des Updates wird die Sitzung beendet " +
                "und die App neu gestartet.\n\n" +
                "Trotzdem fortfahren?",
                "Update installieren?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes) return;
        }

        app.ApplyUpdate();
    }

    /// <summary>
    /// "Auf Updates prüfen" button handler (M5 Phase 2). Fires a
    /// fresh check against GitHub Releases without waiting for the
    /// next app restart. Re-entrancy is guarded inside the service
    /// via <see cref="Models.AcarsClientState.UpdateChecking"/>; the
    /// XAML disables the button while that flag is set so a fast
    /// double-click can't actually queue two concurrent checks.
    ///
    /// Fire-and-forget: the method returns immediately, the actual
    /// HTTP round-trip runs on a background thread, and the
    /// dispatcher-marshalled state mutations push the UI through
    /// "Prüfe..." → either back to "Auf Updates prüfen" (no update)
    /// or hidden behind the install block (update found).
    /// </summary>
    private void OnCheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        app.CheckForUpdatesNow();
    }

    /// <summary>
    /// Re-pair button handler (option #3). Confirms with the user that
    /// they really want to delete the stored token, then delegates to
    /// <see cref="Models.AcarsClientService.UnpairAsync"/>.
    ///
    /// Confirmation defaults to No (a stray Enter-press doesn't unpair).
    /// The dialog spells out the consequence ("Token löschen + Sitzung
    /// trennen") and the recovery step ("danach über die CLI neu pairen")
    /// so the user knows what they're committing to. We don't try to
    /// open the CLI for them — the CLI lives next to the tray exe but
    /// invoking it from a GUI process needs a console to be useful, and
    /// the in-app pairing dialog is itself on the roadmap.
    ///
    /// Async-void is fine here: it's a UI event handler (the canonical
    /// async-void scenario), exceptions inside UnpairAsync are already
    /// logged by the service, and there's no caller to await us. We
    /// don't show errors to the user beyond what UnpairAsync already
    /// surfaces via state.StatusMessage.
    /// </summary>
    private async void OnRepairClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "Den gespeicherten ACARS-Token löschen und die laufende Sitzung trennen?\n\n"
                + "Nach Bestätigung musst du das Gerät über die CLI mit einem frischen Pairing-Code neu koppeln, bevor du wieder verbinden kannst.",
            "Gerät neu koppeln?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes) return;

        var app = (App)Application.Current;
        var service = app.Service;
        if (service is null)
        {
            // Defensive: should never happen — the button is only
            // visible when HasToken=true, and HasToken can't be true
            // before the service is constructed in App.OnStartup. But
            // a startup-race or future refactor could trip this; fail
            // loud rather than silently swallow the click.
            MessageBox.Show(
                this,
                "Service ist nicht verfügbar — bitte App neu starten.",
                "Fehler",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        await service.UnpairAsync();
    }

    /// <summary>
    /// Sim-probe button handler (option #11). Delegates to
    /// <see cref="App.ProbeSimAsync"/> which owns the SimConnect probe
    /// + state-mutation + status-message. Async-void is fine here for
    /// the same reasons as the other click handlers in this file:
    /// canonical async-void scenario (UI event), upstream errors are
    /// already logged in the App method, no caller to await.
    ///
    /// Re-entrancy is guarded inside ProbeSimAsync via
    /// <see cref="Models.AcarsClientState.IsProbingSim"/>; the XAML
    /// disables the button while that flag is set so a fast double-
    /// click can't queue two concurrent SimConnect handshakes. The
    /// state-flag belt-and-braces against any timing window where the
    /// IsEnabled binding hasn't yet propagated.
    /// </summary>
    private async void OnProbeSimClick(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        await app.ProbeSimAsync();
    }
}
