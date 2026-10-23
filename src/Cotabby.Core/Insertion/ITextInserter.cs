using Cotabby.Core.Focus;

namespace Cotabby.Core.Insertion;

/// <summary>
/// Inserts a string of text into the currently focused field. Implementations
/// prefer UIA <c>TextPattern.InsertText</c> when supported, falling back to
/// synthesized keystrokes (<c>SendInput</c>) for fields that don't expose the
/// pattern (Chrome, Electron apps, raw Win32 edits, …).
/// </summary>
public interface ITextInserter
{
    /// <summary>
    /// Atomically replace the requested number of trailing characters in
    /// <paramref name="target"/> with <paramref name="text"/>.
    /// </summary>
    /// <remarks>
    /// Implementations must fail closed unless strong field ownership, the
    /// expected trailing text and caret are proven; serialize input blocking and
    /// injection on one native thread; and expose partial native dispatches.
    /// A partial result is not atomic and may already have mutated the field.
    /// </remarks>
    Task<TextReplacementResult> ReplaceTextAsync(
        FocusedField target,
        int charactersToReplace,
        string text,
        string expectedTail,
        int expectedCaretOffset,
        CancellationToken ct);

    /// <summary>
    /// Insert <paramref name="text"/> at the caret of <paramref name="target"/>.
    /// Returns false if the field is no longer focused or the insertion was rejected.
    /// </summary>
    /// <remarks>
    /// Implementations must be reentrancy-safe: a Tab-driven acceptance may fire
    /// while a previous insertion is still in-flight if the user is rapid-fire.
    /// </remarks>
    Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct);
}
