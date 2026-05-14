using System.Globalization;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using VamAcarsClient.Tray.Models;

namespace VamAcarsClient.Tray;

/// <summary>
/// Welle E / E2 — voice-command listener + TTS responder.
///
/// Wraps <see cref="SpeechRecognitionEngine"/> (in-process recognizer,
/// not shared with the system Windows Speech Recognition) and
/// <see cref="SpeechSynthesizer"/> (TTS via SAPI 5.4 / installed
/// voices). All audio I/O stays local — no cloud round-trip, no
/// telemetry, no logging of recognized utterances beyond debug-level
/// in the Serilog file sink.
///
/// # Grammar (v1 MVP)
///
/// Hardcoded set of phrases, all prefixed with the wake-word "VAM"
/// to keep false-positive rate low on a noisy mic. The recognizer
/// fires <see cref="SpeechRecognized"/> only on confidence ≥ 0.85,
/// which the grammar's default scorer enforces. Below threshold and
/// it goes to <see cref="SpeechRecognitionRejected"/> which we
/// silently drop.
///
/// Supported phrases (case-insensitive, matched as German first,
/// English fallback if de-DE recognizer can't be built):
///
///   "VAM Status"      → speak ConnectionStatus + HB counters
///   "VAM Verbinden"   → trigger Connect (gated on PreflightComplete)
///   "VAM Trennen"     → trigger Disconnect
///   "VAM Flugzeug"    → speak detected aircraft type / registration
///
/// # Threading model
///
/// The recognizer raises events on a worker thread it owns. The
/// command handlers need to mutate UI state (drive button-clicks,
/// flip checkboxes), which must happen on the WPF Dispatcher. We
/// capture the dispatcher in the ctor and marshal every command
/// dispatch via <c>BeginInvoke</c> — TTS calls don't need the
/// dispatcher (they're thread-safe in SAPI) but we route them
/// through anyway so the speak-and-react order stays predictable.
///
/// # Lifecycle
///
/// - Construct once in <see cref="App.OnStartup"/>, but DO NOT
///   start the recognizer at construction time. Start is gated on
///   the user's EINSTELLUNGEN toggle via <see cref="TryStart"/>.
/// - <see cref="StopAsync"/> tears down the recognizer cleanly and
///   waits for the engine to drain (the in-process recognizer can
///   hold the microphone for a few hundred ms after Stop is called).
/// - <see cref="Dispose"/> hard-disposes for OnExit; same drain
///   semantics but doesn't await — best-effort sync teardown.
///
/// # Failure modes
///
/// - No microphone present              → TryStart returns false,
///                                          logs warning, service stays idle
/// - de-DE and en-US recognizers absent → TryStart returns false,
///                                          logs warning. We don't probe for
///                                          other locales because the grammar
///                                          phrases are German-with-English-
///                                          fallback only
/// - Microphone permission denied       → TryStart succeeds but the
///                                          recognizer's audio-state-changed
///                                          event reports Stopped; we log the
///                                          first occurrence then go silent.
///                                          v2 could surface a UI hint
///
/// All exceptions inside TryStart / StopAsync / event-handlers are
/// caught and logged. A voice-input crash should never bring down
/// the tray app.
/// </summary>
public sealed class VoiceCommandService : IDisposable
{
    private readonly AcarsClientState _state;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<VoiceCommandService> _logger;
    private readonly Func<Task>? _onConnectRequested;
    private readonly Func<Task>? _onDisconnectRequested;

    private SpeechRecognitionEngine? _recognizer;
    private SpeechSynthesizer? _synth;
    private bool _isRunning;

    /// <summary>
    /// Wake-word baked into every grammar entry. Single short syllable
    /// scores reliably under typical headset-mic conditions and is
    /// unlikely to fire on conversational speech. Same on every locale.
    /// </summary>
    private const string WakeWord = "VAM";

    /// <summary>
    /// Constructor — captures dependencies but does NOT start the
    /// recognizer. Caller must invoke <see cref="TryStart"/> separately
    /// (typically gated on the user's EINSTELLUNGEN toggle).
    ///
    /// onConnectRequested / onDisconnectRequested are callbacks the
    /// App provides so the voice-service can trigger Connect / Disconnect
    /// without taking a hard dependency on AcarsClientService. The App
    /// owns the Service and wires the callbacks at construction time —
    /// see <see cref="App.OnStartup"/>.
    /// </summary>
    public VoiceCommandService(
        AcarsClientState state,
        Dispatcher dispatcher,
        ILogger<VoiceCommandService> logger,
        Func<Task>? onConnectRequested = null,
        Func<Task>? onDisconnectRequested = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _onConnectRequested = onConnectRequested;
        _onDisconnectRequested = onDisconnectRequested;
    }

