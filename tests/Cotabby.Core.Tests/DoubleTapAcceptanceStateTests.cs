using Cotabby.Core.Suggestions;
using Xunit;

namespace Cotabby.Core.Tests;

public sealed class DoubleTapAcceptanceStateTests
{
    private static readonly DoubleTapAcceptanceState.SessionToken Session = new("request-1");

    [Fact]
    public void SecondDistinctPressWithinWindowOnSameSuggestion_CompletesPairOnce()
    {
        var state = new DoubleTapAcceptanceState();
        state.RecordWordAccept(Session, atSeconds: 10.0);

        Assert.True(state.ConsumeDoubleTap(Session, atSeconds: 10.2));
        Assert.False(state.ConsumeDoubleTap(Session, atSeconds: 10.25));
    }

    [Fact]
    public void SecondPressAtWindowBoundaryCompletesPair()
    {
        var state = new DoubleTapAcceptanceState();
        state.RecordWordAccept(Session, atSeconds: 10.0);

        Assert.True(state.ConsumeDoubleTap(Session, atSeconds: 10.0 + DoubleTapAcceptanceState.WindowSeconds));
    }

    [Fact]
    public void PressAfterWindowOrOnDifferentSuggestionDoesNotCompletePair()
    {
        var state = new DoubleTapAcceptanceState();
        state.RecordWordAccept(Session, atSeconds: 10.0);

        Assert.False(state.ConsumeDoubleTap(Session, atSeconds: 10.0 + DoubleTapAcceptanceState.WindowSeconds + 0.01));
        Assert.False(state.HasPendingPress);

        state.RecordWordAccept(Session, atSeconds: 11.0);
        Assert.False(state.ConsumeDoubleTap(new("request-2"), atSeconds: 11.1));
        Assert.False(state.HasPendingPress);
    }

    [Fact]
    public void ClockRegressionAndExplicitResetDoNotCompletePair()
    {
        var state = new DoubleTapAcceptanceState();
        state.RecordWordAccept(Session, atSeconds: 10.0);

        Assert.False(state.ConsumeDoubleTap(Session, atSeconds: 9.9));
        Assert.False(state.HasPendingPress);

        state.RecordWordAccept(Session, atSeconds: 11.0);
        state.Reset();
        Assert.False(state.ConsumeDoubleTap(Session, atSeconds: 11.1));
    }
}
