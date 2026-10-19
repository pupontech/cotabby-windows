using Cotabby.Core.Settings;

namespace Cotabby.App.Settings;

/// <summary>Maps persisted App settings into platform-independent Core settings DTOs.</summary>
public static class AppProfileSettingsMapper
{
    public static (EffectiveSuggestionSettings Global, IReadOnlyList<AppProfileOverride?>? Profiles) Map(
        AppSettings settings)
    {
        var preset = CompletionLengthPreset.All.Any(item => item.Id == settings.CompletionLengthPreset)
            ? settings.CompletionLengthPreset
            : CompletionLengthPreset.Long;
        var acceptance = string.Equals(settings.AcceptanceMode, "phrase", StringComparison.OrdinalIgnoreCase)
            ? "phrase"
            : "word";
        var display = settings.DisplayMode is "auto" or "inline" or "popup"
            ? settings.DisplayMode
            : "auto";
        var (single, multi) = CompletionLengthPreset.Tokens(preset);
        var global = new EffectiveSuggestionSettings(
            settings.Enabled,
            acceptance,
            preset,
            display,
            settings.AllowMultiLine,
            Math.Clamp(settings.DebounceMs, 20, 300),
            single,
            multi);
        var profiles = settings.AppProfiles?.Select(profile => profile is null
            ? null
            : new AppProfileOverride
            {
                ProcessName = profile.ProcessName,
                Enabled = profile.Enabled,
                AcceptanceMode = profile.AcceptanceMode,
                CompletionLengthPreset = profile.CompletionLengthPreset,
                DisplayMode = profile.DisplayMode,
                AllowMultiLine = profile.AllowMultiLine,
                DebounceMs = profile.DebounceMs,
                MaxTokensSingleLine = profile.MaxTokensSingleLine,
                MaxTokensMultiLine = profile.MaxTokensMultiLine,
            }).ToArray();
        return (global, profiles);
    }
}
