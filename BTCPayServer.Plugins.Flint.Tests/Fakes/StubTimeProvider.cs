namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock only moves when a test moves it.
/// </summary>
/// <remarks>
/// Hand-rolled rather than taking a dependency on <c>Microsoft.Extensions.TimeProvider.Testing</c>: the only
/// thing under test that reads a clock is the sweep engine's grace period, and it reads it through
/// <see cref="TimeProvider.GetUtcNow"/> alone. Nothing here schedules timers, so there is nothing else to
/// simulate.
/// <para>
/// Without it, "an unresolved sweep is written off after five minutes" could only be tested by waiting five
/// minutes, and a test that waited would be deleted by the first person whose build it slowed down.
/// </para>
/// </remarks>
public sealed class StubTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public StubTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
