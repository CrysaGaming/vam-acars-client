using Microsoft.Win32;

namespace VamAcarsClient.Core;

/// <summary>
/// Per-user "start with Windows" registration. Reads / writes the
/// well-known <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>
/// key, which Windows scans during user logon and silently launches
/// every value as a process. HKCU (not HKLM) so the install is
/// per-user — no admin rights needed for the toggle, and a different
/// Windows user on the same PC isn't dragged along.
///
/// The registry value's data is the absolute, quoted path to the
/// currently running <c>VamAcarsClientTray.exe</c>. Quoting matters
/// because Windows splits the run-line on whitespace by default, so
/// an unquoted path with a space in it (e.g. <c>C:\Program Files\…</c>)
/// would launch the wrong target. We write quoted, we don't read /
/// validate the contents — <see cref="IsEnabled"/> only checks
/// that the value exists, not that it points where we'd write it.
/// That keeps the toggle honest after a binary move: the user can
/// uncheck-and-recheck to repoint at the new location, and we don't
/// silently lie about "enabled" because of a stale string.
///
/// Threading: registry IO is synchronous and runs on the UI thread.
/// The toggle is a single click and the work is one or two registry
/// calls, &lt; 1 ms in practice. Async would buy nothing.
///
/// Mirror of <see cref="TokenStore"/> / <see cref="FlightContextStore"/>'s
/// shape — same constructor signature, similar Try-style API. The
/// caller (App.xaml.cs) treats it the same way: probe at startup,
/// mutate on user action.
/// </summary>
public sealed class AutoStartService
{
    /// <summary>
    /// The well-known per-user autostart key. Lives under HKCU so
    /// no UAC prompt; the OS reads this on every interactive logon.
    /// </summary>
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Registry value name. Hardcoded (not configurable via VamConfig)
    /// because changing it across versions would orphan the previous
    /// entry — we'd have a "ghost" registration the user couldn't
    /// disable from the UI. Stable name = clean upgrade path.
    /// </summary>
    private const string ValueName = "VamAcarsClient";

    /// <summary>
    /// True if a Run-key value with our name currently exists. Doesn't
    /// dereference the path or sanity-check the file — see class
    /// remarks for why "any value present" is the right truth here.
    ///
    /// Returns false on any access error (key missing, permission,
    /// IO). Auto-start is a convenience, not a safety boundary, so
    /// failing closed is the safe default — the user will see the
    /// checkbox unticked and can attempt re-enabling.
    /// </summary>
    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key is null) return false;
            return key.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Register the current executable for auto-start. Overwrites any
    /// existing value (acts as "repoint to current location" if the
    /// binary moved). Idempotent — safe to call when already enabled.
    ///
    /// The path comes from <see cref="Environment.ProcessPath"/>, the
    /// .NET 6+ canonical answer to "what file is this process?". On
    /// .NET 10 it always resolves for a normal process, including
    /// single-file-published apps where the AppContext.BaseDirectory
    /// would point at the extracted bundle dir instead of the .exe.
    ///
    /// Throws if the registry can't be written (rare — HKCU rarely
    /// denies writes). Caller catches and surfaces in the log; see
    /// <see cref="App.SetAutoStart"/>.
    /// </summary>
    public void Enable()
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "Environment.ProcessPath is null — can't determine current executable to register for auto-start");

        // CreateSubKey opens-or-creates with write access; if the Run
        // key happens to be missing on a stripped-down Windows install
        // we want to create it rather than fail.
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException(
                $"Failed to open or create HKCU\\{RunKeyPath}");

        // Quote the path so Windows treats it as a single argv[0]
        // even when the install path contains spaces (Program Files,
        // user profiles with spaces, etc.). REG_SZ is the canonical
        // type for Run entries — REG_EXPAND_SZ would also work but
        // adds nothing for a literal absolute path.
        key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
    }

    /// <summary>
    /// Remove our auto-start registration. Idempotent — silently
    /// does nothing if no value exists. Doesn't touch other apps'
    /// Run entries.
    /// </summary>
    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return;

        // throwOnMissingValue: false makes this a no-op if the user
        // already removed the entry by hand (regedit, third-party
        // startup-manager, etc.). We don't want to crash the toggle
        // because the work was already done.
        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
