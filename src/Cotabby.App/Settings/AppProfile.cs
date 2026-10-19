using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cotabby.App.Settings;

/// <summary>
/// Persisted per-process overrides. Null values inherit the global setting.
/// ProcessName is an executable's base name without its .exe suffix.
/// </summary>
public sealed class AppProfile
{
    public static string? GetProcessNameValidationError(string? name, string selfProcessName)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Enter the app's executable name, for example notepad.";
        if (string.Equals(name, selfProcessName, StringComparison.OrdinalIgnoreCase))
            return "Cotabby cannot target itself.";
        return Cotabby.Core.Settings.AppProfileResolver.IsValidProfile(
            new Cotabby.Core.Settings.AppProfileOverride { ProcessName = name })
            ? null : "Use an executable base name, without a path or wildcard.";
    }

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

public sealed class AppProfileListJsonConverter : JsonConverter<List<AppProfile?>>
{
    public override List<AppProfile?> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            using var malformedContainer = JsonDocument.ParseValue(ref reader);
            return new List<AppProfile?>();
        }

        var profiles = new List<AppProfile?>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return profiles;
            if (reader.TokenType == JsonTokenType.Null)
            {
                profiles.Add(null);
                continue;
            }

            using var entry = JsonDocument.ParseValue(ref reader);
            if (entry.RootElement.ValueKind != JsonValueKind.Object) continue;

            try
            {
                profiles.Add(entry.RootElement.Deserialize<AppProfile>(options));
            }
            catch (JsonException)
            {
                // Ignore a malformed profile without discarding the rest of settings.
            }
        }

        throw new JsonException("AppProfiles array was not terminated.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<AppProfile?> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var profile in value)
            JsonSerializer.Serialize(writer, profile, options);
        writer.WriteEndArray();
    }
}
