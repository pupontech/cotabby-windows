using System.Collections.Concurrent;
using System.Diagnostics;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Cotabby.Core.Overlay;
using Microsoft.Extensions.Logging;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// Top-level orchestrator that ties the keyboard hook, focus tracker, suggestion
/// engine, overlay, and inserter together. Mirrors the macOS
/// <c>SuggestionCoordinator</c>; this single class corresponds to the
/// <c>+Lifecycle</c>, <c>+Input</c>, <c>+Prediction</c>, and <c>+Acceptance</c>
/// extension files in the macOS port.
/// </summary>
/// <remarks>
/// Threading: keyboard events arrive on the hook thread, UIA events on a UIA
/// pool thread. The coordinator marshals all session mutations through a single
/// SynchronizationContext (the UI thread) so the state machine never sees racy
/// reads of <see cref="_session"/>. Pure decision logic stays in
/// <see cref="SuggestionSessionReconciler"/> / <see cref="SuggestionAvailability"/>
/// so this class only owns wiring.
/// </remarks>
public sealed class SuggestionCoordinator : IAsyncDisposable
{
    private readonly IKeyboardHook _hook;
    private readonly IFocusTracker _focus;
    private readonly ISuggestionEngine _engine;
    private readonly IOverlayPresenter _overlay;
    private readonly ITextInserter _inserter;
    private readonly SuggestionWorkController _work;
    private readonly ILogger<SuggestionCoordinator> _logger;
    private readonly SynchronizationContext _ui;

    private SuggestionSession? _session;
    private int _sessionPid;
    private bool _enabled = true;
    private volatile bool _disposed;
    /// <summary>True while a generation is past its debounce and the engine is producing tokens.</summary>
    private bool _generationInFlight;
    /// <summary>Process id of the host that owned focus when the in-flight request fired.</summary>
    private int _inFlightPid;
    private string? _inFlightRequestId;
    private sealed record PresentationSnapshot(
        string RequestId,
        string VisibleText,
        int ProcessId,
        string? StableFieldIdentity,
        bool IsCredentialField,
        long Version);

    private sealed class AcceptIntent(
        long sequence,
        PresentationSnapshot presentation,
        double eventTimeSeconds,
        bool isTabAccept,
        bool isAutorepeat,
        bool whole,
        KeyboardEvent capturedEvent)
    {
        private int _completed;

        public long Sequence { get; } = sequence;
        public PresentationSnapshot Presentation { get; } = presentation;
        public double EventTimeSeconds { get; } = eventTimeSeconds;
        public bool IsTabAccept { get; } = isTabAccept;
        public bool IsAutorepeat { get; } = isAutorepeat;
        public bool Whole { get; } = whole;
        // Preserve all normalized metadata observed with this suppressed key. It
        // intentionally is not treated as sufficient to synthesize a replay.
        public KeyboardEvent CapturedEvent { get; } = capturedEvent;
        public bool TryComplete() => Interlocked.Exchange(ref _completed, 1) == 0;
    }

    private PresentationSnapshot? _presentation;
    private long _presentationVersion;
    private readonly Queue<AcceptIntent> _acceptanceQueue = new();
    private readonly object _acceptanceGate = new();
    private readonly ConcurrentDictionary<long, AcceptIntent> _outstandingAcceptanceIntents = new();
    private readonly CancellationTokenSource _acceptanceLifetime = new();
    private bool _acceptanceQueueRunning;
    private long _acceptanceProcessorGeneration;
    private long _nextAcceptIntentSequence;
    private readonly DoubleTapAcceptanceState _doubleTapAcceptanceState = new();
    private readonly Func<double> _doubleTapUptimeProvider;
    private bool _doubleTapFullAcceptanceEnabled;

    public SuggestionCoordinator(
        IKeyboardHook hook,
        IFocusTracker focus,
        ISuggestionEngine engine,
        IOverlayPresenter overlay,
        ITextInserter inserter,
        SuggestionWorkController work,
        ILogger<SuggestionCoordinator> logger,
        SynchronizationContext ui,
        Func<double>? doubleTapUptimeProvider = null)
    {
        _hook = hook;
        _focus = focus;
        _engine = engine;
        _overlay = overlay;
        _inserter = inserter;
        _work = work;
        _logger = logger;
        _ui = ui;
        _doubleTapUptimeProvider = doubleTapUptimeProvider
            ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
    }

    /// <summary>Opt-in latency log behind the Performance pane, or null when the
    /// host does not wire one. Recording is a no-op while its tracking toggle is off.</summary>
    public PerformanceMetricsStore? PerformanceMetrics { get; set; }

    /// <summary>
    /// Raised exactly once for each key event captured by the acceptance path.
    /// The payload contains no field text, request prefix, or field identity.
    /// </summary>
    public event Action<AcceptanceIntentOutcome>? AcceptanceIntentCompleted;

    /// <summary>Display name of the model currently answering, stamped onto each
    /// recorded entry. Set by the host when a model finishes loading.</summary>
    public string? RuntimeModelId
    {
        get => _runtimeModelId;
        set => _runtimeModelId = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }

    private string _runtimeModelId = "unknown";

