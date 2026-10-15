using Cotabby.Win32.Focus;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cotabby.Win32.Tests;

public sealed class FieldIdentityCollisionTests
{
    [Fact]
    public void IdenticalFallbackDescriptorsAreRefusedForLaterFields()
    {
        using var tracker = new UiaFocusTracker(NullLogger<UiaFocusTracker>.Instance);
        string? first = tracker.FallbackIdentityFromDescriptor("ct=Edit|aid=|nm=|hwnd=123|pid=42");
        string? second = tracker.FallbackIdentityFromDescriptor("ct=Edit|aid=|nm=|hwnd=123|pid=42");

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void DistinctDescriptorsRemainUsable()
    {
        using var tracker = new UiaFocusTracker(NullLogger<UiaFocusTracker>.Instance);
        string? first = tracker.FallbackIdentityFromDescriptor("ct=Edit|aid=|nm=|hwnd=123|pid=42");
        string? second = tracker.FallbackIdentityFromDescriptor("ct=Edit|aid=|nm=|hwnd=456|pid=42");

        Assert.NotEqual(first, second);
        Assert.NotNull(second);
    }
}
