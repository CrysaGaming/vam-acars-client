using System.ComponentModel;
using System.Windows;
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
    public MainWindow()
    {
        InitializeComponent();
        PopulateFromSavedContext();
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
    /// staged. No additional gating needed here.
    /// </summary>
    private void OnApplyUpdateClick(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        app.ApplyUpdate();
    }
}
