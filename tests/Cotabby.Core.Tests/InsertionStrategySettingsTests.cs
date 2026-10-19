using System.Text.Json;
using Cotabby.App.Settings;

namespace Cotabby.Core.Tests;

public sealed class InsertionStrategySettingsTests
{
    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void MalformedStrategyValueDoesNotResetGlobalSafetySettings(string malformedValue)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cotabby-insertion-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"Enabled\":false,\"BlockedApps\":[\"protected-app\"],\"InsertionStrategy\":" + malformedValue + "}");
            var loaded = new SettingsStore(path).Load();
            Assert.False(loaded.Enabled);
            Assert.Equal(new[] { "protected-app" }, loaded.BlockedApps);
            Assert.Equal("auto", loaded.InsertionStrategy);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AppSettings_DefaultAndRoundTripInsertionStrategy()
    {
        var settings = new AppSettings();
        Assert.Equal("auto", settings.InsertionStrategy);

        settings.InsertionStrategy = "clipboard";
        var json = JsonSerializer.Serialize(settings);
        Assert.Contains("\"InsertionStrategy\":\"clipboard\"", json, StringComparison.Ordinal);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.Equal("clipboard", loaded?.InsertionStrategy);
    }
}
