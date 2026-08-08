using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The probe's contract: it renders a status page, so it must never throw and never hang.
/// </summary>
/// <remarks>
/// The live call is covered by the regtest integration suite. What is worth pinning here is the behaviour the
/// status page depends on — that an unreachable or slow third-party status endpoint degrades to "unknown" rather
/// than 500ing the page — plus the classification, which decides whether the page shows green or amber.
/// </remarks>
public class SparkNetworkStatusProbeTests
{
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
}
