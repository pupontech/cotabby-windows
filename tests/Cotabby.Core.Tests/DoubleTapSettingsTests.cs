using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Cotabby.App.Settings;

namespace Cotabby.Core.Tests;

public sealed class DoubleTapSettingsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Enabled\":true,\"AcceptWordKey\":\"Tab\"}")]
    public void MissingOrLegacySettingsKeepBacktickDefault(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal("Backtick", settings.AcceptSuggestionKey);
        Assert.Equal("Tab", settings.AcceptWordKey);
    }

    [Theory]
    [InlineData("Backtick")]
    [InlineData("Tilde")]
    [InlineData("Backslash")]
    [InlineData("")]
    public void ExistingFullAcceptanceChoicesPersistUnchanged(string choice)
    {
        var settings = new AppSettings { AcceptSuggestionKey = choice };

        var json = JsonSerializer.Serialize(settings);
        var reloaded = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(choice, reloaded.AcceptSuggestionKey);
        Assert.Equal("Tab", reloaded.AcceptWordKey);
    }

    [Fact]
    public void DoubleTapRoundTripsAndAnExplicitSwitchbackReplacesIt()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            "{\"AcceptSuggestionKey\":\"DoubleTap\"}")!;

        Assert.Equal("DoubleTap", settings.AcceptSuggestionKey);
        var doubleTapReloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal("DoubleTap", doubleTapReloaded.AcceptSuggestionKey);
        Assert.Equal("Tab", doubleTapReloaded.AcceptWordKey);
        settings.AcceptSuggestionKey = "Backtick";
        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

        Assert.Equal("Backtick", reloaded.AcceptSuggestionKey);
        Assert.Equal(typeof(string), typeof(AppSettings)
            .GetProperty(nameof(AppSettings.AcceptSuggestionKey), BindingFlags.Instance | BindingFlags.Public)!
            .PropertyType);
    }

    [Fact]
    public void SettingsExplainsWordOrPhraseAsTheFirstDoubleTapAccept()
    {
        var settingsMarkup = LoadMarkup("Cotabby.SettingsWindow.xaml");
        var caption = TextBlocks(settingsMarkup)
            .Single(text => text.StartsWith("Double-tap Tab accepts", StringComparison.Ordinal));

        Assert.Contains("word or phrase first", caption, StringComparison.Ordinal);
    }

    [Fact]
    public void WelcomeCopyDoesNotClaimBacktickIsAlwaysTheActiveBinding()
    {
        var welcomeMarkup = LoadMarkup("Cotabby.WelcomeWindow.xaml");
        var doneCopy = TextBlocks(welcomeMarkup)
            .Single(text => text.StartsWith("Start typing anywhere.", StringComparison.Ordinal));

        Assert.Equal(
            "Start typing anywhere. Press Tab to accept a word; see Settings for your full-suggestion shortcut.",
            doneCopy);
    }

    private static XDocument LoadMarkup(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded markup resource {resourceName}.");
        return XDocument.Load(stream);
    }

    private static IEnumerable<string> TextBlocks(XDocument document) =>
        document.Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(text => text is not null)
            .Select(text => text!);
}
