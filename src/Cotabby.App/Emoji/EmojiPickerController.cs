using System.Threading;
using System.Windows.Threading;
using Cotabby.Core.Emoji;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Microsoft.Extensions.Logging;

namespace Cotabby.App.Emoji;

/// <summary>
/// Wires keyboard events into the emoji trigger state machine and drives the
/// popup window + final insertion. Mirrors the macOS port's
/// <c>EmojiPickerController</c>: observes the global keyboard stream
/// alongside the suggestion coordinator. The low-level hook path consumes an
/// atomically published focus snapshot; commit-time refresh and insertion checks
/// run asynchronously off the hook callback.
/// </summary>
/// <remarks>
/// Two notable Windows constraints vs the macOS port:
/// <list type="bullet">
///   <item>We let the trailing <c>:</c> reach the host. A successful guarded
///   replacement erases the complete <c>:query:</c> and inserts the glyph; if
///   focus ownership is lost, the literal text is left intact.</item>
///   <item>Emoji handling never suppresses keyboard events. The picker is
///   additive: a trailing-colon commit starts a guarded replacement transaction.</item>
/// </list>
/// </remarks>
public sealed class EmojiPickerController : IDisposable
{
    private readonly IKeyboardHook _hook;
    private readonly IFocusTracker _focus;
    private readonly ITextInserter _inserter;
    private readonly EmojiPopupWindow _popup;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<EmojiPickerController> _logger;

    private readonly EmojiTriggerStateMachine _state = new();
    private List<EmojiEntry> _matches = new();
    private int _selected;
    private FocusedField? _triggerField;
    private long _focusGeneration;
    private long _triggerFocusGeneration;

    public bool Enabled { get; set; } = true;

    public EmojiPickerController(
        IKeyboardHook hook,
        IFocusTracker focus,
        ITextInserter inserter,
        EmojiPopupWindow popup,
        Dispatcher dispatcher,
        ILogger<EmojiPickerController> logger)
    {
        _hook = hook;
        _focus = focus;
        _inserter = inserter;
        _popup = popup;
        _dispatcher = dispatcher;
        _logger = logger;
        _focus.FocusChanged += OnFocusChanged;
        _hook.KeyEvent += OnKeyEvent;
    }

    public void Dispose()
    {
        _focus.FocusChanged -= OnFocusChanged;
        _hook.KeyEvent -= OnKeyEvent;
        Interlocked.Increment(ref _focusGeneration);
        _dispatcher.BeginInvoke(() => _popup.Hide());
    }

    private void OnKeyEvent(object? sender, KeyEventArgs args)
    {
        if (!Enabled) return;

        var input = args.Event;
        bool refreshFocus = EmojiFocusPolicy.RequiresFreshFocus(input);

        // UIA focus notifications arrive off the hook thread. If one invalidated
        // the active target, reset here before processing the next key event.
        if (_state.IsActive && _triggerFocusGeneration != Volatile.Read(ref _focusGeneration))
        {
            _state.Reset();
            ClearPopup();
        }

        // The low-level keyboard callback must never perform synchronous UIA.
        // Current is an atomically published snapshot; the inserter performs a
        // bounded, strong-identity/text/caret check before any mutation.
        if (!_state.IsActive && refreshFocus && input.Character == ':')
        {
            long focusGeneration = Volatile.Read(ref _focusGeneration);
            var triggerField = _focus.Current;
            Volatile.Write(ref _triggerField, triggerField);
            if (focusGeneration != Volatile.Read(ref _focusGeneration))
            {
                Volatile.Write(ref _triggerField, null);
                return;
            }
            _triggerFocusGeneration = focusGeneration;
            if (!EmojiFocusPolicy.IsUsableTarget(triggerField))
            {
                Volatile.Write(ref _triggerField, null);
                return; // Don't start in fields where we can't insert anyway,
                        // or in a sign-in/verification field we must leave alone.
            }
        }
        else if (_state.IsActive && refreshFocus)
        {
            var liveField = _focus.Current;
            if (!EmojiFocusPolicy.SameUsableTarget(Volatile.Read(ref _triggerField), liveField))
            {
                _state.Reset();
                ClearPopup();
                return;
            }
        }

        var outcome = _state.Apply(args.Event);
        switch (outcome)
        {
            case EmojiTriggerStateMachine.Outcome.QueryChanged:
                RefreshPopup();
                return;
            case EmojiTriggerStateMachine.Outcome.Commit:
                if (_matches.Count > 0 && EmojiFocusPolicy.IsUsableTarget(_triggerField))
                {
                    var entry = _matches[_selected];
                    _ = CommitAsync(_triggerField!, entry);
                }
                ClearPopup();
                return;
            case EmojiTriggerStateMachine.Outcome.Cancel:
                ClearPopup();
                return;
            case EmojiTriggerStateMachine.Outcome.None:
            default:
                return;
        }
    }

