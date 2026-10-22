using Cotabby.Core.Emoji;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;

namespace Cotabby.Core.Tests;

public sealed class EmojiFocusPolicyTests
{
    [Fact]
    public void DescriptorOnlyFieldsWithIdenticalIdentityAndBoundsAreNeverAcceptedAsEmojiTarget()
    {
        var first = Field("ct=Edit|aid=|nm=|hwnd=123|pid=42", hasRuntimeIdIdentity: false);
        var second = Field("ct=Edit|aid=|nm=|hwnd=123|pid=42", hasRuntimeIdIdentity: false);

        Assert.Equal(first.StableFieldIdentity, second.StableFieldIdentity);
        Assert.Equal(first.FieldRect, second.FieldRect);
        Assert.False(EmojiFocusPolicy.SameUsableTarget(first, second));
    }

    [Fact]
    public void MatchingRuntimeIdFieldsRemainUsableForEmoji()
    {
        var first = Field("42.7.9", hasRuntimeIdIdentity: true);
        var second = Field("42.7.9", hasRuntimeIdIdentity: true);

        Assert.True(EmojiFocusPolicy.SameUsableTarget(first, second));
    }

    [Fact]
    public void FocusGuardCoversOnlyUnmodifiedQueryMutationKeyDowns()
    {
        Assert.True(EmojiFocusPolicy.RequiresFreshFocus(Input(KeyKind.Character, isKeyDown: true)));
        Assert.True(EmojiFocusPolicy.RequiresFreshFocus(Input(KeyKind.Backspace, isKeyDown: true)));
        Assert.False(EmojiFocusPolicy.RequiresFreshFocus(Input(KeyKind.Character, isKeyDown: false)));
        Assert.False(EmojiFocusPolicy.RequiresFreshFocus(Input(KeyKind.Backspace, isKeyDown: false)));
        Assert.False(EmojiFocusPolicy.RequiresFreshFocus(Input(KeyKind.Other, isKeyDown: true)));
        Assert.False(EmojiFocusPolicy.RequiresFreshFocus(
            Input(KeyKind.Character, isKeyDown: true, hasNonShiftModifier: true)));
        Assert.False(EmojiFocusPolicy.RequiresFreshFocus(
            Input(KeyKind.Backspace, isKeyDown: true, hasNonShiftModifier: true)));
    }

    private static KeyboardEvent Input(
        KeyKind kind, bool isKeyDown, bool hasNonShiftModifier = false) => new()
    {
        Kind = kind,
        Character = 'a',
        HasNonShiftModifier = hasNonShiftModifier,
        IsKeyDown = isKeyDown,
    };

    [Fact]
    public void OneFallbackSnapshotCannotMatchAProvenRuntimeIdTarget()
    {
        var proven = Field("42.7.9", hasRuntimeIdIdentity: true);
        var fallback = Field("42.7.9", hasRuntimeIdIdentity: false);

        Assert.False(EmojiFocusPolicy.SameUsableTarget(proven, fallback));
    }

    private static FocusedField Field(string identity, bool hasRuntimeIdIdentity) => new()
    {
        ElementHandle = new object(),
        StableFieldIdentity = identity,
        HasRuntimeIdIdentity = hasRuntimeIdIdentity,
        ProcessId = 42,
        ProcessName = "fixture",
        Text = string.Empty,
        CaretOffset = 0,
        CaretRect = ScreenRect.Empty,
        FieldRect = new ScreenRect(10, 20, 200, 24),
        IsSingleLine = true,
        IsSecure = false,
        IsCredentialField = false,
    };
}
