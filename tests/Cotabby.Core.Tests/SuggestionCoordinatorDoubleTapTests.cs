using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Cotabby.Core.Overlay;
using Cotabby.Core.Suggestions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cotabby.Core.Tests;

public sealed class SuggestionCoordinatorDoubleTapTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyDiagnosticsNeverRecordTypedCharacterCodes(bool credential)
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true, showSuggestion: false);
        if (credential) rig.Focus.MakeCredentialField();
        rig.Logger.Messages.Clear();

        Assert.False(rig.Hook.Emit(Character('S')));

        Assert.DoesNotContain(rig.Logger.Messages, entry => entry.Message.Contains("char=", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("credential")]
    [InlineData("other-field")]
    [InlineData("other-process")]
    public async Task ChangedFocusBeforeFirstChunkDoesNotPresentPriorFieldSuggestion(string change)
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true, showSuggestion: false);
        rig.Hook.Emit(Character('x'));
        await rig.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (change == "credential") rig.Focus.MakeCredentialField();
        else if (change == "other-field") rig.Focus.MoveToAnotherField();
        else rig.Focus.MoveToAnotherProcess();
        rig.Engine.Release.TrySetResult();
        await rig.Ui.WaitForPendingAtLeastAsync(1);
        rig.Ui.DrainAll();
        Assert.False(rig.Overlay.IsVisible);
        Assert.False(rig.Hook.Emit(Tab()));
        Assert.Empty(rig.Inserter.Inserted);
    }

    [Fact]
    public async Task RefusedCapturedKeyIsLoggedWithoutAnyDispositionSubscriber()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);
        rig.Coordinator.AcceptanceIntentCompleted -= rig.Outcomes.Enqueue;
        rig.Logger.Messages.Clear();
        rig.Inserter.DelayNext().Release(false);

        Assert.True(rig.Hook.Emit(Tab()));

        var entry = Assert.Single(rig.Logger.Messages, m => m.Message.StartsWith("Captured acceptance key not replayed"));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("InsertionRejected", entry.Message);
        Assert.DoesNotContain("Hello", entry.Message);
        Assert.DoesNotContain("world", entry.Message);
    }

    [Fact]
    public async Task QuickDistinctSecondTabAcceptsOnlyTheRemainingSuggestion()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);

        Assert.True(rig.Hook.Emit(Tab()));
        Assert.Equal(new[] { "world" }, rig.Inserter.Inserted);
        Assert.Equal(" again tomorrow today", rig.Overlay.Text);

        rig.Clock.Now += 0.1;
        Assert.True(rig.Hook.Emit(Tab()));

        Assert.Equal(new[] { "world", " again tomorrow today" }, rig.Inserter.Inserted);
        Assert.Equal("Hello world again tomorrow today", rig.Focus.HostText);
        Assert.False(rig.Overlay.IsVisible);
    }

    [Fact]
    public async Task PromotedTailUsesKnownInsertedTextWhenUiaSnapshotIsStale()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);
        rig.Focus.KeepSnapshotStale = true;

        Assert.True(rig.Hook.Emit(Tab()));
        rig.Clock.Now += 0.1;
        Assert.True(rig.Hook.Emit(Tab()));

        Assert.Equal("Hello world again tomorrow today", rig.Focus.HostText);
        Assert.Equal("Hello ", rig.Focus.Refresh()!.Text);
    }

    [Fact]
    public async Task AutoRepeatResetsPendingPairWithoutPromotingTail()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);

        Assert.True(rig.Hook.Emit(Tab()));
        rig.Clock.Now += 0.05;
        Assert.True(rig.Hook.Emit(Tab(isAutorepeat: true)));
        rig.Clock.Now += 0.05;
        Assert.True(rig.Hook.Emit(Tab()));

        Assert.Equal(new[] { "world", " again", " tomorrow" }, rig.Inserter.Inserted);
        Assert.Equal(" today", rig.Overlay.Text);
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

        Assert.Equal(new[] { "world", " again" }, rig.Inserter.Inserted);
        Assert.Equal(" tomorrow today", rig.Overlay.Text);
    }

    [Fact]
    public async Task CredentialRejectionCancelsPendingPairWithoutFurtherInsertion()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);

        Assert.True(rig.Hook.Emit(Tab()));
        rig.Focus.MakeCredentialField();
        rig.Clock.Now += 0.1;
        Assert.False(rig.Hook.Emit(Tab()));

        Assert.Equal(new[] { "world" }, rig.Inserter.Inserted);
        Assert.False(rig.Overlay.IsVisible);
    }

    [Fact]
    public async Task FocusTransitionToAnotherProcessCancelsBeforeInsertion()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);

        rig.Focus.MoveToAnotherProcess();
        Assert.False(rig.Hook.Emit(Tab()));

        Assert.Empty(rig.Inserter.Inserted);
        Assert.False(rig.Overlay.IsVisible);
    }

    [Fact]
    public async Task SameProcessFocusChangePassesTabThroughWithoutWrongFieldInsertion()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);

        rig.Focus.MoveToAnotherField();
        Assert.False(rig.Hook.Emit(Tab()));

        Assert.Empty(rig.Inserter.Inserted);
        Assert.False(rig.Overlay.IsVisible);
    }

    [Fact]
    public async Task NullFocusPassesTabThroughWithoutInsertion()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);

        rig.Focus.ClearField();
        Assert.False(rig.Hook.Emit(Tab()));

        Assert.Empty(rig.Inserter.Inserted);
        Assert.False(rig.Overlay.IsVisible);
    }

    [Fact]
    public async Task ModifiedTabIsNeverSuppressedOrAccepted()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);

        Assert.False(rig.Hook.Emit(Tab() with { HasNonShiftModifier = true }));

        Assert.Empty(rig.Inserter.Inserted);
        Assert.Equal("world again tomorrow today", rig.Overlay.Text);
    }

    [Fact]
    public async Task TwoQueuedPhysicalTabsAcceptHeadAndRemainingTailExactlyOnce()
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

        await rig.WaitForOutcomesAsync(2);
        Assert.Equal(new[] { "world", " again tomorrow today" }, rig.Inserter.Inserted);
        Assert.Equal("Hello world again tomorrow today", rig.Focus.HostText);
        Assert.False(rig.Overlay.IsVisible);
        var outcomes = rig.Outcomes.ToArray();
        Assert.Equal(2, outcomes.Length);
        Assert.Equal(2, outcomes.Select(outcome => outcome.IntentSequence).Distinct().Count());
        Assert.All(outcomes, outcome => Assert.Equal(AcceptanceIntentStatus.StateCommitted, outcome.Status));
        Assert.All(outcomes, outcome => Assert.Equal(AcceptanceKeyDisposition.ConsumedBySuggestion, outcome.KeyDisposition));
    }

    [Fact]
    public async Task DelayedFalseHeadDoesNotAdvanceOrArmAndQueuedTailIsNotInserted()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);
        var head = rig.Inserter.DelayNext();

        Assert.True(rig.Hook.Emit(Tab()));
        await head.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Clock.Now += 0.1;
        Assert.True(rig.Hook.Emit(Tab()));
        Assert.Equal(new[] { "world" }, rig.Inserter.Attempts);

        head.Release(false);
        await head.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rig.WaitForOutcomesAsync(2);
        Assert.Equal("world again tomorrow today", rig.Overlay.Text);
        Assert.Equal("Hello ", rig.Focus.HostText);
        Assert.Equal(new[] { "world" }, rig.Inserter.Attempts);
        Assert.Equal(new[] { AcceptanceIntentStatus.InsertionRejected, AcceptanceIntentStatus.BlockedByEarlierFailure },
            rig.Outcomes.Select(outcome => outcome.Status));
        Assert.All(rig.Outcomes, outcome => Assert.Equal(AcceptanceKeyDisposition.ReplayRefusedIncompleteKeyMetadata, outcome.KeyDisposition));

        rig.Clock.Now += 0.1;
        Assert.True(rig.Hook.Emit(Tab()));
        await rig.WaitForOutcomesAsync(3);
        Assert.Equal(new[] { "world", "world" }, rig.Inserter.Attempts);
        Assert.Equal("Hello world", rig.Focus.HostText);
        Assert.Equal(" again tomorrow today", rig.Overlay.Text);
        Assert.Equal(AcceptanceIntentStatus.StateCommitted, rig.Outcomes.Last().Status);
    }

    [Fact]
    public async Task DelayedThrowingHeadDoesNotAdvanceOrArmAndQueuedTailIsNotInserted()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);
        var head = rig.Inserter.DelayNext();

        Assert.True(rig.Hook.Emit(Tab()));
        await head.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Clock.Now += 0.1;
        Assert.True(rig.Hook.Emit(Tab()));
        Assert.Equal(new[] { "world" }, rig.Inserter.Attempts);

        head.Throw(new InvalidOperationException("controlled insertion failure"));
        await head.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rig.WaitForOutcomesAsync(2);
        Assert.Equal("world again tomorrow today", rig.Overlay.Text);
        Assert.Equal("Hello ", rig.Focus.HostText);
        Assert.Equal(new[] { "world" }, rig.Inserter.Attempts);
        Assert.Equal(new[] { AcceptanceIntentStatus.InsertionFaulted, AcceptanceIntentStatus.BlockedByEarlierFailure },
            rig.Outcomes.Select(outcome => outcome.Status));
        Assert.All(rig.Outcomes, outcome => Assert.Equal(AcceptanceKeyDisposition.ReplayRefusedIncompleteKeyMetadata, outcome.KeyDisposition));

        rig.Clock.Now += 0.1;
        Assert.True(rig.Hook.Emit(Tab()));
        await rig.WaitForOutcomesAsync(3);
        Assert.Equal(new[] { "world", "world" }, rig.Inserter.Attempts);
        Assert.Equal("Hello world", rig.Focus.HostText);
        Assert.Equal(" again tomorrow today", rig.Overlay.Text);
        Assert.Equal(AcceptanceIntentStatus.StateCommitted, rig.Outcomes.Last().Status);
    }

    [Fact]
    public async Task DisposalCancelsInFlightHeadAndNeverStartsQueuedTail()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);
        var head = rig.Inserter.DelayNext();

        Assert.True(rig.Hook.Emit(Tab()));
        await head.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await head.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Clock.Now += 0.1;
        Assert.True(rig.Hook.Emit(Tab()));

        await rig.Coordinator.DisposeAsync();
        await head.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await head.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rig.WaitForOutcomesAsync(2);

        Assert.Equal(new[] { "world" }, rig.Inserter.Attempts);
        Assert.Empty(rig.Inserter.Inserted);
        Assert.All(rig.Outcomes, outcome => Assert.Equal(AcceptanceIntentStatus.CoordinatorDisposed, outcome.Status));
        Assert.All(rig.Outcomes, outcome => Assert.Equal(AcceptanceKeyDisposition.ReplayRefusedIncompleteKeyMetadata, outcome.KeyDisposition));
    }

    [Fact]
    public async Task GeometryOnlyAnchorRefreshDoesNotInvalidateConfirmedInsertion()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);

        Assert.True(rig.Hook.Emit(Tab()));
        Assert.Equal(new[] { "world" }, rig.Inserter.Inserted);
        rig.Clock.Now += 0.1;
        var tail = rig.Inserter.DelayNext();
        rig.Focus.SetCaretRect(new ScreenRect(200, 300, 1, 20));

        Assert.True(rig.Hook.Emit(Tab()));
        await tail.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rig.Ui.WaitForPendingAtLeastAsync(1);
        rig.Ui.DrainAll();
        tail.Release(true);
        await tail.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rig.WaitForOutcomesAsync(2);

        Assert.Equal("Hello world again tomorrow today", rig.Focus.HostText);
        Assert.False(rig.Overlay.IsVisible);
    }

    [Fact]
    public async Task QueuedTabsUsePhysicalEventGapInsteadOfDispatchTime()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);
        var pendingBefore = rig.Ui.PendingCount;

        Assert.True(await Task.Run(() => rig.Hook.Emit(Tab(), useUiContext: false)));
        rig.Clock.Now += 0.5;
        Assert.True(await Task.Run(() => rig.Hook.Emit(Tab(), useUiContext: false)));
        await rig.Ui.WaitForPendingAtLeastAsync(pendingBefore + 2);
        rig.Ui.DrainAll();

        Assert.Equal(new[] { "world", " again" }, rig.Inserter.Inserted);
        Assert.Equal(" tomorrow today", rig.Overlay.Text);
        Assert.Equal("Hello world again", rig.Focus.HostText);
    }

    [Fact]
    public async Task TabWithoutAnOwnedVisibleSuggestionIsNotSwallowed()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true, showSuggestion: false);

        Assert.False(rig.Hook.Emit(Tab()));
        Assert.Empty(rig.Inserter.Inserted);
    }

    [Fact]
    public async Task ReconcilerConsumedKeyDuringHeldInsertReanchorsInsteadOfReportingRefusal()
    {
        // Double-tap promotion stays off so the second press is an ordinary word
        // accept and the assertions isolate the re-anchored remainder.
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: false);
        var head = rig.Inserter.DelayNext();

        Assert.True(rig.Hook.Emit(Tab()));
        await head.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A reconciler-consumed key moves the base text and the consumed prefix
        // while the insertion is still held in the inserter.
        Assert.False(rig.Hook.Emit(Character('w')));

        head.Release(true);
        await head.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rig.WaitForOutcomesAsync(1);

        var landed = rig.Outcomes.Single();
        Assert.Equal(new[] { "world" }, rig.Inserter.Inserted);
        Assert.Equal(AcceptanceIntentStatus.InsertedNotCommitted, landed.Status);
        Assert.Equal(AcceptanceKeyDisposition.ConsumedBySuggestion, landed.KeyDisposition);

        rig.Clock.Now += 0.1;
        Assert.True(rig.Hook.Emit(Tab()));
        await rig.WaitForOutcomesAsync(2);

        Assert.Equal(new[] { "world", " again" }, rig.Inserter.Inserted);
        Assert.Equal(" tomorrow today", rig.Overlay.Text);
        Assert.Equal(AcceptanceIntentStatus.StateCommitted, rig.Outcomes.Last().Status);
    }

    [Fact]
    public async Task QueueProcessorFaultLeavesQueueDrainableAndProducesOutcomes()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: false);
        var head = rig.Inserter.DelayNext();

        Assert.True(rig.Hook.Emit(Tab()));
        await head.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Queue a second intent behind the in-flight head. It is posted to the UI
        // context, so drain once to enqueue it (the processor is busy on the head).
        rig.Clock.Now += 0.1;
        Assert.True(rig.Hook.Emit(Tab(), useUiContext: false));
        rig.Ui.DrainAll();

        // The head's insertion is about to land and its commit post will call
        // overlay.Update, which throws; the faulting logger then turns the
        // processor's own error log into a second throw that escapes the loop.
        rig.Overlay.ThrowOnUpdate = true;
        rig.Logger.ThrowOn = message => message.Contains("Accept intent processing failed");

        head.Release(true);
        await head.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rig.WaitForOutcomesAsync(2);

        // The insertion landed, but the commit post faulted: both the in-flight
        // head and the still-queued tail are completed with a fault status instead
        // of being dropped.
        Assert.Equal(new[] { "world" }, rig.Inserter.Inserted);
        Assert.Equal(new[]
        {
            AcceptanceIntentStatus.QueueFaulted,
            AcceptanceIntentStatus.QueueFaulted,
        }, rig.Outcomes.Select(outcome => outcome.Status));
        Assert.Equal(2, rig.Outcomes.Select(outcome => outcome.IntentSequence).Distinct().Count());

        // The queue must be recoverable: a later Tab is accepted again.
        rig.Overlay.ThrowOnUpdate = false;
        rig.Logger.ThrowOn = null;
        rig.Clock.Now += 0.1;
        Assert.True(rig.Hook.Emit(Tab()));
        await rig.WaitForOutcomesAsync(3);

        Assert.Equal(new[] { "world", " again" }, rig.Inserter.Inserted);
        Assert.Equal(AcceptanceIntentStatus.StateCommitted, rig.Outcomes.Last().Status);
    }

    [Fact]
    public async Task RetiringProcessorDoesNotDrainAReentrantReplacementQueue()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: true);
        var failedHead = rig.Inserter.DelayNext();
        var recoveryHead = rig.Inserter.DelayNext();
        var replacementQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var suppressions = new ConcurrentQueue<bool>();
        rig.Coordinator.AcceptanceIntentCompleted += outcome =>
        {
            if (outcome.Status != AcceptanceIntentStatus.BlockedByEarlierFailure) return;
            // Completion observers may enqueue new work after the failed queue
            // releases ownership, but before that old processor's finally runs.
            suppressions.Enqueue(rig.Hook.Emit(Tab()));
            suppressions.Enqueue(rig.Hook.Emit(Tab()));
            replacementQueued.TrySetResult();
        };
        Assert.True(rig.Hook.Emit(Tab()));
        Assert.True(rig.Hook.Emit(Tab()));
        failedHead.Release(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!replacementQueued.Task.IsCompleted)
        {
            rig.Ui.DrainAll();
            await Task.Delay(5, timeout.Token);
        }
        // Let the failed processor retire before the replacement insertion lands.
        await Task.Delay(30);
        recoveryHead.Release(true);
        await rig.WaitForOutcomesAsync(4);
        Assert.Equal(new[] { true, true }, suppressions);
        Assert.DoesNotContain(rig.Outcomes, outcome => outcome.Status == AcceptanceIntentStatus.QueueFaulted);
        Assert.Equal("Hello world again tomorrow today", rig.Focus.HostText);
    }

    [Fact]
    public async Task DisposalCompletesDrainedQueuedIntentsExactlyOnce()
    {
        await using var rig = await Rig.CreateAsync(doubleTapEnabled: false);
        var head = rig.Inserter.DelayNext();

        Assert.True(rig.Hook.Emit(Tab()));
        await head.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Clock.Now += 0.1;
        Assert.True(rig.Hook.Emit(Tab()));

        await rig.Coordinator.DisposeAsync();
        await head.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rig.WaitForOutcomesAsync(2);

        var outcomes = rig.Outcomes.ToArray();
        Assert.Equal(2, outcomes.Length);
        Assert.Equal(2, outcomes.Select(outcome => outcome.IntentSequence).Distinct().Count());
        Assert.All(outcomes, outcome => Assert.Equal(AcceptanceIntentStatus.CoordinatorDisposed, outcome.Status));
    }

    private static KeyboardEvent Tab(bool isAutorepeat = false) => new()
    {
        Kind = KeyKind.Tab,
        Character = '\0',
        HasNonShiftModifier = false,
        IsKeyDown = true,
        IsAutorepeat = isAutorepeat,
        ForegroundProcessId = Environment.ProcessId + 100,
        ForegroundWindowHandle = 1234,
    };

    private static KeyboardEvent Character(char value) => new()
    {
        Kind = KeyKind.Character,
        Character = value,
        HasNonShiftModifier = false,
        IsKeyDown = true,
        ForegroundProcessId = Environment.ProcessId + 100,
        ForegroundWindowHandle = 1234,
    };

    internal sealed class ManualClock
    {
        public double Now { get; set; } = 100;
    }

    /// <summary>Logger that forwards every entry and can be armed to throw on a
    /// chosen message, so a test can drive a fault out of the acceptance queue
    /// processor's own error logging.</summary>
    internal sealed class FaultingLogger : ILogger<SuggestionCoordinator>
    {
        private Func<string, bool>? _throwOn;
        public ConcurrentQueue<(LogLevel Level, string Message)> Messages { get; } = new();

        public Func<string, bool>? ThrowOn
        {
            get => _throwOn;
            set => _throwOn = value;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue((logLevel, formatter(state, exception)));
            if (_throwOn is { } predicate && predicate(formatter(state, exception)))
            {
                throw new InvalidOperationException("controlled logger failure");
            }
        }
    }

    internal sealed class Rig : IAsyncDisposable
    {
        public QueuedSynchronizationContext Ui { get; } = new();
        public TestKeyboardHook Hook { get; } = new();
        public TestFocusTracker Focus { get; } = new();
        public TestOverlay Overlay { get; } = new();
        public TestTextInserter Inserter { get; }
        public SuggestionWorkController Work { get; } = new(TimeSpan.Zero);
        public TestSuggestionEngine Engine { get; } = new();
        public ManualClock Clock { get; } = new();
        public ConcurrentQueue<AcceptanceIntentOutcome> Outcomes { get; } = new();
        public FaultingLogger Logger { get; } = new();
        public SuggestionCoordinator Coordinator { get; }

        private Rig(bool doubleTapEnabled)
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
                Logger,
                Ui,
                doubleTapUptimeProvider: () => Clock.Now)
            {
                DoubleTapFullAcceptanceEnabled = doubleTapEnabled,
            };
            Coordinator.AcceptanceIntentCompleted += Outcomes.Enqueue;
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

    internal sealed class TestSuggestionEngine : ISuggestionEngine
    {
        private int _generationCount;
        public SuggestionRequest? LastRequest { get; private set; }
        public int GenerationCount => Volatile.Read(ref _generationCount);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsReady => true;

        public async IAsyncEnumerable<SuggestionChunk> GenerateAsync(
            SuggestionRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref _generationCount);
            LastRequest = request;
            Started.TrySetResult();
            using var registration = ct.Register(() => CancellationObserved.TrySetResult());
            await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            yield return new SuggestionChunk("world again tomorrow today", IsFinal: true);
        }
    }

    internal sealed class QueuedSynchronizationContext : SynchronizationContext
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

    internal sealed class TestKeyboardHook : IKeyboardHook
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

    internal sealed class TestFocusTracker : IFocusTracker
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

        public bool KeepSnapshotStale { get; set; }
        public string HostText { get; private set; } = "Hello ";

        public FocusedField? Current => _field;
        public event EventHandler<FocusedField?>? FocusChanged;
        public void SetField(FocusedField field)
        {
            _field = field;
            FocusChanged?.Invoke(this, field);
        }
        public FocusedField? Refresh() => _field is null
            ? null
            : _field with { Text = KeepSnapshotStale ? "Hello " : HostText };
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void MakeCredentialField() => _field = _field with { IsCredentialField = true };
        public void MoveToAnotherProcess() => _field = _field with { ProcessId = _field.ProcessId + 1 };
        public void MoveToAnotherField() => _field = _field with { StableFieldIdentity = "other-field" };
        public void SetCaretRect(ScreenRect rect) => _field = _field with { CaretRect = rect };
        public void ClearField() => _field = null!;
        public void ApplyInsertion(string text) => HostText += text;
    }

    internal sealed class TestOverlay : IOverlayPresenter, IOverlayDisplayMode
    {
        public string? Text { get; private set; }
        public bool IsVisible => Text is not null;
        public string DisplayMode { get; private set; } = "auto";
        /// <summary>When set, the next Update() call (used by the commit post to
        /// reflect the remaining tail) throws, to drive a fault through the
        /// acceptance queue processor.</summary>
        public bool ThrowOnUpdate { get; set; }
        public void Show(string text, ScreenRect anchor)
        {
            if (ThrowOnUpdate) throw new InvalidOperationException("controlled overlay failure");
            Text = text;
        }
        public void Update(string text)
        {
            if (ThrowOnUpdate) throw new InvalidOperationException("controlled overlay failure");
            Text = text;
        }
        public void Hide() => Text = null;
        public void SetDisplayMode(string displayMode) => DisplayMode = displayMode;
    }

    internal sealed class TestTextInserter : ITextInserter
    {
        private readonly TestFocusTracker _focus;
        private readonly object _gate = new();
        private readonly Queue<InsertGate> _plans = new();
        private readonly List<string> _attempts = new();
        private readonly List<string> _inserted = new();

        public TestTextInserter(TestFocusTracker focus) => _focus = focus;

        public string[] Attempts { get { lock (_gate) return _attempts.ToArray(); } }
        public string[] Inserted { get { lock (_gate) return _inserted.ToArray(); } }

        public InsertGate DelayNext()
        {
            var plan = new InsertGate();
            lock (_gate) _plans.Enqueue(plan);
            return plan;
        }

        public async Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct)
        {
            InsertGate? plan;
            lock (_gate)
            {
                _attempts.Add(text);
                plan = _plans.Count == 0 ? null : _plans.Dequeue();
            }

            try
            {
                bool inserted;
                if (plan is null)
                {
                    inserted = true;
                }
                else
                {
                    plan.Started.TrySetResult();
                    using var registration = ct.Register(() => plan.CancellationObserved.TrySetResult());
                    plan.Registered.TrySetResult();
                    try
                    {
                        inserted = await plan.Result.Task.WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Observe the cancellation from the throw itself: the token
                        // callback races the registration window, the throw does not.
                        plan.CancellationObserved.TrySetResult();
                        throw;
                    }
                }

                if (inserted)
                {
                    lock (_gate) _inserted.Add(text);
                    _focus.ApplyInsertion(text);
                }
                return inserted;
            }
            finally
            {
                plan?.Finished.TrySetResult();
            }
        }
    }

    internal sealed class InsertGate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>
        /// Set once the cancellation registration is actually installed. Without
        /// this the disposal test races the window between "insert started" and
        /// "cancel callback wired", and a cancellation delivered inside that
        /// window is never observed even though the insert itself was cancelled.
        /// </summary>
        public TaskCompletionSource Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release(bool inserted) => Result.TrySetResult(inserted);
        public void Throw(Exception exception) => Result.TrySetException(exception);
    }
}
