using Cotabby.Core.Focus;

namespace Cotabby.Core.Tests;

public sealed class FieldSuppressionPolicyTests
{
    [Theory]
    [InlineData("1Password")]
    [InlineData("KeePass.exe")]
    [InlineData("keepassxc")]
    [InlineData("Bitwarden")]
    [InlineData("enpass")]
    [InlineData("dashlane")]
    public void PasswordManagersAreMatchedExactly(string process) =>
        Assert.True(FieldSuppressionPolicy.IsProtectedProcess(process));

    [Theory]
    [InlineData("notepad")]
    [InlineData("my-bitwarden-notes")]
    [InlineData(null)]
    public void OrdinaryProcessesRemainEligible(string? process) =>
        Assert.False(FieldSuppressionPolicy.IsProtectedProcess(process));

    [Theory]
    [InlineData("Xk9#mQ2$vLp7", true, true)]
    [InlineData("Xk9#mQ2$vLp7 ", true, false)]
    [InlineData("ordinary prose", true, false)]
    [InlineData("Short1!", true, false)]
    [InlineData("1234567890", true, false)]
    [InlineData("Xk9#mQ2$vLp7", false, false)]
    public void EntropyBackoffIsBoundedSingleLineAndReleasesOnWhitespace(string text, bool singleLine, bool expected) =>
        Assert.Equal(expected, FieldSuppressionPolicy.ShouldBackOff(text, singleLine));

    [Theory]
    [InlineData("Passphrase")]
    [InlineData("Pass phrase")]
    [InlineData("Security answer")]
    [InlineData("Recovery code")]
    public void AdditionalCredentialLabelsAreRecognized(string label) =>
        Assert.True(CredentialFieldDetector.IsCredentialField("Edit", [label], null, null));
}
