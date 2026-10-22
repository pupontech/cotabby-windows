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
/// alongside the suggestion coordinator, owns no focus/UIA work of its own,
/// only consumes events when a trigger is active.
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
        _hook.KeyEvent += OnKeyEvent;
    }

    public void Dispose()
    {
        _hook.KeyEvent -= OnKeyEvent;
        _dispatcher.BeginInvoke(() => _popup.Hide());
    }

    private void OnKeyEvent(object? sender, KeyEventArgs args)
    {
        if (!Enabled) return;

        var input = args.Event;
        bool refreshFocus = EmojiFocusPolicy.RequiresFreshFocus(input);

        // UIA is synchronous on the low-level hook callback; only events that
        // can change emoji query text need a fresh ownership read.
        if (!_state.IsActive && refreshFocus && input.Character == ':')
        {
            _triggerField = _focus.Refresh();
            if (!EmojiFocusPolicy.IsUsableTarget(_triggerField))
            {
                _triggerField = null;
                return; // Don't start in fields where we can't insert anyway,
                        // or in a sign-in/verification field we must leave alone.
            }
        }
        else if (_state.IsActive && refreshFocus)
        {
            var liveField = _focus.Refresh();
            if (!EmojiFocusPolicy.SameUsableTarget(_triggerField, liveField))
            {
                _state.Reset();
                ClearPopup();
                return;
            }

            // Retain the latest snapshot for caret geometry and commit, but only
            // after process + stable field identity have proved ownership.
            _triggerField = liveField;
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

    private void RefreshPopup()
    {
        _matches = EmojiMatcher.Search(_state.Query, max: 6);
        _selected = 0;
        // OnKeyEvent has already revalidated and refreshed this field before
        // every active query update. Never resurrect a stale target here.
        var anchor = _triggerField?.CaretRect ?? new ScreenRect(100, 100, 1, 20);
        _dispatcher.BeginInvoke(() => _popup.Show(_matches, _state.Query, anchor, _selected));
    }

    private void ClearPopup()
    {
        _matches = new List<EmojiEntry>();
        _selected = 0;
        _triggerField = null;
        _dispatcher.BeginInvoke(() => _popup.Hide());
    }

    private async Task CommitAsync(FocusedField target, EmojiEntry entry)
    {
        try
        {
            // Focus can change between the hook event and this asynchronous
            // commit. The inserter repeats ownership validation inside the
            // input-blocked replacement transaction immediately before mutation.
            if (!EmojiFocusPolicy.SameUsableTarget(target, _focus.Refresh())) return;

            int eat = _state.QueryAtCommitLength;
            if (!await _inserter.ReplaceTextAsync(target, eat, entry.Glyph, CancellationToken.None))
                return;

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
