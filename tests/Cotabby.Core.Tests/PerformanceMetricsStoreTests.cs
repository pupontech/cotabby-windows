using Cotabby.Core.Suggestions;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Cotabby.Core.Tests;

public class PerformanceMetricsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cotabby-perf-" + Guid.NewGuid().ToString("N"));
    private string LogPath => Path.Combine(_dir, "performance.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void RecordsNothingWhileTrackingIsOff()
    {
        var store = new PerformanceMetricsStore();
        store.Record("tabby-2-nano", 120);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void RecordsTimestampModelAndElapsedWhenEnabled()
    {
        var store = new PerformanceMetricsStore { TrackingEnabled = true };
        var when = DateTimeOffset.Parse("2026-10-11T12:00:00+00:00");
        store.Record("tabby-2-plus", 240, when);

        var entry = Assert.Single(store.Entries);
        Assert.Equal("tabby-2-plus", entry.ModelId);
        Assert.Equal(240, entry.ElapsedMilliseconds);
        Assert.Equal(when, entry.Timestamp);
    }

    [Fact]
    public void KeepsOnlyTheNewestEntries()
    {
        var store = new PerformanceMetricsStore { TrackingEnabled = true };
        for (int i = 0; i < PerformanceMetricsStore.MaximumEntries + 25; i++)
        {
            store.Record("tabby-2-nano", i);
        }

        var entries = store.Entries;
        Assert.Equal(PerformanceMetricsStore.MaximumEntries, entries.Count);
        Assert.Equal(25, entries[0].ElapsedMilliseconds);
        Assert.Equal(PerformanceMetricsStore.MaximumEntries + 24, entries[^1].ElapsedMilliseconds);
    }

    [Fact]
    public void RejectsNegativeElapsed()
    {
        var store = new PerformanceMetricsStore { TrackingEnabled = true };
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Record("m", -1));
    }

    [Fact]
    public void PersistsAndReloadsAcrossInstances()
    {
        var first = new PerformanceMetricsStore(LogPath) { TrackingEnabled = true };
        first.Record("tabby-2-plus", 111);
        first.Record("tabby-2-nano", 222);

        var second = new PerformanceMetricsStore(LogPath);
        Assert.Equal(new[] { 111L, 222L }, second.Entries.Select(e => e.ElapsedMilliseconds));
        Assert.Equal("tabby-2-nano", second.Entries[^1].ModelId);
        Assert.False(second.TrackingEnabled);   // opt-in is settings-owned, not log-owned
    }

    [Fact]
    public void ClearEmptiesAndPersistsTheEmptyLog()
    {
        var store = new PerformanceMetricsStore(LogPath) { TrackingEnabled = true };
        store.Record("tabby-2-plus", 50);
        store.Clear();

        Assert.Empty(store.Entries);
        Assert.Empty(new PerformanceMetricsStore(LogPath).Entries);
    }

    [Fact]
    public void CorruptOrOversizedLogLoadsAsEmptyOrTrimmed()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LogPath, "{ not json");
        Assert.Empty(new PerformanceMetricsStore(LogPath).Entries);

        var many = Enumerable.Range(0, PerformanceMetricsStore.MaximumEntries + 10)
            .Select(i => new PerformanceEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                ModelId = "m",
                ElapsedMilliseconds = i,
            });
        File.WriteAllText(LogPath, System.Text.Json.JsonSerializer.Serialize(many.ToList()));
        var trimmed = new PerformanceMetricsStore(LogPath).Entries;
        Assert.Equal(PerformanceMetricsStore.MaximumEntries, trimmed.Count);
        Assert.Equal(10, trimmed[0].ElapsedMilliseconds);
    }

    [Fact]
    public async Task ConcurrentRecordsPersistTheLatestCompleteSnapshot()
    {
        var store = new PerformanceMetricsStore(LogPath) { TrackingEnabled = true };
        using var start = new ManualResetEventSlim(false);
        const int writers = 4;
        const int recordsPerWriter = 32;
        var tasks = Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < recordsPerWriter; i++)
            {
                store.Record($"model-{writer}", writer * recordsPerWriter + i);
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(tasks);

        var persisted = JsonSerializer.Deserialize<List<PerformanceEntry>>(File.ReadAllText(LogPath));
        Assert.NotNull(persisted);
        Assert.Equal(store.Entries, persisted);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void SaveDoesNotLoseLatestRecordWhenTheLegacyTempPathIsBusy()
    {
        Directory.CreateDirectory(_dir);
        using var busyLegacyTemp = new FileStream(
            LogPath + ".tmp", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var store = new PerformanceMetricsStore(LogPath) { TrackingEnabled = true };

        store.Record("latest", 17);

        var loaded = new PerformanceMetricsStore(LogPath).Entries;
        var entry = Assert.Single(loaded);
        Assert.Equal("latest", entry.ModelId);
        Assert.Equal(17, entry.ElapsedMilliseconds);
    }

    [Fact]
    public void FailedSaveCleansItsTemporaryFile()
    {
        Directory.CreateDirectory(LogPath); // Moving a file over a directory fails after writing the temp.
        var store = new PerformanceMetricsStore(LogPath) { TrackingEnabled = true };

        store.Record("model", 5);

        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void DisablingTrackingSynchronizesWithARecordWaitingToAppend()
    {
        var store = new PerformanceMetricsStore { TrackingEnabled = true };
        var gate = typeof(PerformanceMetricsStore)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        using var started = new ManualResetEventSlim(false);
        var recorder = new Thread(() =>
        {
            started.Set();
            store.Record("racing-model", 1);
        });

        Monitor.Enter(gate);
        try
        {
            recorder.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(
                () => (recorder.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5)));
            store.TrackingEnabled = false;
        }
        finally
        {
            Monitor.Exit(gate);
        }

        Assert.True(recorder.Join(TimeSpan.FromSeconds(5)));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void LoadSkipsNullAndSemanticallyInvalidEntries()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LogPath, """
            [
              {"Timestamp":"2026-10-11T12:00:00+00:00","ModelId":"valid","ElapsedMilliseconds":5},
              null,
              {"Timestamp":"2026-10-11T12:00:01+00:00","ElapsedMilliseconds":7},
              {"Timestamp":"2026-10-11T12:00:02+00:00","ModelId":"  ","ElapsedMilliseconds":8},
              {"Timestamp":"2026-10-11T12:00:03+00:00","ModelId":"negative","ElapsedMilliseconds":-1}
            ]
            """);

        var entries = new PerformanceMetricsStore(LogPath).Entries;

        var entry = Assert.Single(entries);
        Assert.Equal("valid", entry.ModelId);
        Assert.Equal(5, entry.ElapsedMilliseconds);
    }

    [Fact]
    public void LoadRejectsFilesLargerThanOneMiB()
    {
        Directory.CreateDirectory(_dir);
        var validEntry = "[{\"Timestamp\":\"2026-10-11T12:00:00+00:00\",\"ModelId\":\"m\",\"ElapsedMilliseconds\":5}]";
        File.WriteAllText(LogPath, new string(' ', 1024 * 1024) + validEntry);

        Assert.Empty(new PerformanceMetricsStore(LogPath).Entries);
    }

    [Fact]
    public void RecordNormalizesNullAndBlankModelIdentifiers()
    {
        var store = new PerformanceMetricsStore(LogPath) { TrackingEnabled = true };

        store.Record(null!, 5);
        store.Record(" \t", 6);

        Assert.Equal(new[] { "unknown", "unknown" }, store.Entries.Select(entry => entry.ModelId));
        Assert.Equal(new[] { "unknown", "unknown" },
            new PerformanceMetricsStore(LogPath).Entries.Select(entry => entry.ModelId));
    }
}