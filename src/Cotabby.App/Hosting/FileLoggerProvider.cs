using System.IO;
using System.Text;
using Cotabby.Core.Support;
using Microsoft.Extensions.Logging;

namespace Cotabby.App.Hosting;

/// <summary>Bounded, sanitized, synchronous local diagnostics. IO failure disables this sink.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly Func<LogLevel> _minimumLevel;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private bool _disposed;

    public FileLoggerProvider(string path, Func<LogLevel>? minimumLevel = null,
        long maxBytes = 8 * 1024 * 1024, int maxFiles = 3)
    {
        _path = path;
        _minimumLevel = minimumLevel ?? (() => LogLevel.Information);
        _maxBytes = Math.Clamp(maxBytes, 256, 64 * 1024 * 1024);
        _maxFiles = Math.Clamp(maxFiles, 1, 10);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Open();
        }
        catch (Exception) { Disable(); }
    }

    private void Open() => _writer = new StreamWriter(new FileStream(
        _path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };

    private void Disable()
    {
        try { _writer?.Dispose(); } catch (Exception) { }
        _writer = null;
    }

    private void Rotate()
    {
        _writer!.Dispose();
        _writer = null;
        for (int i = _maxFiles; i >= 1; i--)
        {
            string destination = _path + "." + i;
            string source = i == 1 ? _path : _path + "." + (i - 1);
            if (File.Exists(source)) File.Move(source, destination, overwrite: true);
        }
        Open();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate) { _disposed = true; Disable(); }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= owner._minimumLevel();

        public void Log<TState>(LogLevel level, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            lock (owner._gate)
            {
                if (owner._disposed || owner._writer is null) return;
                try
                {
                    string line = SecretSanitizer.Sanitize($"{DateTime.Now:HH:mm:ss.fff} {level}: {category}[{eventId.Id}] {formatter(state, exception)}" +
                        (exception is null ? "" : $" :: {exception}"));
                    // A single huge record must not defeat the disk bound.
                    if (Encoding.UTF8.GetByteCount(line + Environment.NewLine) > owner._maxBytes)
                        line = "[oversized diagnostic suppressed]";
                    int bytes = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
                    if (owner._writer.BaseStream.Length + bytes > owner._maxBytes) owner.Rotate();
                    owner._writer!.WriteLine(line);
                }
                catch (Exception) { owner.Disable(); }
            }
        }
    }
}
