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

public sealed class SuggestionCoordinatorDoubleTapTests
{
    [Fact]
    public async Task QuickDistinctSecondTabAcceptsOnlyTheRemainingSuggestion()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);

        Assert.True(rig.Hook.Emit(Tab()));
        Assert.Equal(new[] { "world" }, rig.Inserter.Inserted);
        Assert.Equal(" again tomorrow", rig.Overlay.Text);

        rig.Clock.Now += 0.1;
        Assert.True(rig.Hook.Emit(Tab()));

        Assert.Equal(new[] { "world", "again tomorrow" }, rig.Inserter.Inserted);
        Assert.False(rig.Overlay.IsVisible);
    }

    [Fact]
    public async Task AutoRepeatAcceptsNormallyButDoesNotArmOrCompleteDoubleTap()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);

        Assert.True(rig.Hook.Emit(Tab()));
        rig.Clock.Now += 0.05;
        Assert.True(rig.Hook.Emit(Tab(isAutorepeat: true)));
        rig.Clock.Now += 0.05;
        Assert.True(rig.Hook.Emit(Tab()));

        Assert.Equal(new[] { "world", "again", "tomorrow" }, rig.Inserter.Inserted);
        Assert.False(rig.Overlay.IsVisible);
    }

    [Fact]
    public async Task InterveningKeyResetsPendingPair()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);

        Assert.True(rig.Hook.Emit(Tab()));
        Assert.False(rig.Hook.Emit(new KeyboardEvent
        {
            Kind = KeyKind.Other,
            Character = '\0',
            HasNonShiftModifier = false,
            IsKeyDown = true,
        }));
        rig.Clock.Now += 0.1;
        Assert.True(rig.Hook.Emit(Tab()));

        Assert.Equal(new[] { "world", "again" }, rig.Inserter.Inserted);
        Assert.Equal(" tomorrow", rig.Overlay.Text);
    }

    [Fact]
    public async Task CredentialRejectionCancelsPendingPairWithoutFurtherInsertion()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);

        Assert.True(rig.Hook.Emit(Tab()));
        rig.Focus.MakeCredentialField();
        rig.Clock.Now += 0.1;
        Assert.True(rig.Hook.Emit(Tab()));

        Assert.Equal(new[] { "world" }, rig.Inserter.Inserted);
        Assert.False(rig.Overlay.IsVisible);
    }

    [Fact]
    public async Task FocusTransitionToAnotherProcessCancelsBeforeInsertion()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);

        rig.Focus.MoveToAnotherProcess();
        Assert.True(rig.Hook.Emit(Tab()));

        Assert.Empty(rig.Inserter.Inserted);
        Assert.False(rig.Overlay.IsVisible);
    }

    [Fact]
    public async Task StaleQueuedTabSnapshotsCannotInsertTheSameChunkTwice()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);
        var pendingBefore = rig.Ui.PendingCount;

        var suppressions = await Task.Run(() => new[]
        {
            rig.Hook.Emit(Tab(), useUiContext: false),
            rig.Hook.Emit(Tab(), useUiContext: false),
        });
        Assert.Equal(new[] { true, true }, suppressions);
        await rig.Ui.WaitForPendingAtLeastAsync(pendingBefore + 2);
        rig.Ui.DrainAll();

        Assert.Equal(new[] { "world" }, rig.Inserter.Inserted);
        Assert.Equal(" again tomorrow", rig.Overlay.Text);
    }

    [Fact]
    public async Task TabWithoutAnOwnedVisibleSuggestionIsNotSwallowed()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true, showSuggestion: false);

        Assert.False(rig.Hook.Emit(Tab()));
        Assert.Empty(rig.Inserter.Inserted);
    }

    private static KeyboardEvent Tab(bool isAutorepeat = false) => new()
    {
        Kind = KeyKind.Tab,
        Character = '\0',
        HasNonShiftModifier = false,
        IsKeyDown = true,
        IsAutorepeat = isAutorepeat,
    };

    private sealed class ManualClock
    {
        public double Now { get; set; } = 100;
    }

    private sealed class Rig : IAsyncDisposable
    {
        public QueuedSynchronizationContext Ui { get; } = new();
        public TestKeyboardHook Hook { get; } = new();
        public TestFocusTracker Focus { get; } = new();
        public TestOverlay Overlay { get; } = new();
        public TestTextInserter Inserter { get; } = new();
        public SuggestionWorkController Work { get; } = new(TimeSpan.Zero);
        public TestSuggestionEngine Engine { get; } = new();
        public ManualClock Clock { get; } = new();
        public SuggestionCoordinator Coordinator { get; }

        private Rig(bool doubleTapEnabled)
        {
            Hook.SetUiContext(Ui);
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
        }

        public static async Task<Rig> CreateAsync(bool doubleTapEnabled, bool showSuggestion = true)
        {
            var rig = new Rig(doubleTapEnabled);
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
            yield return new SuggestionChunk("world again tomorrow", IsFinal: true);
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

        public bool Emit(KeyboardEvent input, bool useUiContext = true)
        {
            var previous = SynchronizationContext.Current;
            if (useUiContext) SynchronizationContext.SetSynchronizationContext(_uiContext);
            try
            {
                var args = new KeyEventArgs(input);
                KeyEvent?.Invoke(this, args);
                return args.Suppress;
            }
            finally
            {
                if (useUiContext) SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
    }

    private sealed class TestFocusTracker : IFocusTracker
    {
        private FocusedField _field = new()
        {
            ElementHandle = new object(),
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

        public FocusedField? Current => _field;
        public event EventHandler<FocusedField?>? FocusChanged { add { } remove { } }
        public FocusedField? Refresh() => _field;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void MakeCredentialField() => _field = _field with { IsCredentialField = true };
        public void MoveToAnotherProcess() => _field = _field with { ProcessId = _field.ProcessId + 1 };
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
        public List<string> Inserted { get; } = new();
        public Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct)
        {
            Inserted.Add(text);
            return Task.FromResult(true);
        }
    }
}
