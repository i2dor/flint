using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// Polls a condition that background work will eventually satisfy, rather than sleeping blindly.
/// </summary>
/// <remarks>
/// <para>
/// The deadline only bounds a failing run — a passing one returns on the first poll that sees the condition —
/// so it is the longest any suite has needed (20s, for the event-loop paths) rather than a per-suite guess. A
/// shorter one would buy nothing on a green run and a flake on a loaded CI runner.
/// </para>
/// <para>
/// The condition is checked once more after the deadline passes, so a condition that became true during the last
/// delay is not reported as a timeout. The delay honours the test's cancellation token, so an aborted run stops
/// polling instead of spinning out the deadline.
/// </para>
/// </remarks>
public static class Eventually
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>Waits for <paramref name="condition"/>, failing with <paramref name="because"/> if it never holds.</summary>
    public static Task True(Func<bool> condition, string because) =>
        True(() => Task.FromResult(condition()), because);

    /// <inheritdoc cref="True(Func{bool}, string)"/>
    public static async Task True(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTimeOffset.UtcNow + Deadline;
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                Assert.Fail($"Not true within {Deadline.TotalSeconds:0}s: {because}");
            await Task.Delay(PollInterval, TestContext.Current.CancellationToken);
        }
    }
}
