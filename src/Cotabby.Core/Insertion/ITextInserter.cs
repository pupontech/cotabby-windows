using Cotabby.Core.Focus;

namespace Cotabby.Core.Insertion;

/// <summary>
/// Inserts a string of text into the currently focused field. The Windows
/// implementation uses Unicode SendInput or clipboard paste; it does not write
/// through UI Automation text or value patterns.
/// </summary>
public interface ITextInserter
{
    /// <summary>
    /// Insert <paramref name="text"/> at the caret of <paramref name="target"/>.
    /// Returns false if the field is no longer focused or the insertion was rejected.
    /// </summary>
    /// <remarks>
    /// Implementations must be reentrancy-safe: a Tab-driven acceptance may fire
    /// while a previous insertion is still in-flight if the user is rapid-fire.
    /// Native dispatch can fail after partial input, including when one
    /// SendInput call reports fewer events than its submitted single batch.
    /// False is not a guarantee of zero host mutation; callers must not blindly
    /// replay the text. When an implementation knows one or more native Unicode
    /// events were dispatched before an incomplete or failed dispatch, it must throw
    /// <see cref="IndeterminateTextInsertionException"/> so acceptance callers
    /// can retire that offer instead of retrying potentially duplicated text.
    /// </remarks>
    Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct);
}
