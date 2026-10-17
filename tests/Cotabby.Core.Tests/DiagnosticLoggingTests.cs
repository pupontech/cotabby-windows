using Cotabby.App.Hosting;
using Microsoft.Extensions.Logging;

namespace Cotabby.Core.Tests;

public sealed class DiagnosticLoggingTests
{
    [Fact]
    public void LegacySettingsDefaultToInformationAndBoundedRetention()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<Cotabby.App.Settings.AppSettings>("{}")!;
        Assert.Equal("information", settings.LogVerbosity);
        Assert.Equal(8 * 1024 * 1024, settings.MaxLogBytes);
        Assert.Equal(3, settings.MaxLogFiles);
    }

    [Fact]
    public void ConsoleAndDebugAdapterSanitizesExceptionsAndHonorsLiveVerbosity()
    {
        var lines = new List<string>();
        LogLevel level = LogLevel.Warning;
        using var provider = new SanitizingLoggerProvider(lines.Add, () => level);
        var logger = provider.CreateLogger("test");
        logger.LogInformation("not visible");
        Assert.Empty(lines);
        level = LogLevel.Debug;
        logger.LogDebug(new Exception("password=synthetic-error"), "token=synthetic-token");
        Assert.Single(lines);
        Assert.DoesNotContain("synthetic-error", lines[0]);
        Assert.DoesNotContain("synthetic-token", lines[0]);
        Assert.Contains("[redacted]", lines[0]);
    }

    [Theory]
    [InlineData(null, LogLevel.Information)]
    [InlineData("invalid", LogLevel.Information)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("warning", LogLevel.Warning)]
    [InlineData("error", LogLevel.Error)]
    public void VerbosityHasSafeDefaults(string? setting, LogLevel expected) =>
        Assert.Equal(expected, SanitizingLoggerProvider.ParseVerbosity(setting));

    [Fact]
    public void FileFailureAndDisposedLoggersNeverEscapeIntoCallers()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cotabby-log-failure-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            using (var unwritable = new FileLoggerProvider(dir))
                unwritable.CreateLogger("test").LogError("unwritable");
            string path = Path.Combine(dir, "live.log");
            var provider = new FileLoggerProvider(path, maxBytes: 256);
            var logger = provider.CreateLogger("test");
            Directory.CreateDirectory(path + ".1"); // refuse rotation, without special permissions
            logger.LogInformation(new string('x', 180));
            logger.LogInformation(new string('y', 180));
            logger.LogInformation("sink disabled after IO failure");
            provider.Dispose();
            logger.LogError("late log after disposal");
            Assert.True(new FileInfo(path).Length <= 256);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void OversizedRecordsCannotExceedTheByteCap()
    {
        string path = Path.Combine(Path.GetTempPath(), "cotabby-log-large-" + Guid.NewGuid());
        try
        {
            using (var provider = new FileLoggerProvider(path, maxBytes: 256))
                provider.CreateLogger("test").LogInformation(new string('界', 1000));
            Assert.True(new FileInfo(path).Length <= 256);
            Assert.Contains("oversized diagnostic suppressed", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FileSinkRedactsSecretsAndKeepsExistingLoggerUsableAcrossRotation()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cotabby-log-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "live.log");
            using (var provider = new FileLoggerProvider(path, () => LogLevel.Debug, 256, 2))
            {
                var logger = provider.CreateLogger("test");
                for (int i = 0; i < 20; i++) logger.LogInformation("password=synthetic-secret count={Count}", i);
            }
            string[] files = Directory.GetFiles(dir);
            Assert.Equal(3, files.Length);
            Assert.All(files, f => {
                string text = File.ReadAllText(f);
                Assert.DoesNotContain("synthetic-secret", text);
                Assert.Contains("[redacted]", text);
                Assert.True(new FileInfo(f).Length <= 256);
            });
            Assert.Contains("count=19", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
