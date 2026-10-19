using Cotabby.Core.Settings;

namespace Cotabby.Core.Tests;

public sealed class AppProfileResolverTests
{
    [Fact]
    public void ProfileValidationRejectsPathsExeWildcardsInvalidEnumsAndOutOfRangeValues()
    {
        Assert.True(AppProfileResolver.IsValidProfile(new AppProfileOverride { ProcessName = "notepad" }));
        Assert.False(AppProfileResolver.IsValidProfile(new AppProfileOverride { ProcessName = "notepad.exe" }));
        Assert.False(AppProfileResolver.IsValidProfile(new AppProfileOverride { ProcessName = "C:\\tools\\notepad" }));
        Assert.False(AppProfileResolver.IsValidProfile(new AppProfileOverride { ProcessName = "note*" }));
        Assert.False(AppProfileResolver.IsValidProfile(new AppProfileOverride { ProcessName = "notepad", DisplayMode = "window" }));
        Assert.False(AppProfileResolver.IsValidProfile(new AppProfileOverride { ProcessName = "notepad", DebounceMs = 301 }));
        Assert.False(AppProfileResolver.IsValidProfile(new AppProfileOverride { ProcessName = "notepad", MaxTokensMultiLine = 129 }));
    }

    [Fact]
    public void MatchingProfile_OverridesAcceptanceMode()
    {
        var global = new EffectiveSuggestionSettings(true, "word", "long", "auto", true, 50, 25, 50);
        var profiles = new AppProfileOverride?[]
        {
            new() { ProcessName = "notepad", AcceptanceMode = "phrase" },
        };

        var resolved = AppProfileResolver.Resolve(global, profiles, "notepad", "cotabby");

        Assert.Equal("phrase", resolved.AcceptanceMode);
        Assert.Equal("long", resolved.CompletionLengthPreset);
        Assert.Equal(25, resolved.MaxTokensSingleLine);
    }

    [Fact]
    public void ExplicitTokenBudgetsOverrideTheProfilePresetPair()
    {
        var global = new EffectiveSuggestionSettings(true, "word", "long", "auto", true, 50, 25, 50);
        var profile = new AppProfileOverride
        {
            ProcessName = "notepad",
            CompletionLengthPreset = "short",
            MaxTokensSingleLine = 14,
            MaxTokensMultiLine = 22,
        };

        var resolved = AppProfileResolver.Resolve(global, [profile], "notepad", "cotabby");

        Assert.Equal("short", resolved.CompletionLengthPreset);
        Assert.Equal(14, resolved.MaxTokensSingleLine);
        Assert.Equal(22, resolved.MaxTokensMultiLine);
    }

    [Fact]
    public void EnabledProfileCannotOverrideGlobalDisable()
    {
        var global = new EffectiveSuggestionSettings(false, "word", "long", "auto", true, 50, 25, 50);
        var profile = new AppProfileOverride { ProcessName = "notepad", Enabled = true };

        var resolved = AppProfileResolver.Resolve(global, [profile], "notepad", "cotabby");

        Assert.False(resolved.Enabled);
    }

    [Fact]
    public void InvalidOverrideValuesAreIgnoredAsAWholeProfile()
    {
        var global = new EffectiveSuggestionSettings(true, "word", "long", "auto", true, 50, 25, 50);
        var profiles = new AppProfileOverride?[]
        {
            null,
            new() { ProcessName = "notepad", AcceptanceMode = "sentence", DebounceMs = 301 },
        };

        var resolved = AppProfileResolver.Resolve(global, profiles, "NOTEPAD", "cotabby");

        Assert.Equal(global, resolved);
    }

    [Fact]
    public void NoProfiles_ReturnsGlobalSnapshot()
    {
        var global = new EffectiveSuggestionSettings(
            Enabled: true,
            AcceptanceMode: "word",
            CompletionLengthPreset: "long",
            DisplayMode: "auto",
            AllowMultiLine: true,
            DebounceMs: 50,
            MaxTokensSingleLine: 25,
            MaxTokensMultiLine: 50);

        var resolved = AppProfileResolver.Resolve(global, null, "notepad", "cotabby");

        Assert.Equal(global, resolved);
    }

    [Fact]
    public void DuplicateCaseInsensitiveKeysFailClosedIndependentOfOrder()
    {
        var global = new EffectiveSuggestionSettings(true, "word", "long", "auto", true, 50, 25, 50);
        var first = new AppProfileOverride { ProcessName = "notepad", AcceptanceMode = "phrase" };
        var second = new AppProfileOverride { ProcessName = "NOTEPAD", AcceptanceMode = "word", DebounceMs = 120 };

        Assert.Equal(global, AppProfileResolver.Resolve(global, [first, second], "Notepad", "cotabby"));
        Assert.Equal(global, AppProfileResolver.Resolve(global, [second, first], "Notepad", "cotabby"));
    }
}
