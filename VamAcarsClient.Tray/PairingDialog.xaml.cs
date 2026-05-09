using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using VamAcarsClient.Core;

namespace VamAcarsClient.Tray;

/// <summary>
/// In-app pairing dialog. Replaces the CLI-only Mode 1 flow for users
/// who never see the dev console — same backend protocol, friendlier UI.
///
/// Lifecycle:
///   1. MainWindow's "Pairen…" button (visible only when HasToken=false)
///      calls <c>new PairingDialog { Owner = main }.ShowDialog()</c>.
///   2. User opens vam.kevindrack.de/settings (Hyperlink → Process.Start),
///      generates a 9-char one-shot code (XXX-XXX-XXX), pastes into
///      <c>CodeBox</c>, clicks Pairen.
///   3. We invoke <see cref="App.PairDeviceAsync"/> which wraps
///      <c>PairingService.RedeemAsync</c> + <c>TokenStore.Save</c> +
///      <c>State.HasToken=true</c>.
///   4. On success: DialogResult=true, dialog closes, MainWindow's
///      VERBINDUNG card already shows "✓ Gepaart" because the underlying
///      State binding flipped.
///   5. On failure: status text shows the error, user can retry without
///      closing the dialog.
///
/// Design notes:
///   - We do NOT take a PairingService dependency directly — the App
///     already owns the shared HttpClient + TokenStore + State. The
///     dialog talks to App through one method (PairDeviceAsync) rather
///     than reaching into App's privates.
///   - DataContext = App.State so the {Binding ServerUrl} inside the
///     Hyperlink-text resolves. We don't bind anything else from State
///     to keep the dialog self-contained.
///   - Async-void on the click handler is fine here — it's the canonical
///     async-void scenario (UI event), upstream errors are caught + shown.
/// </summary>
public partial class PairingDialog : Window
{
    private bool _isSending;

    public PairingDialog()
    {
        InitializeComponent();
        // DataContext = App.State for the Hyperlink's {Binding ServerUrl}.
        // App.State is already constructed by the time any UI surface
        // (including this dialog) opens — guaranteed by App.OnStartup
        // running first.
        DataContext = ((App)Application.Current).State;
        // Initial focus on the code input — user came here to type.
        // Loaded event fires once after the window opens; CodeBox.Focus
        // before the window is visible is a no-op so we hook Loaded.
        Loaded += (_, _) => CodeBox.Focus();
    }

    /// <summary>
    /// Hyperlink RequestNavigate handler. Builds the URL from
    /// <see cref="App.Config"/> (so dev-mode hits localhost:3000) and
    /// hands it to the system shell. Process.Start with UseShellExecute
    /// is the documented way to open a default-browser URL from .NET 6+.
    ///
    /// The Hyperlink doesn't have a static NavigateUri because the
    /// server URL is dynamic (config-driven); we read App.Config inline
    /// rather than maintaining a separate XAML binding for the URI.
    /// </summary>
    private void OnSettingsLinkClick(object sender, RequestNavigateEventArgs e)
    {
        var app = (App)Application.Current;
        var url = app.Config.Vam.ApiBaseUrl.TrimEnd('/') + "/settings";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowError($"Browser konnte nicht geöffnet werden: {ex.Message}");
        }
        e.Handled = true;
    }

    /// <summary>
    /// Live formatting / validation hook. We don't enforce a strict
    /// XXX-XXX-XXX shape (the server is forgiving — case-insensitive,
    /// dashes optional), but we DO clear any prior error message as
    /// soon as the user starts editing again, so a stale "Code abgelehnt"
    /// doesn't haunt them while they correct a typo.
    /// </summary>
    private void OnCodeChanged(object sender, TextChangedEventArgs e)
    {
        if (StatusText.Visibility == Visibility.Visible
            && _isSending == false)
        {
            StatusText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Enter-key fallback. The Submit button is IsDefault="True", so
    /// Enter inside any focused control of the dialog triggers it. This
    /// handler exists only as a guard against the (unlikely) case where
    /// some focus-context swallows the default-button routing — keeps
    /// "type code, hit Enter" working no matter what.
    /// </summary>
    private void OnCodeBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_isSending)
        {
            OnSubmitClick(SubmitButton, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private async void OnSubmitClick(object sender, RoutedEventArgs e)
    {
        if (_isSending) return; // re-entrancy guard

        var rawCode = CodeBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            ShowError("Bitte Pairing-Code eingeben.");
            return;
        }

        _isSending = true;
        SubmitButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        CodeBox.IsEnabled = false;
        ShowStatus("Sende an Server …", (Brush)Resources["AccentBlue"]);

        try
        {
            var app = (App)Application.Current;
            var result = await app.PairDeviceAsync(rawCode);

            if (result.IsSuccess)
            {
                ShowStatus(
                    $"Erfolgreich gepaart als {result.DisplayName ?? "—"}.",
                    (Brush)Resources["AccentGreen"]);

                // Schließen mit kurzer verzögerung damit der user den
                // success-zustand sieht statt ein abruptes verschwinden.
                await Task.Delay(800);
                DialogResult = true;
                Close();
                return;
            }

            ShowError(result.ErrorMessage ?? "Pairing fehlgeschlagen.");
        }
        catch (PairingTransportException ex)
        {
            ShowError($"Verbindungsfehler: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Catch-all so an unforeseen exception path doesn't kill
            // the dialog. App.PairDeviceAsync logs internally; we just
            // surface a friendly message and let the user retry.
            ShowError($"Unerwarteter Fehler: {ex.Message}");
        }
        finally
        {
            _isSending = false;
            // Re-enable nur wenn wir noch offen sind (success-path
            // closed schon via DialogResult=true).
            if (IsLoaded)
            {
                SubmitButton.IsEnabled = true;
                CancelButton.IsEnabled = true;
                CodeBox.IsEnabled = true;
            }
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowStatus(string message, Brush brush)
    {
        StatusText.Text = message;
        StatusText.Foreground = brush;
        StatusText.Visibility = Visibility.Visible;
    }

    private void ShowError(string message)
        => ShowStatus(message, (Brush)Resources["AccentRed"]);
}
