using Cotabby.Core.Input;
using Cotabby.Core.Suggestions;
using Xunit;

namespace Cotabby.Core.Tests;

/// <summary>
/// Ports of upstream #838's rapid-Tab acceptance case: while Cotabby's own
/// next presentation is still in flight, a second Tab must accept the tail the
/// app is about to paint rather than tear the session down. The Windows
/// equivalent of the macOS overlay controller's held present is the
/// coordinator's in-flight presentation mark; this test drives the real
/// double-tap rig (shared with SuggestionCoordinatorDoubleTapTests) to prove
/// the rapid pair survives the commit path end to end.
/// </summary>
public class RapidTabAcceptanceTests
{
    [Fact]
    public async Task SecondTabInsideHeldPresentationWindowAcceptsTheTail()
    {
        await using var rig = await SuggestionCoordinatorDoubleTapTests.Rig.CreateAsync(doubleTapEnabled: true);

        // First accept: word lands, remaining tail stays visible.
        Assert.True(rig.Hook.Emit(Tab()));
        await rig.WaitForOutcomesAsync(1);
        rig.Ui.DrainAll();
        Assert.Equal(new[] { "world" }, rig.Inserter.Inserted);

        // While the replaced tail present is still in flight, a rapid second
        // Tab must accept the remainder instead of being refused and leaked.
        rig.Clock.Now += 0.1;
        Assert.True(rig.Hook.Emit(Tab()));
        await rig.WaitForOutcomesAsync(2);
        rig.Ui.DrainAll();

        Assert.Equal(new[] { "world", " again tomorrow today" }, rig.Inserter.Inserted);
        Assert.Equal(AcceptanceIntentStatus.StateCommitted, rig.Outcomes.Last().Status);
    }

    [Fact]
    public async Task ThirdRapidTabAfterExhaustionIsNotCaptured()
    {
        await using var rig = await SuggestionCoordinatorDoubleTapTests.Rig.CreateAsync(doubleTapEnabled: true);

        // Accept the whole suggestion via double-tap; afterwards nothing is
        // left for a third rapid press to belong to.
        Assert.True(rig.Hook.Emit(Tab()));
        await rig.WaitForOutcomesAsync(1);
        rig.Ui.DrainAll();
        rig.Clock.Now += 0.1;
        Assert.True(rig.Hook.Emit(Tab()));
        await rig.WaitForOutcomesAsync(2);
        rig.Ui.DrainAll();
        Assert.Null(rig.Overlay.Text);

        // No suggestion remains, so this Tab belongs to the host.
        rig.Clock.Now += 0.1;
        Assert.False(rig.Hook.Emit(Tab()));
        Assert.Equal(new[] { "world", " again tomorrow today" }, rig.Inserter.Inserted);
        Assert.Equal(AcceptanceIntentStatus.StateCommitted, rig.Outcomes.Last().Status);
    }

    private static KeyboardEvent Tab(bool isAutorepeat = false) => new()
    {
        Kind = KeyKind.Tab,
        Character = '\0',
        HasNonShiftModifier = false,
        HasShiftModifier = false,
        IsKeyDown = true,
        IsAutorepeat = isAutorepeat,
    };
}
