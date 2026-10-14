using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Cotabby.Core.Overlay;
using Cotabby.Core.Suggestions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cotabby.Core.Tests;

/// <summary>
/// Regression coverage for review finding F2: Shift+Tab must remain a binding
/// distinct from Tab, exactly as upstream matches it
/// (<c>InputMonitor.swift</c>: <c>if keyEvent.keyCode == accept.keyCode, eventModifiers == accept.modifiers</c>,
/// with <c>ShortcutModifierMask</c> carrying <c>.shift</c>). A shifted Tab is the
/// user's reverse-navigation gesture: it must pass through untouched, must not be
/// suppressed by the hook-thread gate, must not insert text, and must not disturb a
/// pending double-tap pair.
/// </summary>
public sealed class ShiftTabTests
{
    [Fact]
    public void Reconciler_ShiftedTab_IsNotAcceptanceInput()
    {
        var session = MakeSession("world");
        var shiftedTab = Tab(shift: true);

        var result = SuggestionSessionReconciler.Apply(session, shiftedTab);

        Assert.Equal(SuggestionSessionReconciler.Outcome.Ignore, result.Outcome);
        Assert.Same(session, result.Next);
    }

    [Fact]
    public void Reconciler_PlainTab_StillReportsAccept()
    {
        var session = MakeSession("world");

        var result = SuggestionSessionReconciler.Apply(session, Tab(shift: false));

        Assert.Equal(SuggestionSessionReconciler.Outcome.Accept, result.Outcome);
    }

    [Fact]
    public async Task ShiftedTab_IsNotSuppressedAndInsertsNothing()
    {
        await using var rig = await ShiftTabRig.CreateAsync();

        var suppressed = rig.Hook.Emit(Tab(shift: true));

        Assert.False(suppressed);
        Assert.Empty(rig.Inserter.Inserted);
        Assert.Equal("world again tomorrow today", rig.Overlay.Text);
    }

    [Fact]
    public async Task ShiftedTab_DoesNotConsumeThePendingWordAccept()
    {
        await using var rig = await ShiftTabRig.CreateAsync();

        Assert.False(rig.Hook.Emit(Tab(shift: true)));
        rig.Clock.Now += 0.1;

        // The shifted Tab must not have eaten the first word or armed a pair, so
        // the next physical Tab still accepts the head chunk.
        Assert.True(rig.Hook.Emit(Tab(shift: false)));

        Assert.Equal(new[] { "world" }, rig.Inserter.Inserted);
        Assert.Equal(" again tomorrow today", rig.Overlay.Text);
    }

    [Fact]
    public async Task ShiftedTab_LeavesPendingDoubleTapPairIntact()
    {
        await using var rig = await ShiftTabRig.CreateAsync();

        Assert.True(rig.Hook.Emit(Tab(shift: false)));
        Assert.Equal(new[] { "world" }, rig.Inserter.Inserted);

        rig.Clock.Now += 0.1;
        Assert.False(rig.Hook.Emit(Tab(shift: true)));
        Assert.Equal(new[] { "world" }, rig.Inserter.Inserted);
        Assert.Equal(" again tomorrow today", rig.Overlay.Text);

        rig.Clock.Now += 0.1;
        Assert.True(rig.Hook.Emit(Tab(shift: false)));

        Assert.Equal(new[] { "world", " again tomorrow today" }, rig.Inserter.Inserted);
        Assert.Equal("Hello world again tomorrow today", rig.Focus.HostText);
        Assert.False(rig.Overlay.IsVisible);
    }

    private static SuggestionSession MakeSession(string visible) => new()
    {
        RequestId = "req_shift_tab",
        ElementHandle = new object(),
        OriginalPrefix = "hello ",
        ConsumedChars = 0,
        VisibleText = visible,
        IsComplete = true,
        AnchorRect = new ScreenRect(0, 0, 1, 1),
    };

    private static KeyboardEvent Tab(bool shift) => new()
    {
        Kind = KeyKind.Tab,
        Character = '\0',
        HasNonShiftModifier = false,
        HasShiftModifier = shift,
        IsKeyDown = true,
        ForegroundProcessId = Environment.ProcessId + 100,
        ForegroundWindowHandle = 1234,
    };

    private sealed class ManualClock
    {
        public double Now { get; set; } = 100;
    }

    private sealed class ShiftTabRig : IAsyncDisposable
    {
        public QueuedSynchronizationContext Ui { get; } = new();
        public TestKeyboardHook Hook { get; } = new();
        public TestFocusTracker Focus { get; } = new();
        public TestOverlay Overlay { get; } = new();
        public TestTextInserter Inserter { get; }
        public SuggestionWorkController Work { get; } = new(TimeSpan.Zero);
        public TestSuggestionEngine Engine { get; } = new();
        public ManualClock Clock { get; } = new();
        public SuggestionCoordinator Coordinator { get; }

