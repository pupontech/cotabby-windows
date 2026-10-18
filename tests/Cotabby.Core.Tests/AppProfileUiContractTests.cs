using System.Xml.Linq;

namespace Cotabby.Core.Tests;

public sealed class AppProfileUiContractTests
{
    [Fact]
    public void SettingsStaticResourceReferencesResolveToDeclaredKeys()
    {
        using var stream = typeof(AppProfileUiContractTests).Assembly.GetManifestResourceStream("Cotabby.SettingsWindow.xaml");
        var doc = XDocument.Load(stream!);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = doc.Descendants().Select(e => (string?)e.Attribute(x + "Key")).Where(k => k is not null).ToHashSet();
        foreach (var attribute in doc.Descendants().Attributes())
        {
            foreach (System.Text.RegularExpressions.Match reference in System.Text.RegularExpressions.Regex.Matches(
                         attribute.Value, @"\{StaticResource\s+([^}\s]+)\}"))
                Assert.Contains(reference.Groups[1].Value, keys);
        }
    }

    [Fact]
    public void AppsPaneExposesProfileEditorAndAllInheritedOverrides()
    {
        using var stream = typeof(AppProfileUiContractTests).Assembly.GetManifestResourceStream("Cotabby.SettingsWindow.xaml");
        Assert.NotNull(stream);
        var doc = XDocument.Load(stream!);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var names = doc.Descendants().Select(e => (string?)e.Attribute(x + "Name")).ToHashSet();
        foreach (var name in new[] { "AppProfileList", "ProfileProcessBox", "ProfileEnabledCombo", "ProfileAcceptanceCombo",
                     "ProfileLengthCombo", "ProfileDisplayCombo", "ProfileMultilineCombo", "ProfileDebounceBox",
                     "ProfileSingleTokensBox", "ProfileMultiTokensBox", "ProfileSaveButton", "ProfileNewButton",
                     "ProfileRemoveButton", "ProfileValidationText" })
            Assert.Contains(name, names);
    }
}
