using System.ComponentModel;
using System.Windows;

namespace VamAcarsClient.Tray;

/// <summary>
/// Status-window code-behind. Most of the UI logic lives in the XAML
/// data-bindings against <see cref="Models.AcarsClientState"/>. The
/// only behavioural quirk this class adds: closing via the X button
/// hides the window instead of disposing it, so the tray-icon click
/// can simply re-show the existing instance with all its state intact.
///
/// Why hide-instead-of-close: the alternative (re-creating the window
/// every time) loses any user-side state — scroll position, expand/
/// collapse-state of future cards, copy-paste selections, etc. Tray-
/// apps universally do hide-on-close, so users expect this behaviour.
/// The actual app-shutdown happens via the tray Exit menu, not the
/// window's X button.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
}
