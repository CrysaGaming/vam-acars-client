using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VamAcarsClient.Tray.Models;

/// <summary>
/// One row in the pre-flight checklist (option #10). A simple
/// POCO with <see cref="System.ComponentModel.INotifyPropertyChanged"/>
/// so the WPF CheckBox in the PRE-FLIGHT card can two-way-bind to
/// <see cref="IsChecked"/> without per-item code-behind plumbing.
///
/// Three fields, no further mutation surface:
///   - <see cref="Key"/>: stable string identifier ("doors_closed",
///     "beacon_on", …). Currently only used for diagnostics, but
///     kept distinct from <see cref="Label"/> so the persisted /
///     wire format never accidentally embeds a translated label.
///   - <see cref="Label"/>: user-facing German text rendered next
///     to the checkbox. Init-only — labels don't change at runtime.
///   - <see cref="IsChecked"/>: the only mutable bit. Toggles via
///     the bound CheckBox click and fires PropertyChanged so the
///     parent <see cref="AcarsClientState.PreflightComplete"/>
///     computed property can re-evaluate.
///
/// We deliberately don't extend this with severity, group, or
/// dependency fields. The checklist is a flat list of independent
/// gates; if it ever grows complex enough to need those concepts
/// we'd be better off pulling in a real CommunityToolkit.Mvvm
/// observable model rather than re-inventing it here.
/// </summary>
public sealed class PreflightChecklistItem : INotifyPropertyChanged
{
    /// <summary>Stable identifier — see class docstring for rationale.</summary>
    public required string Key { get; init; }

    /// <summary>Localised label shown next to the checkbox.</summary>
    public required string Label { get; init; }

    private bool _isChecked;
    /// <summary>
    /// Two-way bound to the CheckBox's IsChecked. Mutating this
    /// fires PropertyChanged, which the parent state listens for
    /// to refresh <see cref="AcarsClientState.PreflightComplete"/>.
    /// </summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
