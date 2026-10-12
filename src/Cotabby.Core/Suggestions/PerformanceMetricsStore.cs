using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cotabby.Core.Suggestions;

/// <summary>One recorded suggestion request. Mirrors upstream's performance
/// log entry: when it happened, which model answered, and how long it took.</summary>
public sealed record PerformanceEntry
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string ModelId { get; init; }
    public required long ElapsedMilliseconds { get; init; }
}

/// <summary>
/// Bounded, opt-in log of per-request latencies. Mirrors upstream's
/// <c>PerformanceMetricsStore</c>: tracking is off by default, only the most
/// recent <see cref="MaximumEntries"/> requests are retained, and the log is
/// persisted so a debugging session sees history across restarts. File IO is
/// best-effort; invalid files/records are ignored and IO failures are contained.
/// Persistence is synchronous and can incur disk latency.
/// </summary>
public sealed class PerformanceMetricsStore
{
    public const int MaximumEntries = 200;
    private const int MaximumFileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly List<PerformanceEntry> _entries = new();
    private readonly object _gate = new();
    private bool _trackingEnabled;

    /// <summary>User-visible opt-in, persisted by the App-side settings.</summary>
    public bool TrackingEnabled
    {
        get { lock (_gate) return _trackingEnabled; }
        set { lock (_gate) _trackingEnabled = value; }
    }

    public string? Path { get; }

    public PerformanceMetricsStore(string? path = null)
    {
        Path = path;
        if (path is not null) Load();
    }

    public IReadOnlyList<PerformanceEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    /// <summary>Record one request. Ignored entirely while tracking is off, so
    /// a disabled log costs nothing beyond this check.</summary>
    public void Record(string modelId, long elapsedMilliseconds, DateTimeOffset? timestamp = null)
    {
        lock (_gate)
        {
            if (!_trackingEnabled) return;
            if (elapsedMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));

            _entries.Add(new PerformanceEntry
            {
                Timestamp = timestamp ?? DateTimeOffset.UtcNow,
                ModelId = string.IsNullOrWhiteSpace(modelId) ? "unknown" : modelId,
                ElapsedMilliseconds = elapsedMilliseconds,
            });
            if (_entries.Count > MaximumEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaximumEntries);
            }
        }
        Save();
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        Save();
    }

    private void Load()
    {
        if (Path is null || !File.Exists(Path)) return;
        try
        {
            using var fs = File.OpenRead(Path);
            if (fs.Length > MaximumFileBytes) return;

            var buffer = new byte[MaximumFileBytes + 1];
            int bytesRead = 0;
            while (bytesRead < buffer.Length)
            {
                int count = fs.Read(buffer, bytesRead, buffer.Length - bytesRead);
                if (count == 0) break;
                bytesRead += count;
            }
            if (bytesRead > MaximumFileBytes) return;

            using var document = JsonDocument.Parse(buffer.AsMemory(0, bytesRead));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return;

            var loaded = new List<PerformanceEntry>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                try
                {
                    var entry = element.Deserialize<PerformanceEntry>(JsonOpts);
                    if (entry is null || string.IsNullOrWhiteSpace(entry.ModelId)
                        || entry.ElapsedMilliseconds < 0)
                    {
                        continue;
                    }

                    loaded.Add(entry);
                    if (loaded.Count > MaximumEntries)
                    {
                        loaded.RemoveAt(0);
                    }
                }
                catch (JsonException)
                {
                    // Skip a malformed element without discarding valid neighbors.
                }
            }

            lock (_gate)
            {
                _entries.Clear();
                _entries.AddRange(loaded);
            }
        }
        catch (Exception)
        {
            // A corrupt or unreadable log must not break startup.
        }
    }

    private void Save()
    {
        if (Path is null) return;
        lock (_gate)
        {
            string? tmp = null;
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var snapshot = _entries.ToList();
                tmp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var fs = File.Create(tmp))
                {
                    JsonSerializer.Serialize(fs, snapshot, JsonOpts);
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, Path, overwrite: true);
            }
            catch (Exception)
            {
                // Best-effort persistence; never surface as a suggestion failure.
            }
            finally
            {
                if (tmp is not null)
                {
                    try
                    {
                        if (File.Exists(tmp)) File.Delete(tmp);
                    }
                    catch (Exception)
                    {
                        // Cleanup is best-effort as well.
                    }
                }
            }
        }
    }
}