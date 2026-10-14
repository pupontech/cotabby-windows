namespace Cotabby.Core.Suggestions;

/// <summary>
/// Recognizes a second physical Accept Word press on the same suggestion session.
/// The first press is handled immediately by the normal acceptance path; this
/// state only decides whether a later press promotes to the remaining suggestion.
/// </summary>
public sealed class DoubleTapAcceptanceState
{
    /// <summary>Upstream's deliberate-Tab pacing threshold: 300 milliseconds.</summary>
    public const double WindowSeconds = 0.3;

    public readonly record struct SessionToken(string RequestId);

    private SessionToken? _pendingSession;
    private double _pendingAtSeconds;

    public bool HasPendingPress => _pendingSession.HasValue;

    /// <summary>Arms a pair after a word accept left a visible continuation.</summary>
    public void RecordWordAccept(SessionToken session, double atSeconds)
    {
        _pendingSession = session;
        _pendingAtSeconds = atSeconds;
    }

    /// <summary>
    /// Consumes the pending press whether the pair succeeds or misses. A press
    /// outside the window, from another session, or after a clock regression
    /// cannot be reused by a later third press.
    /// </summary>
    public bool ConsumeDoubleTap(SessionToken session, double atSeconds)
    {
        var pendingSession = _pendingSession;
        var pendingAtSeconds = _pendingAtSeconds;
        Reset();

        if (pendingSession != session) return false;
        return atSeconds >= pendingAtSeconds
            && atSeconds <= pendingAtSeconds + WindowSeconds;
    }

    public void Reset()
    {
        _pendingSession = null;
        _pendingAtSeconds = 0;
    }
}
