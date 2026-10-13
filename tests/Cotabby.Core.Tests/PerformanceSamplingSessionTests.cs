using Cotabby.Core.Metrics;
using Cotabby.Core.Presentation;

namespace Cotabby.Core.Tests;

/// <summary>
/// Deterministic tests for the bounded UI sampling helper. The provider is a
/// controllable sampler that blocks on a gate the test opens explicitly, so
/// every ordering assertion is exact and no test sleeps.
/// </summary>
public class PerformanceSamplingSessionTests
{
    /// <summary>Records provider/reset calls and can hold a read open.</summary>
    private sealed class Recorder
    {
        private readonly object _gate = new();
        private readonly List<string> _events = new();
        private TaskCompletionSource? _release;

        public GpuUsageSample Next { get; set; } = new() { DeviceUtilizationPercent = 42d };
        public bool SamplerThrows { get; set; }
        public int Reads { get; private set; }
        public int Resets { get; private set; }

        public string[] Events
        {
            get { lock (_gate) return _events.ToArray(); }
        }

        /// <summary>Hold the next read open until <see cref="Release"/>.</summary>
        public void Block()
        {
            lock (_gate)
            {
                _release ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Release()
        {
            TaskCompletionSource? release;
            lock (_gate)
            {
                release = _release;
                _release = null;
            }
            release?.TrySetResult();
        }

        public GpuUsageSample Read()
        {
            TaskCompletionSource? release;
            lock (_gate)
            {
                Reads++;
                _events.Add($"read{Reads}");
                release = _release;
            }
            release?.Task.GetAwaiter().GetResult();
            if (SamplerThrows) throw new InvalidOperationException("provider blew up");
            return Next;
        }

        public void Reset()
        {
            lock (_gate)
            {
                Resets++;
                _events.Add("reset");
            }
        }
    }

    private static PerformanceSamplingSession NewSession(Recorder recorder) =>
        new(recorder.Read, recorder.Reset);

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new PerformanceSamplingSession(null!, () => { }));
        Assert.Throws<ArgumentNullException>(() => new PerformanceSamplingSession(() => GpuUsageSample.Unavailable, null!));
    }

    [Fact]
    public async Task InactiveSession_DoesNoWork()
    {
        var recorder = new Recorder();
        var session = NewSession(recorder);

        var pending = session.SampleAsync();

        Assert.True(pending.IsCompleted);
        Assert.False((await pending).HasValue);
        Assert.Equal(0, recorder.Reads);
        Assert.Equal(0, recorder.Resets);
        Assert.False(session.IsActive);
    }

    [Fact]
    public async Task Start_ResetsBeforeFirstSample_AndTagsEpoch()
    {
        var recorder = new Recorder();
        var session = NewSession(recorder);

        int epoch = session.Start();
        var sample = await session.SampleAsync();

        Assert.Equal(new[] { "reset", "read1" }, recorder.Events);
        Assert.True(session.IsActive);
        Assert.Equal(epoch, session.CurrentEpoch);
        Assert.True(sample.HasValue);
        Assert.Equal(epoch, sample!.Value.Epoch);
        Assert.True(sample.Value.IsCurrent(session.CurrentEpoch));
        Assert.Equal(42d, sample.Value.Sample.DeviceUtilizationPercent);
    }

    [Fact]
    public async Task Reset_HappensOncePerGeneration()
    {
        var recorder = new Recorder();
        var session = NewSession(recorder);

        session.Start();
        await session.SampleAsync();
        await session.SampleAsync();

        Assert.Equal(1, recorder.Resets);
        Assert.Equal(new[] { "reset", "read1", "read2" }, recorder.Events);
    }

    [Fact]
    public async Task WhileReadInFlight_SecondSampleIsRejectedWithoutOverlap()
    {
        var recorder = new Recorder();
        var session = NewSession(recorder);
        session.Start();

        recorder.Block();
        var first = session.SampleAsync();
        Assert.False(first.IsCompleted);

        var second = session.SampleAsync();
        // Rejected synchronously: a dispatcher tick must never queue a second
        // provider read on top of a live one.
        Assert.True(second.IsCompleted);
        Assert.False((await second).HasValue);

        recorder.Release();
        var sample = await first;
        Assert.True(sample.HasValue);
        // One reading for the whole window, no matter how many ticks asked.
        Assert.Equal(1, recorder.Reads);
    }

    [Fact]
    public async Task Stop_ReturnsWithoutWaitingOnBlockedProvider_AndDiscardsResult()
    {
        var recorder = new Recorder();
        var session = NewSession(recorder);
        session.Start();

        recorder.Block();
        var pending = session.SampleAsync();
        Assert.False(pending.IsCompleted);

        int resetsBeforeStop = recorder.Resets;
        session.Stop(); // must return even though the provider read is still stuck

        Assert.False(session.IsActive);
        Assert.Equal(resetsBeforeStop, recorder.Resets);
        Assert.False(pending.IsCompleted);

        recorder.Release();
        var sample = await pending;
        // The late reading belongs to a stopped generation: it must not reach a readout.
        Assert.False(sample.HasValue);
        Assert.Equal(1, recorder.Reads);
    }

