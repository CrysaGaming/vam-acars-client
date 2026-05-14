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

        // Option #13: also refresh the recovery banner's elapsed-time
        // line so "vor 3 Min unterbrochen" → "vor 4 Min unterbrochen"
        // ticks live while the banner is visible. Cheap when no marker
        // is present (the property's getter returns null in that case),
        // so we don't bother branching on HasRecoverableSession.
        app.State?.RaiseRecoverableSessionSummaryChanged();
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

                // ─── Welle B / B4 phase 2C — booking + mismatch dialog ─
                //
                // Before calling StartAsync, fetch the user's active
                // booking via /api/acars/active-booking. If a booking
                // exists, compare its aircraft type against the sim-
                // loaded DetectedAircraftType. On mismatch, show the
                // AircraftSubstitutionDialog and route by disposition:
                //
                //   - "wrongLoaded"             → abort Verbinden so the
                //                                 user can fix the sim
                //   - "intentional"/"wrongBooking" → stage on State.Pending*
                //                                 and continue with
                //                                 StartAsync (which will
                //                                 pick up the pending
                //                                 substitution and ship
                //                                 it on the first heartbeat)
                //   - cancelled (X / Abbrechen) → abort Verbinden — user
                //                                 isn't ready to commit
                //
                // The dialog is shown synchronously (ShowDialog blocks
                // until the user closes it). That's fine here because
                // we're already on the UI thread inside a button-click
                // handler; the connect-flow is intended to gate
                // anyway, and the dialog gives the pilot a moment to
                // decide before heartbeats start.
                //
                // Skip-conditions (no dialog shown, just proceed):
                //   - Booking fetch failed (logged but not fatal — the
                //     pilot might still want to fly free-flight)
                //   - No active booking on the account (free flight)
                //   - Booking has no aircraft assignment (charter, type-
                //     flexible route)
                //   - DetectedAircraftType not available (user hasn't
                //     clicked "Sim erkennen" yet — we don't fault them
                //     here; the existing pre-flight checklist nags them
                //     into running the probe)
                //   - Aircraft types match (case-insensitive trim)
                //
                // Why fire FetchActiveBookingAsync from here rather than
                // automatically when the user opens the window: many
                // sessions are run-then-disconnect-then-rerun within
                // minutes, and we'd rather pay one HTTP per Verbinden
                // click than poll continuously. The Verbinden click is
                // the moment the disposition matters most.
                await service.FetchActiveBookingAsync();

                var bookedType = app.State.ActiveBookingAircraftType?.Trim();
                var flownType = app.State.DetectedAircraftType?.Trim();
                var hasBooking = !string.IsNullOrWhiteSpace(bookedType);
                var hasFlown = !string.IsNullOrWhiteSpace(flownType);
                var typesDiffer = hasBooking && hasFlown
                    && !string.Equals(bookedType, flownType, StringComparison.OrdinalIgnoreCase);

                if (typesDiffer)
                {
                    // Compose pre-formatted "ICAO · REG" strings so the
                    // dialog stays purely presentational.
                    var bookedReg = app.State.ActiveBookingAircraftRegistration?.Trim();
                    var flownReg = app.State.DetectedAircraftRegistration?.Trim();
                    var bookedSummary = string.IsNullOrWhiteSpace(bookedReg)
                        ? bookedType!
                        : $"{bookedType} · {bookedReg}";
                    var flownSummary = string.IsNullOrWhiteSpace(flownReg)
                        ? flownType!
                        : $"{flownType} · {flownReg}";

                    var dialog = new AircraftSubstitutionDialog(bookedSummary, flownSummary)
                    {
                        Owner = this,
                    };
                    var result = dialog.ShowDialog();

                    // Cancel-path: false (Abbrechen) or null (X-close).
                    // Either way the user opted out of committing to a
                    // disposition; do NOT proceed with Verbinden. Status
                    // message lets them know why nothing happened.
                    if (result != true || dialog.ChosenIntent is null)
                    {
                        app.State.StatusMessage =
                            "Verbinden abgebrochen — Flugzeug-Diskrepanz nicht dispositioniert.";
                        return;
                    }

                    // wrongLoaded: client-side abort. Pilot intends to
                    // fix the loaded aircraft; no heartbeat should go
                    // out. Status message routes them through the
                    // expected next step (re-load aircraft in MSFS,
                    // re-run Sim erkennen, click Verbinden again).
                    if (dialog.ChosenIntent == "wrongLoaded")
                    {
                        app.State.StatusMessage =
                            "Lade in MSFS das korrekte Flugzeug, dann erneut „Sim erkennen“ und „Verbinden“.";
                        return;
                    }

                    // intentional or wrongBooking: stage the disposition
                    // on State.Pending* so AcarsClientService.StartAsync
                    // picks it up and forwards to HeartbeatService.
                    // bookedType / flownType captured at this exact
                    // moment — the snapshot the pilot saw when they
                    // made the choice — even if the booking is later
                    // edited or the sim-loaded plane changes.
                    app.State.PendingSubstitutionIntent = dialog.ChosenIntent;
                    app.State.PendingSubstitutionBookedType = bookedType;
                    app.State.PendingSubstitutionFlownType = flownType;
                    app.State.PendingSubstitutionReason = dialog.Reason;
                }

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
    /// Welle D / D5 — Demo-mode checkbox click handler. Same shape as
    /// <see cref="OnAudioCueClick"/>: read post-click IsChecked, delegate
    /// to <see cref="App.SetDemoModeEnabled"/> which routes through
    /// SavePreferencesFromState so other preference fields stay intact.
    ///
    /// Note that toggling this checkbox during an active session does NOT
    /// tear down the running heartbeat-loop — see SetDemoModeEnabled's
    /// docstring for the rationale (don't yank the rug out from a pilot
    /// mid-flight; the gate at the top of AcarsClientService.StartAsync
    /// is what enforces the no-heartbeat contract). The tooltip on the
    /// checkbox ("wirksam ab dem nächsten Verbinden") documents this
    /// for the pilot.
    /// </summary>
    private void OnDemoModeClick(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        var enabled = DemoModeCheck.IsChecked == true;
        app.SetDemoModeEnabled(enabled);
    }

    /// <summary>
    /// Welle E / E1 — OBS-overlay-server checkbox click handler. Same
    /// shape as <see cref="OnDemoModeClick"/>: read post-click IsChecked,
    /// delegate to <see cref="App.SetOverlayServerEnabled"/> which BOTH
    /// persists the pref AND starts/stops the actual
    /// <see cref="OverlayServer"/>.
    ///
    /// Unlike <see cref="OnDemoModeClick"/> the toggle takes effect
    /// immediately — the overlay-server is independent of the heartbeat
    /// lifecycle, so we don't need the "wirksam ab nächstem Verbinden"
    /// caveat. If <c>SetOverlayServerEnabled</c> fails to claim a port
    /// it flips State.OverlayServerEnabled back to false, which the
    /// OneWay binding picks up and re-renders the checkbox as unticked
    /// — the user sees the failure visually without needing a popup.
    /// </summary>
    private void OnOverlayServerClick(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        var enabled = OverlayServerCheck.IsChecked == true;
        app.SetOverlayServerEnabled(enabled);
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
    /// "Changelog anzeigen" button handler (Welle A — option A4).
    /// Opens <see cref="ChangelogDialog"/> as a modal owned by this
    /// MainWindow. The dialog itself fires-and-forgets a GitHub fetch
    /// in its constructor; we just instantiate and show.
    ///
    /// Why modal: the changelog is a single read-only flow. A modal
    /// blocks the rest of the UI which keeps the user focused on
    /// reading the release notes; closing returns them exactly where
    /// they were. A non-modal would create the question "wait, where
    /// did the changelog window go?" if it gets shuffled behind
    /// MainWindow.
    ///
    /// Owner=this so the dialog sits centred over MainWindow and
    /// follows it through z-order and minimize/restore.
    /// </summary>
    private void OnShowChangelogClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ChangelogDialog
        {
            Owner = this,
        };
        dialog.ShowDialog();
    }

    /// <summary>
    /// "Gerät pairen…" button handler. Opens <see cref="PairingDialog"/>
    /// as a modal window owned by this MainWindow, so it sits centred
    /// and blocks input until the user finishes (or cancels) pairing.
    ///
    /// On dialog success (DialogResult=true) we don't need to do
    /// anything explicit here — the App's PairDeviceAsync already flipped
    /// State.HasToken=true, which:
    ///   - hides this button (DataTrigger on HasToken=False → Collapsed)
    ///   - shows the "Gerät neu koppeln" button (BoolToVis on HasToken)
    ///   - flips the VERBINDUNG card's Token row to "✓ Gepaart"
    ///
    /// On cancel/error the dialog closes without state changes; the
    /// button stays available for retry.
    ///
    /// Re-entrancy: ShowDialog blocks until the dialog closes, so the
    /// button can't be clicked twice in flight even without an explicit
    /// guard. We don't need IsEnabled gymnastics.
    /// </summary>
    private void OnPairClick(object sender, RoutedEventArgs e)
    {
        var dialog = new PairingDialog
        {
            Owner = this,
        };
        dialog.ShowDialog();
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
    /// Token-rotate button handler (Welle A — option A3). Routine
    /// workflow: tear down the current session, clear the stored token,
    /// and open the pairing dialog automatically so the user can redeem
    /// a fresh code in one continuous flow.
    ///
    /// Distinct from <see cref="OnRepairClick"/> by intent and tone:
    ///   - Repair = panic mode. "Something is broken, let me unpair."
    ///     MessageBox confirmation, AccentRed framing, no automatic
    ///     follow-on. The user explicitly opts in to the destruction.
    ///   - Rotate = routine mode. "I want a fresh token." No
    ///     confirmation (that IS the intent), AccentBlue framing,
    ///     PairingDialog auto-opens. The user is mid-task and the
    ///     button completes the task.
    ///
    /// The pairing dialog opens RIGHT after UnpairAsync completes —
    /// no intermediate UI step. The dialog itself is modal owned by
    /// MainWindow (same as OnPairClick), so it blocks input and sits
    /// centred. If the user cancels the dialog they're left in the
    /// unpaired state (which matches what they explicitly asked for
    /// by clicking Rotate); they can use the regular "Gerät pairen…"
    /// button to retry whenever.
    ///
    /// Async-void rationale: same as OnRepairClick — UI event handler,
    /// service surfaces errors via state, no caller to await us.
    /// </summary>
    private async void OnRotateTokenClick(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        var service = app.Service;
        if (service is null)
        {
            // Defensive: see OnRepairClick for the same reasoning. The
            // button is HasToken-gated so the service should always
            // exist, but fail loud if a future refactor breaks that.
            MessageBox.Show(
                this,
                "Service ist nicht verfügbar — bitte App neu starten.",
                "Fehler",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        // UnpairAsync stops any running heartbeat session, clears the
        // DPAPI-encrypted token from disk, and flips state.HasToken to
        // false. It also writes a friendly status-message which we
        // immediately override below — the dialog is opening anyway,
        // a "Gerät entkoppelt" line in the footer would be misleading
        // because the rotation isn't actually done yet.
        await service.UnpairAsync();

        // Hand off straight to the pairing dialog. Same pattern as
        // OnPairClick — modal, owned by this window. On success the
        // dialog has already flipped HasToken back to true via App's
        // PairDeviceAsync, so the UI updates itself.
        var dialog = new PairingDialog
        {
            Owner = this,
        };
        dialog.ShowDialog();
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

    /// <summary>
    /// "Wiederverbinden" button on the recovery banner (option #13).
    /// Delegates to <see cref="App.ResumeRecoverableSessionAsync"/>
    /// which translates the marker back into a FlightContext, pre-fills
    /// the form, and clears the in-memory recoverable-session state
    /// (which collapses the banner via the BoolToVis binding).
    ///
    /// Async-void for the same reasons as the other click handlers
    /// here. App.ResumeRecoverableSessionAsync currently completes
    /// synchronously (it's a Task only for future-proofing), so the
    /// await returns essentially immediately.
    /// </summary>
    private async void OnResumeSessionClick(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        await app.ResumeRecoverableSessionAsync();
    }

    /// <summary>
    /// "Verwerfen" button on the recovery banner (option #13).
    /// Delegates to <see cref="App.DiscardRecoverableSession"/> which
    /// deletes the marker file and clears the bound state. Banner
    /// hides immediately as a side effect of the state mutation.
    /// </summary>
    private void OnDiscardSessionClick(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        app.DiscardRecoverableSession();
    }

    /// <summary>
    /// OFP-Import button handler (option #12). Reads the multi-line
    /// text from <c>OfpInputBox</c>, runs it through the heuristic
    /// <see cref="OfpParser"/> in Core, and pre-fills whichever
    /// FLUG-KONTEXT form fields the parser confidently extracted.
    /// Reports the outcome via <see cref="App.State"/>.StatusMessage
    /// so the footer-strip tells the user how many fields landed.
    ///
    /// Why fill via the bound form-fields directly (CallsignBox.Text =
    /// ...) rather than going through SaveFlightContext + reload:
    /// SaveFlightContext is the persistence path for "user just
    /// connected with these values"; OFP-import is "user is editing".
    /// We only commit to disk when they hit Verbinden (existing flow
    /// in OnConnectClick). This keeps OFP-import non-destructive — if
    /// the user pastes garbage and the parser fills nothing, the form
    /// keeps its previous values, no save happened, and they can
    /// retry without losing their last manually-typed plan.
    ///
    /// Fields mapped:
    ///   - Callsign     → CallsignBox.Text
    ///   - DepartureIcao → DeparturBox.Text
    ///   - ArrivalIcao   → ArrivalBox.Text
    ///
    /// Fields the parser extracts but the form has no slot for
    /// (CruiseAltitudeFt, AircraftType, FlightRules): mentioned in the
    /// status message so the user knows the parser saw them, but not
    /// auto-filled. These flow through the heartbeat once Verbinden is
    /// pressed via the saved <see cref="App.LastFlightContext"/> path —
    /// future enhancement could expose them as additional form fields.
    ///
    /// Network is not extracted from OFPs (SimBrief / FlightAware don't
    /// know whether the user intends to fly online or offline), so
    /// NetworkBox stays untouched.
    ///
    /// Empty / whitespace-only input → "Bitte OFP-text einfügen." and
    /// return without touching anything. Parser returns null on the
    /// same condition, so the dual-check is belt-and-braces.
    /// </summary>
    private void OnOfpImportClick(object sender, RoutedEventArgs e)
    {
        var raw = OfpInputBox.Text;
        var app = (App)Application.Current;

        // Welle B / B5: clear any previous OFP-suggestion banner before
        // each fresh import. If the new OFP has no callsign, or its
        // callsign matches a different route, the banner needs to
        // reflect the NEW import — a stale banner from the previous
        // paste would be misleading.
        app.State.ClearOfpSuggestion();

        if (string.IsNullOrWhiteSpace(raw))
        {
            app.State.StatusMessage = "Bitte OFP-text einfügen.";
            return;
        }

        var parsed = OfpParser.Parse(raw);
        if (parsed is null)
        {
            app.State.StatusMessage = "OFP-Import: kein verwertbarer Text.";
            return;
        }

        // Track which fields actually landed in the form so the status
        // message can name them. The user can then sanity-check by
        // glancing at the FLUG-KONTEXT card above.
        var filled = new List<string>();

        if (!string.IsNullOrWhiteSpace(parsed.Callsign))
        {
            CallsignBox.Text = parsed.Callsign;
            filled.Add("Callsign");

            // ─── Welle B / B5: route-suggestion lookup ───────────────
            //
            // Compare the parsed callsign against the cached airline-
            // routes catalogue. Three outcomes:
            //
            //   - Empty cache (catalogue not fetched yet, fetch failed,
            //     or solo pilot)         → skip silently. No banner.
            //   - Match found             → State.OfpMatchedRoute set →
            //     green "Buchung erstellen" banner via XAML binding.
            //   - No match                → State.OfpNoMatchWarning set
            //     → amber info banner via XAML binding.
            //
            // Lookup is case-insensitive trim on FlightNumber. The
            // airline-routes endpoint returns routes ordered by
            // flightNumber asc, so a linear scan is fine at the
            // expected scale (a few hundred routes per airline).
            //
            // Done inside the Callsign branch so we don't run the
            // lookup when the parser found no callsign — the OFP is
            // then unmatched-by-construction, and the empty state is
            // less misleading than a no-match warning would be.
            var callsignNeedle = parsed.Callsign.Trim();
            if (app.State.AirlineRoutes.Count > 0)
            {
                AirlineRoute? matched = null;
                foreach (var route in app.State.AirlineRoutes)
                {
                    if (string.Equals(
                            route.FlightNumber?.Trim(),
                            callsignNeedle,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        matched = route;
                        break;
                    }
                }

                if (matched is not null)
                {
                    app.State.OfpMatchedRoute = matched;
                }
                else
                {
                    app.State.OfpNoMatchWarning =
                        $"Route {callsignNeedle} ist nicht in deiner Airline. " +
                        "Wende dich an den Admin für eine Genehmigung.";
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(parsed.DepartureIcao))
        {
            DeparturBox.Text = parsed.DepartureIcao;
            filled.Add("Departure");
        }
        if (!string.IsNullOrWhiteSpace(parsed.ArrivalIcao))
        {
            ArrivalBox.Text = parsed.ArrivalIcao;
            filled.Add("Arrival");
        }

        // Extracted-but-unbound fields. We collect them into a separate
        // bucket so the status line can mention "FL360 erkannt (kein
        // Form-Feld)" without it looking like a fill action — gives the
        // pilot transparency about what the parser saw.
        var saw = new List<string>();
        if (parsed.CruiseAltitudeFt is { } cruiseFt)
        {
            // Render flight-levels in the same notation the OFP used.
            // 3-digit FL form for ≥ FL100, 2-digit for below — this is
            // what SimBrief and FlightAware both do.
            var fl = cruiseFt / 100;
            saw.Add($"Cruise FL{fl:D3}");
        }
        if (!string.IsNullOrWhiteSpace(parsed.AircraftType)) saw.Add($"Type {parsed.AircraftType}");
        if (!string.IsNullOrWhiteSpace(parsed.FlightRules)) saw.Add(parsed.FlightRules);

        // Compose status. Three branches:
        //   1) Form fields filled + extras seen → "3 Felder + Cruise FL360"
        //   2) Form fields filled, no extras    → "3 Felder importiert: …"
        //   3) Nothing matched                  → "Keine Felder erkannt"
        string status;
        if (filled.Count > 0)
        {
            status = $"OFP-Import: {filled.Count} Felder importiert ({string.Join(", ", filled)})";
            if (saw.Count > 0) status += $" · Erkannt: {string.Join(", ", saw)}";
        }
        else if (saw.Count > 0)
        {
            // Nothing filled but parser saw secondary fields — report
            // those so the user knows the parse worked, just not on
            // form-bound fields. Common when pasting a route summary
            // that has FL but no callsign/dep/arr labels.
            status = $"OFP-Import: keine Form-Felder erkannt · Erkannt: {string.Join(", ", saw)}";
        }
        else
        {
            status = "OFP-Import: keine Felder erkannt — Parser-Heuristik passt nicht zum Format.";
        }

        app.State.StatusMessage = status;
    }

    /// <summary>
    /// "Buchung erstellen" button on the OFP-suggestion match banner
    /// (Welle B / B5). Fires when the user has paste-imported an OFP
    /// whose callsign matched a route in their airline's catalogue,
    /// and they've decided to commit to a Booking against it.
    ///
    /// # Flow
    ///
    /// 1. Snapshot the matched route from state (set during OnOfpImportClick).
    /// 2. Call <see cref="Models.AcarsClientService.CreateBookingFromRouteAsync"/>
    ///    which POSTs /api/acars/create-booking and returns the
    ///    structured result (success, policy-failure, or null on
    ///    transport/auth failure).
    /// 3. Branch on the result:
    ///    - Success      → status message confirms booking-id, then
    ///                     clears the banner (booking is now active,
    ///                     the next Verbinden cycle picks it up via
    ///                     FetchActiveBookingAsync).
    ///    - Policy-fail  → status message shows the server's German
    ///                     message verbatim. Banner stays (the user
    ///                     might want to retry or read the warning
    ///                     again). For active-booking-exists the
    ///                     existing-booking-id is logged but not
    ///                     surfaced — v2 could offer a "switch to
    ///                     existing" affordance.
    ///    - null         → status message asks the user to retry.
    ///                     Banner stays.
    ///
    /// # State
    ///
    /// IsCreatingBookingFromOfp is flipped true inside the service
    /// for the duration of the POST; the XAML disables the button
    /// while that flag is set so a double-click can't fire two
    /// concurrent POSTs.
    ///
    /// Async-void: canonical UI event handler. The service swallows
    /// + logs transport exceptions; we don't await this method.
    /// </summary>
    private async void OnCreateBookingFromOfpClick(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        var service = app.Service;
        if (service is null) return; // shouldn't happen post-OnStartup

        var matched = app.State.OfpMatchedRoute;
        if (matched is null)
        {
            // Defensive: the button is only visible when
            // HasOfpMatchedRoute is true, but a fast click-through
            // a stale state could theoretically race. Just no-op.
            return;
        }

        // Snapshot id locally so a concurrent ClearOfpSuggestion
        // (from a fresh OFP-import landing while this POST is in
        // flight) doesn't null-deref us mid-await.
        var routeId = matched.Id;
        var routeLabel = $"{matched.FlightNumber} ({matched.DepartureIcao}→{matched.ArrivalIcao})";

        var result = await service.CreateBookingFromRouteAsync(routeId);

        if (result is null)
        {
            // Transport / 401 / unexpected failure. Service has already
            // logged the details; surface a brief message so the user
            // knows the click didn't take effect.
            app.State.StatusMessage =
                $"Buchung {routeLabel} konnte nicht erstellt werden — Verbindungsproblem. Bitte erneut versuchen.";
            return;
        }

        if (result.Booking is not null)
        {
            // Happy path. Booking is committed; clear the suggestion
            // banner so the FLUG-KONTEXT area returns to its quiet
            // resting state, and surface a confirmation footer.
            app.State.StatusMessage =
                $"Buchung {result.Booking.FlightNumber} ({result.Booking.DepartureIcao}→{result.Booking.ArrivalIcao}) erstellt. Beim nächsten Verbinden wird sie automatisch geladen.";
            app.State.ClearOfpSuggestion();
            return;
        }

        // Policy-failure path. result.Message is the server's
        // human-readable German explanation — surface verbatim.
        // Banner stays so the user can read it alongside the
        // footer status. Empty-message defense: fallback to a
        // generic message that names the route, so the user at
        // least knows what was attempted.
        app.State.StatusMessage = !string.IsNullOrWhiteSpace(result.Message)
            ? result.Message
            : $"Buchung {routeLabel} konnte nicht erstellt werden ({result.Error ?? "unbekannter Fehler"}).";
    }
}
