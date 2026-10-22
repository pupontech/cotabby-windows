using System.Runtime.CompilerServices;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Cotabby.Core.Overlay;
using Cotabby.Core.Suggestions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotabby.Core.Tests;

public sealed class SuggestionPerformanceMetricsTests
{
    [Fact]
    public async Task DoesNotRecordWhenFinalOutputIsCanceledBeforeQueuedPresentation()
    {
        await using var harness = new Harness(waitAfterFinalChunk: true);
        harness.Start("model-at-request-start");
        harness.Hook.EmitCharacter(harness.Ui);

        await harness.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Engine.ReleaseOutput.TrySetResult();
        await harness.Ui.WaitForPendingAtLeastAsync(1);

        harness.Coordinator.Enabled = false;
        harness.Engine.FinishAfterFinalChunk.TrySetResult();
        await harness.Ui.WaitForPendingAtLeastAsync(2);
        harness.Ui.DrainAll();

        Assert.Empty(harness.Metrics.Entries);
        Assert.Equal(0, harness.Overlay.ShowCount);
    }

    [Fact]
    public async Task RecordsExactlyOnceAfterPresentationUsingModelCapturedAtRequestStart()
    {
        await using var harness = new Harness(waitAfterFinalChunk: false);
        harness.Start("model-at-request-start");
        harness.Hook.EmitCharacter(harness.Ui);

        await harness.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Coordinator.RuntimeModelId = "replacement-model";
        harness.Engine.ReleaseOutput.TrySetResult();
        await harness.Ui.WaitForPendingAtLeastAsync(3);
        harness.Ui.DrainAll();

        var entry = Assert.Single(harness.Metrics.Entries);
        Assert.Equal("model-at-request-start", entry.ModelId);
        Assert.True(entry.ElapsedMilliseconds >= 0);
        Assert.Equal(1, harness.Overlay.ShowCount);
        Assert.Equal(1, harness.Overlay.UpdateCount);
    }

