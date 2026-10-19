namespace Cotabby.Core.Insertion;

/// <summary>
/// Indicates that text insertion may have partially reached the target field.
/// Implementations throw this when they dispatched one or more native Unicode
/// events and a later dispatch batch failed; callers must not retry the text.
/// </summary>
public sealed class IndeterminateTextInsertionException : Exception
{
    /// <summary>Creates the content-free indeterminate-dispatch failure.</summary>
    public IndeterminateTextInsertionException()
        : base("Text insertion may have been partially dispatched.")
    {
    }
}
