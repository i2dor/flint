using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// An <see cref="ILogger{T}"/> that keeps everything written to it, so a test can assert on what was logged.
/// </summary>
/// <remarks>
/// Specifically so "the recovery phrase is never logged" is observable. With <c>NullLogger</c> everywhere, that
/// claim is unfalsifiable by construction: a line that printed the merchant's seed would pass every test in the
/// suite. <see cref="Lines"/> includes the formatted message and any attached exception's text, because both
/// end up in the operator's log sink.
/// </remarks>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<string> _lines = [];

    /// <summary>
    /// A snapshot, and synchronised, because some of the code under test logs from background threads it
    /// deliberately does not await — an abandoned SDK connect, for one.
    /// </summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
                return _lines.ToList();
        }
    }

    /// <summary>Everything logged, concatenated, for a single substring assertion.</summary>
    public string AllText => string.Join('\n', Lines);

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var line = formatter(state, exception);
        if (exception is not null)
            line += $" | {exception.GetType().FullName}: {exception.Message} | {exception.StackTrace}";
        lock (_lines)
            _lines.Add($"{logLevel}: {line}");
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
