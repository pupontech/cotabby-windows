namespace Cotabby.Core.Settings;

/// <summary>Validated, platform-independent settings values used by suggestion behavior.</summary>
public sealed record EffectiveSuggestionSettings(
    bool Enabled,
    string AcceptanceMode,
    string CompletionLengthPreset,
    string DisplayMode,
    bool AllowMultiLine,
    int DebounceMs,
    int? MaxTokensSingleLine,
    int? MaxTokensMultiLine);

/// <summary>App-side profile values mapped into Core without an AppSettings dependency.</summary>
public sealed record AppProfileOverride
{
    public string? ProcessName { get; init; }
    public bool? Enabled { get; init; }
    public string? AcceptanceMode { get; init; }
    public string? CompletionLengthPreset { get; init; }
    public string? DisplayMode { get; init; }
    public bool? AllowMultiLine { get; init; }
    public int? DebounceMs { get; init; }
    public int? MaxTokensSingleLine { get; init; }
    public int? MaxTokensMultiLine { get; init; }
}

/// <summary>Pure resolver for exact, case-insensitive per-process overrides.</summary>
public static class AppProfileResolver
{
    private static readonly HashSet<string> AcceptanceModes = new(StringComparer.Ordinal)
        { "word", "phrase" };
    private static readonly HashSet<string> DisplayModes = new(StringComparer.Ordinal)
        { "auto", "inline", "popup" };
    private static readonly HashSet<string> Presets = new(StringComparer.Ordinal)
        { "very_short", "short", "medium", "long" };

    public static bool IsValidProfile(AppProfileOverride? profile) =>
        profile is not null && IsValid(profile);

    public static EffectiveSuggestionSettings Resolve(
        EffectiveSuggestionSettings global,
        IReadOnlyList<AppProfileOverride?>? profiles,
        string? processName,
        string? selfProcessName)
    {
        // Duplicate valid keys fail closed to the global snapshot. JSON/list
        // order must never decide which application policy wins at runtime.
        if (!IsProcessName(processName) || IsSelf(processName!, selfProcessName) || profiles is null)
            return global;

        AppProfileOverride? match = null;
        foreach (var profile in profiles)
        {
            if (profile is null || !IsValid(profile) || profile.ProcessName is not { } name)
                continue;
            if (!string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)) continue;
            if (match is not null) return global;
            match = profile;
        }

        return match is null ? global : Apply(global, match);
    }

    private static EffectiveSuggestionSettings Apply(
        EffectiveSuggestionSettings global,
        AppProfileOverride profile)
    {
        var preset = profile.CompletionLengthPreset ?? global.CompletionLengthPreset;
        var (single, multi) = profile.CompletionLengthPreset is null
            ? (global.MaxTokensSingleLine, global.MaxTokensMultiLine)
            : Tokens(preset);
        return global with
        {
            Enabled = global.Enabled && (profile.Enabled ?? true),
            AcceptanceMode = profile.AcceptanceMode ?? global.AcceptanceMode,
            CompletionLengthPreset = preset,
            DisplayMode = profile.DisplayMode ?? global.DisplayMode,
            AllowMultiLine = profile.AllowMultiLine ?? global.AllowMultiLine,
            DebounceMs = profile.DebounceMs ?? global.DebounceMs,
            MaxTokensSingleLine = profile.MaxTokensSingleLine ?? single,
            MaxTokensMultiLine = profile.MaxTokensMultiLine ?? multi,
        };
    }

    private static bool IsValid(AppProfileOverride profile) =>
        IsProcessName(profile.ProcessName)
        && (profile.AcceptanceMode is null || AcceptanceModes.Contains(profile.AcceptanceMode))
        && (profile.CompletionLengthPreset is null || Presets.Contains(profile.CompletionLengthPreset))
        && (profile.DisplayMode is null || DisplayModes.Contains(profile.DisplayMode))
        && (profile.DebounceMs is null || profile.DebounceMs is >= 20 and <= 300)
        && (profile.MaxTokensSingleLine is null || profile.MaxTokensSingleLine is >= 4 and <= 96)
        && (profile.MaxTokensMultiLine is null || profile.MaxTokensMultiLine is >= 4 and <= 128);

    private static bool IsProcessName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && string.Equals(name, name.Trim(), StringComparison.Ordinal)
        && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && name.IndexOfAny(['*', '?', '\\', '/', ':', '"', '<', '>', '|']) < 0;

    private static bool IsSelf(string processName, string? selfProcessName) =>
        !string.IsNullOrWhiteSpace(selfProcessName)
        && string.Equals(processName, selfProcessName, StringComparison.OrdinalIgnoreCase);

    private static (int Single, int Multi) Tokens(string preset) => preset switch
    {
        "very_short" => (5, 10),
        "short" => (9, 18),
        "medium" => (15, 30),
        _ => (25, 50),
    };
}
