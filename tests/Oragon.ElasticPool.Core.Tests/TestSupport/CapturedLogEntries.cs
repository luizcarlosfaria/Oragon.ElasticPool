using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Oragon.ElasticPool.Core.Tests.TestSupport;

/// <summary>
/// One captured log record. Carries the level, EventId, formatted message, optional Exception,
/// and the structured state KVPs so tests can assert on individual log fields without
/// pulling Microsoft.Extensions.Logging.Testing FakeLogger (avoids dependency churn beyond
/// what Phase 1 pinned — see Plan 03 Task 1 design note).
/// </summary>
public sealed record CapturedLogEntry(
    LogLevel Level,
    EventId EventId,
    string CategoryName,
    string Message,
    Exception? Exception,
    IReadOnlyList<KeyValuePair<string, object?>> State);

/// <summary>
/// Minimal in-memory <see cref="ILoggerProvider"/> + <see cref="ILogger"/> pair that captures
/// every log entry into a thread-safe bag. Tests register via
/// <c>services.AddLogging(b =&gt; b.AddProvider(_capturedLogProvider).SetMinimumLevel(LogLevel.Trace))</c>.
/// </summary>
public sealed class CapturedLogEntries : ILoggerProvider
{
    private readonly ConcurrentBag<CapturedLogEntry> _entries = new();

    public IReadOnlyCollection<CapturedLogEntry> Entries => _entries.ToArray();

    public IEnumerable<CapturedLogEntry> ByEventId(int eventId) =>
        _entries.Where(e => e.EventId.Id == eventId);

    public ILogger CreateLogger(string categoryName) => new CapturedLogger(categoryName, _entries);

    public void Dispose() { /* no resources */ }

    private sealed class CapturedLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentBag<CapturedLogEntry> _sink;

        public CapturedLogger(string category, ConcurrentBag<CapturedLogEntry> sink)
        {
            _category = category;
            _sink = sink;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter is null ? state?.ToString() ?? string.Empty : formatter(state, exception);
            var kvps = state is IReadOnlyList<KeyValuePair<string, object?>> kv
                ? kv.ToArray()
                : Array.Empty<KeyValuePair<string, object?>>();
            _sink.Add(new CapturedLogEntry(logLevel, eventId, _category, message, exception, kvps));
        }
    }
}
