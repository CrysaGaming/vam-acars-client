using System.Windows;
using System.Windows.Controls;

namespace VamAcarsClient.Tray;

/// <summary>
/// Code-behind for the aircraft-substitution dialog (Welle B / B4 phase 2C).
///
/// # Lifecycle
///
/// Instantiated by <see cref="MainWindow"/>'s connect-flow when the
/// sim-loaded aircraft type differs from the booking's aircraft type.
/// Caller pre-fills <see cref="BookedSummary"/> + <see cref="FlownSummary"/>
/// via the constructor, then calls <see cref="Window.ShowDialog"/> on the
/// UI thread.
///
/// On confirm: <see cref="ChosenIntent"/> + <see cref="Reason"/> hold the
/// pilot's disposition; <see cref="Window.DialogResult"/> is true. On
/// cancel / X-button: DialogResult is false (or null for X), and
/// ChosenIntent is null.
///
/// # Intent values
///
/// "intentional"  → pilot deliberately chose the wrong aircraft, optional
///                  reason captured in <see cref="Reason"/>.
/// "wrongLoaded"  → pilot wants to fix the loaded aircraft. Caller aborts
///                  the connect-flow client-side; no heartbeat sent.
/// "wrongBooking" → pilot believes the booking is wrong, admin should
///                  reconcile. Reason is null (the disposition itself is
///                  the signal).
///
/// These three strings match the server-side zod enum literals for the
/// heartbeat's aircraftSubstitution.intent block (minus "wrongLoaded"
/// which never reaches the server — caller's responsibility to filter).
///
/// # Why three radios + a reason textbox rather than a more elaborate UI
///
/// The decision is small and one-shot. Anything more (sliders, dropdowns,
/// "are you sure" follow-ups) would slow down the pre-connect ritual
/// without adding value — pilots flying the wrong plane mostly know why
/// and just want to get on with the flight.
/// </summary>
public partial class AircraftSubstitutionDialog : Window
{
    /// <summary>
    /// Disposition the user picked, or null if the dialog was cancelled.
    /// One of "intentional", "wrongLoaded", "wrongBooking".
    /// </summary>
    public string? ChosenIntent { get; private set; }

    /// <summary>
    /// Free-text reason supplied for the "intentional" path, trimmed,
    /// max 200 chars (enforced by TextBox.MaxLength). Empty string is
    /// normalized to null so the heartbeat payload can omit the field.
    /// </summary>
    public string? Reason { get; private set; }

    /// <summary>
    /// Construct + pre-fill the mismatch summary. Both summaries are
    /// pre-composed by the caller in display format like
    /// "A20N · D-AINA" so this dialog stays purely presentational.
    /// </summary>
    /// <param name="bookedSummary">Format: "ICAO · registration".</param>
    /// <param name="flownSummary">Format: "ICAO · registration".</param>
    public AircraftSubstitutionDialog(string bookedSummary, string flownSummary)
    {
        InitializeComponent();
        BookedSummary.Text = bookedSummary;
        FlownSummary.Text = flownSummary;
    }

    /// <summary>
    /// Radio-button Checked handler. Three jobs:
    ///   1. Reveal the reason-textbox when "Beabsichtigt" is selected
    ///      (and hide it for the other two options so the dialog
    ///      doesn't carry stale free-text into a different disposition).
    ///   2. Enable the Confirm button — any selection lets the user
    ///      proceed; the Reason field is optional even for "intentional".
    ///   3. No-op when called during InitializeComponent (sender's
    ///      IsChecked can fire before the visual tree is fully built);
    ///      defensive null-checks on ReasonPanel + ConfirmButton.
    /// </summary>
    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (ReasonPanel is null || ConfirmButton is null) return;

        ReasonPanel.Visibility = sender == OptionIntentional
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Clear stale reason text when leaving the intentional option so
        // a later re-tick of "Beabsichtigt" doesn't surface text the
        // pilot typed for a different disposition.
        if (sender != OptionIntentional && ReasonTextBox is not null)
        {
            ReasonTextBox.Text = string.Empty;
        }

        ConfirmButton.IsEnabled = true;
    }

    /// <summary>
    /// Confirm-button handler. Reads the currently-checked radio to
    /// determine ChosenIntent, captures + normalizes the Reason text
    /// when applicable, and closes the dialog with DialogResult=true.
    ///
    /// Defensive: if somehow no radio is checked (shouldn't happen
    /// because the button is gated on selection), we treat the click
    /// as cancel rather than crash.
    /// </summary>
    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (OptionIntentional.IsChecked == true)
        {
            ChosenIntent = "intentional";
            var trimmed = ReasonTextBox?.Text?.Trim();
            Reason = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }
        else if (OptionWrongLoaded.IsChecked == true)
        {
            ChosenIntent = "wrongLoaded";
            Reason = null;
        }
        else if (OptionWrongBooking.IsChecked == true)
        {
            ChosenIntent = "wrongBooking";
            Reason = null;
        }
        else
        {
            // No selection — degrade to cancel.
            DialogResult = false;
            Close();
            return;
        }

        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Cancel-button handler. Sets DialogResult=false (caller treats
    /// this as "user wants to bail out of the connect-flow" — same as
    /// closing with X-button, which leaves DialogResult=null and is
    /// also treated as cancel by the caller).
    /// </summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
