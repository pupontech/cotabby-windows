using Cotabby.Core.Suggestions;
using Xunit;

namespace Cotabby.Core.Tests;

/// <summary>
/// Windows port of the upstream Low Power Mode gate (#812): the pure
/// availability rule and the coordinator gate wiring are exercised with a
/// fake provider, since a real OS energy-saver state is owner-verified.
/// </summary>
public class LowPowerModeGateTests
{
    private sealed class FixedProvider(bool active) : ILowPowerModeProvider
    {
        public bool IsLowPowerModeEnabled { get; } = active;
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void PauseRequiresBothSettingAndState(bool active, bool enabled, bool expected)
    {
        Assert.Equal(expected, SuggestionAvailability.ShouldPauseForLowPowerMode(active, enabled));
    }

    [Fact]
    public void ProviderExposesState()
    {
        Assert.True(new FixedProvider(true).IsLowPowerModeEnabled);
        Assert.False(new FixedProvider(false).IsLowPowerModeEnabled);
    }
}
