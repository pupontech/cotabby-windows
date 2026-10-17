using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

public sealed class BatteryPolicyTests
{
    [Theory]
    [InlineData(true, 10, false, true, InferencePosture.Normal)]
    [InlineData(false, 60, false, true, InferencePosture.Throttled)]
    [InlineData(false, 39, false, true, InferencePosture.Deferred)]
    [InlineData(false, 10, true, true, InferencePosture.Normal)]
    [InlineData(false, 10, false, false, InferencePosture.Normal)]
    public void PowerAndOptInDeterminePosture(bool ac, int charge, bool charging, bool enabled, InferencePosture expected) =>
        Assert.Equal(expected, BatteryPolicy.Evaluate(new PowerSnapshot(ac, charge, charging), enabled, 40));

    [Fact]
    public void UnknownAndInvalidPowerNeverPause() =>
        Assert.Equal(InferencePosture.Normal, BatteryPolicy.Evaluate(default, true, 40));

    [Fact]
    public void DeferredPostureHasFivePercentHysteresis()
    {
        Assert.Equal(InferencePosture.Deferred, BatteryPolicy.Evaluate(new(false, 43, false), true, 40, InferencePosture.Deferred));
        Assert.Equal(InferencePosture.Throttled, BatteryPolicy.Evaluate(new(false, 45, false), true, 40, InferencePosture.Deferred));
    }

    [Theory]
    [InlineData(25, 15)]
    [InlineData(15, 9)]
    [InlineData(9, 5)]
    [InlineData(5, 5)]
    [InlineData(50, 30)]
    public void ThrottlingLowersOneBudgetTier(int tokens, int expected) =>
        Assert.Equal(expected, BatteryPolicy.TokenBudget(tokens, InferencePosture.Throttled));

    [Fact]
    public async Task DeferredAdmissionResumesOnAcWithoutReplacingCoordinator()
    {
        await using var rig = await SuggestionCoordinatorDoubleTapTests.Rig.CreateAsync(false, showSuggestion: false);
        var provider = new MutablePower { Snapshot = new(false, 10, false) };
        rig.Coordinator.PowerProvider = provider;
        rig.Coordinator.BatteryAwareInference = true;
        var key = new Cotabby.Core.Input.KeyboardEvent { Kind = Cotabby.Core.Input.KeyKind.Character, Character = 'x', IsKeyDown = true, HasNonShiftModifier = false };
        rig.Hook.Emit(key);
        rig.Ui.DrainAll();
        Assert.Equal(InferencePosture.Deferred, rig.Coordinator.BatteryPosture);
        Assert.False(rig.Engine.Started.Task.IsCompleted);
        provider.Snapshot = new(true, 10, true);
        rig.Hook.Emit(key);
        await rig.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Engine.Release.TrySetResult();
        Assert.Equal(InferencePosture.Normal, rig.Coordinator.BatteryPosture);
    }

    [Fact]
    public async Task ThrottledCoordinatorPassesReducedBudgetToRealEngineBoundary()
    {
        await using var rig = await SuggestionCoordinatorDoubleTapTests.Rig.CreateAsync(false, showSuggestion: false);
        rig.Coordinator.PowerProvider = new MutablePower { Snapshot = new(false, 70, false) };
        rig.Coordinator.BatteryAwareInference = true;
        rig.Coordinator.MaxTokensSingleLine = 25;
        rig.Hook.Emit(new Cotabby.Core.Input.KeyboardEvent {
            Kind = Cotabby.Core.Input.KeyKind.Character, Character = 'x', IsKeyDown = true, HasNonShiftModifier = false });
        await rig.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(15, rig.Engine.LastRequest!.MaxTokens);
        rig.Engine.Release.TrySetResult();
    }

    private sealed class MutablePower : IPowerSnapshotProvider
    {
        public PowerSnapshot Snapshot { get; set; }
    }

    [Fact]
    public void NativePowerStructUsesWindowsTwelveByteLayout()
    {
        Type native = typeof(Cotabby.App.Power.LowPowerModeMonitor).GetNestedType("SYSTEM_POWER_STATUS", System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(12, System.Runtime.InteropServices.Marshal.SizeOf(native));
        Assert.Equal(new IntPtr(3), System.Runtime.InteropServices.Marshal.OffsetOf(native, "SystemStatusFlag"));
    }

    [Fact]
    public void ThrottlingDoublesDebounce() =>
        Assert.Equal(TimeSpan.FromMilliseconds(100), BatteryPolicy.Debounce(TimeSpan.FromMilliseconds(50), InferencePosture.Throttled));
}
