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
}
