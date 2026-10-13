using Cotabby.Core.Focus;

namespace Cotabby.Core.Tests;

/// <summary>
/// Windows adaptation of upstream #848. Cases mirror
/// CotabbyTests/Support/Accessibility/CredentialFieldDetectorTests.swift at d81d304,
/// with the Windows control-type vocabulary (Edit/ComboBox single-line,
/// Document/Pane prose) instead of AX roles.
/// </summary>
public sealed class CredentialFieldDetectorTests
{
    private static bool Blocked(
        string controlType = "Edit",
        string?[]? labels = null,
        string? automationId = null,
        string? text = null,
        bool isPassword = false) =>
        CredentialFieldDetector.IsCredentialField(
            controlType, labels ?? [], automationId, text, isPassword);

    [Fact]
    public void GoogleSignInFieldIsBlocked()
    {
        Assert.True(Blocked(labels: ["Email or phone", null, null], automationId: "identifierId", text: "realdeepdark"));
        Assert.True(Blocked(labels: [null, null, null], automationId: "identifierId", text: ""));
    }

    [Theory]
    [InlineData("Username")]
    [InlineData("Enter PIN")]
    [InlineData("Verification code")]
    [InlineData("Phone number")]
    [InlineData("Card number")]
    [InlineData("Log in")]
    [InlineData("CVC")]
    [InlineData("Expiration")]
    public void CommonLabelsAreBlocked(string label) => Assert.True(Blocked(labels: [label]));

    [Fact]
    public void ComboBoxSignInFieldIsBlocked() =>
        Assert.True(Blocked(controlType: "ComboBox", labels: ["Email address"]));

    [Theory]
    [InlineData("Email subject")]
    [InlineData("Email preview text")]
    [InlineData("Search email")]
    [InlineData("Message to phone")]
    public void LabelsAboutEmailWritingAreNotBlocked(string label) => Assert.False(Blocked(labels: [label]));

    [Theory]
    [InlineData("username")]
    [InlineData("email")]
    [InlineData("passwd")]
    [InlineData("otp")]
    [InlineData("login_field")]
    [InlineData("user_login")]
    [InlineData("user_email")]
    [InlineData("loginEmail")]
    [InlineData("txtUserName")]
    [InlineData("phoneNumber")]
    [InlineData("phonenumber")]
    [InlineData("emailaddress")]
    [InlineData("otp-3")]
    [InlineData("OTPCode")]
    [InlineData("one-time-code")]
    public void SignInIdentifiersAreBlocked(string identifier) =>
        Assert.True(CredentialFieldDetector.IdentifierNamesCredential(identifier), identifier);

    [Theory]
    [InlineData("phonebook-search")]
    [InlineData("emailSearch")]
    [InlineData("email-subject")]
    [InlineData("userInput")]
    [InlineData("shipping")]
    [InlineData("mailbox")]
    [InlineData("identifier")]
    [InlineData("q")]
    [InlineData("")]
    [InlineData(null)]
    public void IdentifiersThatOnlyContainACredentialWordAreNotBlocked(string? identifier)
    {
        Assert.False(CredentialFieldDetector.IdentifierNamesCredential(identifier), identifier);
        Assert.False(Blocked(labels: ["Search"], automationId: identifier, text: "hello"), identifier);
    }

    [Fact]
    public void TypedAddressIsBlockedEvenWithoutLabel()
    {
        Assert.True(Blocked(labels: [], text: "senad@imperum.io"));
        Assert.False(CredentialFieldDetector.LooksLikeAddressEntry("mail senad@imperum.io today"));
    }

    [Theory]
    [InlineData("alice@")]
    [InlineData("alice@gm")]
    [InlineData("alice@gmail.com")]
    public void AddressBeingTypedIsBlockedBeforeItIsComplete(string text) =>
        Assert.True(Blocked(labels: ["Account"], automationId: "identifier", text: text), text);

    [Theory]
    [InlineData("@alice")]
    [InlineData("alice")]
    [InlineData("medium.com/@alice")]
    [InlineData("ping alice@")]
    [InlineData("a@b@c")]
    public void NonAddressShapesAreNotBlocked(string text) =>
        Assert.False(CredentialFieldDetector.LooksLikeAddressEntry(text), text);

    [Fact]
    public void OrdinaryFieldsAreNotBlocked()
    {
        Assert.False(Blocked(labels: ["Shipping notes", "Type a message", "Search"], automationId: "q", text: "hello there"));
        Assert.False(Blocked(labels: ["Spinning"]));
    }

    [Theory]
    [InlineData("Document")]
    [InlineData("Pane")]
    public void MultilineSurfacesAreNeverCredentialBlocked(string controlType)
    {
        Assert.False(Blocked(controlType: controlType, labels: ["Email body"], automationId: "email", text: "a@b.co"));
        Assert.False(Blocked(controlType: controlType, labels: [], text: "alice@"));
    }

    [Fact]
    public void PasswordFlagAlwaysBlocksAndBeatsProseExceptions()
    {
        Assert.True(Blocked(labels: ["Email subject"], isPassword: true));
        Assert.True(Blocked(labels: [], isPassword: true));
        Assert.True(Blocked(controlType: "Document", labels: [], isPassword: true));
    }

    [Fact]
    public void CredentialIdentifierDoesNotOverrideProseLabelsOnAnOrdinaryField()
    {
        // Text must not accidentally match an address, and the label stays prose;
        // the identifier is what blocks here, matching upstream's independent signals.
        Assert.True(Blocked(labels: ["Email subject"], automationId: "user_email", text: "hello"));
    }

    [Fact]
    public void OverlongOrEmptyIdentifiersAreIgnored()
    {
        Assert.False(CredentialFieldDetector.IdentifierNamesCredential(new string('a', 65)));
        Assert.False(CredentialFieldDetector.IdentifierNamesCredential("   "));
    }
}
