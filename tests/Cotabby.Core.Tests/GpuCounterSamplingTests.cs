using Cotabby.Core.Metrics;

namespace Cotabby.Core.Tests;

public class GpuCounterSamplingTests
{
    [Fact]
    public void ProductionSamplerKeepsUtilizationAndMemoryAvailabilityIndependent()
    {
        var memoryOnly = GpuCounterSampler.CreateSample(
            Array.Empty<GpuEngineCounterReading>(), processId: 42, deviceMemoryBytes: 0);

        Assert.Null(memoryOnly.DeviceUtilizationPercent);
        Assert.Null(memoryOnly.ProcessEngineTicks);
        Assert.Equal(0, memoryOnly.DeviceMemoryBytes);

        var firstEngineSample = GpuCounterSampler.CreateSample(
            [new GpuEngineCounterReading(Engine("42", "0", "0", "3D"), null, 100)],
            processId: 42, deviceMemoryBytes: null);

        Assert.Null(firstEngineSample.DeviceUtilizationPercent);
        Assert.Equal(100, firstEngineSample.ProcessEngineTicks);
        Assert.Null(firstEngineSample.DeviceMemoryBytes);
    }

    [Fact]
    public void ProductionSamplerUsesAllEngineTypesAndRawRunningTimeTicks()
    {
        var reading = GpuCounterSampler.CreateSample(
            [
                new GpuEngineCounterReading(Engine("42", "0", "0", "3D"), 20, 1_000),
                new GpuEngineCounterReading(Engine("42", "0", "1", "Copy"), 30, 2_000),
                new GpuEngineCounterReading(Engine("42", "0", "2", "Compute_0"), 40, 3_000),
                new GpuEngineCounterReading(Engine("84", "0", "0", "3D"), 10, 900),
            ], processId: 42, deviceMemoryBytes: 1234);

        Assert.Equal(40, reading.DeviceUtilizationPercent);
        Assert.Equal(6_000, reading.ProcessEngineTicks);
        Assert.Equal(1234, reading.DeviceMemoryBytes);
    }

    [Fact]
    public void ProductionSamplerRejectsMalformedAndNonFiniteUtilizationButRetainsReadableMemory()
    {
        var reading = GpuCounterSampler.CreateSample(
            [
                new GpuEngineCounterReading("pid_42_engtype_3D", 85, 10),
                new GpuEngineCounterReading(Engine("42", "0", "0", "3D"), double.NaN, 10),
            ], processId: 42, deviceMemoryBytes: 4096);

        Assert.Null(reading.DeviceUtilizationPercent);
        Assert.Equal(10, reading.ProcessEngineTicks);
        Assert.Equal(4096, reading.DeviceMemoryBytes);
    }

    [Fact]
    public void TrackerResetResetsTheProvidersCounterBaseline()
    {
        var provider = new ResettableProvider();
        var tracker = new GpuUsageTracker(provider, () => 0);

        tracker.Sample();
        tracker.Reset();

        Assert.Equal(1, provider.ResetCount);
    }

    [Fact]
    public void TrackerRejectsAnEngineResetHiddenByAnotherEnginesGrowth()
    {
        var readings = new Queue<GpuUsageSample>(new[]
        {
            GpuCounterSampler.CreateSample(new[]
            {
                new GpuEngineCounterReading(Engine("42", "0", "0", "3D"), 10, 10_000_000),
                new GpuEngineCounterReading(Engine("42", "0", "1", "Copy"), 20, 100_000_000),
            }, 42, null),
            GpuCounterSampler.CreateSample(new[]
            {
                new GpuEngineCounterReading(Engine("42", "0", "0", "3D"), 10, 0),
                new GpuEngineCounterReading(Engine("42", "0", "1", "Copy"), 20, 110_000_000),
            }, 42, null),
        });
        long now = 0;
        var tracker = new GpuUsageTracker(new QueueProvider(readings), () => now);
        Assert.Null(tracker.Sample().ProcessUtilizationPercent);
        now = 1000;
        Assert.Null(tracker.Sample().ProcessUtilizationPercent);
    }

    private sealed class QueueProvider(Queue<GpuUsageSample> readings) : IGpuUsageProvider
    {
        public GpuUsageSample Read() => readings.Dequeue();
    }

    private static string Engine(string pid, string physicalEngine, string engine, string engineType) =>
        $"pid_{pid}_luid_0x00000000_0x0000AAAA_phys_{physicalEngine}_eng_{engine}_engtype_{engineType}";

    private sealed class ResettableProvider : IGpuUsageProvider, IGpuUsageProviderResettable
    {
        public int ResetCount { get; private set; }

        public GpuUsageSample Read() => GpuUsageSample.Unavailable;

        public void Reset() => ResetCount++;
    }
}