    [Fact]
    public async Task DisposalRejectsPresentationQueuedByAnAlreadyFinishedRequest()
    {
        await using var harness = new Harness(waitAfterFinalChunk: false);
        harness.Start("model");
        harness.Hook.EmitCharacter(harness.Ui);
        await harness.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Engine.ReleaseOutput.TrySetResult();
        await harness.Ui.WaitForPendingAtLeastAsync(4);
        // Await the real work task: disposing while it is still running would
        // merely test cancellation, not the completed-but-queued callback race.
        var pending = (Task)typeof(SuggestionWorkController).GetField("_pendingTask",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(harness.Work)!;
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Coordinator.DisposeAsync();
        harness.Ui.DrainAll();
        Assert.Equal(0, harness.Overlay.ShowCount);
        Assert.Empty(harness.Metrics.Entries);
    }

    [Fact]
    public async Task AcceptRefusesToInsertIntoASignInFieldFocusedAfterTheSuggestionShowed()
    {
        // Focus can move onto a sign-in field inside one process without the
        // pid-based cancellation firing, so the accept path must re-check the
        // live field instead of trusting the trigger-time decision.
        await using var harness = new Harness(waitAfterFinalChunk: false);
        harness.Start("model");
        harness.Hook.EmitCharacter(harness.Ui);
        await harness.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Engine.ReleaseOutput.TrySetResult();
        await harness.Ui.WaitForPendingAtLeastAsync(4);
        harness.Ui.DrainAll();
        Assert.Equal(1, harness.Overlay.ShowCount);

        harness.Focus.MakeCredentialField();
        harness.Hook.EmitAcceptTab(harness.Ui);
        await Task.Delay(50);
        harness.Ui.DrainAll();

        Assert.Equal(0, harness.Inserter.InsertCount);
    }

    [Fact]
    public async Task NoRequestIsMadeForAFieldAlreadyFlaggedAsSignIn()
    {
        await using var harness = new Harness(waitAfterFinalChunk: false);
        harness.Start("model");
        harness.Focus.MakeCredentialField();
        harness.Hook.EmitCharacter(harness.Ui);

        // The gate is upstream of the engine, so it must never even start.
        var started = await Task.WhenAny(harness.Engine.Started.Task, Task.Delay(300)) == harness.Engine.Started.Task;
        Assert.False(started);
        Assert.Equal(0, harness.Overlay.ShowCount);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public QueuedSynchronizationContext Ui { get; } = new();
        public ControlledSuggestionEngine Engine { get; }
        public PerformanceMetricsStore Metrics { get; } = new() { TrackingEnabled = true };
        public RecordingOverlay Overlay { get; } = new();
        public TestKeyboardHook Hook { get; } = new();
        public SuggestionWorkController Work { get; } = new(TimeSpan.FromMilliseconds(1));
        public TestFocusTracker Focus { get; } = new();
        public TestTextInserter Inserter { get; } = new();
        public SuggestionCoordinator Coordinator { get; }

        public Harness(bool waitAfterFinalChunk)
        {
            Engine = new ControlledSuggestionEngine(waitAfterFinalChunk);
            Coordinator = new SuggestionCoordinator(
                Hook,
                Focus,
                Engine,
                Overlay,
                Inserter,
                Work,
                NullLogger<SuggestionCoordinator>.Instance,
                Ui)
            {
                PerformanceMetrics = Metrics,
            };
        }

        public void Start(string modelId)
        {
            Coordinator.RuntimeModelId = modelId;
            Coordinator.Start();
        }

        public async ValueTask DisposeAsync() => await Coordinator.DisposeAsync();
    }

    private sealed class ControlledSuggestionEngine(bool waitAfterFinalChunk) : ISuggestionEngine
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOutput { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishAfterFinalChunk { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsReady => true;

        public async IAsyncEnumerable<SuggestionChunk> GenerateAsync(
            SuggestionRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Started.TrySetResult();
            await ReleaseOutput.Task.WaitAsync(ct).ConfigureAwait(false);

            if (waitAfterFinalChunk)
            {
                yield return new SuggestionChunk("ghost", IsFinal: true);
                await FinishAfterFinalChunk.Task.ConfigureAwait(false);
                yield break;
            }

            yield return new SuggestionChunk("hel", IsFinal: false);
            yield return new SuggestionChunk("lo", IsFinal: true);
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly object _gate = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
        private TaskCompletionSource _changed = NewSignal();

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
                await changed.WaitAsync(timeout.Token);
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

        private static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TestKeyboardHook : IKeyboardHook
    {
        public event EventHandler<KeyEventArgs>? KeyEvent;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }

        public void EmitAcceptTab(SynchronizationContext ui)
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(ui);
            try
            {
                KeyEvent?.Invoke(this, new KeyEventArgs(new KeyboardEvent
                {
                    Kind = KeyKind.Tab,
                    Character = '\0',
                    HasNonShiftModifier = false,
                    IsKeyDown = true,
                }));
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        public void EmitCharacter(SynchronizationContext ui)
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(ui);
            try
            {
                KeyEvent?.Invoke(this, new KeyEventArgs(new KeyboardEvent
                {
                    Kind = KeyKind.Character,
                    Character = 'a',
                    HasNonShiftModifier = false,
                    IsKeyDown = true,
                }));
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
            ProcessId = Environment.ProcessId + 1,
            ProcessName = "test-host",
            Text = "typed text",
            CaretOffset = 10,
            CaretRect = new ScreenRect(10, 20, 1, 20),
            FieldRect = new ScreenRect(0, 0, 100, 30),
            IsSingleLine = true,
            IsSecure = false,
            IsCredentialField = false,
        };

        /// <summary>Simulate focus moving onto a sign-in field within one process.</summary>
        public void MakeCredentialField() =>
            _field = _field with { IsCredentialField = true, Text = string.Empty };

        public FocusedField? Current => _field;
        public event EventHandler<FocusedField?>? FocusChanged
        {
            add { }
            remove { }
        }
        public FocusedField? Refresh() => _field;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class RecordingOverlay : IOverlayPresenter
    {
        public int ShowCount { get; private set; }
        public int UpdateCount { get; private set; }
        public void Show(string text, ScreenRect anchor) => ShowCount++;
        public void Update(string text) => UpdateCount++;
        public void Hide() { }
    }

    private sealed class TestTextInserter : ITextInserter
    {
        public int InsertCount { get; private set; }

        public Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct)
        {
            InsertCount++;
            return Task.FromResult(true);
        }

        public Task<bool> ReplaceTextAsync(FocusedField target, int charactersToReplace, string text, CancellationToken ct) =>
            Task.FromResult(false);
    }
}
