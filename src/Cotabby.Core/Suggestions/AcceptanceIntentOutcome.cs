namespace Cotabby.Core.Suggestions;

/// <summary>
/// Terminal result for one key event that the coordinator captured and suppressed.
/// StateCommitted means the inserter reported success and the suggestion state was
/// committed; it is not evidence that the host application received the text.
/// </summary>
public enum AcceptanceIntentStatus
{
    StateCommitted,
    /// <summary>
    /// The inserter reported success, so the text reached the host, but the
    /// coordinator could not apply the ordinary commit: the acceptance base
    /// moved and was re-anchored, or the commit was abandoned after the text
    /// landed. This is never a replay refusal - the captured key was consumed by
    /// the suggestion; only the coordinator state was not committed in the
    /// ordinary way.
    /// </summary>
    InsertedNotCommitted,
    InsertionRejected,
    InsertionFaulted,
    InsertionCancelled,
    IntentInvalidated,
    /// <summary>
    /// The acceptance-queue processor itself faulted (an exception escaped its
    /// loop body). The in-flight intent and every intent still queued behind it
    /// are completed with this status so the queue drains instead of wedging.
    /// </summary>
    QueueFaulted,
    BlockedByEarlierFailure,
    CoordinatorDisposed,
    DispatchFailed,
}

/// <summary>Whether the captured physical key was safely accounted for.</summary>
public enum AcceptanceKeyDisposition
{
    ConsumedBySuggestion,
    ReplayRefusedUnknownForeground,
    ReplayRefusedIncompleteKeyMetadata,
}

/// <summary>
/// Non-content-bearing disposition for a captured acceptance key. A replay refusal
/// is deliberately distinct from a committed suggestion acceptance.
/// </summary>
public sealed record AcceptanceIntentOutcome(
    long IntentSequence,
    AcceptanceIntentStatus Status,
    AcceptanceKeyDisposition KeyDisposition);