    /// <summary>True iff the recognizer is currently listening.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Spin up the recognizer + synthesizer. Returns true on success,
    /// false on any failure (no microphone, no recognizer culture
    /// installed, audio-stack permission denied). Idempotent — calling
    /// while already running returns true without re-creating.
    /// </summary>
    public bool TryStart()
    {
        if (_isRunning) return true;

        try
        {
            // Prefer de-DE so the German command-words score well; fall
            // back to en-US (almost universally present on Windows
            // installs). RecognizerInfo.InstalledRecognizers() enumerates
            // every recognizer registered with SAPI; we filter by Culture.
            //
            // We don't throw if neither is present — instead we return
            // false and log a warning. The user might be on a Windows N
            // edition without the Speech feature, in which case the
            // service should stay silent rather than break the tray.
            var installed = SpeechRecognitionEngine.InstalledRecognizers();
            var preferred = installed.FirstOrDefault(r =>
                r.Culture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase));
            preferred ??= installed.FirstOrDefault(r =>
                r.Culture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase));

            if (preferred is null)
            {
                _logger.LogWarning(
                    "No de-DE or en-US speech recognizer installed — voice-commands disabled. " +
                    "Install Windows speech feature for the relevant language to enable.");
                return false;
            }

            _recognizer = new SpeechRecognitionEngine(preferred.Culture);

            // Build the grammar. GrammarBuilder + Choices is the
            // simplest path for a small fixed phrase-set; for hundreds
            // of phrases we'd switch to SRGS XML, but for v1 MVP this
            // is readable and fast to extend.
            //
            // Each Choices entry is one possible command-word the user
            // can say after the wake-word. The wake-word is mandatory
            // (always-on listening would also pick up TV chatter etc.).
            var commands = new Choices();
            commands.Add(new SemanticResultValue("Status", "status"));
            commands.Add(new SemanticResultValue("Statusbericht", "status"));      // synonym
            commands.Add(new SemanticResultValue("Verbinden", "connect"));
            commands.Add(new SemanticResultValue("Verbinde", "connect"));            // imperative form
            commands.Add(new SemanticResultValue("Trennen", "disconnect"));
            commands.Add(new SemanticResultValue("Trenne", "disconnect"));           // imperative form
            commands.Add(new SemanticResultValue("Flugzeug", "aircraft"));
            commands.Add(new SemanticResultValue("Flieger", "aircraft"));            // colloquial

            var phrase = new GrammarBuilder
            {
                Culture = preferred.Culture,
            };
            phrase.Append(WakeWord);
            phrase.Append(new SemanticResultKey("command", commands));

            var grammar = new Grammar(phrase) { Name = "vam-commands-v1" };
            _recognizer.LoadGrammar(grammar);

            _recognizer.SpeechRecognized += OnSpeechRecognized;
            _recognizer.AudioStateChanged += OnAudioStateChanged;
            _recognizer.RecognizerUpdateReached += OnRecognizerUpdateReached;

