using Cotabby.App.Settings;
using Cotabby.Core.Settings;

namespace Cotabby.Core.Tests;

public sealed class AppProfileSettingsMapperTests
{
    [Fact]
    public void MapsGlobalDefaultsAndNullableProfileOverridesToCoreDtos()
    {
        var settings = new AppSettings
        {
            Enabled = false,
            AcceptanceMode = "phrase",
            CompletionLengthPreset = "medium",
            DisplayMode = "popup",
            AllowMultiLine = false,
            DebounceMs = 90,
            AppProfiles = new List<AppProfile?>
            {
                null,
                new() { ProcessName = "notepad", Enabled = true, MaxTokensSingleLine = 18 },
            },
        };

        var mapped = AppProfileSettingsMapper.Map(settings);

        Assert.Equal(new EffectiveSuggestionSettings(false, "phrase", "medium", "popup", false, 90, 15, 30), mapped.Global);
        Assert.Null(mapped.Profiles![0]);
        Assert.Equal(new AppProfileOverride { ProcessName = "notepad", Enabled = true, MaxTokensSingleLine = 18 }, mapped.Profiles[1]);
    }

    [Fact]
    public void NullDeserializedProfileListMapsSafely()
    {
        var mapped = AppProfileSettingsMapper.Map(new AppSettings { AppProfiles = null });

        Assert.Null(mapped.Profiles);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(301, 300)]
    public void GlobalDebounceUsesTheRuntimeClamp(int configured, int expected)
    {
        var mapped = AppProfileSettingsMapper.Map(new AppSettings { DebounceMs = configured });

        Assert.Equal(expected, mapped.Global.DebounceMs);
    }
}
