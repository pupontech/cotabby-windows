using Cotabby.Core.Metrics;

namespace Cotabby.Core.Presentation;

/// <summary>
/// How much the GPU counters have told us so far. <see cref="Unknown"/> is the
/// state before the first reading of a generation arrives, so a caller never
/// claims "unavailable" from a probe — only from actual returned data.
/// </summary>
public enum PerformanceCounterAvailability
{
    Unknown = 0,
    Available = 1,
    Unavailable = 2,
}

/// <summary>
/// One reading tagged with the sampling generation it was taken in. Callers
/// compare <see cref="Epoch"/> against the session's current epoch and drop
/// anything that no longer matches instead of writing it to a readout.
/// </summary>
public readonly record struct PerformanceSample(int Epoch, GpuUsageSample Sample)
{
    public bool IsCurrent(int currentEpoch) => Epoch == currentEpoch;
}

/// <summary>
/// Bounded UI sampling helper: owns the single-flight gate, the active
/// generation (epoch) and the baseline reset for one sampled readout, so the
/// WPF pane never performs a provider read on the UI thread and never overlaps
/// two reads. Platform-free — the provider and the baseline reset are injected
/// as delegates — so its ordering rules are testable without a GPU or a clock.
///
/// Contract:
/// <list type="bullet">
/// <item>Outside a started generation <see cref="SampleAsync"/> does no work at
/// all: it returns null without touching the provider.</item>
/// <item>At most one provider read is in flight; a second attempt is rejected
/// immediately rather than queued.</item>
/// <item><see cref="Start"/> always begins a new generation, and the injected
/// reset runs once before that generation's first read — never while a read
/// from a previous generation is still live.</item>
/// <item><see cref="Stop"/> returns without waiting for a blocked provider,
/// stops further polling, and makes any late result stale.</item>
/// </list>
/// </summary>
public sealed class PerformanceSamplingSession
{
    private readonly Func<GpuUsageSample> _sampler;
    private readonly Action _reset;
    private readonly object _gate = new();

    private int _epoch;
    private bool _active;
    private bool _readInFlight;
    private bool _needsReset;

    public PerformanceSamplingSession(Func<GpuUsageSample> sampler, Action reset)
    {
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _reset = reset ?? throw new ArgumentNullException(nameof(reset));
    }

    /// <summary>The generation results must match to be accepted; bumped by
    /// every <see cref="Start"/>; stopped sessions reject results via active state.</summary>
    public int CurrentEpoch
    {
        get { lock (_gate) return _epoch; }
    }

    public bool IsActive
    {
        get { lock (_gate) return _active; }
    }

    /// <summary>Begin a generation and return its epoch. A fresh baseline is
    /// requested here and applied as the next read's first step.</summary>
    public int Start()
    {
        lock (_gate)
        {
            _active = true;
            _epoch++;
            _needsReset = true;
            return _epoch;
        }
    }

    /// <summary>
    /// End the generation. Returns immediately — a read stuck inside the OS is
    /// never awaited, so the caller's UI thread cannot stall on it. The read
    /// itself cannot be cancelled, so it may still complete in the background;
    /// what <see cref="Stop"/> guarantees is that nothing further is polled and
    /// that its late result is rejected as stale.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            _active = false;
            // Staleness is decided by _active plus the generation id, so the
            // in-flight and later results of the old generation are rejected
            // without a further epoch bump (Start owns the generation counter).
            // The reset stays pending for the next Start so it can never run
            // alongside a read that is still live.
        }
    }

    /// <summary>
    /// Take at most one reading for the current generation. Returns null when
    /// the session is inactive, when a read is already in flight, or when the
    /// reading turned out to be stale by the time it finished, so the caller
    /// only ever sees data it may act on. It still re-checks the epoch before
    /// writing, because its own continuation can be deferred.
    /// </summary>
    public async Task<PerformanceSample?> SampleAsync()
    {
        int epoch;
        lock (_gate)
        {
            if (!_active) return null;
            if (_readInFlight) return null;
            if (_needsReset)
            {
                // Reset-first: the tracker's baseline must be cleared before the
                // read that opens this generation, and only while no read is live.
                _reset();
                _needsReset = false;
            }
            _readInFlight = true;
            epoch = _epoch;
        }

        GpuUsageSample raw;
        try
        {
            raw = await Task.Run(_sampler).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Providers are contractually non-throwing; a broken one still must
            // not fault the UI or leave the single-flight slot occupied.
            raw = GpuUsageSample.Unavailable;
        }

        lock (_gate)
        {
            _readInFlight = false;
            if (!_active || epoch != _epoch) return null;
            return new PerformanceSample(epoch, raw);
        }
    }

    /// <summary>
    /// Availability is read out of the returned data — a reading that carries no
    /// value at all is the only evidence for "unavailable". Category existence
    /// is never consulted here, because that check is a synchronous OS probe and
    /// says nothing about whether this adapter can be read.
    /// </summary>
    public static PerformanceCounterAvailability Classify(GpuUsageSample sample) =>
        sample.DeviceUtilizationPercent is not null
        || sample.ProcessUtilizationPercent is not null
        || sample.DeviceMemoryBytes is not null
        || sample.ProcessEngineTicks is not null
            ? PerformanceCounterAvailability.Available
            : PerformanceCounterAvailability.Unavailable;
}