    [Fact]
    public async Task ReentryWhileOldReadPending_IgnoresOldCompletion_AndResetsBeforeNewGenerationRead()
    {
        var recorder = new Recorder();
        var session = NewSession(recorder);
        int epoch1 = session.Start();

        recorder.Block();
        var oldRead = session.SampleAsync();
        Assert.False(oldRead.IsCompleted);
        // Generation 1 already consumed its one baseline reset.
        int resetsAfterFirstGeneration = recorder.Resets;
        Assert.Equal(1, resetsAfterFirstGeneration);

        session.Stop();
        int epoch2 = session.Start();
        Assert.Equal(epoch1 + 1, epoch2);

        // The old read still owns the single-flight slot: no new read, and above
        // all no Reset racing a live provider read.
        var duringReentry = session.SampleAsync();
        Assert.True(duringReentry.IsCompleted);
        Assert.False((await duringReentry).HasValue);
        Assert.Equal(resetsAfterFirstGeneration, recorder.Resets);

        recorder.Release();
        Assert.False((await oldRead).HasValue);
        Assert.Equal(new[] { "reset", "read1" }, recorder.Events);
        Assert.Equal(1, recorder.Reads); // the dead generation read exactly once

        // First read of the new generation: fresh baseline first, then exactly
        // one provider read.
        recorder.Block();
        var fresh = session.SampleAsync();
        Assert.Equal(resetsAfterFirstGeneration + 1, recorder.Resets);
        recorder.Release();

        var sample = await fresh;
        Assert.True(sample.HasValue);
        Assert.Equal(epoch2, sample!.Value.Epoch);
        Assert.True(sample.Value.IsCurrent(epoch2));
        Assert.False(sample.Value.IsCurrent(epoch1));
        Assert.Equal(new[] { "reset", "read1", "reset", "read2" }, recorder.Events);
    }

    [Fact]
    public async Task StopThenSample_DoesNoWorkUntilRestart()
    {
        var recorder = new Recorder();
        var session = NewSession(recorder);
        session.Start();
        await session.SampleAsync();

        session.Stop();
        var afterStop = session.SampleAsync();

        Assert.True(afterStop.IsCompleted);
        Assert.False((await afterStop).HasValue);
        Assert.Equal(1, recorder.Reads);

        int epoch = session.Start();
        var restarted = await session.SampleAsync();
        Assert.True(restarted.HasValue);
        Assert.Equal(epoch, restarted!.Value.Epoch);
        Assert.Equal(2, recorder.Reads);
        Assert.Equal(2, recorder.Resets);
    }

    [Fact]
    public async Task ThrowingSampler_DoesNotFaultCallerOrLeakTheSingleFlightSlot()
    {
        var recorder = new Recorder { SamplerThrows = true };
        var session = NewSession(recorder);
        session.Start();

        var failed = await session.SampleAsync();

        Assert.True(failed.HasValue);
        Assert.Null(failed!.Value.Sample.DeviceUtilizationPercent);
        Assert.Null(failed.Value.Sample.DeviceMemoryBytes);
        Assert.Null(failed.Value.Sample.ProcessEngineTicks);

        recorder.SamplerThrows = false;
        var recovered = await session.SampleAsync();
        Assert.True(recovered.HasValue);
        Assert.Equal(42d, recovered!.Value.Sample.DeviceUtilizationPercent);
    }

    [Fact]
    public void Availability_ComesFromReturnedData_NotCategoryExistence()
    {
        Assert.Equal(
            PerformanceCounterAvailability.Unavailable,
            PerformanceSamplingSession.Classify(GpuUsageSample.Unavailable));
        Assert.Equal(
            PerformanceCounterAvailability.Unavailable,
            PerformanceSamplingSession.Classify(new GpuUsageSample()));
        Assert.Equal(
            PerformanceCounterAvailability.Available,
            PerformanceSamplingSession.Classify(new GpuUsageSample { ProcessEngineTicks = 1000L }));
        Assert.Equal(
            PerformanceCounterAvailability.Available,
            PerformanceSamplingSession.Classify(new GpuUsageSample { DeviceMemoryBytes = 512L }));
    }

    [Fact]
    public void InitialAvailability_IsUnknown_SoNoHintIsClaimedBeforeAnySample()
    {
        Assert.Equal(PerformanceCounterAvailability.Unknown, default(PerformanceCounterAvailability));
        Assert.NotEqual(PerformanceCounterAvailability.Unknown, PerformanceCounterAvailability.Unavailable);
    }
}
