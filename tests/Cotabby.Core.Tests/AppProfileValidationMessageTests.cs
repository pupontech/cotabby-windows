using Cotabby.App.Settings;

namespace Cotabby.Core.Tests;

public sealed class AppProfileValidationMessageTests
{
    [Theory]
    [InlineData("", "Enter the app's executable name, for example notepad.")]
    [InlineData("   ", "Enter the app's executable name, for example notepad.")]
    [InlineData("COTABBY", "Cotabby cannot target itself.")]
    [InlineData("C:\\notepad", "Use an executable base name, without a path or wildcard.")]
    [InlineData("note*", "Use an executable base name, without a path or wildcard.")]
    [InlineData("notepad", null)]
    public void ProcessNameValidationExplainsTheActualFailure(string name, string? expected)
    {
        Assert.Equal(expected, AppProfile.GetProcessNameValidationError(name, "cotabby"));
    }
}
