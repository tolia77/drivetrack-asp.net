using Microsoft.Extensions.Logging;

namespace DriveTrack.Integration.Tests.Support;

/// <summary>
/// A logging provider that keeps every record instead of writing it anywhere.
/// <para>
/// Some outcomes are only visible in a log. The geocoder answers a null address whether the
/// provider had none, whether the request was refused for rate limiting, or whether the pacing gate
/// gave up before the request was sent — DR-11 gives the caller one answer for all three, and that
/// is the contract. The difference matters to the operator reading the log, so the only way to
/// assert it is to read the log back.
/// </para>
/// <para>
/// Written for two threads: a side effect runs on a background loop and the assertion runs on the
/// test's own.
/// </para>
/// </summary>
internal sealed class RecordingLogger : ILoggerProvider
{
    private readonly Lock _gate = new();
    private readonly List<Record> _records = [];

    /// <summary>One record as the logging pipeline handed it over.</summary>
    /// <param name="Level">The level it was written at.</param>
    /// <param name="Category">The category, which is the logger's generic argument's full name.</param>
    /// <param name="Message">The message, already formatted with its arguments substituted in.</param>
    internal sealed record Record(LogLevel Level, string Category, string Message);

    /// <summary>Everything written so far, in order.</summary>
    public IReadOnlyList<Record> Records
    {
        get
        {
            lock (_gate)
            {
                return [.. _records];
            }
        }
    }

    /// <summary>The messages written at a level, from categories whose name contains a fragment.</summary>
    /// <param name="level">The level to keep.</param>
    /// <param name="category">A fragment of the category name, matched case-sensitively.</param>
    public IReadOnlyList<string> Messages(LogLevel level, string category) =>
        [.. Records
            .Where(record => record.Level == level
                && record.Category.Contains(category, StringComparison.Ordinal))
            .Select(record => record.Message)];

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing is held open: the records are a list in memory and outlive the provider on
        // purpose, so an assertion can still read them after the container has been disposed.
    }

    private void Add(Record record)
    {
        lock (_gate)
        {
            _records.Add(record);
        }
    }

    private sealed class Sink(RecordingLogger owner, string category) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            owner.Add(new Record(logLevel, category, formatter(state, exception)));
        }
    }
}
