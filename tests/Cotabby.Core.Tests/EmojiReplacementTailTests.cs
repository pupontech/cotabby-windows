using System.Reflection;
using Cotabby.Core.Emoji;
using Cotabby.Core.Focus;

namespace Cotabby.Core.Tests;

public sealed class EmojiReplacementTailTests
{
    private static readonly MethodInfo? MatchesExpectedTailAtTriggerCaret = typeof(EmojiFocusPolicy).GetMethod(
        "MatchesExpectedTailAtTriggerCaret",
        BindingFlags.Public | BindingFlags.Static,
        [typeof(FocusedField), typeof(FocusedField), typeof(string)]);
    private static readonly MethodInfo? MatchesExpectedReplacement = typeof(EmojiFocusPolicy).GetMethod(
        "MatchesExpectedReplacement",
        BindingFlags.Public | BindingFlags.Static,
        [typeof(FocusedField), typeof(FocusedField), typeof(string), typeof(int)]);

    [Fact]
    public void SameFieldTypingAbcAfterDelayedCommitIsRejected()
    {
        var target = Field("prefix", caretOffset: 6);
        var current = target with { Text = "prefix:smile:abc", CaretOffset = 16 };

        Assert.NotNull(MatchesExpectedReplacement);
        Assert.False(Invoke(target, current, ":smile:", 13));
    }

    [Fact]
    public void SameFieldTailAtAnotherCaretPositionIsRejected()
    {
        var target = Field("prefix", caretOffset: 6);
        var current = target with { Text = "prefix :smile:", CaretOffset = 14 };

        Assert.NotNull(MatchesExpectedTailAtTriggerCaret);
        Assert.False(InvokeTail(target, current, ":smile:"));
    }

    [Fact]
    public void ExactTailAtOriginalTriggerCaretIsAccepted()
    {
        var target = Field("prefix", caretOffset: 6);
        var current = target with { Text = "prefix:smile:", CaretOffset = 13 };

        Assert.NotNull(MatchesExpectedTailAtTriggerCaret);
        Assert.True(InvokeTail(target, current, ":smile:"));
    }

    [Fact]
    public void ExactExpectedTailAndCaretPermitReplacement()
    {
        var target = Field(":smile", caretOffset: 6);
        var current = target with { Text = "prefix:smile:", CaretOffset = 13 };

        Assert.NotNull(MatchesExpectedReplacement);
        Assert.True(Invoke(target, current, ":smile:", 13));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SecureAndCredentialFieldsAreNeverReplacementTargets(bool secure, bool credential)
    {
        var target = Field(":smile:", caretOffset: 7);
        var current = target with { IsSecure = secure, IsCredentialField = credential };

        Assert.NotNull(MatchesExpectedReplacement);
        Assert.False(Invoke(target, current, ":smile:", 7));
    }

    private static bool InvokeTail(FocusedField expected, FocusedField current, string tail) =>
        (bool)MatchesExpectedTailAtTriggerCaret!.Invoke(null, [expected, current, tail])!;

    private static bool Invoke(FocusedField expected, FocusedField current, string tail, int caretOffset) =>
        (bool)MatchesExpectedReplacement!.Invoke(null, [expected, current, tail, caretOffset])!;

    private static FocusedField Field(string text, int caretOffset) => new()
    {
        ElementHandle = new object(),
        StableFieldIdentity = "42.7.9",
        HasRuntimeIdIdentity = true,
        ProcessId = 42,
        ProcessName = "fixture",
        Text = text,
        CaretOffset = caretOffset,
        CaretRect = ScreenRect.Empty,
        FieldRect = new ScreenRect(10, 20, 200, 24),
        IsSingleLine = true,
        IsSecure = false,
        IsCredentialField = false,
    };
}
