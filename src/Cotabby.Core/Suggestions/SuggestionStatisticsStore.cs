using System.Collections.ObjectModel;

namespace Cotabby.Core.Suggestions;

public enum SuggestionStatistic
{
    Triggered, Requested, EngineFired, Presented, AcceptedWord, AcceptedPhrase, AcceptedWhole,
    Exhausted, Cancelled, SuppressedCredential, SuppressedBattery, InsertionRejected,
}

/// <summary>Fixed-size session-only counters. No content, identifiers, history, disk IO or telemetry.</summary>
public sealed class SuggestionStatisticsStore
{
    private readonly object _gate = new();
    private readonly long[] _counts = new long[Enum.GetValues<SuggestionStatistic>().Length];
    private bool _enabled;
    public bool Enabled { get { lock (_gate) return _enabled; } set { lock (_gate) _enabled = value; } }

    public void Record(SuggestionStatistic statistic)
    {
        lock (_gate)
        {
            int index = (int)statistic;
            if (!_enabled || index < 0 || index >= _counts.Length) return;
            if (_counts[index] < long.MaxValue) _counts[index]++;
        }
    }

    public IReadOnlyDictionary<SuggestionStatistic, long> Snapshot()
    {
        lock (_gate)
            return new ReadOnlyDictionary<SuggestionStatistic, long>(Enum.GetValues<SuggestionStatistic>()
                .ToDictionary(kind => kind, kind => _counts[(int)kind]));
    }

    public void Clear() { lock (_gate) Array.Clear(_counts); }
}
