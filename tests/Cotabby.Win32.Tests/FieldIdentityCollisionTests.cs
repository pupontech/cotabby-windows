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
    public void DistinctDescriptorsRemainDistinct()
    {
        using var tracker = new UiaFocusTracker(NullLogger<UiaFocusTracker>.Instance);
        string first = tracker.FallbackIdentityFromDescriptor(AnonymousEdit);
        string second = tracker.FallbackIdentityFromDescriptor("ct=Edit|aid=|nm=|hwnd=456|pid=42");

        Assert.NotEqual(first, second);
    }
}
