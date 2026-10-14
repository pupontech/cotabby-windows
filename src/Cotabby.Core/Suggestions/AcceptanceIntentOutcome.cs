namespace Cotabby.Core.Suggestions;

/// <summary>
/// Terminal result for one key event that the coordinator captured and suppressed.
/// StateCommitted means the inserter reported success and the suggestion state was
/// committed; it is not evidence that the host application received the text.
/// </summary>
public enum AcceptanceIntentStatus
{
    StateCommitted,
    InsertionRejected,
    InsertionFaulted,
    InsertionCancelled,
    IntentInvalidated,
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
