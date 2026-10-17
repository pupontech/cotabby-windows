using Cotabby.Core.Suggestions;
using Cotabby.Core.Input;

namespace Cotabby.Core.Tests;

public sealed class SuggestionStatisticsTests
{
    [Fact]
    public void CountersAreOptInAtomicAndResettable()
    {
        var store = new SuggestionStatisticsStore();
        store.Record(SuggestionStatistic.Triggered);
        Assert.Equal(0, store.Snapshot()[SuggestionStatistic.Triggered]);
        store.Enabled = true;
        Parallel.For(0, 1000, _ => store.Record(SuggestionStatistic.Triggered));
        var snapshot = store.Snapshot();
        Assert.Equal(1000, snapshot[SuggestionStatistic.Triggered]);
        store.Clear();
        Assert.All(store.Snapshot().Values, count => Assert.Equal(0, count));
        Assert.Equal(1000, snapshot[SuggestionStatistic.Triggered]);
    }

    [Fact]
    public async Task CoordinatorCountsPresentedAndAcceptedWordOnce()
    {
        await using var rig = await SuggestionCoordinatorDoubleTapTests.Rig.CreateAsync(false, showSuggestion: false);
        var stats = new SuggestionStatisticsStore { Enabled = true };
        rig.Coordinator.Statistics = stats;
        rig.Engine.Release.TrySetResult();
        rig.Hook.Emit(Key(KeyKind.Character));
        await rig.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!rig.Overlay.IsVisible)
        {
            await rig.Ui.WaitForPendingAtLeastAsync(1).WaitAsync(timeout.Token);
            rig.Ui.DrainAll();
        }
        rig.Hook.Emit(Key(KeyKind.Tab));
        await rig.WaitForOutcomesAsync(1);
        var counts = stats.Snapshot();
        Assert.Equal(1, counts[SuggestionStatistic.Triggered]);
        Assert.Equal(1, counts[SuggestionStatistic.Requested]);
        Assert.Equal(1, counts[SuggestionStatistic.EngineFired]);
        Assert.Equal(1, counts[SuggestionStatistic.Presented]);
        Assert.Equal(1, counts[SuggestionStatistic.AcceptedWord]);
    }

    [Fact]
    public async Task CredentialSuppressionRecordsNoRequestOrContent()
    {
        await using var rig = await SuggestionCoordinatorDoubleTapTests.Rig.CreateAsync(false, showSuggestion: false);
        var stats = new SuggestionStatisticsStore { Enabled = true };
        rig.Coordinator.Statistics = stats;
        rig.Focus.MakeCredentialField();
        rig.Hook.Emit(Key(KeyKind.Character));
        rig.Ui.DrainAll();
        Assert.Equal(1, stats.Snapshot()[SuggestionStatistic.SuppressedCredential]);
        Assert.Equal(0, stats.Snapshot()[SuggestionStatistic.Requested]);
    }

    [Fact]
    public async Task RejectedInsertionIsNotAcceptanceAndCancellationIsCounted()
    {
        await using var rig = await SuggestionCoordinatorDoubleTapTests.Rig.CreateAsync(false);
        var stats = new SuggestionStatisticsStore { Enabled = true };
        rig.Coordinator.Statistics = stats;
        rig.Inserter.DelayNext().Release(false);
        rig.Hook.Emit(Key(KeyKind.Tab));
        await rig.WaitForOutcomesAsync(1);
        Assert.Equal(1, stats.Snapshot()[SuggestionStatistic.InsertionRejected]);
        Assert.Equal(0, stats.Snapshot()[SuggestionStatistic.AcceptedWord]);
        rig.Hook.Emit(Key(KeyKind.Escape));
        rig.Ui.DrainAll();
        Assert.Equal(1, stats.Snapshot()[SuggestionStatistic.Cancelled]);
    }

    private static KeyboardEvent Key(KeyKind kind) => new() {
        Kind = kind, Character = 'x', IsKeyDown = true, HasNonShiftModifier = false };
}
