using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Suggestions;
using Xunit;

namespace Cotabby.Core.Tests;

/// <summary>Parent-owned gate behaviour: one policy flag, every caller.</summary>
public class SuggestionAvailabilityCredentialTests
{
    private static FocusedField Field(bool credential, bool secure = false, string text = "hello") => new()
    {
        ElementHandle = new object(),
        ProcessId = Environment.ProcessId + 1,
        ProcessName = "notepad",
        Text = text,
        CaretOffset = text.Length,
        CaretRect = new ScreenRect(10, 20, 1, 20),
        FieldRect = new ScreenRect(0, 0, 200, 40),
        IsSingleLine = true,
        IsSecure = secure,
        IsCredentialField = credential,
    };

    private static KeyboardEvent Key() => new()
    {
        Kind = KeyKind.Character,
        Character = 'a',
        IsKeyDown = true,
        HasNonShiftModifier = false,
    };

    [Fact]
    public void CredentialFieldIsNotRequested()
    {
        Assert.False(SuggestionAvailability.ShouldRequest(Field(credential: true), Key()));
    }

    [Fact]
    public void OrdinaryFieldIsStillRequested()
    {
        Assert.True(SuggestionAvailability.ShouldRequest(Field(credential: false), Key()));
    }

    [Fact]
    public void CredentialFieldStillNeedsTheSecureCheck()
    {
        // A credential field is also secure when the platform says so; the
        // secure rule must not be replaced by the new flag.
        Assert.False(SuggestionAvailability.ShouldRequest(Field(credential: true, secure: true), Key()));
    }
}
