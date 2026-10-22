namespace Cotabby.Core.Insertion;

public enum TextReplacementStatus
{
    /// <summary>No replacement input was dispatched.</summary>
    Rejected,
    /// <summary>Every input event in the replacement batch was dispatched.</summary>
    Completed,
    /// <summary>Only a prefix of the replacement batch was dispatched.</summary>
    Partial,
}

/// <summary>Result of one replacement input dispatch; partial dispatch is not atomic.</summary>
public readonly record struct TextReplacementResult(
    TextReplacementStatus Status,
    uint RequestedInputCount,
    uint InsertedInputCount,
    bool KeyReleaseRecovered)
{
    public bool IsComplete => Status == TextReplacementStatus.Completed;

    /// <summary>
    /// Converts SendInput's inserted-event count into an honest transaction
    /// outcome. A successful key-up recovery does not make a partial edit atomic.
    /// </summary>
    public static TextReplacementResult FromSendInput(
        uint requestedInputCount,
        uint insertedInputCount,
        bool keyReleaseRecovered)
    {
        var status = insertedInputCount == requestedInputCount && requestedInputCount > 0
            ? TextReplacementStatus.Completed
            : insertedInputCount == 0
                ? TextReplacementStatus.Rejected
                : TextReplacementStatus.Partial;
        return new TextReplacementResult(status, requestedInputCount, insertedInputCount, keyReleaseRecovered);
    }

    public static TextReplacementResult Rejected { get; } =
        new(TextReplacementStatus.Rejected, 0, 0, false);
}