    /// <summary>Whether suggestions are globally enabled. Tray toggle binds here.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (!value) CancelSession("disabled");
        }
    }

    /// <summary>
    /// Case-insensitive process-name blocklist (no .exe). When a focused field's
    /// host is in this set we suppress suggestions for it. Settable from the
    /// settings UI; null/empty disables the check.
    /// </summary>
    public ISet<string>? BlockedApps { get; set; }

    /// <summary>
    /// When set (App wires the Windows energy-saver reader behind the
    /// "Pause in Low Power Mode" setting), an active energy-saver state pauses
    /// new requests. Null disables the check entirely. Mirrors upstream #812.
    /// </summary>
    public ILowPowerModeProvider? LowPowerModePause { get; set; }

    /// <summary>
    /// Per-request max-token overrides driven by the completion length preset.
    /// Null falls back to the factory defaults.
    /// </summary>
    public int? MaxTokensSingleLine { get; set; }

    /// <summary>See <see cref="MaxTokensSingleLine"/>.</summary>
    public int? MaxTokensMultiLine { get; set; }

    /// <summary>
    /// When false, even multi-line fields receive a single-line request
    /// (the engine stops on the first newline). Default true mirrors the
    /// macOS port's "Allow multi-line suggestions" toggle.
    /// </summary>
    public bool AllowMultiLineSuggestions { get; set; } = true;

    /// <summary>
    /// Optional accept-whole-suggestion key character (case-insensitive).
    /// Mirrors the macOS default of Backtick: pressing it with a visible
    /// suggestion inserts the entire remaining text, while Tab honors
    /// <see cref="AcceptanceGranularity"/>.
    /// </summary>
    public char? AlternateAcceptKey { get; set; }

    /// <summary>
    /// "word" (default) or "phrase". Governs how much of the suggestion a
    /// Tab press takes; the alternate key always takes everything. Mirrors
    /// the macOS AcceptanceGranularity setting.
    /// </summary>
    public string AcceptanceGranularity { get; set; } = "word";

    /// <summary>
    /// Opt-in: a second distinct Tab within the upstream window accepts the
    /// remaining text from the same suggestion. Setting this (including while
    /// already enabled) retires any pending first press.
    /// </summary>
    public bool DoubleTapFullAcceptanceEnabled
    {
        get => _doubleTapFullAcceptanceEnabled;
        set
        {
            _doubleTapFullAcceptanceEnabled = value;
            _doubleTapAcceptanceState.Reset();
        }
    }

    /// <summary>When false, trailing punctuation is left as its own chunk on
    /// word accepts. Mirrors macOS "Accept Punctuation With Word".</summary>
    public bool AcceptPunctuationWithWord { get; set; } = true;

    /// <summary>The user's name for persona conditioning. Flows into requests.</summary>
    public string UserName { get; set; } = "";

    /// <summary>Language conditioning sentence for the prompt preface.</summary>
    public string LanguageInstruction { get; set; } = "";

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _hook.KeyEvent += OnKeyEvent;
        _focus.FocusChanged += OnFocusChanged;
        _hook.Start();
        _focus.Start();
        _logger.LogInformation("SuggestionCoordinator started.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_acceptanceGate)
        {
            // Complete every drained intent here. This is the single completion
            // point for queued keys at disposal; the outstanding sweep below only
            // backstops an intent already dequeued and in flight.
            foreach (var intent in DrainAcceptanceQueueLocked())
            {
                CompleteAcceptanceIntent(intent, AcceptanceIntentStatus.CoordinatorDisposed);
            }
        }
        _acceptanceLifetime.Cancel();
        foreach (var intent in _outstandingAcceptanceIntents.Values)
        {
            CompleteAcceptanceIntent(intent, AcceptanceIntentStatus.CoordinatorDisposed);
        }
        _inFlightRequestId = null;
        _generationInFlight = false;
        _inFlightPid = 0;
        _doubleTapAcceptanceState.Reset();
        PublishSessionSnapshot(null);
        void ClearDisposedPresentation()
        {
            _session = null;
            _sessionPid = 0;
            _overlay.Hide();
        }
        if (SynchronizationContext.Current == _ui) ClearDisposedPresentation();
        else _ui.Post(_ => ClearDisposedPresentation(), null);
        _hook.KeyEvent -= OnKeyEvent;
        _focus.FocusChanged -= OnFocusChanged;
        _focus.Stop();
        _hook.Stop();
        await _work.DisposeAsync().ConfigureAwait(false);
    }

    private void OnFocusChanged(object? sender, FocusedField? field)
    {
        Post(() =>
        {
            // Even a focus transition within one process ends the user's
            // physical double-tap sequence.
            _doubleTapAcceptanceState.Reset();

            int newPid = field?.ProcessId ?? 0;

            if (_session is not null
                && (field is null
                    || newPid != _sessionPid
                    || !string.Equals(field.StableFieldIdentity, _session.StableFieldIdentity, StringComparison.Ordinal)
                    || field.IsCredentialField))
            {
                CancelSession("focus changed to different field");
            }

            // Also abort any in-flight work whose request was for a different
            // process — otherwise the in-flight generation keeps streaming for
            // the OLD window's prefix and eventually pops a stale overlay over
            // the new window. The work's Post handler also re-checks before
            // touching state, but cancelling immediately frees CPU sooner.
            if (_generationInFlight && _inFlightPid != 0 && newPid != _inFlightPid)
            {
                _logger.LogInformation("Focus moved (oldPid={Old}, newPid={New}); cancelling in-flight work.",
                    _inFlightPid, newPid);
                _work.Cancel();
                _generationInFlight = false;
                _inFlightPid = 0;
                _inFlightRequestId = null;
            }
        });
    }

    private void OnKeyEvent(object? sender, KeyEventArgs args)
    {
        if (_disposed || !_enabled) return;

        // One acquire read gets one immutable set of presentation and ownership metadata.
        var presentation = Volatile.Read(ref _presentation);
        var cachedField = _focus.Current;
        bool hasSession = presentation is { VisibleText.Length: > 0 };

        // This executes for credential fields too: never record the character
        // or its numeric code, which would reconstruct passwords in debug logs.
        _logger.LogDebug("Key kind={Kind} down={Down} mod={Mod} session={HasSession}",
            args.Event.Kind, args.Event.IsKeyDown,
            args.Event.HasNonShiftModifier, hasSession);

        // Shift is deliberately not part of HasNonShiftModifier: upstream matches
        // the accept key by keyCode plus the whole normalized modifier set, so Tab
        // and Shift+Tab stay distinct bindings. A shifted Tab is the host's
        // reverse-navigation gesture and must reach the app untouched.
        bool isTabAccept = args.Event.Kind == KeyKind.Tab
            && args.Event.IsKeyDown
            && !args.Event.HasNonShiftModifier
            && !args.Event.HasShiftModifier;
        bool isAltAccept = AlternateAcceptKey is { } alt
            && args.Event.Kind == KeyKind.Character
            && args.Event.IsKeyDown
            && char.ToLowerInvariant(args.Event.Character) == char.ToLowerInvariant(alt);
        bool ownedPresentation = hasSession
            && presentation is { IsCredentialField: false }
            && cachedField is { IsCredentialField: false }
            && cachedField.ProcessId == presentation.ProcessId
            && FieldIdentityProvesOwnership(cachedField.StableFieldIdentity, presentation.StableFieldIdentity)
            && (args.Event.ForegroundProcessId == 0 || args.Event.ForegroundProcessId == presentation.ProcessId);

        if (ownedPresentation && (isTabAccept || isAltAccept))
        {
            double eventTimeSeconds = _doubleTapUptimeProvider();
            var intent = new AcceptIntent(
                Interlocked.Increment(ref _nextAcceptIntentSequence),
                presentation!, eventTimeSeconds, isTabAccept, args.Event.IsAutorepeat,
                isAltAccept, args.Event);
            _outstandingAcceptanceIntents.TryAdd(intent.Sequence, intent);
            args.Suppress = true;
            try
            {
                _work.Cancel();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not cancel prior suggestion work (type={ExceptionType}).", ex.GetType().Name);
                CompleteAcceptanceIntent(intent, AcceptanceIntentStatus.DispatchFailed);
                return;
            }
            PostAcceptIntent(intent);
            return;
        }

        Post(() =>
        {
            if (args.Event.IsKeyDown && (args.Event.Kind != KeyKind.Tab || !ownedPresentation))
            {
                _doubleTapAcceptanceState.Reset();
            }
            if (isTabAccept)
            {
                // The host already receives this physical Tab. Never insert it
                // later on the dispatcher after losing the hook fast-path race.
                if (_session is not null) CancelSession("Tab passed through without verified ownership");
                return;
            }
            HandleEvent(args.Event);
        });
    }

    /// <summary>
    /// Field-identity half of the ownership gate on the hook thread. Only a
    /// PROVEN mismatch refuses: an identity that exists on one side and is absent
    /// on the other is not proof of a different field. A host whose UIA provider
    /// yields no runtime id publishes no identity at all, and treating that
    /// absence as "unowned" silently switched the whole accept fast path off and
    /// delivered a literal Tab into the app (tab character in an editor, focus
    /// move in a browser) instead of accepting the suggestion. An identity the
    /// presentation proved is still required to match the live field exactly.
    /// </summary>
    private static bool FieldIdentityProvesOwnership(string? liveIdentity, string? presentedIdentity)
    {
        if (string.IsNullOrEmpty(presentedIdentity)) return true;
        return !string.IsNullOrEmpty(liveIdentity)
            && string.Equals(liveIdentity, presentedIdentity, StringComparison.Ordinal);
    }

    private void HandleEvent(KeyboardEvent ev)
    {
        if (ev.IsKeyDown && (ev.Kind != KeyKind.Tab || _session is null))
        {
            _doubleTapAcceptanceState.Reset();
        }

        if (_session is not null)
        {
            var result = SuggestionSessionReconciler.Apply(_session, ev);
            switch (result.Outcome)
            {
                case SuggestionSessionReconciler.Outcome.Accept:
                    // This key already reached the host. Do not insert on the
                    // reconciler path; that would duplicate host navigation.
                    CancelSession("Tab already passed through to host");
                    return;
                case SuggestionSessionReconciler.Outcome.AdvanceVisible when result.Next is { } next:
                    _session = next with
                    {
                        KnownPrecedingText = _session.KnownPrecedingText + ev.Character,
                    };
                    PublishSessionSnapshot(_session);
                    if (next.VisibleText.Length == 0) _doubleTapAcceptanceState.Reset();
                    _overlay.Update(next.VisibleText);
                    if (next.VisibleText.Length == 0 && next.IsComplete)
                    {
                        CancelSession("consumed");
                    }
                    return;
                case SuggestionSessionReconciler.Outcome.Cancel:
                    CancelSession("reconciler");
                    // fall through to potentially trigger a new request
                    break;
                case SuggestionSessionReconciler.Outcome.Ignore:
                    return;
            }
        }

        if (SuggestionAvailability.ShouldCancel(ev))
        {
            CancelSession("availability cancel");
            return;
        }

        var field = _focus.Refresh();
        if (field is null)
        {
            _logger.LogDebug("No focused field at trigger-decision time.");
            return;
        }
        if (LowPowerModePause is { } pause && pause.IsLowPowerModeEnabled)
        {
            _logger.LogDebug("Suggestions paused while Windows energy saver is on.");
            return;
        }
        if (!SuggestionAvailability.ShouldRequest(field, ev, BlockedApps))
        {
            _logger.LogDebug("ShouldRequest false: secure={Sec} textLen={Len} host={Host} blocked={Blocked}",
                field.IsSecure, field.Text.Length, field.ProcessName,
                BlockedApps?.Contains(field.ProcessName) ?? false);
            return;
        }

        // Match upstream Cotabby behavior: every new keystroke cancels the
        // prior debounce/work and reschedules a fresh one. Without this, the
        // FIRST keystroke captures the prefix for the entire typing burst and
        // ~5s later we surface a completion for context that no longer exists
        // ("ork" appearing after the user already typed "ork" themselves and
        // moved on). The cancel-prior path lets Submit replace pending work,
        // and the work function snapshots the *live* focus at debounce expiry.
        _logger.LogInformation("Triggering suggestion: host={Host} textLen={Len} caret={Caret}",
            field.ProcessName, field.Text.Length, field.CaretOffset);
        TriggerGeneration(field);
    }

    private void TriggerGeneration(FocusedField fieldAtTrigger)
    {
        // Single admission point for every caller, including the post-exhaustion
        // continuation in AcceptAsync which never goes through ShouldRequest. A
        // sign-in/verification field must never have its text built into a request.
        if (fieldAtTrigger.IsCredentialField)
        {
            _logger.LogDebug("TriggerGeneration skipped: sign-in/verification field ({Host}).",
                fieldAtTrigger.ProcessName);
            return;
        }

        var reqId = "req_" + Guid.NewGuid().ToString("N")[..8];
        var pid = fieldAtTrigger.ProcessId;
        var metrics = PerformanceMetrics;
        var modelId = _runtimeModelId;

        // Capture the prefix synchronously at trigger time. The PostAsync +
        // _focus.Refresh() round-trip we used to do inside the work function
        // adds 100-300ms of UIA overhead per request, which combined with
        // 250ms of debounce means the next keystroke almost always cancels
        // the engine BEFORE it can produce a chunk. Now we lock the snapshot
        // here; if the user keeps typing during the debounce window, Submit's
        // cancel-prior takes care of dropping the stale request.
        var request = SuggestionRequestFactory.Build(
            fieldAtTrigger, reqId, MaxTokensSingleLine, MaxTokensMultiLine,
            UserName, LanguageInstruction);

        // Honor the "Allow multi-line suggestions" toggle by forcing the
        // single-line stop on multi-line fields when the user has opted out.
        // Done here, not in the factory, because the override is a settings
        // concern that the factory shouldn't know about.
        if (!AllowMultiLineSuggestions && !request.SingleLine)
        {
            request = request with { SingleLine = true };
        }
        var anchor = fieldAtTrigger.CaretRect;
        var element = fieldAtTrigger.ElementHandle;
        var field = fieldAtTrigger;

        _logger.LogInformation(
            "Scheduled request {ReqId} pid={Pid} prefix={Len} host={Host} anchor={X},{Y}",
            reqId, pid, request.Prefix.Length, request.HostApp,
            (int)anchor.X, (int)anchor.Y);

        _generationInFlight = true;
        _inFlightPid = pid;
        _inFlightRequestId = reqId;
        _ = _work.Submit(async ct =>
        {
            _logger.LogInformation("Firing request {ReqId} (post-debounce).", reqId);

            try
            {
                if (!_engine.IsReady)
                {
                    _logger.LogWarning("Engine not ready when work fired for {ReqId}.", reqId);
                    return;
                }

                var sw = Stopwatch.StartNew();
                string accumulated = string.Empty;
                bool overlayShown = false;
                bool requestPresented = false;
                bool generationCompleted = false;
                bool metricsRecorded = false;
                long elapsedMilliseconds = 0;
                var lastPosted = DateTime.MinValue;
                var minInterval = TimeSpan.FromMilliseconds(50);

                // Watchdog: cap any single generation at 10 seconds. Without
                // this a hung engine call leaves _generationInFlight=true
                // forever and the next keystroke is silently dropped — that's
                // the "Notepad randomly stops showing suggestions" symptom.
                // The engine surfaces its result as one final chunk, so the
                // watchdog must cover a full 50-token decode on the slowest
                // supported hardware (CPU-only laptop); anything past 10s is
                // a wedge to be killed, not a slow-but-recoverable generation.
                using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, watchdog.Token);
                var combinedToken = linked.Token;

                void RecordLatencyIfReady()
                {
                    if (metricsRecorded || !generationCompleted || !requestPresented
                        || combinedToken.IsCancellationRequested
                        || _inFlightRequestId != reqId
                        || _session?.RequestId != reqId)
                    {
                        return;
                    }

                    metricsRecorded = true;
                    metrics?.Record(modelId, elapsedMilliseconds);
                }

            int chunkSeq = 0;
            await foreach (var chunk in _engine.GenerateAsync(request, combinedToken).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested)
                {
                    _logger.LogInformation(
                        "Req {ReqId} aborted by cancel after {Chunks} chunks ({Chars} chars).",
                        reqId, chunkSeq, accumulated.Length);
                    return;
                }
                chunkSeq++;
                accumulated += chunk.Text;

                // Quality gating happens in the engine (constrained decode +
                // one-shot normalizer); the coordinator only keeps the
                // insert-time StripDegenerateTail firewall. The old streaming
                // hard-cap/char-run truncation belonged to the instruct-model
                // era and silently dropped legitimate completions.
                if (!chunk.IsFinal && string.IsNullOrEmpty(chunk.Text)) continue;

                // Always Post the first chunk (so overlay appears ASAP) and the
                // final chunk (so trailing text isn't dropped). Throttle the
                // middle.
                bool isFirstShow = !overlayShown;
                if (!isFirstShow && !chunk.IsFinal)
                {
                    var now = DateTime.UtcNow;
                    if (now - lastPosted < minInterval) continue;
                    lastPosted = now;
                }
                else
                {
                    lastPosted = DateTime.UtcNow;
                }

                var ctSnapshot = combinedToken;
                Post(() =>
                {
                    if (ctSnapshot.IsCancellationRequested) return;
                    if (_inFlightRequestId != reqId) return;

                    if (_session is not null && _session.RequestId != reqId)
                    {
                        _logger.LogDebug("Chunk for stale req {ReqId} (current session is {Cur}); dropping.",
                            reqId, _session.RequestId);
                        return;
                    }

                    if (_session is null)
                    {
                        if (string.IsNullOrEmpty(accumulated))
                        {
                            _logger.LogDebug("First chunk empty for {ReqId}; skipping show.", reqId);
                            return;
                        }

                        // Focus notifications can lag or be missing entirely.
                        // Revalidate the target before presenting the old field's
                        // completion, not only later when the user accepts it.
                        var live = _focus.Refresh();
                        if (live is { } focused
                            && (focused.IsCredentialField || focused.ProcessId != field.ProcessId
                                || !FieldIdentityProvesOwnership(focused.StableFieldIdentity, field.StableFieldIdentity)))
                        {
                            CancelSession("focus changed before presentation");
                            return;
                        }
                        var liveAnchor = (live is not null && !live.CaretRect.IsEmpty)
                            ? live.CaretRect : anchor;
                        int livePid = live?.ProcessId ?? field.ProcessId;

                        _session = new SuggestionSession
                        {
                            RequestId = reqId,
                            ElementHandle = element,
                            StableFieldIdentity = field.StableFieldIdentity,
                            IsCredentialField = field.IsCredentialField,
                            OriginalPrefix = request.Prefix,
                            ConsumedChars = 0,
                            VisibleText = accumulated,
                            KnownPrecedingText = request.Prefix,
                            IsComplete = chunk.IsFinal,
                            AnchorRect = liveAnchor,
                        };
                        _sessionPid = livePid;
                        _doubleTapAcceptanceState.Reset();
                        PublishSessionSnapshot(_session);
                        _overlay.Show(accumulated, liveAnchor);
                        overlayShown = true;
                        requestPresented = true;
                        _logger.LogInformation(
                            "Overlay shown req={ReqId} text=\"{Text}\" hex={Hex} anchor={X},{Y}",
                            reqId, EscapeFull(accumulated), HexDump(accumulated),
                            (int)liveAnchor.X, (int)liveAnchor.Y);
                    }
                    else
                    {
                        _session = _session with
                        {
                            VisibleText = accumulated,
                            IsComplete = chunk.IsFinal,
                        };
                        PublishSessionSnapshot(_session);
                        if (overlayShown) _overlay.Update(accumulated);
                        else { _overlay.Show(accumulated, _session.AnchorRect); overlayShown = true; }
                        requestPresented = true;
                        _logger.LogInformation(
                            "Overlay updated req={ReqId} text=\"{Text}\" hex={Hex}",
                            reqId, EscapeFull(accumulated), HexDump(accumulated));
                    }

                    RecordLatencyIfReady();
                });
            }

                sw.Stop();
                elapsedMilliseconds = sw.ElapsedMilliseconds;
                _logger.LogInformation("Request {ReqId} finished in {Ms} ms.", reqId, elapsedMilliseconds);
                if (accumulated.Length > 0 && !ct.IsCancellationRequested
                    && !combinedToken.IsCancellationRequested)
                {
                    Post(() =>
                    {
                        if (ct.IsCancellationRequested || combinedToken.IsCancellationRequested
                            || _inFlightRequestId != reqId)
                        {
                            return;
                        }

                        generationCompleted = true;
                        RecordLatencyIfReady();
                    });
                }
            }
            finally
            {
                Post(() =>
                {
                    if (_inFlightRequestId != reqId) return;
                    _generationInFlight = false;
                    _inFlightPid = 0;
                    _inFlightRequestId = null;
                });
            }
        });
    }

    private void PostAcceptIntent(AcceptIntent intent)
    {
        void EnqueueOrRefuse()
        {
            if (_disposed)
            {
                CompleteAcceptanceIntent(intent, AcceptanceIntentStatus.CoordinatorDisposed);
                return;
            }
            EnqueueAcceptIntent(intent);
        }

        if (_disposed)
        {
            CompleteAcceptanceIntent(intent, AcceptanceIntentStatus.CoordinatorDisposed);
            return;
        }
        if (SynchronizationContext.Current == _ui)
        {
            EnqueueOrRefuse();
            return;
        }

        try
        {
            _ui.Post(_ => EnqueueOrRefuse(), null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not dispatch a captured accept intent (type={ExceptionType}).", ex.GetType().Name);
            CompleteAcceptanceIntent(intent, AcceptanceIntentStatus.DispatchFailed);
        }
    }

    private void EnqueueAcceptIntent(AcceptIntent intent)
    {
        bool startProcessor = false;
        long processorGeneration = 0;
        bool disposed;
        lock (_acceptanceGate)
        {
            disposed = _disposed;
            if (!disposed)
            {
                _acceptanceQueue.Enqueue(intent);
                if (!_acceptanceQueueRunning)
                {
                    _acceptanceQueueRunning = true;
                    processorGeneration = ++_acceptanceProcessorGeneration;
                    startProcessor = true;
                }
            }
        }

        if (disposed)
        {
            CompleteAcceptanceIntent(intent, AcceptanceIntentStatus.CoordinatorDisposed);
        }
        else if (startProcessor)
        {
            _ = ProcessAcceptanceQueueAsync(processorGeneration).ContinueWith(LogFault, TaskScheduler.Default);
        }
    }

    private async Task ProcessAcceptanceQueueAsync(long processorGeneration)
    {
        AcceptIntent? inFlight = null;
        try
        {
            while (true)
            {
                AcceptIntent? intent;
                lock (_acceptanceGate)
                {
                    if (_disposed || _acceptanceQueue.Count == 0)
                    {
                        _acceptanceQueueRunning = false;
                        return;
                    }
                    intent = _acceptanceQueue.Dequeue();
                }

                inFlight = intent;
                AcceptanceIntentStatus status;
                try
                {
                    status = await AcceptIntentOnUiAsync(intent, _acceptanceLifetime.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Accept intent processing failed (type={ExceptionType}).", ex.GetType().Name);
                    status = AcceptanceIntentStatus.InsertionFaulted;
                }

                CompleteAcceptanceIntent(intent, status);
                // A landed insertion that could not be committed in the ordinary
                // way (InsertedNotCommitted) is still a consumed key, so the
                // queue keeps draining behind it instead of stopping.
                if (status is AcceptanceIntentStatus.StateCommitted or AcceptanceIntentStatus.InsertedNotCommitted) continue;

                AcceptanceIntentStatus queuedStatus = _disposed
                    ? AcceptanceIntentStatus.CoordinatorDisposed
                    : AcceptanceIntentStatus.BlockedByEarlierFailure;
                StopQueueAndCompletePending(queuedStatus);
                return;
            }
        }
        finally
        {
            // Fault path: anything that escapes the loop body above — including a
            // throw from the error logger or a disposition observer — must not
            // leave _acceptanceQueueRunning set with no processor, or every later
            // Tab is suppressed, queued and never dequeued. Drain and complete the
            // in-flight intent and whatever is still queued behind it.
            List<AcceptIntent> leftover;
            lock (_acceptanceGate)
            {
                // A completion observer or another thread can start a new
                // processor after this one released the running flag. Its queue
                // is not ours to drain or mark idle during retirement.
                if (_acceptanceProcessorGeneration == processorGeneration)
                {
                    _acceptanceQueueRunning = false;
                    leftover = DrainAcceptanceQueueLocked();
                }
                else leftover = new List<AcceptIntent>();
            }
            if (inFlight is not null) CompleteAcceptanceIntent(inFlight, AcceptanceIntentStatus.QueueFaulted);
            foreach (var intent in leftover) CompleteAcceptanceIntent(intent, AcceptanceIntentStatus.QueueFaulted);
        }
    }

    private async Task<AcceptanceIntentStatus> AcceptIntentOnUiAsync(
        AcceptIntent intent,
        CancellationToken cancellationToken)
    {
        if (_disposed) return AcceptanceIntentStatus.CoordinatorDisposed;
        if (SynchronizationContext.Current == _ui)
        {
            return await AcceptIntentAsync(intent, cancellationToken).ConfigureAwait(false);
        }

        var completion = new TaskCompletionSource<AcceptanceIntentStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _ui.Post(async _ =>
            {
                if (_disposed)
                {
                    completion.TrySetResult(AcceptanceIntentStatus.CoordinatorDisposed);
                    return;
                }
                try
                {
                    completion.TrySetResult(await AcceptIntentAsync(intent, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    _logger.LogError("Accept intent dispatch failed (type={ExceptionType}).", ex.GetType().Name);
                    completion.TrySetResult(AcceptanceIntentStatus.InsertionFaulted);
                }
            }, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not dispatch a captured accept intent (type={ExceptionType}).", ex.GetType().Name);
            return AcceptanceIntentStatus.DispatchFailed;
        }
        return await completion.Task.ConfigureAwait(false);
    }

    private async Task<AcceptanceIntentStatus> AcceptIntentAsync(
        AcceptIntent intent,
        CancellationToken cancellationToken)
    {
        if (_disposed) return AcceptanceIntentStatus.CoordinatorDisposed;
        var session = _session;
        var presentation = intent.Presentation;
        if (session is null
            || session.RequestId != presentation.RequestId
            || !string.Equals(session.StableFieldIdentity, presentation.StableFieldIdentity, StringComparison.Ordinal)
            || presentation.IsCredentialField)
        {
            _doubleTapAcceptanceState.Reset();
            _logger.LogInformation("Accept intent no longer belongs to the presented request/field; not accepting replacement text.");
            return AcceptanceIntentStatus.IntentInvalidated;
        }

        var field = _focus.Refresh();
        if (field is null
            || field.IsCredentialField
            || field.ProcessId != presentation.ProcessId
            || field.ProcessId != _sessionPid
            || !string.Equals(field.StableFieldIdentity, presentation.StableFieldIdentity, StringComparison.Ordinal)
            || (intent.Presentation.ProcessId != 0 && field.ProcessId != intent.Presentation.ProcessId))
        {
            _doubleTapAcceptanceState.Reset();
            if (field is null || field.IsCredentialField || field.ProcessId != _sessionPid
                || !string.Equals(field.StableFieldIdentity, session.StableFieldIdentity, StringComparison.Ordinal))
            {
                CancelSession("field ownership changed before insertion");
            }
            _logger.LogInformation("Accept intent rejected because live focus does not match its field identity.");
            return AcceptanceIntentStatus.IntentInvalidated;
        }

        var sessionToken = new DoubleTapAcceptanceState.SessionToken(session.RequestId);
        bool promotedByDoubleTap = false;
        if (intent.IsTabAccept && DoubleTapFullAcceptanceEnabled && !intent.IsAutorepeat)
        {
            promotedByDoubleTap = _doubleTapAcceptanceState.ConsumeDoubleTap(sessionToken, intent.EventTimeSeconds);
        }
        else
        {
            // Preserve upstream semantics: an autorepeat clears the pending pair,
            // then follows ordinary word acceptance without arming another pair.
            _doubleTapAcceptanceState.Reset();
        }

        bool whole = intent.Whole || promotedByDoubleTap;
        bool armDoubleTap = intent.IsTabAccept && DoubleTapFullAcceptanceEnabled
            && !intent.IsAutorepeat && !whole;
        string visible = StripDegenerateTail(session.VisibleText);
        if (string.IsNullOrEmpty(visible))
        {
            _doubleTapAcceptanceState.Reset();
            CancelSession("accepted text empty after trim");
            return AcceptanceIntentStatus.IntentInvalidated;
        }

        string chunk = whole
            ? visible
            : AcceptanceGranularity == "phrase"
                ? SuggestionSessionReconciler.NextAcceptancePhrase(visible, AcceptPunctuationWithWord)
                : SuggestionSessionReconciler.NextAcceptanceChunk(visible, AcceptPunctuationWithWord);
        if (chunk.Length == 0) chunk = visible;
        string remaining = visible[chunk.Length..];
        string insertion = SuggestionSessionReconciler.InsertionChunk(chunk, session.KnownPrecedingText);
        bool exhausted = remaining.Trim().Length == 0;
        // True once the inserter reported the text reached the host, so a later
        // bail-out can be reported honestly instead of as a replay refusal.
        bool insertionLanded = false;

        try
        {
            if (insertion.Length > 0)
            {
                bool inserted = await _inserter.InsertAsync(field, insertion, cancellationToken)
                    .ConfigureAwait(false);
                if (!inserted)
                {
                    await PostAsync(_doubleTapAcceptanceState.Reset).ConfigureAwait(false);
                    _logger.LogWarning("Accept insertion returned false; session was not advanced or armed.");
                    return AcceptanceIntentStatus.InsertionRejected;
                }
                insertionLanded = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await PostAsync(_doubleTapAcceptanceState.Reset).ConfigureAwait(false);
            // The text already reached the host, so this is not a refusal.
            return insertionLanded
                ? AcceptanceIntentStatus.InsertedNotCommitted
                : _disposed
                    ? AcceptanceIntentStatus.CoordinatorDisposed
                    : AcceptanceIntentStatus.InsertionCancelled;
        }
        catch (Exception ex)
        {
            await PostAsync(_doubleTapAcceptanceState.Reset).ConfigureAwait(false);
            _logger.LogWarning("Accept insertion threw (type={ExceptionType}); session was not advanced or armed.", ex.GetType().Name);
            return AcceptanceIntentStatus.InsertionFaulted;
        }

        if (cancellationToken.IsCancellationRequested || _disposed)
        {
            await PostAsync(_doubleTapAcceptanceState.Reset).ConfigureAwait(false);
            return insertionLanded
                ? AcceptanceIntentStatus.InsertedNotCommitted
                : _disposed
                    ? AcceptanceIntentStatus.CoordinatorDisposed
                    : AcceptanceIntentStatus.InsertionCancelled;
        }

        bool committed = false;
        bool reAnchored = false;
        bool anchorExhausted = exhausted;
        await PostAsync(() =>
        {
            var live = _session;
            if (live is null
                || live.RequestId != presentation.RequestId
                || !string.Equals(live.StableFieldIdentity, presentation.StableFieldIdentity, StringComparison.Ordinal))
            {
                // The request or field no longer owns the presentation: fail
                // closed. The insertion already landed, so the caller reports
                // InsertedNotCommitted rather than a refusal.
                _doubleTapAcceptanceState.Reset();
                return;
            }

            if (!IsSameAcceptanceBase(live, session))
            {
                // Only the base text / consumed prefix moved (a reconciler-consumed
                // key, or a late chunk for the same request). The text is already in
                // the host, so re-anchor on the live session instead of failing
                // closed: always record the confirmed insertion and recompute the
                // remainder from the live tail so a later accept cannot re-derive
                // spacing from a base that no longer matches the host.
                int skip = Math.Max(0, session.ConsumedChars + chunk.Length - live.ConsumedChars);
                string liveRemaining = live.VisibleText.Length >= skip ? live.VisibleText[skip..] : string.Empty;
                reAnchored = true;
                anchorExhausted = liveRemaining.Trim().Length == 0;
                if (anchorExhausted)
                {
                    CancelSession("accepted");
                }
                else
                {
                    _session = live with
                    {
                        VisibleText = liveRemaining,
                        ConsumedChars = live.ConsumedChars + skip,
                        KnownPrecedingText = live.KnownPrecedingText + insertion,
                    };
                    PublishSessionSnapshot(_session);
                    _overlay.Update(liveRemaining);
                    if (armDoubleTap)
                    {
                        _doubleTapAcceptanceState.RecordWordAccept(sessionToken, intent.EventTimeSeconds);
                    }
                }
                committed = true;
                return;
            }

            if (exhausted)
            {
                CancelSession("accepted");
            }
            else
            {
                _session = live with
                {
                    VisibleText = remaining,
                    ConsumedChars = session.ConsumedChars + chunk.Length,
                    KnownPrecedingText = session.KnownPrecedingText + insertion,
                };
                PublishSessionSnapshot(_session);
                _overlay.Update(remaining);
                if (armDoubleTap)
                {
                    _doubleTapAcceptanceState.RecordWordAccept(sessionToken, intent.EventTimeSeconds);
                }
            }
            committed = true;
        }).ConfigureAwait(false);

        if (!committed)
        {
            // The insertion landed but the base could not be reconciled; report it
            // honestly rather than as a replay refusal.
            if (insertionLanded) return AcceptanceIntentStatus.InsertedNotCommitted;
            // Nothing landed and the commit was abandoned (session nulled); the
            // live focus no longer matches this request.
            return AcceptanceIntentStatus.IntentInvalidated;
        }

        _ = RefreshAfterAcceptAsync(session.RequestId, anchorExhausted)
            .ContinueWith(LogFault, TaskScheduler.Default);
        return reAnchored
            ? AcceptanceIntentStatus.InsertedNotCommitted
            : AcceptanceIntentStatus.StateCommitted;
    }

    private static bool IsSameAcceptanceBase(SuggestionSession current, SuggestionSession expected) =>
        current.RequestId == expected.RequestId
        && current.ConsumedChars == expected.ConsumedChars
        && string.Equals(current.VisibleText, expected.VisibleText, StringComparison.Ordinal)
        && string.Equals(current.KnownPrecedingText, expected.KnownPrecedingText, StringComparison.Ordinal)
        && string.Equals(current.StableFieldIdentity, expected.StableFieldIdentity, StringComparison.Ordinal);

    private List<AcceptIntent> DrainAcceptanceQueueLocked()
    {
        var intents = new List<AcceptIntent>(_acceptanceQueue.Count);
        while (_acceptanceQueue.Count > 0) intents.Add(_acceptanceQueue.Dequeue());
        return intents;
    }

    private void CompletePendingAcceptanceIntents(AcceptanceIntentStatus status)
    {
        List<AcceptIntent> pending;
        lock (_acceptanceGate) pending = DrainAcceptanceQueueLocked();
        foreach (var intent in pending) CompleteAcceptanceIntent(intent, status);
    }

    private void StopQueueAndCompletePending(AcceptanceIntentStatus status)
    {
        List<AcceptIntent> pending;
        lock (_acceptanceGate)
        {
            pending = DrainAcceptanceQueueLocked();
            _acceptanceQueueRunning = false;
        }
        foreach (var intent in pending) CompleteAcceptanceIntent(intent, status);
    }

    private void CompleteAcceptanceIntent(AcceptIntent intent, AcceptanceIntentStatus status)
    {
        if (!intent.TryComplete()) return;
        _outstandingAcceptanceIntents.TryRemove(intent.Sequence, out _);

        var keyDisposition = status is AcceptanceIntentStatus.StateCommitted or AcceptanceIntentStatus.InsertedNotCommitted
            ? AcceptanceKeyDisposition.ConsumedBySuggestion
            : intent.CapturedEvent.ForegroundProcessId == 0
                || intent.CapturedEvent.ForegroundWindowHandle == 0
                    ? AcceptanceKeyDisposition.ReplayRefusedUnknownForeground
                    : AcceptanceKeyDisposition.ReplayRefusedIncompleteKeyMetadata;
        var outcome = new AcceptanceIntentOutcome(intent.Sequence, status, keyDisposition);
        if (keyDisposition != AcceptanceKeyDisposition.ConsumedBySuggestion)
        {
            // This sink is wired by AppHost even when nobody subscribes to the
            // diagnostic event. Never include field/request/key text or identity.
            try
            {
                _logger.LogWarning("Captured acceptance key not replayed: status={Status}, disposition={Disposition}.",
                    status, keyDisposition);
            }
            catch (Exception) { /* A diagnostic sink must not strand captured keys. */ }
        }
        var handlers = AcceptanceIntentCompleted;
        if (handlers is null) return;

        foreach (Action<AcceptanceIntentOutcome> handler in handlers.GetInvocationList())
        {
            try { handler(outcome); }
            catch (Exception ex)
            {
                _logger.LogError("Acceptance disposition observer failed (type={ExceptionType}).", ex.GetType().Name);
            }
        }
    }

    private async Task RefreshAfterAcceptAsync(string requestId, bool exhausted)
    {
        await Task.Delay(60).ConfigureAwait(false);
        await PostAsync(() =>
        {
            var after = _focus.Refresh();
            if (after is null) return;
            if (!exhausted && _session is { } live && live.RequestId == requestId
                && string.Equals(after.StableFieldIdentity, live.StableFieldIdentity, StringComparison.Ordinal))
            {
                if (!after.CaretRect.IsEmpty)
                {
                    _session = live with { AnchorRect = after.CaretRect };
                    PublishSessionSnapshot(_session);
                    _overlay.Show(live.VisibleText, after.CaretRect);
                }
            }
            else if (exhausted && _enabled && _session is null)
            {
                TriggerGeneration(after);
            }
        }).ConfigureAwait(false);
    }

    private void CancelSession(string reason)
    {
        _doubleTapAcceptanceState.Reset();
        CompletePendingAcceptanceIntents(AcceptanceIntentStatus.IntentInvalidated);
        if (_session is null && !_generationInFlight) return;
        _logger.LogInformation("Cancel session ({Reason}).", reason);
        _session = null;
        _sessionPid = 0;
        _generationInFlight = false;
        _inFlightPid = 0;
        _inFlightRequestId = null;
        PublishSessionSnapshot(null);
        _overlay.Hide();
        _work.Cancel();
    }

    /// <summary>
    /// Publish one coherent, hook-thread-readable presentation snapshot. The
    /// request and field identity bind queued intents to the same presentation;
    /// successful insertion advances its tail before the next intent is handled.
    /// </summary>
    private void PublishSessionSnapshot(SuggestionSession? session)
    {
        long version = Interlocked.Increment(ref _presentationVersion);
        var presentation = session is not null && session.VisibleText.Length > 0
            ? new PresentationSnapshot(
                session.RequestId,
                session.VisibleText,
                _sessionPid,
                session.StableFieldIdentity,
                session.IsCredentialField,
                version)
            : null;
        Volatile.Write(ref _presentation, presentation);


    }

    private static string Preview(string s) => s.Length <= 40 ? s : s[..40] + "…";

    /// <summary>
    /// Escape control chars and newlines so a single-line log entry shows the
    /// FULL string verbatim. Used by the detailed-monitoring path so we can
    /// reconstruct exactly what the engine produced vs what the inserter sent
    /// vs what the host received.
    /// </summary>
    private static string EscapeFull(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (c < 0x20 || c == 0x7F) sb.Append($"\\x{(int)c:X2}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>UTF-16 hex dump of <paramref name="s"/>. Lets us spot doubled
    /// chars, invisible whitespace, or off-by-one differences between snapshot
    /// and inserter at a glance.</summary>
    private static string HexDump(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length * 5);
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(((int)s[i]).ToString("X4"));
        }
        return sb.ToString();
    }

    /// <summary>True if the tail of <paramref name="s"/> ends with 5+ identical
    /// non-whitespace chars — a degenerate run that should be stripped.</summary>
    private static bool HasTrailingCharRun(string s, int threshold)
    {
        if (s.Length <= threshold) return false;
        char last = s[^1];
        if (last == ' ' || last == '\t' || last == '\n' || last == '\r') return false;
        int count = 0;
        for (int i = s.Length - 1; i >= 0 && count <= threshold; i--)
        {
            if (s[i] != last) return false;
            count++;
        }
        return count > threshold;
    }

    /// <summary>Find the first index of a run of 5+ identical non-whitespace
    /// chars anywhere in <paramref name="s"/>, or -1 if none. Used to truncate
    /// the suggestion before an interior degenerate run reaches the host —
    /// degeneration that the engine's tail-only check missed when the run was
    /// split across many small chunks.</summary>
    private static int FindInteriorRunStart(string s, int threshold)
    {
        if (s.Length <= threshold) return -1;
        int runStart = 0;
        for (int i = 1; i < s.Length; i++)
        {
            if (s[i] != s[runStart])
            {
                runStart = i;
                continue;
            }
            int runLen = i - runStart + 1;
            if (runLen > threshold)
            {
                char c = s[runStart];
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r') return runStart;
            }
        }
        return -1;
    }

    /// <summary>
    /// Pre-insertion firewall: if the suggestion contains a degenerate run
    /// (5+ identical non-whitespace chars) anywhere, truncate at the start of
    /// the run. If after trimming nothing useful is left, return empty so
    /// AcceptAsync skips insertion entirely.
    /// </summary>
    private static string StripDegenerateTail(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        int idx = FindInteriorRunStart(s, 4);
        if (idx >= 0) s = s[..idx];
        return s.TrimEnd();
    }

    private void Post(Action action)
    {
        if (_disposed) return;
        if (SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => { if (!_disposed) action(); }, null);
    }

    private Task PostAsync(Action action)
    {
        if (_disposed) return Task.CompletedTask;
        if (SynchronizationContext.Current == _ui)
        {
            try { action(); return Task.CompletedTask; }
            catch (Exception ex) { return Task.FromException(ex); }
        }
        var tcs = new TaskCompletionSource();
        _ui.Post(_ =>
        {
            try { if (!_disposed) action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }

    private void LogFault(Task t)
    {
        if (t.Exception is { } ex) _logger.LogError(ex, "Background task faulted.");
    }
}
