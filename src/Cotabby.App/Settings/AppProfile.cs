namespace Cotabby.App.Settings;

/// <summary>
/// Persisted per-process overrides. Null values inherit the global setting.
/// ProcessName is an executable's base name without its .exe suffix.
/// </summary>
public sealed class AppProfile
{
    public string? ProcessName { get; set; }
    public bool? Enabled { get; set; }
    public string? AcceptanceMode { get; set; }
    public string? CompletionLengthPreset { get; set; }
    public string? DisplayMode { get; set; }
    public bool? AllowMultiLine { get; set; }
    public int? DebounceMs { get; set; }
    public int? MaxTokensSingleLine { get; set; }
    public int? MaxTokensMultiLine { get; set; }
}
