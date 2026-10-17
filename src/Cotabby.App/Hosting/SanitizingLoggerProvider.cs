using Cotabby.Core.Support;
using Microsoft.Extensions.Logging;

namespace Cotabby.App.Hosting;

/// <summary>Formats and redacts before either console or debugger sees a diagnostic.</summary>
public sealed class SanitizingLoggerProvider(Action<string> write, Func<LogLevel> minimumLevel) : ILoggerProvider
{
    public static LogLevel ParseVerbosity(string? value) => value?.ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        _ => LogLevel.Information,
    };

    public ILogger CreateLogger(string categoryName) => new Logger(write, minimumLevel, categoryName);
    public void Dispose() { }

    private sealed class Logger(Action<string> write, Func<LogLevel> minimumLevel, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= minimumLevel();
        public void Log<TState>(LogLevel level, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            try
            {
                string line = $"{DateTime.Now:HH:mm:ss.fff} {level}: {category}[{eventId.Id}] {formatter(state, exception)}";
                if (exception is not null) line += $" :: {exception}";
                write(SecretSanitizer.Sanitize(line));
            }
            catch (Exception) { /* Diagnostics must not break inference or input. */ }
        }
    }
}