    private void OnFocusChanged(object? sender, FocusedField? field)
    {
        var target = Volatile.Read(ref _triggerField);
        if (target is null || EmojiFocusPolicy.SameUsableTarget(target, field)) return;

        Interlocked.Increment(ref _focusGeneration);
        try { _dispatcher.BeginInvoke(() => _popup.Hide()); }
        catch (InvalidOperationException) { }
    }

    private void RefreshPopup()
    {
        _matches = EmojiMatcher.Search(_state.Query, max: 6);
        _selected = 0;
        // The published current snapshot must still belong to the trigger target.
        // Replacement uses the original trigger caret and refreshes asynchronously.
        var target = Volatile.Read(ref _triggerField);
        var matches = _matches;
        string query = _state.Query;
        int selected = _selected;
        var anchor = target?.CaretRect ?? new ScreenRect(100, 100, 1, 20);
        long generation = _triggerFocusGeneration;
        _dispatcher.BeginInvoke(() =>
        {
            if (generation != Volatile.Read(ref _focusGeneration)
                || !EmojiFocusPolicy.SameUsableTarget(target, _focus.Current))
            {
                _popup.Hide();
                return;
            }

            _popup.Show(matches, query, anchor, selected);
        });
    }

    private void ClearPopup()
    {
        Interlocked.Increment(ref _focusGeneration);
        _matches = new List<EmojiEntry>();
        _selected = 0;
        Volatile.Write(ref _triggerField, null);
        _dispatcher.BeginInvoke(() => _popup.Hide());
    }

    private async Task CommitAsync(FocusedField target, EmojiEntry entry)
    {
        try
        {
            int eat = _state.QueryAtCommitLength;
            string expectedTail = string.Concat(":", _state.LastCommittedQuery, ":");
            string tailBeforeTrailingColon = expectedTail[..^1];
            if (target.CaretOffset < 0
                || target.CaretOffset > target.Text.Length
                || target.CaretOffset > int.MaxValue - expectedTail.Length)
                return;

            int expectedCaretOffset = target.CaretOffset + expectedTail.Length;
            FocusedField? liveField = null;

            // Refresh off the low-level hook stack. The host may not have
            // processed the triggering colon yet, so briefly wait for the exact
            // tail at the original trigger caret; any same-field edit or focus
            // change aborts.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                liveField = await Task.Run(_focus.Refresh).ConfigureAwait(false);
                if (!EmojiFocusPolicy.SameUsableTarget(target, liveField)) return;
                if (EmojiFocusPolicy.MatchesExpectedTailAtTriggerCaret(target, liveField, expectedTail)) break;
                if (!EmojiFocusPolicy.MatchesExpectedTailAtTriggerCaret(
                    target, liveField, tailBeforeTrailingColon)) return;
                await Task.Delay(10).ConfigureAwait(false);
            }

            if (liveField is null
                || !EmojiFocusPolicy.MatchesExpectedTailAtTriggerCaret(target, liveField, expectedTail)) return;
            var result = await _inserter.ReplaceTextAsync(
                target, eat, entry.Glyph, expectedTail, expectedCaretOffset, CancellationToken.None);
            if (result.Status == TextReplacementStatus.Partial)
            {
                _logger.LogWarning(
                    "Emoji replacement partially dispatched ({Inserted}/{Requested} input events; key release recovered={Recovered}).",
                    result.InsertedInputCount, result.RequestedInputCount, result.KeyReleaseRecovered);
                return;
            }
            if (!result.IsComplete) return;

            // Deliberately logs the catalog name, not the typed query: the query
            // is user text and is not written to the local log.
            _logger.LogInformation(
                "Emoji committed: catalog=\"{Catalog}\" → {Glyph} (eaten chars={Eat})",
                entry.Name, entry.Glyph, eat);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Emoji commit failed.");
        }
    }

}
