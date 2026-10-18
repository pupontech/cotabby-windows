using System.Xml.Linq;

namespace Cotabby.Core.Tests;

public sealed class AppProfileUiContractTests
{
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