        private ShiftTabRig()
        {
            Hook.SetUiContext(Ui);
            Inserter = new TestTextInserter(Focus);
            Coordinator = new SuggestionCoordinator(
                Hook,
                Focus,
                Engine,
                Overlay,
                Inserter,
                Work,
                NullLogger<SuggestionCoordinator>.Instance,
                Ui,
                doubleTapUptimeProvider: () => Clock.Now)
            {
                DoubleTapFullAcceptanceEnabled = true,
            };
        }

        public static async Task<ShiftTabRig> CreateAsync()
        {
            var rig = new ShiftTabRig();
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(rig.Ui);
            try
            {
                rig.Coordinator.Start();
                rig.Hook.Emit(new KeyboardEvent
                {
                    Kind = KeyKind.Character,
                    Character = 'x',
                    HasNonShiftModifier = false,
                    IsKeyDown = true,
                });
                await rig.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                rig.Engine.Release.TrySetResult();
                await rig.WaitForOverlayAsync().ConfigureAwait(false);
            }
            catch
            {
                await rig.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
            return rig;
        }

        private async Task WaitForOverlayAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!Overlay.IsVisible)
            {
                await Ui.WaitForPendingAtLeastAsync(1).WaitAsync(timeout.Token).ConfigureAwait(false);
                Ui.DrainAll();
            }
            Ui.DrainAll();
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync().ConfigureAwait(false);
            Ui.DrainAll();
        }
    }

    private sealed class TestSuggestionEngine : ISuggestionEngine
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsReady => true;

        public async IAsyncEnumerable<SuggestionChunk> GenerateAsync(
            SuggestionRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            yield return new SuggestionChunk("world again tomorrow today", IsFinal: true);
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly object _gate = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
        private TaskCompletionSource _changed = NewSignal();

        public int PendingCount
        {
            get { lock (_gate) return _queue.Count; }
        }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            TaskCompletionSource changed;
            lock (_gate)
            {
                _queue.Enqueue((callback, state));
                changed = _changed;
                _changed = NewSignal();
            }
            changed.TrySetResult();
        }

        public async Task WaitForPendingAtLeastAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (_queue.Count >= count) return;
                    changed = _changed.Task;
                }
                await changed.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
        }

        public void DrainAll()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                while (true)
                {
                    (SendOrPostCallback Callback, object? State) item;
                    lock (_gate)
                    {
                        if (_queue.Count == 0) return;
                        item = _queue.Dequeue();
                    }
                    item.Callback(item.State);
                }
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TestKeyboardHook : IKeyboardHook
    {
        private SynchronizationContext? _uiContext;
        public event EventHandler<KeyEventArgs>? KeyEvent;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void SetUiContext(SynchronizationContext ui) => _uiContext = ui;

        public bool Emit(KeyboardEvent input)
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(_uiContext);
            try
            {
                var args = new KeyEventArgs(input);
                KeyEvent?.Invoke(this, args);
                return args.Suppress;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
    }

    private sealed class TestFocusTracker : IFocusTracker
    {
        private FocusedField _field = new()
        {
            ElementHandle = new object(),
            StableFieldIdentity = "test-field",
            ProcessId = Environment.ProcessId + 100,
            ProcessName = "test-host",
            Text = "Hello ",
            CaretOffset = 6,
            CaretRect = new ScreenRect(10, 20, 1, 20),
            FieldRect = new ScreenRect(0, 0, 100, 30),
            IsSingleLine = true,
            IsSecure = false,
            IsCredentialField = false,
        };

        public string HostText { get; private set; } = "Hello ";

        public FocusedField? Current => _field;
        public event EventHandler<FocusedField?>? FocusChanged { add { } remove { } }
        public FocusedField? Refresh() => _field with { Text = HostText };
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void ApplyInsertion(string text) => HostText += text;
    }

    private sealed class TestOverlay : IOverlayPresenter
    {
        public string? Text { get; private set; }
        public bool IsVisible => Text is not null;
        public void Show(string text, ScreenRect anchor) => Text = text;
        public void Update(string text) => Text = text;
        public void Hide() => Text = null;
    }

    private sealed class TestTextInserter : ITextInserter
    {
        private readonly TestFocusTracker _focus;
        private readonly object _gate = new();
        private readonly List<string> _inserted = new();

        public TestTextInserter(TestFocusTracker focus) => _focus = focus;

        public string[] Inserted { get { lock (_gate) return _inserted.ToArray(); } }

        public Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct)
        {
            lock (_gate) _inserted.Add(text);
            _focus.ApplyInsertion(text);
            return Task.FromResult(true);
        }
    }
}
