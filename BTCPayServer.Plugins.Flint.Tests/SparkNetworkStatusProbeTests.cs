using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Two contracts on one class: the probe must never throw and never hang (a status page depends on it),
/// and its third-party round trip must be shared across stores, not multiplied per store.
/// </summary>
/// <remarks>
/// <para>
/// The never-throw tests run against the real static SDK call. Where the native library loads it performs a
/// network call and returns something; where it does not, it returns null. Either is a pass — what must not
/// happen is an exception reaching the status action.
/// </para>
/// <para>
/// The cache tests drive the probe seam with a fake clock, because the behaviour worth pinning is the cache
/// policy — call counts and TTL boundaries — not the SDK call itself, which the plugin cannot fake.
/// </para>
/// </remarks>
public class SparkNetworkStatusProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_failing_probe_reports_unknown_rather_than_throwing()
    {
        // Runs against the real probe. Where the native library loads it performs a network call and returns
        // something; where it does not, it returns null. Either is a pass — what must not happen is an exception
        // reaching the status action.
        var probe = new SparkNetworkStatusProbe(new CapturingLogger<SparkNetworkStatusProbe>());

        var status = await probe.TryGetAsync(CancellationToken.None);

        Assert.True(status is null || status.Status.Length > 0);
    }

    [Fact]
    public async Task A_cancelled_probe_still_returns_rather_than_throwing()
    {
        var probe = new SparkNetworkStatusProbe(new CapturingLogger<SparkNetworkStatusProbe>());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var status = await probe.TryGetAsync(cts.Token);

        Assert.True(status is null || status.Status.Length > 0);
    }

    [Theory]
    [InlineData("Operational", true)]
    [InlineData("operational", true)]
    // Everything else is not green, and Unknown in particular is not treated as healthy: it is what the SDK
    // reports when it cannot tell, which is not the same as "fine".
    [InlineData("Degraded", false)]
    [InlineData("Partial", false)]
    [InlineData("Major", false)]
    [InlineData("Unknown", false)]
    public void Only_an_explicitly_operational_network_reads_as_healthy(string status, bool expected)
    {
        var reported = new SparkNetworkStatus(status, DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, reported.IsOperational);
    }

    // ---------------------------------------------------------------------------------------------
    // Cache semantics (probe seam + fake clock).
    // ---------------------------------------------------------------------------------------------

    private static SparkNetworkStatus Operational() =>
        new("Operational", new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero));

    private static SparkNetworkStatus Degraded() =>
        new("Degraded", new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero));

    private static SparkNetworkStatusProbe Create(
        Func<CancellationToken, Task<SparkNetworkStatus?>> probe,
        Func<DateTimeOffset> clock) =>
        new(NullLogger<SparkNetworkStatusProbe>.Instance, (_, ct) => probe(ct), clock);

    private sealed class FakeClock
    {
        public DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow() => Now;
        public void Advance(TimeSpan by) => Now = Now.Add(by);
    }

    [Fact]
    public async Task A_success_is_reused_within_its_ttl_without_re_probing()
    {
        var clock = new FakeClock();
        var calls = 0;
        var probe = Create(async _ => { calls++; return Operational(); }, clock.UtcNow);

        var first = await probe.TryGetAsync(Ct);
        clock.Advance(TimeSpan.FromSeconds(30));
        var second = await probe.TryGetAsync(Ct);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_success_is_re_probed_once_its_ttl_expires()
    {
        var clock = new FakeClock();
        var calls = 0;
        var probe = Create(async _ => { calls++; return calls == 1 ? Operational() : Degraded(); }, clock.UtcNow);

        Assert.Equal("Operational", (await probe.TryGetAsync(Ct))!.Status);
        clock.Advance(SparkNetworkStatusProbe.SuccessTtl + TimeSpan.FromSeconds(1));

        var second = await probe.TryGetAsync(Ct);
        Assert.Equal(2, calls);
        Assert.Equal("Degraded", second!.Status);
    }

    [Fact]
    public async Task A_failure_is_backed_off_shorter_than_a_success_is_cached()
    {
        var clock = new FakeClock();
        var calls = 0;
        var probe = Create(async _ =>
        {
            calls++;
            return calls == 1 ? null : Operational();
        }, clock.UtcNow);

        Assert.Null(await probe.TryGetAsync(Ct));          // failure caches a null for RetryTtl

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(await probe.TryGetAsync(Ct));          // still within the retry backoff
        Assert.Equal(1, calls);

        clock.Advance(SparkNetworkStatusProbe.RetryTtl);   // past it — and NOT yet past SuccessTtl,
                                                           // so a retry does not have to wait out the
                                                           // success window
        var recovered = await probe.TryGetAsync(Ct);
        Assert.Equal(2, calls);
        Assert.Equal("Operational", recovered!.Status);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_probe_round_trip()
    {
        var clock = new FakeClock();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var probe = Create(async _ =>
        {
            calls++;
            await gate.Task;
            return Operational();
        }, clock.UtcNow);

        var first = probe.TryGetAsync(Ct);
        while (Volatile.Read(ref calls) == 0)
            await Task.Yield();
        var second = probe.TryGetAsync(Ct);               // arrives mid-probe; waits at the gate

        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Equal(results[0], results[1]);
        Assert.Equal("Operational", results[0]!.Status);
    }

    [Fact]
    public async Task A_stale_success_is_served_while_a_refresh_is_in_flight()
    {
        // The documented degrade: a caller that cannot get through the gate — here because its request
        // is cancelled while queueing — leaves with the previous cache rather than hanging or throwing.
        // (The bounded real-time timeout takes the same `return _cached` branch; testing that would just
        // spend the full 5 s deadline.)
        var clock = new FakeClock();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var probe = Create(async _ =>
        {
            calls++;
            if (calls == 1)
                return Operational();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return Degraded();
        }, clock.UtcNow);

        var cached = await probe.TryGetAsync(Ct);
        clock.Advance(SparkNetworkStatusProbe.SuccessTtl + TimeSpan.FromSeconds(1));

        var refresh = probe.TryGetAsync(Ct);              // first caller: probes, blocks here
        while (Volatile.Read(ref calls) < 2)
            await Task.Yield();

        using var queuer = new CancellationTokenSource(200);
        var served = await probe.TryGetAsync(queuer.Token);   // cancelled at the gate → previous cache
        Assert.Equal(cached, served);

        gate.SetResult();
        var refreshed = await refresh;
        Assert.Equal("Degraded", refreshed!.Status);      // the blocked probe still completes and refreshes
    }
}
