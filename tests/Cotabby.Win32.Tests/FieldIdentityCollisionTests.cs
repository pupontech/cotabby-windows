using Cotabby.Core.Emoji;
using Cotabby.Core.Focus;
using Cotabby.Win32.Focus;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cotabby.Win32.Tests;

public sealed class FieldIdentityCollisionTests
{
    private const string AnonymousEdit = "ct=Edit|aid=|nm=|hwnd=123|pid=42";

    [Fact]
    public void RepeatedReadOfOneFieldKeepsASingleStableIdentity()
    {
        using var tracker = new UiaFocusTracker(NullLogger<UiaFocusTracker>.Instance);
        string? first = tracker.FallbackIdentityFromDescriptor(AnonymousEdit);
        string? second = tracker.FallbackIdentityFromDescriptor(AnonymousEdit);

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void MovingToAnotherFieldRetiresTheEarlierIdentity()
    {
        using var tracker = new UiaFocusTracker(NullLogger<UiaFocusTracker>.Instance);
        tracker.AdvanceFocusPartition(42, 10, 10, 200, 24);
        string? first = tracker.FallbackIdentityFromDescriptor(AnonymousEdit);
        // A second anonymous field in the same window with identical metadata.
        tracker.AdvanceFocusPartition(42, 10, 80, 200, 24);
        string? second = tracker.FallbackIdentityFromDescriptor(AnonymousEdit);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void UnchangedFocusPartitionDoesNotRetireTheIdentity()
    {
        using var tracker = new UiaFocusTracker(NullLogger<UiaFocusTracker>.Instance);
        tracker.AdvanceFocusPartition(42, 10.4, 10.4, 200, 24);
        string? first = tracker.FallbackIdentityFromDescriptor(AnonymousEdit);
        tracker.AdvanceFocusPartition(42, 10, 10, 200, 24);
        string? second = tracker.FallbackIdentityFromDescriptor(AnonymousEdit);

        Assert.Equal(first, second);
    }

    [Fact]
    public void RepeatedFallbackReadsAtSameRoundedBoundsStayStableForOtherCallers()
    {
        using var tracker = new UiaFocusTracker(NullLogger<UiaFocusTracker>.Instance);
        tracker.AdvanceFocusPartition(42, 10.2, 20.2, 200, 24);
        string first = tracker.FallbackIdentityFromDescriptor(AnonymousEdit);

        // A second anonymous field with identical metadata and geometry collides
        // by design; emoji safety must come from the explicit RuntimeId-strength flag.
        tracker.AdvanceFocusPartition(42, 10.4, 20.4, 200, 24);
        string second = tracker.FallbackIdentityFromDescriptor(AnonymousEdit);

        Assert.Equal(first, second);
    }

    [Fact]
    public void IdenticalFallbackFieldsAtSameRoundedBoundsAreNotAnEmojiTarget()
    {
        using var tracker = new UiaFocusTracker(NullLogger<UiaFocusTracker>.Instance);
        tracker.AdvanceFocusPartition(42, 10.2, 20.2, 200, 24);
        string firstIdentity = tracker.FallbackIdentityFromDescriptor(AnonymousEdit);
        tracker.AdvanceFocusPartition(42, 10.4, 20.4, 200, 24);
        string secondIdentity = tracker.FallbackIdentityFromDescriptor(AnonymousEdit);
        var first = Field(firstIdentity);
        var second = Field(secondIdentity);

        Assert.Equal(first.StableFieldIdentity, second.StableFieldIdentity);
        Assert.Equal(first.FieldRect, second.FieldRect);
        Assert.False(EmojiFocusPolicy.SameUsableTarget(first, second));
    }

    private static FocusedField Field(string identity) => new()
    {
        ElementHandle = new object(),
        StableFieldIdentity = identity,
        HasRuntimeIdIdentity = false,
        ProcessId = 42,
        ProcessName = "fixture",
        Text = string.Empty,
        CaretOffset = 0,
        CaretRect = ScreenRect.Empty,
        FieldRect = new ScreenRect(10.2, 20.2, 200, 24),
        IsSingleLine = true,
        IsSecure = false,
        IsCredentialField = false,
    };

    [Fact]
    public void DistinctDescriptorsRemainDistinct()
    {
        using var tracker = new UiaFocusTracker(NullLogger<UiaFocusTracker>.Instance);
        string first = tracker.FallbackIdentityFromDescriptor(AnonymousEdit);
        string second = tracker.FallbackIdentityFromDescriptor("ct=Edit|aid=|nm=|hwnd=456|pid=42");

        Assert.NotEqual(first, second);
    }
}
