using System.Text;
using Cotabby.Core.Metrics;
using Xunit;

namespace Cotabby.Core.Tests;

public class MetricsAggregatorTests
{
    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0000C4B2_phys_0_eng_0_engtype_3D", 1234, "3D", "0X00000000_0X0000C4B2", 0, 0)]
    [InlineData("pid_88_luid_0x00000000_0x0000AAAA_phys_1_eng_3_engtype_VideoDecode", 88, "VideoDecode", "0X00000000_0X0000AAAA", 1, 3)]
    [InlineData("pid_88_luid_0x00000000_0x0000AAAA_phys_1_eng_3_engtype_Compute_0", 88, "Compute_0", "0X00000000_0X0000AAAA", 1, 3)]
    public void ParsesFullEngineIdentity(string name, int pid, string engine, string adapterId, int physicalEngineId, int engineId)
    {
        var parsed = MetricsAggregator.ParseEngineInstance(name, 12.5);
        Assert.NotNull(parsed);
        Assert.Equal(pid, parsed!.Value.ProcessId);
        Assert.Equal(engine, parsed.Value.EngineType);
        Assert.Equal(12.5, parsed.Value.Percent);
        Assert.Equal(adapterId, parsed.Value.AdapterId);
        Assert.Equal(physicalEngineId, parsed.Value.PhysicalEngineId);
        Assert.Equal(engineId, parsed.Value.EngineId);

    }

    [Theory]
    [InlineData("pid_1_luid_0x0_0x0_phys_0_eng_0", 5d)]        // no engtype
    [InlineData("_Total", 5d)]                                  // not a pid instance
    [InlineData("pid_notanumber_engtype_3D", 5d)]
    [InlineData("pid_-1_luid_0x0_0x0_phys_0_eng_0_engtype_3D", 5d)]
    [InlineData("pid_1_luid_0x0_0x0_phys_x_eng_0_engtype_3D", 5d)]
    [InlineData("pid_1_luid_0x0_0x0_phys_0_eng_x_engtype_3D", 5d)]
    [InlineData("pid_1_luid_0x0_0x0_phys_0_eng_0_engtype_", 5d)]
    [InlineData("pid_1_engtype_3D", null)]                      // no utilization reading
    [InlineData(null, 5d)]
    public void RejectsUnparsableOrUnreadableInstances(string? name, double? percent)
    {
        Assert.Null(MetricsAggregator.ParseEngineInstance(name, percent));
    }

    [Fact]
    public void RejectsNonFiniteUtilization()
    {
        foreach (double percent in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Null(MetricsAggregator.ParseEngineInstance(
                "pid_1_luid_0x0_0x0_phys_0_eng_0_engtype_3D", percent));
        }
    }

    [Fact]
    public void DeviceUtilizationUsesBusiestAdapterAfterSummingEachPhysicalEngine()
    {
        var instances = new[]
        {
            new GpuEngineInstance(1, "3D", 20, "adapter-a", 0, 0),
            new GpuEngineInstance(2, "Copy", 55, "adapter-a", 0, 0),
            new GpuEngineInstance(1, "VideoDecode", 40, "adapter-a", 0, 1),
            new GpuEngineInstance(3, "Compute_0", 90, "adapter-b", 1, 0),
            new GpuEngineInstance(4, "Copy", 25, "adapter-b", 1, 1),
        };
        Assert.Equal(90, MetricsAggregator.DeviceUtilizationPercent(instances));
        Assert.Null(MetricsAggregator.DeviceUtilizationPercent(Array.Empty<GpuEngineInstance>()));
    }

    [Fact]
    public void DeviceUtilizationIsClampedTo100()
    {
        var instances = new[]
        {
            new GpuEngineInstance(1, "3D", 80, "adapter-a", 0, 0),
            new GpuEngineInstance(2, "Copy", 80, "adapter-a", 0, 0),
        };
        Assert.Equal(100, MetricsAggregator.DeviceUtilizationPercent(instances));
    }

    [Fact]
    public void ProcessUtilizationCountsOnlyThatProcessesAggregateEngines()
    {
        var instances = new[]
        {
            new GpuEngineInstance(7, "3D", 30, "adapter-a", 0, 0),
            new GpuEngineInstance(7, "Copy", 40, "adapter-a", 0, 1),
            new GpuEngineInstance(7, "Compute_0", 35, "adapter-a", 0, 2),
            new GpuEngineInstance(8, "3D", 25, "adapter-a", 0, 0),
        };
        Assert.Equal(100, MetricsAggregator.ProcessUtilizationPercent(instances, 7));
        Assert.Equal(25, MetricsAggregator.ProcessUtilizationPercent(instances, 8));
        Assert.Equal(0, MetricsAggregator.ProcessUtilizationPercent(instances, 99));
    }

    [Fact]
    public void ProcessShareIsCounterGrowthOverElapsedTime()
    {
        // 0.25 s of GPU time (2_500_000 ticks of 100 ns) across 0.5 s wall time = 50%.
        Assert.Equal(50, MetricsAggregator.ProcessSharePercent(1_000, 2_501_000, 10_000, 10_500));
    }

    [Theory]
    [InlineData(null, 2_000_000L)]      // no previous reading
    [InlineData(2_000_000L, null)]      // process lost its engine instance
    [InlineData(2_000_000L, 1_000_000L)]// counter shrank: a queue was released
    public void ProcessShareReportsNoDataRatherThanANumber(long? previous, long? current)
    {
        Assert.Null(MetricsAggregator.ProcessSharePercent(previous, current, 10_000, 10_500));
    }

    [Fact]
    public void ProcessShareIgnoresNonPositiveElapsedTime()
    {
        Assert.Null(MetricsAggregator.ProcessSharePercent(0, 1_000_000, 10_000, 10_000));
        Assert.Null(MetricsAggregator.ProcessSharePercent(0, 1_000_000, 10_500, 10_000));
    }

    [Fact]
    public void TrackerDerivesShareFromConsecutiveSamples()
    {
        var samples = new Queue<GpuUsageSample>(
        [
            new GpuUsageSample { DeviceUtilizationPercent = 40, ProcessEngineTicks = 0 },
            new GpuUsageSample { DeviceUtilizationPercent = 60, ProcessEngineTicks = 2_500_000 },
        ]);
        long now = 0;
        var tracker = new GpuUsageTracker(new QueueProvider(samples), () => now);

        Assert.Null(tracker.Sample().ProcessUtilizationPercent);  // first sample has no baseline
        now = 500;
        var second = tracker.Sample();
        Assert.Equal(50, second.ProcessUtilizationPercent);
        Assert.Equal(60, second.DeviceUtilizationPercent);

        tracker.Reset();
        now = 1000;
        Assert.Null(tracker.Sample().ProcessUtilizationPercent);
    }

    [Fact]
    public void ProductionFormattingHelpersHandleUnknownAndReadableZero()
    {
        Assert.Equal("—", MetricsAggregator.FormatPercent(null));
        Assert.Equal("—", MetricsAggregator.FormatPercent(double.NaN));
        Assert.Equal("42%", MetricsAggregator.FormatPercent(42));
        Assert.Equal("0 MB", MetricsAggregator.FormatBytes(0));
        Assert.Equal("—", MetricsAggregator.FormatBytes(null));
        Assert.Equal("—", MetricsAggregator.FormatBytes(-1));
    }

    private sealed class QueueProvider(Queue<GpuUsageSample> samples) : IGpuUsageProvider
    {
        public GpuUsageSample Read() => samples.Count > 0 ? samples.Dequeue() : GpuUsageSample.Unavailable;
    }

}