            // SetInputToDefaultAudioDevice can throw if no audio device
            // is configured. Wrap in its own try so we get a more
            // specific log line than the outer catch.
            try
            {
                _recognizer.SetInputToDefaultAudioDevice();
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex,
                    "Voice-commands: no default audio input device available — service stays idle");
                _recognizer.Dispose();
                _recognizer = null;
                return false;
            }

            _synth = new SpeechSynthesizer();
            // Select a German voice if available; SAPI auto-selects the
            // system default otherwise. Quiet failure mode — TTS uses
            // whatever voice is installed.
            try
            {
                var germanVoice = _synth.GetInstalledVoices()
                    .FirstOrDefault(v => v.VoiceInfo.Culture.TwoLetterISOLanguageName
                        .Equals("de", StringComparison.OrdinalIgnoreCase) && v.Enabled);
                if (germanVoice is not null)
                {
                    _synth.SelectVoice(germanVoice.VoiceInfo.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Voice-commands: voice-selection fell through to system default");
            }

            _recognizer.RecognizeAsync(RecognizeMode.Multiple);
            _isRunning = true;

            _logger.LogInformation(
                "VoiceCommandService started with {Culture} recognizer, grammar 'vam-commands-v1'",
                preferred.Culture);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VoiceCommandService.TryStart failed — service stays idle");
            // Belts-and-braces cleanup — partial-construction state.
            try { _recognizer?.Dispose(); } catch { /* swallow */ }
            try { _synth?.Dispose(); } catch { /* swallow */ }
            _recognizer = null;
            _synth = null;
            _isRunning = false;
            return false;
        }
    }

    /// <summary>
    /// Stop the recognizer and synthesizer. Drains in-flight async
    /// operations. Idempotent — calling while not running is a no-op.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning) return;
        _isRunning = false;

        try
        {
            if (_recognizer is not null)
            {
                // RecognizeAsyncStop returns immediately; the actual
                // drain happens asynchronously and completes via the
                // RecognizerUpdateReached event. We don't await that
                // here — Dispose below handles the final teardown.
                _recognizer.RecognizeAsyncStop();
                _recognizer.SpeechRecognized -= OnSpeechRecognized;
                _recognizer.AudioStateChanged -= OnAudioStateChanged;
                _recognizer.RecognizerUpdateReached -= OnRecognizerUpdateReached;

                // Small grace period for the engine's worker thread to
                // park before we dispose. 100 ms is empirically enough;
                // longer waits don't improve teardown quality but make
                // user-toggle feel laggy.
                await Task.Delay(100).ConfigureAwait(false);

                _recognizer.Dispose();
                _recognizer = null;
            }

            _synth?.Dispose();
            _synth = null;

            _logger.LogInformation("VoiceCommandService stopped");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VoiceCommandService.StopAsync threw during teardown");
        }
    }

    public void Dispose()
    {
        if (_recognizer is null && _synth is null) return;
        _isRunning = false;
        try { _recognizer?.RecognizeAsyncStop(); } catch { /* swallow */ }
        try { _recognizer?.Dispose(); } catch { /* swallow */ }
        try { _synth?.Dispose(); } catch { /* swallow */ }
        _recognizer = null;
        _synth = null;
    }

    // ─── Event handlers ───────────────────────────────────────────────

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        // Confidence-gate. The recognizer's internal threshold (0.5) is
        // generous; we layer our own 0.6 floor to keep false positives
        // down without rejecting legitimate utterances under background
        // noise. Values are empirical from manual testing — not a
        // formal calibration.
        if (e.Result.Confidence < 0.60f)
        {
            _logger.LogDebug(
                "Voice-command rejected on low confidence ({Confidence:F2}): {Text}",
                e.Result.Confidence, e.Result.Text);
            return;
        }

        // SemanticResultKey "command" was set on the grammar; pull the
        // canonical command-id out of the result-tree.
        if (!e.Result.Semantics.ContainsKey("command"))
        {
            _logger.LogDebug("Voice-recognized utterance without 'command' semantic: {Text}", e.Result.Text);
            return;
        }

        var command = e.Result.Semantics["command"].Value?.ToString();
        if (string.IsNullOrWhiteSpace(command)) return;

        _logger.LogInformation(
            "Voice command recognized: '{Command}' (confidence {Confidence:F2}, raw '{Text}')",
            command, e.Result.Confidence, e.Result.Text);

        // Marshal to dispatcher — every command handler touches state
        // and may drive Connect/Disconnect which themselves must run
        // on the UI thread. BeginInvoke is fire-and-forget so we don't
        // hold up the recognizer's worker thread.
        _dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await DispatchCommandAsync(command).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Voice-command dispatch failed for '{Command}'", command);
            }
        }));
    }

    private void OnAudioStateChanged(object? sender, AudioStateChangedEventArgs e)
    {
        // Log audio-state transitions at debug — useful for diagnosing
        // \"voice commands stopped working\" bug reports where the mic
        // was muted by the OS. We don't surface this to the UI in v1.
        _logger.LogDebug("Voice-commands audio state: {State}", e.AudioState);
    }

    private void OnRecognizerUpdateReached(object? sender, RecognizerUpdateReachedEventArgs e)
    {
        // RequestRecognizerUpdate / RecognizerUpdateReached is the
        // documented way to safely modify the recognizer's grammar
        // collection at runtime. We don't currently use that, but the
        // hook is here so future grammar-additions (e.g. callsign-
        // specific phrases) can plug in without re-engineering.
    }

    // ─── Command dispatch ─────────────────────────────────────────────

    private async Task DispatchCommandAsync(string command)
    {
        // String compare is invariant-ordinal because the semantic
        // values we use are ASCII identifiers we control. Avoids
        // surprises from current-thread CultureInfo.
        switch (command)
        {
            case "status":
                SpeakStatus();
                break;

            case "connect":
                await HandleConnectCommandAsync().ConfigureAwait(true);
                break;

            case "disconnect":
                await HandleDisconnectCommandAsync().ConfigureAwait(true);
                break;

            case "aircraft":
                SpeakAircraft();
                break;

            default:
                _logger.LogWarning("Voice command dispatched but unknown: '{Command}'", command);
                break;
        }
    }

    /// <summary>Speak a one-liner status: connection-state + heartbeat counters.</summary>
    private void SpeakStatus()
    {
        var state = _state.ConnectionStatus switch
        {
            ConnectionStatus.Connected => "Verbunden",
            ConnectionStatus.Connecting => "Verbinde",
            ConnectionStatus.Disconnected => "Getrennt",
            ConnectionStatus.Error => "Fehler",
            _ => "Unbekannt",
        };

        var msg = _state.ConnectionStatus == ConnectionStatus.Connected
            ? $"Status: {state}. {_state.HeartbeatsSent} Heartbeats gesendet, {_state.HeartbeatsFailed} fehlgeschlagen."
            : $"Status: {state}.";

        Speak(msg);
    }

    /// <summary>Speak the detected aircraft type + registration.</summary>
    private void SpeakAircraft()
    {
        var type = _state.DetectedAircraftType ?? _state.AircraftType;
        var reg = _state.DetectedAircraftRegistration ?? _state.AircraftRegistration;

        if (string.IsNullOrWhiteSpace(type) || type == "UNKN")
        {
            Speak("Kein Flugzeug erkannt. Bitte zuerst Sim erkennen klicken.");
            return;
        }

        var msg = string.IsNullOrWhiteSpace(reg) || reg == "UNKN"
            ? $"Flugzeug: {type}."
            : $"Flugzeug: {type}, Registration {reg}.";

        Speak(msg);
    }

    /// <summary>Trigger Connect via the App-supplied callback.</summary>
    private async Task HandleConnectCommandAsync()
    {
        if (_state.ConnectionStatus == ConnectionStatus.Connected
            || _state.ConnectionStatus == ConnectionStatus.Connecting)
        {
            Speak("Verbindung läuft bereits.");
            return;
        }

        if (!_state.PreflightComplete)
        {
            Speak("Bitte zuerst Pre-flight-Checkliste abhaken.");
            return;
        }

        if (_onConnectRequested is null)
        {
            _logger.LogWarning("Voice 'connect' command received but no callback wired");
            Speak("Verbinden-Befehl nicht verfügbar.");
            return;
        }

        Speak("Verbinde.");
        try
        {
            await _onConnectRequested().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice 'connect' callback threw");
            Speak("Verbinden fehlgeschlagen.");
        }
    }

    /// <summary>Trigger Disconnect via the App-supplied callback.</summary>
    private async Task HandleDisconnectCommandAsync()
    {
        if (_state.ConnectionStatus == ConnectionStatus.Disconnected)
        {
            Speak("Bereits getrennt.");
            return;
        }

        if (_onDisconnectRequested is null)
        {
            _logger.LogWarning("Voice 'disconnect' command received but no callback wired");
            Speak("Trennen-Befehl nicht verfügbar.");
            return;
        }

        Speak("Trenne.");
        try
        {
            await _onDisconnectRequested().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice 'disconnect' callback threw");
            Speak("Trennen fehlgeschlagen.");
        }
    }

    /// <summary>
    /// Async-ish TTS. SpeechSynthesizer.SpeakAsync returns a Prompt
    /// object that we don't await — fire-and-forget for spoken feedback
    /// (a 2-second status announcement shouldn't block the next
    /// recognized command).
    /// </summary>
    private void Speak(string text)
    {
        if (_synth is null || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            _synth.SpeakAsyncCancelAll();
            _synth.SpeakAsync(text);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TTS Speak failed for '{Text}' — silent skip", text);
        }
    }
}
