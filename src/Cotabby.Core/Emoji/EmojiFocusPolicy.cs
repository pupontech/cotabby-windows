using Cotabby.Core.Focus;
using Cotabby.Core.Input;

namespace Cotabby.Core.Emoji;

/// <summary>Ownership checks for mutations initiated by the emoji picker.</summary>
public static class EmojiFocusPolicy
{
    public static bool RequiresFreshFocus(KeyboardEvent input) =>
        input.Kind is KeyKind.Character or KeyKind.Backspace
        && input.IsKeyDown
        && !input.HasNonShiftModifier;

    public static bool IsUsableTarget(FocusedField? field) =>
        field is { ProcessId: > 0, IsSecure: false, IsCredentialField: false, HasRuntimeIdIdentity: true }
        && !string.IsNullOrWhiteSpace(field.StableFieldIdentity);

    public static bool SameUsableTarget(FocusedField? expected, FocusedField? live) =>
        IsUsableTarget(expected)
        && IsUsableTarget(live)
        && expected!.ProcessId == live!.ProcessId
        && string.Equals(expected.StableFieldIdentity, live.StableFieldIdentity, StringComparison.Ordinal);

    public static bool MatchesExpectedTailAtTriggerCaret(
        FocusedField? expected,
        FocusedField? live,
        string expectedTail)
    {
        if (!SameUsableTarget(expected, live) || string.IsNullOrEmpty(expectedTail)) return false;

        int triggerCaretOffset = expected!.CaretOffset;
        if (triggerCaretOffset < 0
            || triggerCaretOffset > expected.Text.Length
            || triggerCaretOffset > int.MaxValue - expectedTail.Length)
            return false;

        int expectedCaretOffset = triggerCaretOffset + expectedTail.Length;
        return live!.CaretOffset == expectedCaretOffset
            && expectedCaretOffset <= live.Text.Length
            && string.CompareOrdinal(
                live.Text, triggerCaretOffset, expectedTail, 0, expectedTail.Length) == 0;
    }

    /// <summary>
    /// Confirms that the live focused snapshot still has exactly the text tail
    /// and caret captured by a replacement request. The expected token must begin
    /// at the original trigger caret; any same-field edit or caret movement fails
    /// closed before destructive input is sent.
    /// </summary>
    public static bool MatchesExpectedReplacement(
        FocusedField? expected,
        FocusedField? live,
        string expectedTail,
        int expectedCaretOffset)
    {
        if (expected is null
            || string.IsNullOrEmpty(expectedTail)
            || expected.CaretOffset > int.MaxValue - expectedTail.Length
            || expectedCaretOffset != expected.CaretOffset + expectedTail.Length)
            return false;

        return MatchesExpectedTailAtTriggerCaret(expected, live, expectedTail);
    }
}
