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
/// Review finding F3. A host whose UIA provider yields no runtime id (GetRuntimeId
/// throws or returns an empty array) used to produce a null <see cref="FocusedField.StableFieldIdentity"/>,
/// and the accept fast path required that identity to be non-null. The result was
/// that the double-tap feature was silently off in that host and every Tab was
/// passed through as a literal Tab (tab character in an editor, focus move in a
/// browser).
///
/// The rule these tests pin: "no identity" is UNPROVEN ownership, never unowned.
/// Acceptance is refused only when an identity EXISTS and DIFFERS from the one the
/// presentation was published with. The fail-open Tab pass-through is unchanged.
/// </summary>
public sealed class FieldIdentityTests
{
    [Fact]
    public async Task DoubleTapStillWorksWhenTheHostCannotProveAFieldIdentity()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true, identity: null);

        Assert.True(rig.Hook.Emit(Tab()));
        Assert.Equal(new[] { "world" }, rig.Inserter.Inserted);
        Assert.Equal(" again tomorrow today", rig.Overlay.Text);

        rig.Clock.Now += 0.1;
        Assert.True(rig.Hook.Emit(Tab()));

        Assert.Equal(new[] { "world", " again tomorrow today" }, rig.Inserter.Inserted);
        Assert.Equal("Hello world again tomorrow today", rig.Focus.HostText);
        Assert.False(rig.Overlay.IsVisible);

        await rig.WaitForOutcomesAsync(2);
        Assert.Equal(
            new[] { AcceptanceIntentStatus.StateCommitted, AcceptanceIntentStatus.StateCommitted },
            rig.Outcomes.Select(outcome => outcome.Status));
        Assert.All(rig.Outcomes, outcome => Assert.Equal(AcceptanceKeyDisposition.ConsumedBySuggestion, outcome.KeyDisposition));
    }

    [Fact]
    public async Task DoubleTapStillWorksWithDescriptorOnlyFallbackIdentity()
    {
        await using var rig = await Rig.CreateAsync(
            doubleTapEnabled: true,
            identity: "ct=Edit|aid=|nm=|hwnd=123|pid=42|gen=0");

        Assert.True(rig.Hook.Emit(Tab()));
        Assert.Equal(new[] { "world" }, rig.Inserter.Inserted);
    }

    [Fact]
    public async Task EmptyFieldIdentityIsTreatedAsUnprovenRatherThanUnowned()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true, identity: string.Empty);

        Assert.True(rig.Hook.Emit(Tab()));

        Assert.Equal(new[] { "world" }, rig.Inserter.Inserted);
        Assert.Equal(" again tomorrow today", rig.Overlay.Text);
    }

    [Fact]
    public async Task ProvenIdentityMismatchStillRefusesAcceptanceAndCancelsTheSession()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true, identity: "field-a");

        Assert.True(rig.Hook.Emit(Tab()));
        Assert.Equal(new[] { "world" }, rig.Inserter.Inserted);

        // Same process, same window, genuinely different field: the identity that
        // exists must be allowed to refuse, and the Tab must reach the host.
        rig.Focus.MoveToAnotherField("field-b");
        rig.Clock.Now += 0.1;
        Assert.False(rig.Hook.Emit(Tab()));

        Assert.Equal(new[] { "world" }, rig.Inserter.Inserted);
        Assert.False(rig.Overlay.IsVisible);
    }

    [Fact]
    public async Task PresentationWithAProvenIdentityIsNotAcceptedByAFieldWithoutOne()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true, identity: "field-a");

        // The presentation proved an identity, so the live field has to prove the
        // same one; "it stopped being readable" is not evidence of ownership.
        rig.Focus.MoveToAnotherField(null);
        Assert.False(rig.Hook.Emit(Tab()));

        Assert.Empty(rig.Inserter.Inserted);
        Assert.False(rig.Overlay.IsVisible);
    }

    [Fact]
    public async Task TabWithoutAnOwnedVisibleSuggestionStillPassesThroughWhenIdentityIsUnproven()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true, identity: null, showSuggestion: false);

        Assert.False(rig.Hook.Emit(Tab()));

        Assert.Empty(rig.Inserter.Inserted);
        Assert.False(rig.Overlay.IsVisible);
    }

    private static KeyboardEvent Tab() => new()
    {
        Kind = KeyKind.Tab,
        Character = '\0',
        HasNonShiftModifier = false,
        IsKeyDown = true,
        ForegroundProcessId = Environment.ProcessId + 100,
        ForegroundWindowHandle = 1234,
    };

    private sealed class ManualClock
    {
        public double Now { get; set; } = 100;
    }

    private sealed class Rig : IAsyncDisposable
    {
        public QueuedSynchronizationContext Ui { get; } = new();
        public TestKeyboardHook Hook { get; } = new();
        public TestFocusTracker Focus { get; }
        public TestOverlay Overlay { get; } = new();
        public TestTextInserter Inserter { get; }
        public SuggestionWorkController Work { get; } = new(TimeSpan.Zero);
        public TestSuggestionEngine Engine { get; } = new();
        public ManualClock Clock { get; } = new();
        public ConcurrentQueue<AcceptanceIntentOutcome> Outcomes { get; } = new();
        public SuggestionCoordinator Coordinator { get; }

        private Rig(bool doubleTapEnabled, string? identity)
        {
            Focus = new TestFocusTracker(identity);
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
                DoubleTapFullAcceptanceEnabled = doubleTapEnabled,
            };
            Coordinator.AcceptanceIntentCompleted += Outcomes.Enqueue;
        }

        public static async Task<Rig> CreateAsync(bool doubleTapEnabled, string? identity, bool showSuggestion = true)
        {
            var rig = new Rig(doubleTapEnabled, identity);
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(rig.Ui);
            try
            {
                rig.Coordinator.Start();
                if (showSuggestion)
                {
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

        public async Task WaitForOutcomesAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Outcomes.Count < count)
            {
                Ui.DrainAll();
                await Task.Delay(10, timeout.Token);
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
        private FocusedField _field;

        public TestFocusTracker(string? identity)
        {
            _field = new FocusedField
            {
                ElementHandle = new object(),
                StableFieldIdentity = identity,
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
        }

        public FocusedField? Current => _field;

        public string HostText { get; private set; } = "Hello ";

        public event EventHandler<FocusedField?>? FocusChanged { add { } remove { } }

        public FocusedField? Refresh() => _field is null
            ? null
            : _field with { Text = HostText };

        public void Start() { }
        public void Stop() { }
        public void Dispose() { }

        public void MoveToAnotherField(string? identity)
        {
            _field = _field with { StableFieldIdentity = identity };
        }

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

        public Task<TextReplacementResult> ReplaceTextAsync(
            FocusedField target, int charactersToReplace, string text, string expectedTail, int expectedCaretOffset, CancellationToken ct) =>
            Task.FromResult(TextReplacementResult.Rejected);
    }
}
