using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.Tests.TestDoubles;

/// <summary>
/// Captures every BeginScope call's state so tests can assert on correlation properties (e.g.
/// TicketId, ExternalMessageId) pushed via ILogger.BeginScope, without depending on Serilog - the
/// production code only ever calls the provider-agnostic ILogger.BeginScope (see Application's
/// dependency rule: it must not reference Serilog directly).
/// </summary>
public class RecordingLogger<T> : ILogger<T>
{
    public List<IReadOnlyDictionary<string, object?>> Scopes { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        if (state is IEnumerable<KeyValuePair<string, object>> pairs)
        {
            Scopes.Add(pairs.ToDictionary(p => p.Key, p => (object?)p.Value));
        }

        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}
