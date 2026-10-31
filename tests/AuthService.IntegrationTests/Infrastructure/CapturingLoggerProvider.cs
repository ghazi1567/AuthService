using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AuthService.IntegrationTests.Infrastructure;

/// <summary>
/// Keeps every log entry the service writes, from every category and level, so a test can check what was logged
/// (for example that no token or link ever appears in a log).
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    /// <summary>Each entry as one string: level, category, message and any exception.</summary>
    public IReadOnlyList<string> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var text = $"[{logLevel}] {category}: {formatter(state, exception)}";
            entries.Enqueue(exception is null ? text : $"{text}{Environment.NewLine}{exception}");
        }
    }
}
